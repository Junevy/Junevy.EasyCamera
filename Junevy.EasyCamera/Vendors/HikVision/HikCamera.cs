using Junevy.EasyCamera.Core.Abstractions;
using MvCameraControl;
using System;
using System.Threading;

namespace Junevy.EasyCamera.Vendors.HikVision
{
    /// <summary>
    /// 海康工业相机
    /// </summary>
    public class HikCamera : ICamera
    {
        private readonly object stateLock = new();
        private readonly SemaphoreSlim operationGate = new(1, 1);
        private readonly Func<IDeviceInfo, IDevice> deviceFactory;

        /// <summary>
        /// 海康原生设备信息
        /// </summary>
        private readonly IDeviceInfo deviceInfo;

        /// <summary>
        /// 相机帧数据流，由调用方创建并管理生命周期
        /// </summary>
        private readonly ICameraStream stream;

        /// <summary>
        /// 海康相机设备实例
        /// </summary>
        private IDevice device;

        /// <summary>
        /// 设备对应的取流器。回调归还缓冲区时使用捕获的取流器，
        /// 不依赖 Dispose 后被清空的 device 字段。
        /// </summary>
        private IStreamGrabber streamGrabber;

        /// <summary>
        /// 相机内部图像缓冲区数量，0表示使用SDK默认值
        /// </summary>
        private int bufferCount;

        /// <summary>
        /// 是否已打开
        /// </summary>
        private int isOpen;

        /// <summary>
        /// 是否正在取流
        /// </summary>
        private int isGrabbing;

        /// <summary>
        /// 是否已释放
        /// </summary>
        private int disposed;

        /// <summary>
        /// 已进入回调但尚未完成归还缓冲区的数量。
        /// </summary>
        private int inFlightCallbacks;

        /// <summary>
        /// 关闭/释放时等待在途回调排空的超时时间。
        /// </summary>
        private static readonly TimeSpan CallbackDrainTimeout = TimeSpan.FromSeconds(5);

        private bool callbacksEnabled;
        private bool closing;
        private string lastError;

        /// <summary>
        /// 是否已打开
        /// </summary>
        public bool IsConnected
            => Volatile.Read(ref this.isOpen) == 1
            && this.device != null
            && this.device.IsConnected;

        /// <summary>
        /// 最近一次无法通过返回值表达的生命周期错误（例如 StopGrab 失败）。
        /// </summary>
        public string LastError
        {
            get
            {
                lock (this.stateLock)
                    return this.lastError;
            }
        }

        /// <summary>
        /// 是否正在取流
        /// </summary>
        public bool IsGrabbing
            => Volatile.Read(ref this.isGrabbing) == 1;

        /// <summary>
        /// 构造海康工业相机
        /// </summary>
        /// <param name="info">海康原生设备信息</param>
        /// <param name="stream">相机帧数据流</param>
        /// <exception cref="ArgumentNullException">
        /// <c>info</c> 或 <c>stream</c> 为 <c>null</c>
        /// </exception>
        public HikCamera(IDeviceInfo info, ICameraStream stream)
            : this(info, stream, DeviceFactory.CreateDevice)
        {
        }

        /// <summary>
        /// 构造带设备创建 seam 的海康相机，避免生命周期测试依赖真实硬件。
        /// </summary>
        /// <param name="info">海康原生设备信息</param>
        /// <param name="stream">相机帧数据流</param>
        /// <param name="deviceFactory">设备创建委托</param>
        public HikCamera(IDeviceInfo info, ICameraStream stream, Func<IDeviceInfo, IDevice> deviceFactory)
        {
            this.deviceInfo = info ?? throw new ArgumentNullException(nameof(info));
            this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
            this.deviceFactory = deviceFactory ?? throw new ArgumentNullException(nameof(deviceFactory));
        }

