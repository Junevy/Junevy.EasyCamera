using Junevy.EasyCamera.Core.Abstractions;
using Junevy.EasyCamera.Core.Common;
using MVSDK_Net;
using System;
using System.Threading;
using static MVSDK_Net.IMVDefine;

namespace Junevy.EasyCamera.Vendors.IRayple
{
    /// <summary>
    /// Irayple工业相机。
    /// 尚未开发完毕（类型标记 <see cref="ObsoleteAttribute" />），启用即抛未实现异常；
    /// 此处按与海康一致的生命周期纪律维护：原生访问串行化、释放前排空在途回调。
    /// </summary>
    [Obsolete("未开发完毕", true)]
    public class IRaypleCamera : ICamera, IParameterSource, IBufferConfigurable
    {
        /// <summary>
        /// Irayple原生设备信息
        /// </summary>
        private readonly IMV_DeviceInfo cameraInfo;

        /// <summary>
        /// 相机帧数据流，由调用方创建并管理生命周期
        /// </summary>
        private readonly ICameraStream stream;

        /// <summary>
        /// 帧到达回调委托，构造时缓存以避免被GC回收
        /// </summary>
        private readonly IMV_FrameCallBack frameHandler;

        /// <summary>
        /// 串行化全部原生访问（打开/关闭/取流/参数/释放）
        /// </summary>
        private readonly SemaphoreSlim operationGate = new(1, 1);

        /// <summary>
        /// Irayple相机实例
        /// </summary>
        private MyCamera camera;

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
        /// 帧回调是否已绑定到原生句柄：同一句柄只绑定一次，避免重复回调导致重复发布
        /// </summary>
        private int callbackAttached;

        /// <summary>
        /// 已进入回调但尚未完成归还帧的数量
        /// </summary>
        private int inFlightCallbacks;

        /// <summary>
        /// 释放时等待在途回调排空的超时时间
        /// </summary>
        private static readonly TimeSpan CallbackDrainTimeout = TimeSpan.FromSeconds(5);

        private readonly object stateLock = new();
        private string lastError;

        /// <summary>
        /// 是否已打开
        /// </summary>
        public bool IsConnected => Volatile.Read(ref this.isOpen) == 1 && (this.camera?.IMV_IsOpen() ?? false);

        /// <summary>
        /// 是否正在取流
        /// </summary>
        public bool IsGrabbing => Volatile.Read(ref this.isGrabbing) == 1 && (this.camera?.IMV_IsGrabbing() ?? false);

        /// <summary>
        /// 最近一次无法通过返回值表达的生命周期错误的诊断信息
        /// </summary>
        public string LastError
        {
            get
            {
                lock (this.stateLock)
                    return this.lastError;
            }
        }

        private void SetLastError(string message)
        {
            lock (this.stateLock)
                this.lastError = message;
        }

        /// <summary>
        /// 构造Irayple工业相机
        /// </summary>
        /// <param name="deviceInfo">Irayple原生设备信息</param>
        /// <param name="stream">相机帧数据流</param>
        /// <exception cref="ArgumentNullException">
        /// <c>stream</c> 为 <c>null</c>
        /// </exception>
        public IRaypleCamera(IMV_DeviceInfo deviceInfo, ICameraStream stream)
        {
            this.cameraInfo = deviceInfo;
            this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
            this.frameHandler = this.ProcessFrameCallBack;
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
                if (Volatile.Read(ref this.disposed) == 1)
                    return CameraResult.Fail(-1, "The camera has been disposed");

                if (this.IsConnected)
                    return CameraResult.Fail(-1, "The camera has been opened");

                try
                {
                    if (this.camera == null)
                    {
                        this.camera = new MyCamera();

                        var key = string.IsNullOrEmpty(this.cameraInfo.cameraKey)
                            ? this.cameraInfo.serialNumber
                            : this.cameraInfo.cameraKey;

                        var createResult = this.camera.IMV_CreateHandle(IMV_ECreateHandleMode.modeByCameraKey, 0, key);

                        if (createResult != IMV_OK)
                        {
                            // 句柄创建失败时必须立即销毁，避免残留半初始化的原生句柄
                            this.ReleaseHandle();
                            return CameraResult.Fail(createResult, "Create camera handle failed");
                        }
                    }

                    var openResult = this.camera.IMV_Open();

                    if (openResult != IMV_OK)
                        return CameraResult.Fail(openResult, "Open camera failed");

                    // 设备打开成功即置位，避免后续可选配置失败时状态位与设备真实状态不一致
                    Interlocked.Exchange(ref this.isOpen, 1);

                    // 延迟应用缓冲区配置：必须在设备打开之后、开始取流之前。
                    // 该配置为可选优化项，失败不影响相机可用性
                    if (this.bufferCount > 0)
                        this.TryApplyBufferCount();

                    this.SetLastError(null);
                    return CameraResult.Success(openResult);
                }
                catch (Exception e)
                {
                    this.SetLastError(e.Message);
                    return CameraResult.Fail(-1, e.Message);
                }
            }
            finally
            {
                this.operationGate.Release();
            }
        }

