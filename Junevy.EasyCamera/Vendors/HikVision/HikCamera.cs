using Junevy.EasyCamera.Core.Abstractions;
using Junevy.EasyCamera.Core.Common;
using MvCameraControl;
using System;
using System.Threading;

namespace Junevy.EasyCamera.Vendors.HikVision
{
    /// <summary>
    /// 海康工业相机。
    /// </summary>
    /// <remarks>
    /// 并发纪律：所有原生（native）访问都必须经 <see cref="operationGate" />，
    /// 由 <see cref="ExecuteGuarded" /> 统一把门、异常翻译与诊断收敛到一处；
    /// 唯一例外是 SDK 采集回调线程（<see cref="ProcessFrameCallBack" />），它只读状态并归还缓冲区。
    /// <para>
    /// 生命周期用单一 <see cref="CameraState" /> 表达（取代多个布尔标志位），
    /// 从结构上排除"标志位组合非法"（例如 closing 卡死导致静默丢帧）。
    /// </para>
    /// </remarks>
    public class HikCamera : ICamera, IParameterSource, IBufferConfigurable
    {
        /// <summary>
        /// 相机生命周期状态。
        /// </summary>
        private enum CameraState
        {
            /// <summary>已创建未打开</summary>
            Closed = 0,

            /// <summary>已打开未取流（允许发布帧：SDK 只在取流时投递，但状态语义保持一致）</summary>
            Open = 1,

            /// <summary>正在取流，帧回调发布</summary>
            Grabbing = 2,

            /// <summary>正在关闭：已停止发布帧，等待在途回调排空</summary>
            Closing = 3,

            /// <summary>已释放（终态）</summary>
            Disposed = 4
        }

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
        /// 海康相机设备实例。所有读写都在 operationGate 内完成
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
        /// 生命周期状态（Volatile 读写，回调线程与操作线程共享）
        /// </summary>
        private int state = (int)CameraState.Closed;

        /// <summary>
        /// 已进入回调但尚未完成归还缓冲区的数量。
        /// </summary>
        private int inFlightCallbacks;

        /// <summary>
        /// 关闭/释放时等待在途回调排空的超时时间。
        /// </summary>
        private static readonly TimeSpan CallbackDrainTimeout = TimeSpan.FromSeconds(5);

        private string lastError;

        /// <summary>
        /// 是否已打开（含正在关闭：此时设备尚未释放）
        /// </summary>
        public bool IsConnected
            => Volatile.Read(ref this.state) is (int)CameraState.Open or (int)CameraState.Grabbing or (int)CameraState.Closing
            && this.device != null
            && this.device.IsConnected;

        /// <summary>
        /// 最近一次无法通过返回值表达的生命周期错误（例如停止取流失败）。
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
        public bool IsGrabbing => Volatile.Read(ref this.state) == (int)CameraState.Grabbing;

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
            => this.ExecuteGuarded(this.ConnectCore);

        /// <summary>
        /// 关闭相机。失败（含原生异常）时恢复打开前的状态与回调订阅，相机仍可继续使用或重试关闭。
        /// </summary>
        /// <returns>
        /// 相机操作结果
        /// </returns>
        public CameraResult Close()
            => this.ExecuteGuarded(this.CloseCore);

        /// <summary>
        /// 开始取流
        /// </summary>
        /// <returns>
        /// 相机操作结果
        /// </returns>
        public CameraResult StartGrab()
            => this.ExecuteGuarded(this.StartGrabCore);

        /// <summary>
        /// 停止取流（幂等，可重复调用）
        /// </summary>
        /// <returns>
        /// 相机操作结果；未在取流时返回成功
        /// </returns>
        public CameraResult StopGrab()
            => this.ExecuteGuarded(() => this.StopGrabCore(this.CurrentGrabber(), this.IsGrabbing));

        /// <summary>
        /// 释放相机（幂等，可重复调用）
        /// </summary>
        public void Dispose()
            => this.ExecuteDispose(this.DisposeCore);