        /// <summary>
        /// 打开相机
        /// </summary>
        /// <returns>
        /// 相机操作结果
        /// </returns>
        public CameraResult Connect()
        {
            this.operationGate.Wait();
            try
            {
                IDevice currentDevice;
                IStreamGrabber currentGrabber;
                lock (this.stateLock)
                {
                    if (this.disposed == 1)
                        return CameraResult.Fail(-1, "The camera has been disposed");
                    if (this.IsConnected)
                        return CameraResult.Fail(-1, "The camera has been opened");

                    this.device ??= this.deviceFactory(this.deviceInfo);
                    currentDevice = this.device;
                }

                var result = currentDevice.Open();
                if (result != MvError.MV_OK)
                {
                    this.SetLastError($"Open camera failed with error code {result}.");
                    return CameraResult.Fail(result, "Open camera failed");
                }

                currentGrabber = currentDevice.StreamGrabber;
                if (currentGrabber == null)
                {
                    currentDevice.Close();
                    this.SetLastError("Open camera failed because the stream grabber is null.");
                    return CameraResult.Fail(-1, "Open camera failed");
                }

                lock (this.stateLock)
                {
                    this.streamGrabber = currentGrabber;
                    this.isOpen = 1;
                    this.closing = false;
                    this.callbacksEnabled = true;
                }

                try
                {
                    // 先解绑再绑定，避免重复打开时回调被注册多次。
                    currentGrabber.FrameGrabedEvent -= this.ProcessFrameCallBack;
                    currentGrabber.FrameGrabedEvent += this.ProcessFrameCallBack;

                    // 延迟应用缓冲区配置：必须在设备打开之后、开始取流之前。
                    // 该配置为可选优化项，失败不影响相机可用性。
                    if (this.bufferCount > 0)
                        this.TryApplyBufferCount();

                    this.SetLastError(null);
                    return CameraResult.Success(result);
                }
                catch (Exception e)
                {
                    lock (this.stateLock)
                    {
                        this.callbacksEnabled = false;
                        this.isOpen = 0;
                    }

                    try
                    {
                        currentGrabber.FrameGrabedEvent -= this.ProcessFrameCallBack;
                        currentDevice.Close();
                    }
                    catch
                    {
                    }

                    this.SetLastError(e.Message);
                    return CameraResult.Fail(-1, e.Message);
                }
            }
            catch (MvException mve)
            {
                this.SetLastError(mve.Message);
                return CameraResult.Fail(mve.ErrorCode, mve.Message);
            }
            catch (Exception e)
            {
                this.SetLastError(e.Message);
                return CameraResult.Fail(-1, e.Message);
            }
            finally
            {
                this.operationGate.Release();
            }
        }

        /// <summary>
        /// 关闭相机
        /// </summary>
        /// <returns>
        /// 相机操作结果
        /// </returns>
        public CameraResult Close()
        {
            this.operationGate.Wait();
            try
            {
                IDevice currentDevice;
                IStreamGrabber currentGrabber;
                lock (this.stateLock)
                {
                    if (this.disposed == 1)
                        return CameraResult.Fail(-1, "Camera has been disposed");

                    currentDevice = this.device;
                    currentGrabber = this.streamGrabber ?? currentDevice?.StreamGrabber;
                    if (currentDevice == null)
                        return CameraResult.Fail(-1, "Camera not initialized");

                    this.closing = true;
                    this.callbacksEnabled = false;
                }

                try
                {
                    if (currentGrabber != null)
                        currentGrabber.FrameGrabedEvent -= this.ProcessFrameCallBack;
                }
                catch (Exception freeError)
                {
                    this.SetLastError(freeError.Message);
                }

                var stopResult = this.StopGrabCore(currentGrabber);
                if (!stopResult.IsSuccess)
                {
                    this.RestoreCallbackSubscription(currentGrabber);
                    lock (this.stateLock)
                    {
                        this.closing = false;
                        this.callbacksEnabled = true;
                    }

                    return stopResult;
                }

                this.WaitForCallbacks();

                var closeCode = currentDevice.Close();
                if (closeCode != MvError.MV_OK)
                {
                    this.SetLastError($"Close camera failed with error code {closeCode}.");
                    this.RestoreCallbackSubscription(currentGrabber);
                    lock (this.stateLock)
                    {
                        this.closing = false;
                        this.callbacksEnabled = true;
                    }

                    return CameraResult.Fail(closeCode, "Close camera failed");
                }

                lock (this.stateLock)
                {
                    this.isOpen = 0;
                    this.isGrabbing = 0;
                    this.callbacksEnabled = false;
                    this.closing = false;
                }
                this.SetLastError(null);
                return CameraResult.Success(closeCode);
            }
            catch (MvException me)
            {
                this.SetLastError(me.Message);
                return CameraResult.Fail(me.ErrorCode, me.Message);
            }
            catch (Exception e)
            {
                this.SetLastError(e.Message);
                return CameraResult.Fail(-1, e.Message);
            }
            finally
            {
                this.operationGate.Release();
            }
        }