        /// <summary>
        /// 关闭相机（幂等，可重复调用）
        /// </summary>
        /// <returns>
        /// 相机操作结果
        /// </returns>
        public CameraResult Close()
        {
            this.operationGate.Wait();
            try
            {
                if (this.camera == null)
                    return CameraResult.Fail(-1, "Camera not initialized");

                Interlocked.Exchange(ref this.isGrabbing, 0);
                var stopResult = StopGrabbingCore();
                if (!stopResult.IsSuccess)
                    return stopResult;

                // 关闭句柄前必须排空在途回调，否则回调中的原生帧会失效
                this.WaitForCallbacks();

                try
                {
                    if (this.camera.IMV_IsOpen())
                    {
                        var result = this.camera.IMV_Close();
                        Interlocked.Exchange(ref this.isOpen, 0);

                        if (result != IMV_OK)
                            return CameraResult.Fail(result, "Close camera failed");

                        return CameraResult.Success(result, "Camera closed");
                    }

                    Interlocked.Exchange(ref this.isOpen, 0);
                    return CameraResult.Success(IMV_OK, "Camera closed");
                }
                catch (Exception e)
                {
                    return CameraResult.Fail(-1, e.Message);
                }
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
                if (!this.IsConnected)
                    return CameraResult.Fail(-1, "Camera is not open");

                // 由取流状态位保证并发调用只有一个线程能真正启动取流
                if (Interlocked.CompareExchange(ref this.isGrabbing, 1, 0) == 1)
                    return CameraResult.Fail(-1, "Camera is already grabbing");

                try
                {
                    // 同一原生句柄只绑定一次回调：重复绑定会让每帧被多次发布
                    if (Interlocked.Exchange(ref this.callbackAttached, 1) == 0)
                    {
                        var attachResult = this.camera.IMV_AttachGrabbing(this.frameHandler, IntPtr.Zero);
                        if (attachResult != IMV_OK)
                        {
                            Interlocked.Exchange(ref this.callbackAttached, 0);
                            Interlocked.Exchange(ref this.isGrabbing, 0);
                            return CameraResult.Fail(attachResult, "Attach grabbing failed");
                        }
                    }

                    var startResult = this.camera.IMV_StartGrabbing();

                    if (startResult != IMV_OK)
                    {
                        Interlocked.Exchange(ref this.isGrabbing, 0);
                        return CameraResult.Fail(startResult, "Start grabbing failed");
                    }

                    this.SetLastError(null);
                    return CameraResult.Success(startResult, "Start grabbing is successful");
                }
                catch (Exception e)
                {
                    Interlocked.Exchange(ref this.isGrabbing, 0);
                    return CameraResult.Fail(-1, e.Message);
                }
            }
            finally
            {
                this.operationGate.Release();
            }
        }