        private CameraResult ConnectCore()
        {
            if (this.IsDisposed)
                return CameraResult.Fail(-1, "The camera has been disposed");

            if (this.IsConnected)
                return CameraResult.Fail(-1, "The camera has been opened");

            if (this.device == null)
                this.device = this.deviceFactory(this.deviceInfo);

            var currentDevice = this.device;

            var result = currentDevice.Open();
            if (result != MvError.MV_OK)
            {
                this.SetLastError($"Open camera failed with error code {result}.");
                return CameraResult.Fail(result, "Open camera failed");
            }

            var currentGrabber = currentDevice.StreamGrabber;
            if (currentGrabber == null)
            {
                SafeClose(currentDevice);
                this.SetLastError("Open camera failed because the stream grabber is null.");
                return CameraResult.Fail(-1, "Open camera failed");
            }

            lock (this.stateLock)
            {
                this.streamGrabber = currentGrabber;
            }

            this.SetState(CameraState.Open);

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
                this.SetState(CameraState.Closed);
                lock (this.stateLock)
                {
                    this.streamGrabber = null;
                }

                try
                {
                    currentGrabber.FrameGrabedEvent -= this.ProcessFrameCallBack;
                }
                catch
                {
                }

                SafeClose(currentDevice);

                this.SetLastError(e.Message);
                return CameraResult.Fail(-1, e.Message);
            }
        }

        private CameraResult CloseCore()
        {
            if (this.IsDisposed)
                return CameraResult.Fail(-1, "Camera has been disposed");

            var currentDevice = this.device;
            if (currentDevice == null)
                return CameraResult.Fail(-1, "Camera not initialized");

            var currentGrabber = this.CurrentGrabber();
            var previousState = (CameraState)Volatile.Read(ref this.state);

            // 进入 Closing：立刻停止发布帧，等待在途回调排空后再释放设备
            this.SetState(CameraState.Closing);

            try
            {
                if (currentGrabber != null)
                    currentGrabber.FrameGrabedEvent -= this.ProcessFrameCallBack;
            }
            catch (Exception unbindError)
            {
                this.SetLastError(unbindError.Message);
            }

            var stopResult = this.StopGrabCore(currentGrabber, previousState == CameraState.Grabbing);
            if (!stopResult.IsSuccess)
                return this.RestoreAfterFailedClose(previousState, currentGrabber, stopResult);

            this.WaitForCallbacks();

            var closeCode = SafeClose(currentDevice, out var closeFailed);
            if (closeFailed)
            {
                this.SetLastError($"Close camera failed with error code {closeCode}.");
                return this.RestoreAfterFailedClose(previousState, currentGrabber, CameraResult.Fail(closeCode, "Close camera failed"));
            }

            this.SetState(CameraState.Closed);
            this.SetLastError(null);
            return CameraResult.Success(closeCode);
        }

        /// <summary>
        /// 关闭失败后的统一回滚：恢复状态与回调订阅，使相机仍可被调用方重试关闭或继续使用。
        /// 任何一条关闭失败路径都必须经过这里，杜绝"状态位卡死 → 静默丢帧"。
        /// </summary>
        private CameraResult RestoreAfterFailedClose(CameraState previousState, IStreamGrabber currentGrabber, CameraResult failure)
        {
            this.SetState(previousState);
            this.RestoreCallbackSubscription(currentGrabber);
            return failure;
        }

        private CameraResult StartGrabCore()
        {
            if (this.IsDisposed)
                return CameraResult.Fail(-1, "The camera has been disposed");

            if (!this.IsConnected)
                return CameraResult.Fail(-1, "Camera is not open");

            if (this.IsGrabbing)
                return CameraResult.Fail(-1, "Camera is already grabbing");

            var currentGrabber = this.CurrentGrabber();
            if (currentGrabber == null)
                return CameraResult.Fail(-1, "Camera stream grabber is not initialized");

            var result = currentGrabber.StartGrabbing();
            if (result != MvError.MV_OK)
            {
                this.SetLastError($"Start grabbing failed with error code {result}.");
                return CameraResult.Fail(result, "Start grabbing failed");
            }

            this.SetState(CameraState.Grabbing);
            this.SetLastError(null);
            return CameraResult.Success(result);
        }

        /// <summary>
        /// 调用 native StopGrabbing，并且只在 native 成功后清除取流状态。
        /// </summary>
        /// <param name="currentGrabber">取流器</param>
        /// <param name="wasGrabbing">进入本方法前是否处于取流状态（释放路径上状态已置为终态，须显式传入）</param>
        private CameraResult StopGrabCore(IStreamGrabber currentGrabber, bool wasGrabbing)
        {
            if (!wasGrabbing)
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
                    // native 失败时保留取流状态，避免向上层伪装成已停止
                    this.SetLastError($"Stop grabbing failed with error code {result}.");
                    return CameraResult.Fail(result, "Stop grabbing failed");
                }