        /// <summary>
        /// 开始取流
        /// </summary>
        /// <returns>
        /// 相机操作结果
        /// </returns>
        public CameraResult StartGrab()
        {
            this.operationGate.Wait();
            try
            {
                IStreamGrabber currentGrabber;
                lock (this.stateLock)
                {
                    if (this.disposed == 1)
                        return CameraResult.Fail(-1, "The camera has been disposed");
                    if (!this.IsConnected)
                        return CameraResult.Fail(-1, "Camera is not open");
                    if (Volatile.Read(ref this.isGrabbing) == 1)
                        return CameraResult.Fail(-1, "Camera is already grabbing");

                    currentGrabber = this.streamGrabber ?? this.device?.StreamGrabber;
                }

                if (currentGrabber == null)
                    return CameraResult.Fail(-1, "Camera stream grabber is not initialized");

                var result = currentGrabber.StartGrabbing();

                if (result != MvError.MV_OK)
                {
                    this.SetLastError($"Start grabbing failed with error code {result}.");
                    return CameraResult.Fail(result, "Start grabbing failed");
                }

                Interlocked.Exchange(ref this.isGrabbing, 1);
                this.SetLastError(null);
                return CameraResult.Result(true, result);
            }
            catch (MvException me)
            {
                this.SetLastError(me.Message);
                return CameraResult.Fail(me.ErrorCode, me.Message);
            }
            catch (Exception e)
            {
                this.SetLastError(e.Message);
                return CameraResult.Fail(-1, e.Message);
            }
            finally
            {
                this.operationGate.Release();
            }
        }

        /// <summary>
        /// 停止取流（幂等，可重复调用）
        /// </summary>
        public void StopGrab()
        {
            this.operationGate.Wait();
            try
            {
                IStreamGrabber currentGrabber;
                lock (this.stateLock)
                {
                    if (Volatile.Read(ref this.isGrabbing) == 0)
                        return;
                    currentGrabber = this.streamGrabber ?? this.device?.StreamGrabber;
                }

                this.StopGrabCore(currentGrabber);
            }
            finally
            {
                this.operationGate.Release();
            }
        }

        /// <summary>
        /// 调用 native StopGrabbing，并且只在 native 成功后清除取流状态。
        /// </summary>
        private CameraResult StopGrabCore(IStreamGrabber currentGrabber)
        {
            if (Volatile.Read(ref this.isGrabbing) == 0)
                return CameraResult.Success(MvError.MV_OK);

            if (currentGrabber == null)
            {
                this.SetLastError("Stop grabbing failed because the stream grabber is null.");
                return CameraResult.Fail(-1, "Stop grabbing failed");
            }

            try
            {
                var result = currentGrabber.StopGrabbing();
                if (result != MvError.MV_OK)
                {
                    // native 失败时保留 IsGrabbing=true，避免向上层伪装成已停止。
                    this.SetLastError($"Stop grabbing failed with error code {result}.");
                    return CameraResult.Fail(result, "Stop grabbing failed");
                }

                Interlocked.Exchange(ref this.isGrabbing, 0);
                this.SetLastError(null);
                return CameraResult.Success(result);
            }
            catch (MvException me)
            {
                this.SetLastError(me.Message);
                return CameraResult.Fail(me.ErrorCode, me.Message);
            }
            catch (Exception e)
            {
                this.SetLastError(e.Message);
                return CameraResult.Fail(-1, e.Message);
            }
        }

        /// <summary>
        /// 设置相机内部图像缓冲区数量（帧数）。
        /// 相机已打开时立即生效，否则延迟到 <see cref="Connect" /> 成功后应用
        /// </summary>
        /// <param name="count">缓冲区数量，须大于0</param>
        /// <returns>
        /// 相机操作结果
        /// </returns>
        public CameraResult SetBufferCount(int count)
        {
            if (count < 1)
                return CameraResult.Fail(-1, "The buffer count must be greater than zero");

            this.bufferCount = count;

            if (this.device == null || !this.IsConnected)
                return CameraResult.Success(0);

            try
            {
                var result = this.device.StreamGrabber.SetImageNodeNum((uint)count);
                return CameraResult.Result(result == MvError.MV_OK, result);
            }
            catch (MvException me)
            {
                return CameraResult.Fail(me.ErrorCode, me.Message);
            }
            catch (Exception e)
            {
                return CameraResult.Fail(-1, e.Message);
            }
        }