        /// <summary>
        /// 停止取流（幂等，可重复调用）
        /// </summary>
        /// <returns>相机操作结果；未在取流时返回成功</returns>
        public CameraResult StopGrab()
        {
            this.operationGate.Wait();
            try
            {
                // 状态位由1翻转为0的线程负责真正停止取流
                if (Interlocked.CompareExchange(ref this.isGrabbing, 0, 1) != 1)
                    return CameraResult.Success(IMV_OK);

                return StopGrabbingCore();
            }
            finally
            {
                this.operationGate.Release();
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
        CameraResult IBufferConfigurable.SetBufferCount(int count)
        {
            if (count < 1)
                return CameraResult.Fail(-1, "The buffer count must be greater than zero");

            this.bufferCount = count;

            if (!this.IsConnected)
                return CameraResult.Success(0);

            this.operationGate.Wait();
            try
            {
                var result = this.camera.IMV_SetBufferCount((uint)count);
                return CameraResult.Result(result == IMV_OK, result);
            }
            catch (Exception e)
            {
                return CameraResult.Fail(-1, e.Message);
            }
            finally
            {
                this.operationGate.Release();
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
                this.camera?.IMV_SetBufferCount((uint)this.bufferCount);
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
        public string GetSerialNumber() => this.cameraInfo.serialNumber ?? string.Empty;

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
            var state = this.CheckWriteable(paramName);
            if (!state.IsSuccess)
                return state;

            return this.ToWriteResult(this.camera.IMV_SetIntFeatureValue(paramName, value), paramName);
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
            var state = this.CheckWriteable(paramName);
            if (!state.IsSuccess)
                return state;

            return this.ToWriteResult(this.camera.IMV_SetDoubleFeatureValue(paramName, value), paramName);
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
            var state = this.CheckWriteable(paramName);
            if (!state.IsSuccess)
                return state;

            return this.ToWriteResult(this.camera.IMV_SetBoolFeatureValue(paramName, value), paramName);
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
            var state = this.CheckWriteable(paramName);
            if (!state.IsSuccess)
                return state;

            return this.ToWriteResult(this.camera.IMV_SetStringFeatureValue(paramName, value), paramName);
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
            if (string.IsNullOrEmpty(paramName) || string.IsNullOrEmpty(value))
                return CameraResult.Fail(-1, "The paramName or value is null or empty");

            var state = this.CheckWriteable(paramName);
            if (!state.IsSuccess)
                return state;

            try
            {
                return this.ToWriteResult(this.camera.IMV_SetEnumFeatureSymbol(paramName, value), paramName);
            }
            catch (Exception e)
            {
                return CameraResult.Fail(-1, e.Message);
            }
        }

        /// <summary>
        /// long → int 的受检转换：越界按"取值失败"处理，禁止静默回绕
        /// </summary>
        private static bool TryConvertToInt64ToInt32(long value, out int result)
            => ParameterReader.TryConvertToInt64ToInt32(value, out result);

        public bool TryGetParam<T>(string paramName, out T value)
            => ParameterReader.TryRead<T>(this, paramName, out value);

        #region 参数读取原语（IParameterSource 显式实现）

        bool IParameterSource.TryReadInt(string paramName, out int value)
        {
            value = 0;

            if (!this.IsConnected)
                return false;

            long longValue = 0;
            if (this.camera.IMV_GetIntFeatureValue(paramName, ref longValue) != IMV_OK)
                return false;

            return TryConvertToInt64ToInt32(longValue, out value);
        }

        bool IParameterSource.TryReadLong(string paramName, out long value)
        {
            value = 0;

            if (!this.IsConnected)
                return false;

            long longValue = 0;
            if (this.camera.IMV_GetIntFeatureValue(paramName, ref longValue) != IMV_OK)
                return false;

            value = longValue;
            return true;
        }

        bool IParameterSource.TryReadFloat(string paramName, out float value)
        {
            value = 0;

            double doubleValue = 0;
            if (!this.TryReadDoubleCore(paramName, out doubleValue))
                return false;

            value = (float)doubleValue;
            return true;
        }

        bool IParameterSource.TryReadDouble(string paramName, out double value)
        {
            value = 0;
            return this.TryReadDoubleCore(paramName, out value);
        }

        bool IParameterSource.TryReadBool(string paramName, out bool value)
        {
            value = false;

            if (!this.IsConnected)
                return false;

            var boolValue = false;
            if (this.camera.IMV_GetBoolFeatureValue(paramName, ref boolValue) != IMV_OK)
                return false;

            value = boolValue;
            return true;
        }

        bool IParameterSource.TryReadString(string paramName, out string value)
        {
            value = null;

            if (!this.IsConnected)
                return false;

            var stringValue = new IMV_String { str = string.Empty };
            if (this.camera.IMV_GetStringFeatureValue(paramName, ref stringValue) != IMV_OK)
                return false;

            value = stringValue.str ?? string.Empty;
            return true;
        }

        private bool TryReadDoubleCore(string paramName, out double value)
        {
            value = 0;

            if (!this.IsConnected)
                return false;

            var doubleValue = 0d;
            if (this.camera.IMV_GetDoubleFeatureValue(paramName, ref doubleValue) != IMV_OK)
                return false;

            value = doubleValue;
            return true;
        }

        #endregion

        /// <summary>
        /// 获取参数
        /// </summary>
        /// <typeparam name="T">参数类型，支持 int、long、float、double、bool、string</typeparam>
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
                var enumValue = new IMV_String { str = string.Empty };

                if (this.camera.IMV_GetEnumFeatureSymbol(paramName, ref enumValue) == IMV_OK)
                {
                    value = enumValue.str ?? string.Empty;
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

            try
            {
                var result = this.camera.IMV_ExecuteCommandFeature(command);

                if (result == IMV_OK)
                    return CameraResult.Success(result);

                return CameraResult.Fail(result, "Execute command error");
            }
            catch (Exception e)
            {
                return CameraResult.Fail(-1, e.Message);
            }
        }

        /// <summary>
        /// 释放相机（幂等，可重复调用），确保原生句柄被销毁。
        /// 释放前必须排空在途回调：句柄先销毁会让回调中的原生帧失效。
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref this.disposed, 1, 0) == 1)
                return;

            this.operationGate.Wait();
            try
            {
                if (this.camera != null)
                {
                    // 释放阶段不依赖状态位，直接以SDK真实状态为准，避免句柄在取流中被销毁
                    Interlocked.Exchange(ref this.isGrabbing, 0);
                    StopGrabbingCore();

                    this.WaitForCallbacks();

                    try
                    {
                        if (this.camera.IMV_IsOpen())
                            this.camera.IMV_Close();
                    }
                    catch
                    {
                        // 关闭失败不阻断释放流程
                    }

                    this.ReleaseHandle();
                }
            }
            finally
            {
                Interlocked.Exchange(ref this.isOpen, 0);
                Interlocked.Exchange(ref this.isGrabbing, 0);
                this.operationGate.Release();
            }
        }