                // 关闭路径上状态已是 Closing：只有真正的停流才回落到 Open，
                // 否则会在 Close 期间把状态改回 Open，让帧回调又开始发布
                if (Volatile.Read(ref this.state) == (int)CameraState.Grabbing)
                    this.SetState(CameraState.Open);

                this.SetLastError(null);                return CameraResult.Success(result);
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

        private void DisposeCore()
        {
            if (this.IsDisposed)
                return;

            var currentDevice = this.device;
            var currentGrabber = this.CurrentGrabber();
            var wasGrabbing = this.IsGrabbing;

            // 终态先行：回调线程立即停止发布帧
            this.SetState(CameraState.Disposed);

            try
            {
                if (currentGrabber != null)
                    currentGrabber.FrameGrabedEvent -= this.ProcessFrameCallBack;
            }
            catch (Exception unbindError)
            {
                this.SetLastError(unbindError.Message);
            }

            this.StopGrabCore(currentGrabber, wasGrabbing);
            this.WaitForCallbacks();

            SafeClose(currentDevice);

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

            if (!this.IsConnected)
            {
                // 未打开：只记录配置，Connect 成功后应用
                this.bufferCount = count;
                return CameraResult.Success(0);
            }

            this.bufferCount = count;

            return this.ExecuteGuarded(() =>
            {
                var grabber = this.CurrentGrabber();
                if (grabber == null)
                    return CameraResult.Success(0, "The buffer count will be applied after the camera is reopened.");

                try
                {
                    var result = grabber.SetImageNodeNum((uint)count);
                    return CameraResult.Result(result == MvError.MV_OK, result, result == MvError.MV_OK ? string.Empty : "Set the image node number failed.");
                }
                catch (MvException me)
                {
                    return CameraResult.Fail(me.ErrorCode, me.Message);
                }
            });
        }