        /// <summary>
        /// 尝试应用相机内部图像缓冲区配置。
        /// 该配置属于可选优化项，失败时不影响相机可用性，故不向上抛出异常
        /// </summary>
        private void TryApplyBufferCount()
        {
            try
            {
                this.device?.StreamGrabber?.SetImageNodeNum((uint)this.bufferCount);
            }
            catch
            {
                // 可选配置失败不影响相机可用性
            }
        }

        /// <summary>
        /// 获取相机序列号
        /// </summary>
        /// <returns>
        /// 相机序列号，未知时返回空字符串
        /// </returns>
        public string GetSerialNumber() => this.deviceInfo?.SerialNumber ?? string.Empty;

        /// <summary>
        /// 设置整型参数
        /// </summary>
        /// <param name="paramName">参数名</param>
        /// <param name="value">参数值</param>
        /// <returns>
        /// 相机操作结果
        /// </returns>
        public CameraResult SetParam(string paramName, int value)
        {
            var state = this.CheckReady(paramName);
            if (!state.IsSuccess)
                return state;

            return ToResult(this.device.Parameters.SetIntValue(paramName, value), "Check the paramName!");
        }

        /// <summary>
        /// 设置浮点型参数
        /// </summary>
        /// <param name="paramName">参数名</param>
        /// <param name="value">参数值</param>
        /// <returns>
        /// 相机操作结果
        /// </returns>
        public CameraResult SetParam(string paramName, float value)
        {
            var state = this.CheckReady(paramName);
            if (!state.IsSuccess)
                return state;

            return ToResult(this.device.Parameters.SetFloatValue(paramName, value), "Check the paramName!");
        }

        /// <summary>
        /// 设置布尔型参数
        /// </summary>
        /// <param name="paramName">参数名</param>
        /// <param name="value">参数值</param>
        /// <returns>
        /// 相机操作结果
        /// </returns>
        public CameraResult SetParam(string paramName, bool value)
        {
            var state = this.CheckReady(paramName);
            if (!state.IsSuccess)
                return state;

            return ToResult(this.device.Parameters.SetBoolValue(paramName, value), "Check the paramName!");
        }

        /// <summary>
        /// 设置字符串参数
        /// </summary>
        /// <param name="paramName">参数名</param>
        /// <param name="value">参数值</param>
        /// <returns>
        /// 相机操作结果
        /// </returns>
        public CameraResult SetParam(string paramName, string value)
        {
            var state = this.CheckReady(paramName);
            if (!state.IsSuccess)
                return state;

            return ToResult(this.device.Parameters.SetStringValue(paramName, value), "Check the paramName!");
        }

        /// <summary>
        /// 设置枚举参数
        /// </summary>
        /// <param name="paramName">参数名</param>
        /// <param name="value">枚举符号名</param>
        /// <returns>
        /// 相机操作结果
        /// </returns>
        public CameraResult SetEnumParam(string paramName, string value)
        {
            if (!this.IsConnected)
                return CameraResult.Fail(-1, "Camera is not open");

            if (string.IsNullOrEmpty(paramName) || string.IsNullOrEmpty(value))
                return CameraResult.Fail(-1, "The paramName or value is null or empty");

            return ToResult(this.device.Parameters.SetEnumValueByString(paramName, value), "Check the paramName or value!");
        }

        /// <summary>
        /// long → int 的受检转换：越界按"取值失败"处理，禁止静默回绕
        /// </summary>
        internal static bool TryConvertToInt64ToInt32(long value, out int result)
        {
            if (value < int.MinValue || value > int.MaxValue)
            {
                result = 0;
                return false;
            }

            result = (int)value;
            return true;
        }