        /// <summary>
        /// 停止取流，以SDK真实取流状态为准
        /// </summary>
        private CameraResult StopGrabbingCore()
        {
            try
            {
                if (this.camera != null && this.camera.IMV_IsGrabbing())
                {
                    var result = this.camera.IMV_StopGrabbing();
                    if (result != IMV_OK)
                    {
                        // native 失败时保留 isGrabbing=1，避免向上层伪装成已停止
                        Interlocked.Exchange(ref this.isGrabbing, 1);
                        this.SetLastError($"Stop grabbing failed with error code {result}.");
                        return CameraResult.Fail(result, "Stop grabbing failed");
                    }
                }

                this.SetLastError(null);
                return CameraResult.Success(IMV_OK);
            }
            catch (Exception e)
            {
                this.SetLastError(e.Message);
                return CameraResult.Fail(-1, e.Message);
            }
        }

        /// <summary>
        /// 等待在途帧回调归还帧后再销毁原生句柄（带有限超时，避免释放永久阻塞）
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
        /// 销毁原生相机句柄并清空引用
        /// </summary>
        private void ReleaseHandle()
        {
            try
            {
                this.camera?.IMV_DestroyHandle();
            }
            catch
            {
                // 销毁句柄失败不向上抛出
            }
            finally
            {
                this.camera = null;
                Interlocked.Exchange(ref this.callbackAttached, 0);
            }
        }

        /// <summary>
        /// Irayple相机帧到达回调，运行在SDK采集线程
        /// </summary>
        /// <param name="pFrame">原生图像帧</param>
        /// <param name="pUser">用户数据</param>
        private void ProcessFrameCallBack(ref IMV_Frame pFrame, IntPtr pUser)
        {
            if (pFrame.pData == IntPtr.Zero)
                return;

            bool shouldPublish;
            lock (this.stateLock)
            {
                this.inFlightCallbacks++;
                shouldPublish = Volatile.Read(ref this.disposed) == 0
                    && Volatile.Read(ref this.isOpen) == 1
                    && Volatile.Read(ref this.isGrabbing) == 1;
            }

            try
            {
                if (shouldPublish)
                {
                    // 包装器构造时已复制像素数据，因此可安全地将原生帧归还SDK
                    var frame = new IRaypleFrameWrapper(pFrame);
                    this.stream.Publish(frame);

                    // 所有权已移交数据流，由订阅者负责释放
                }
            }
            catch
            {
                // 回调运行在SDK采集线程，异常不得外泄，否则会中断采集
            }
            finally
            {
                try
                {
                    this.camera?.IMV_ReleaseFrame(ref pFrame);
                }
                catch
                {
                    // 归还帧失败不向上抛出
                }

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

        /// <summary>
        /// 校验相机与参数名是否可写
        /// </summary>
        /// <param name="paramName">参数名</param>
        /// <returns>
        /// 相机操作结果
        /// </returns>
        private CameraResult CheckWriteable(string paramName)
        {
            if (!this.IsConnected || this.camera == null)
                return CameraResult.Fail(-1, "Camera is not open");

            if (string.IsNullOrEmpty(paramName))
                return CameraResult.Fail(-1, "The paramName is null or empty");

            if (!this.camera.IMV_FeatureIsAvailable(paramName))
                return CameraResult.Fail(-1, $"The feature '{paramName}' is not available");

            if (!this.camera.IMV_FeatureIsWriteable(paramName))
                return CameraResult.Fail(-1, $"The feature '{paramName}' is not writeable");

            return CameraResult.Success(IMV_OK);
        }

        /// <summary>
        /// 将Irayple错误码转换为统一结果
        /// </summary>
        /// <param name="errorCode">Irayple错误码</param>
        /// <param name="paramName">参数名</param>
        /// <returns>
        /// 相机操作结果
        /// </returns>
        private CameraResult ToWriteResult(int errorCode, string paramName)
        {
            if (errorCode != IMV_OK)
                return CameraResult.Fail(errorCode, $"Set param '{paramName}' is failed");

            return CameraResult.Success(errorCode, $"Set param '{paramName}' is successful");
        }
    }
}