        /// <summary>
        /// 尝试应用相机内部图像缓冲区配置。
        /// 该配置属于可选优化项，失败时不影响相机可用性，故不向上抛出异常
        /// </summary>
        private void TryApplyBufferCount()
        {
            try
            {
                this.CurrentGrabber()?.SetImageNodeNum((uint)this.bufferCount);
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
            => this.ExecuteGuarded(() =>
            {
                var state = this.CheckReady(paramName);
                if (!state.IsSuccess)
                    return state;

                var parameters = this.CurrentParameters();
                return parameters == null
                    ? ParametersUnavailable()
                    : ToResult(parameters.SetIntValue(paramName, value), "Check the paramName!");
            });

        /// <summary>
        /// 设置浮点型参数
        /// </summary>
        /// <param name="paramName">参数名</param>
        /// <param name="value">参数值</param>
        /// <returns>
        /// 相机操作结果
        /// </returns>
        public CameraResult SetParam(string paramName, float value)
            => this.ExecuteGuarded(() =>
            {
                var state = this.CheckReady(paramName);
                if (!state.IsSuccess)
                    return state;

                var parameters = this.CurrentParameters();
                return parameters == null
                    ? ParametersUnavailable()
                    : ToResult(parameters.SetFloatValue(paramName, value), "Check the paramName!");
            });

        /// <summary>
        /// 设置布尔型参数
        /// </summary>
        /// <param name="paramName">参数名</param>
        /// <param name="value">参数值</param>
        /// <returns>
        /// 相机操作结果
        /// </returns>
        public CameraResult SetParam(string paramName, bool value)
            => this.ExecuteGuarded(() =>
            {
                var state = this.CheckReady(paramName);
                if (!state.IsSuccess)
                    return state;

                var parameters = this.CurrentParameters();
                return parameters == null
                    ? ParametersUnavailable()
                    : ToResult(parameters.SetBoolValue(paramName, value), "Check the paramName!");
            });

        /// <summary>
        /// 设置字符串参数
        /// </summary>
        /// <param name="paramName">参数名</param>
        /// <param name="value">参数值</param>
        /// <returns>
        /// 相机操作结果
        /// </returns>
        public CameraResult SetParam(string paramName, string value)
            => this.ExecuteGuarded(() =>
            {
                var state = this.CheckReady(paramName);
                if (!state.IsSuccess)
                    return state;

                var parameters = this.CurrentParameters();
                return parameters == null
                    ? ParametersUnavailable()
                    : ToResult(parameters.SetStringValue(paramName, value), "Check the paramName!");
            });

        /// <summary>
        /// 设置枚举参数
        /// </summary>
        /// <param name="paramName">参数名</param>
        /// <param name="value">枚举符号名</param>
        /// <returns>
        /// 相机操作结果
        /// </returns>
        public CameraResult SetEnumParam(string paramName, string value)
            => this.ExecuteGuarded(() =>
            {
                if (!this.IsConnected)
                    return CameraResult.Fail(-1, "Camera is not open");

                if (string.IsNullOrEmpty(paramName) || string.IsNullOrEmpty(value))
                    return CameraResult.Fail(-1, "The paramName or value is null or empty");

                var parameters = this.CurrentParameters();
                return parameters == null
                    ? ParametersUnavailable()
                    : ToResult(parameters.SetEnumValueByString(paramName, value), "Check the paramName or value!");
            });

        /// <summary>
        /// long → int 的受检转换：越界按"取值失败"处理，禁止静默回绕
        /// </summary>
        internal static bool TryConvertToInt64ToInt32(long value, out int result)
            => ParameterReader.TryConvertToInt64ToInt32(value, out result);

        public bool TryGetParam<T>(string paramName, out T value)
        {
            value = default;

            if (string.IsNullOrEmpty(paramName))
                return false;

            return this.TryExecuteGuarded(
                () => this.IsConnected && ParameterReader.TryRead<T>(this, paramName, out var read)
                    ? (Ok: true, Value: read)
                    : (Ok: false, Value: default(T)),
                out value);
        }

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

            if (string.IsNullOrEmpty(paramName))
                return false;

            return this.TryExecuteGuarded(
                () =>
                {
                    var parameters = this.IsConnected ? this.CurrentParameters() : null;
                    if (parameters == null)
                        return (Ok: false, Value: (string)null);

                    if (parameters.GetEnumValue(paramName, out IEnumValue enumValue) != MvError.MV_OK)
                        return (Ok: false, Value: (string)null);

                    return (Ok: true, Value: enumValue.CurEnumEntry?.Symbolic ?? string.Empty);
                },
                out value);
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
            => this.ExecuteGuarded(() =>
            {
                if (!this.IsConnected)
                    return CameraResult.Fail(-1, "Camera is not open");

                if (string.IsNullOrEmpty(command))
                    return CameraResult.Fail(-1, "Check the command");

                var parameters = this.CurrentParameters();
                return parameters == null
                    ? ParametersUnavailable()
                    : ToResult(parameters.SetCommandValue(command), "Execute command failed");
            });

        #region 参数读取原语（IParameterSource 显式实现）

        bool IParameterSource.TryReadInt(string paramName, out int value)
        {
            value = 0;

            var parameters = this.CurrentParameters();
            if (parameters == null)
                return false;

            if (parameters.GetIntValue(paramName, out IIntValue intValue) != MvError.MV_OK)
                return false;

            return ParameterReader.TryConvertToInt64ToInt32(intValue.CurValue, out value);
        }

        bool IParameterSource.TryReadLong(string paramName, out long value)
        {
            value = 0;

            var parameters = this.CurrentParameters();
            if (parameters == null)
                return false;

            if (parameters.GetIntValue(paramName, out IIntValue intValue) != MvError.MV_OK)
                return false;

            value = intValue.CurValue;
            return true;
        }

        bool IParameterSource.TryReadFloat(string paramName, out float value)
        {
            value = 0;

            var parameters = this.CurrentParameters();
            if (parameters == null)
                return false;

            if (parameters.GetFloatValue(paramName, out IFloatValue floatValue) != MvError.MV_OK)
                return false;

            value = floatValue.CurValue;
            return true;
        }

        bool IParameterSource.TryReadDouble(string paramName, out double value)
        {
            value = 0;

            // 海康参数节点无独立 double 类型：由单精度提升而来
            if (!((IParameterSource)this).TryReadFloat(paramName, out var floatValue))
                return false;

            value = floatValue;
            return true;
        }

        bool IParameterSource.TryReadBool(string paramName, out bool value)
        {
            value = false;

            var parameters = this.CurrentParameters();
            if (parameters == null)
                return false;

            if (parameters.GetBoolValue(paramName, out bool boolValue) != MvError.MV_OK)
                return false;

            value = boolValue;
            return true;
        }

        bool IParameterSource.TryReadString(string paramName, out string value)
        {
            value = null;

            var parameters = this.CurrentParameters();
            if (parameters == null)
                return false;

            if (parameters.GetStringValue(paramName, out IStringValue stringValue) != MvError.MV_OK)
                return false;

            value = stringValue.CurValue ?? string.Empty;
            return true;
        }

        #endregion
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

                // 单一状态判定：Open/Grabbing 才发布；Closing/Disposed/Closed 一律不发布
                var currentState = (CameraState)Volatile.Read(ref this.state);
                shouldPublish = currentState == CameraState.Open || currentState == CameraState.Grabbing;

                fallbackGrabber = this.streamGrabber;
            }

            IFrame frame = null;

            try
            {
                // 克隆帧后再归还SDK缓冲区，使下游订阅者持有独立于SDK缓冲区的内存
                if (shouldPublish)
                {
                    var cloned = frameOut.Clone();

                    // Clone 非空但类型不符时必须释放，否则克隆帧泄漏
                    if (cloned is IFrameOut clonedFrame)
                    {
                        frame = new HikFrameWrapper(clonedFrame);
                        this.stream.Publish(frame);

                        // 所有权已移交数据流，由订阅者负责释放
                        frame = null;
                    }
                    else if (cloned is IDisposable disposableClone)
                    {
                        disposableClone.Dispose();
                    }
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

        #region 门与异常收敛

        /// <summary>
        /// 统一守卫：串行化全部原生访问，并把厂商异常翻译成 <see cref="CameraResult" />。
        /// 相机契约要求"失败通过返回值表达"，绝不让异常穿透到调用方。
        /// </summary>
        private CameraResult ExecuteGuarded(Func<CameraResult> action)
        {
            this.operationGate.Wait();
            try
            {
                return action();
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
        /// 取参路径的守卫：与 <see cref="ExecuteGuarded" /> 相同的门与异常收敛，
        /// 但以"是否取到值"表达结果，且不允许在 lambda 中使用外层 out 参数
        /// </summary>
        private bool TryExecuteGuarded<T>(Func<(bool Ok, T Value)> action, out T value)
        {
            value = default;

            this.operationGate.Wait();
            try
            {
                var result = action();
                value = result.Value;
                return result.Ok;
            }
            catch (Exception e)
            {
                this.SetLastError(e.Message);
                return false;
            }
            finally
            {
                this.operationGate.Release();
            }
        }

        /// <summary>
        /// 释放路径的守卫：与 <see cref="ExecuteGuarded" /> 相同的门与异常收敛，但不返回结果且不外泄异常
        /// </summary>
        private void ExecuteDispose(Action action)
        {
            this.operationGate.Wait();
            try
            {
                action();
            }
            catch (Exception e)
            {
                this.SetLastError(e.Message);
            }
            finally
            {
                this.operationGate.Release();
            }
        }

        private bool IsDisposed => Volatile.Read(ref this.state) == (int)CameraState.Disposed;

        private void SetState(CameraState newState) => Volatile.Write(ref this.state, (int)newState);

        private IStreamGrabber CurrentGrabber()
        {
            lock (this.stateLock)
                return this.streamGrabber ?? this.device?.StreamGrabber;
        }

        /// <summary>
        /// 取当前参数节点；相机不可用或已释放时返回 <c>null</c>，由调用方按"相机不可用"表达失败。
        /// </summary>
        private IParameters CurrentParameters() => this.IsConnected ? this.device?.Parameters : null;

        private static int SafeClose(IDevice device, out bool failed)
        {
            failed = false;

            if (device == null)
                return MvError.MV_OK;

            try
            {
                var code = device.Close();
                failed = code != MvError.MV_OK;
                return code;
            }
            catch (Exception)
            {
                failed = true;
                return -1;
            }
        }

        private static void SafeClose(IDevice device)
            => SafeClose(device, out _);

        /// <summary>
        /// 相机已连接但厂商未暴露参数节点时的失败结果（统一文案，避免报成功假象）
        /// </summary>
        private static CameraResult ParametersUnavailable()
            => CameraResult.Fail(-1, "The camera parameters are not available.");

        private void SetLastError(string message)
        {
            lock (this.stateLock)
                this.lastError = message;
        }

        #endregion

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