        public bool TryGetParam<T>(string paramName, out T value)
        {
            value = default;
            if (!this.IsConnected || string.IsNullOrEmpty(paramName))
                return false;

            try
            {
                var type = typeof(T);

                if (type == typeof(int))
                {
                    if (this.device.Parameters.GetIntValue(paramName, out IIntValue intValue) == MvError.MV_OK
                        && TryConvertToInt64ToInt32(intValue.CurValue, out var value32))
                    {
                        value = (T)(object)value32;
                        return true;
                    }

                    return false;
                }

                if (type == typeof(long))
                {
                    if (this.device.Parameters.GetIntValue(paramName, out IIntValue intValue64) == MvError.MV_OK)
                    {
                        value = (T)(object)intValue64.CurValue;
                        return true;
                    }

                    return false;
                }

                if (type == typeof(float))
                {
                    if (this.device.Parameters.GetFloatValue(paramName, out IFloatValue floatValue) == MvError.MV_OK)
                    {
                        value = (T)(object)floatValue.CurValue;
                        return true;
                    }

                    return false;
                }

                if (type == typeof(string))
                {
                    if (this.device.Parameters.GetStringValue(paramName, out IStringValue stringValue) == MvError.MV_OK)
                    {
                        value = (T)(object)(stringValue.CurValue ?? string.Empty);
                        return true;
                    }

                    return false;
                }

                if (type == typeof(bool))
                {
                    if (this.device.Parameters.GetBoolValue(paramName, out bool boolValue) == MvError.MV_OK)
                    {
                        value = (T)(object)boolValue;
                        return true;
                    }

                    return false;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 获取参数
        /// </summary>
        /// <typeparam name="T">参数类型，支持 int、long、float、string、bool</typeparam>
        /// <param name="paramName">参数名</param>
        /// <returns>
        /// 参数值，类型不支持或相机不可用时返回 default(T)
        /// </returns>
        public T GetParam<T>(string paramName)
            => this.TryGetParam<T>(paramName, out var value) ? value : default;

        public bool TryGetEnumParam(string paramName, out string value)
        {
            value = null;
            if (!this.IsConnected || string.IsNullOrEmpty(paramName))
                return false;

            try
            {
                if (this.device.Parameters.GetEnumValue(paramName, out IEnumValue enumValue) == MvError.MV_OK)
                {
                    value = enumValue.CurEnumEntry?.Symbolic ?? string.Empty;
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 获取枚举参数的符号名
        /// </summary>
        /// <param name="paramName">参数名</param>
        /// <returns>
        /// 枚举符号名，获取失败时返回空字符串
        /// </returns>
        public string GetEnumParam(string paramName)
            => this.TryGetEnumParam(paramName, out var value) ? value : string.Empty;

        /// <summary>
        /// 执行命令
        /// </summary>
        /// <param name="command">命令</param>
        /// <returns>
        /// 相机操作结果
        /// </returns>
        public CameraResult ExecuteCommand(string command)
        {
            if (!this.IsConnected)
                return CameraResult.Fail(-1, "Camera is not open");

            if (string.IsNullOrEmpty(command))
                return CameraResult.Fail(-1, "Check the command");

            return ToResult(this.device.Parameters.SetCommandValue(command), "Execute command failed");
        }

        /// <summary>
        /// 释放相机（幂等，可重复调用）
        /// </summary>
        public void Dispose()
        {
            this.operationGate.Wait();
            try
            {
                IDevice currentDevice;
                IStreamGrabber currentGrabber;
                lock (this.stateLock)
                {
                    if (this.disposed == 1)
                        return;

                    this.disposed = 1;
                    this.closing = true;
                    this.callbacksEnabled = false;
                    currentDevice = this.device;
                    currentGrabber = this.streamGrabber ?? currentDevice?.StreamGrabber;
                }

                try
                {
                    if (currentGrabber != null)
                        currentGrabber.FrameGrabedEvent -= this.ProcessFrameCallBack;
                }
                catch (Exception freeError)
                {
                    this.SetLastError(freeError.Message);
                }

                if (currentGrabber != null)
                {
                    this.StopGrabCore(currentGrabber);
                }

                this.WaitForCallbacks();

                try
                {
                    currentDevice?.Close();
                }
                catch (Exception e)
                {
                    this.SetLastError(e.Message);
                }

                try
                {
                    if (currentDevice is IDisposable disposable)
                        disposable.Dispose();
                }
                catch (Exception e)
                {
                    this.SetLastError(e.Message);
                }

                lock (this.stateLock)
                {
                    this.device = null;
                    this.streamGrabber = null;
                    this.isOpen = 0;
                    this.isGrabbing = 0;
                    this.callbacksEnabled = false;
                    this.closing = false;
                }
            }
            finally
            {
                this.operationGate.Release();
            }
        }

        /// <summary>
        /// 海康相机帧到达回调，运行在SDK采集线程
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="e">帧到达事件参数</param>
        private void ProcessFrameCallBack(object sender, FrameGrabbedEventArgs e)
        {
            var frameOut = e?.FrameOut;
            if (frameOut == null) return;

            var callbackGrabber = sender as IStreamGrabber;
            bool shouldPublish;
            IStreamGrabber fallbackGrabber;
            lock (this.stateLock)
            {
                this.inFlightCallbacks++;
                shouldPublish = this.callbacksEnabled
                    && this.disposed == 0
                    && this.isOpen == 1
                    && !this.closing;
                fallbackGrabber = this.streamGrabber;
            }

            HikFrameWrapper frame = null;

            try
            {
                // 克隆帧后再归还SDK缓冲区，使下游订阅者持有独立于SDK缓冲区的内存
                if (shouldPublish && frameOut.Clone() is IFrameOut cloned)
                {
                    frame = new HikFrameWrapper(cloned);
                    this.stream.Publish(frame);

                    // 所有权已移交数据流，由订阅者负责释放
                    frame = null;
                }
            }
            catch
            {
                // 回调运行在SDK采集线程，异常不得外泄，否则会中断采集
                frame?.Dispose();
                frame = null;
            }
            finally
            {
                try
                {
                    // frameOut 的所有权在进入回调时已经由SDK交给了本方法；
                    // 无论相机是否正在 Dispose，都必须使用回调来源取流器归还。
                    (callbackGrabber ?? fallbackGrabber)?.FreeImageBuffer(frameOut);
                }
                catch (Exception freeError)
                {
                    this.SetLastError(freeError.Message);
                }
                finally
                {
                    lock (this.stateLock)
                    {
                        this.inFlightCallbacks--;
                        if (this.inFlightCallbacks <= 0)
                        {
                            this.inFlightCallbacks = 0;
                            Monitor.PulseAll(this.stateLock);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 等待已经开始的回调释放 SDK 缓冲区，避免设备句柄提前销毁。
        /// 带有限超时：SDK 回调挂死时记录诊断信息并继续关闭，避免 Close/Dispose 永久阻塞。
        /// </summary>
        private void WaitForCallbacks()
        {
            lock (this.stateLock)
            {
                var deadline = DateTime.UtcNow + CallbackDrainTimeout;
                while (this.inFlightCallbacks > 0)
                {
                    var remaining = deadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero || !Monitor.Wait(this.stateLock, remaining))
                    {
                        this.SetLastError(
                            $"Timed out waiting for {this.inFlightCallbacks} in-flight frame callback(s) to drain.");
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// 关闭失败后恢复回调订阅，使相机仍可被调用方重试关闭或停止取流。
        /// </summary>
        private void RestoreCallbackSubscription(IStreamGrabber currentGrabber)
        {
            if (currentGrabber == null)
                return;

            try
            {
                currentGrabber.FrameGrabedEvent -= this.ProcessFrameCallBack;
                currentGrabber.FrameGrabedEvent += this.ProcessFrameCallBack;
            }
            catch (Exception e)
            {
                this.SetLastError(e.Message);
            }
        }

        private void SetLastError(string message)
        {
            lock (this.stateLock)
                this.lastError = message;
        }

        /// <summary>
        /// 校验相机与参数名是否可用
        /// </summary>
        /// <param name="paramName">参数名</param>
        /// <returns>
        /// 相机操作结果
        /// </returns>
        private CameraResult CheckReady(string paramName)
        {
            if (!this.IsConnected)
                return CameraResult.Fail(-1, "Camera is not open");

            if (string.IsNullOrEmpty(paramName))
                return CameraResult.Fail(-1, "The paramName is null or empty");

            return CameraResult.Success(0);
        }

        /// <summary>
        /// 将海康错误码转换为统一结果
        /// </summary>
        /// <param name="errorCode">海康错误码</param>
        /// <param name="failMessage">失败提示信息</param>
        /// <returns>
        /// 相机操作结果
        /// </returns>
        private static CameraResult ToResult(int errorCode, string failMessage)
        {
            var success = errorCode == MvError.MV_OK;
            return CameraResult.Result(success, errorCode, success ? string.Empty : failMessage);
        }
    }
}
