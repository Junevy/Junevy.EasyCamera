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
    public class HikCamera : ICamera, IParameterSource, IBufferConfigurable, IConnectionMonitor
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
            Disposed = 4,

            /// <summary>设备已掉线（SDK 异常回调触发）：不发布帧；原生句柄须经 Close 或 Connect 释放后重建</summary>
            Lost = 5
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
        /// 是否已打开（含正在关闭：此时设备尚未释放）。
        /// 纯状态读取：不读 device 字段、不做原生调用，可在任意线程（含与 Dispose 并发时）安全调用；
        /// 原生层面的连接检查只能在 <see cref="operationGate" /> 内通过 <see cref="IsDeviceReady" /> 完成。
        /// </summary>
        public bool IsConnected
            => Volatile.Read(ref this.state) is (int)CameraState.Open or (int)CameraState.Grabbing or (int)CameraState.Closing;

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
        /// 相机掉线通知（<see cref="IConnectionMonitor" />）。事件在线程池线程上触发，处理程序异常被吞掉；同一次连接最多一次。
        /// </summary>
        public event EventHandler<CameraDisconnectedEventArgs> Disconnected;

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
        /// 关闭相机（幂等）。已关闭或从未打开的相机返回成功且不做原生调用；掉线后的相机释放句柄并返回成功；
        /// 其它失败（含原生异常）时恢复打开前的状态与回调订阅，相机仍可继续使用或重试关闭。
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
        /// 停止取流（幂等，可重复调用）。设备掉线后同样返回成功（尽力通知 SDK 停流）。
        /// </summary>
        /// <returns>
        /// 相机操作结果；未在取流时返回成功
        /// </returns>
        public CameraResult StopGrab()
            => this.ExecuteGuarded(() => Volatile.Read(ref this.state) == (int)CameraState.Lost
                ? this.StopGrabOnLostDevice()
                : this.StopGrabCore(this.CurrentGrabber(), this.IsGrabbing));

        /// <summary>
        /// 释放相机（幂等，可重复调用）
        /// </summary>
        public void Dispose()
            => this.ExecuteDispose(this.DisposeCore);

        private CameraResult ConnectCore()
        {
            if (this.IsDisposed)
                return CameraResult.Fail(-1, "The camera has been disposed");

            var currentState = Volatile.Read(ref this.state);
            if (currentState is (int)CameraState.Open or (int)CameraState.Grabbing)
                return CameraResult.Fail(-1, "The camera has been opened");

            // 掉线后的句柄不可复用：先彻底释放，再由 deviceFactory 重建（即"同一 key 再次 OpenCamera"的重连入口）
            if (currentState == (int)CameraState.Lost)
                this.TearDownLostDevice();

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

            // 先清除旧诊断再进入 Open：之后若掉线，处理程序写入的原因不会被本方法覆盖
            this.SetLastError(null);
            this.SetState(CameraState.Open);

            try
            {
                // 先解绑再绑定，避免重复打开时回调被注册多次。
                currentGrabber.FrameGrabedEvent -= this.ProcessFrameCallBack;
                currentGrabber.FrameGrabedEvent += this.ProcessFrameCallBack;
                currentDevice.DeviceExceptionEvent -= this.OnDeviceException;
                currentDevice.DeviceExceptionEvent += this.OnDeviceException;

                // 延迟应用缓冲区配置：必须在设备打开之后、开始取流之前。
                // 该配置为可选优化项，失败不影响相机可用性。
                if (this.bufferCount > 0)
                    this.TryApplyBufferCount();

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

                try
                {
                    currentDevice.DeviceExceptionEvent -= this.OnDeviceException;
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

            var state = (CameraState)Volatile.Read(ref this.state);

            // 已关闭（含从未打开过）：幂等成功，不做任何原生调用，且不依赖 device 是否为 null。
            // 与 ICamera.Close 的幂等语义一致：CameraManager.DisposeCamera 会对 Connect 失败的候选先调用 Close，
            // 若此处报失败会制造假的 ReleaseFailed。
            if (state == CameraState.Closed)
            {
                this.SetLastError(null);
                return CameraResult.Success(MvError.MV_OK);
            }

            // 掉线：句柄已不可用，走释放路径而不是"失败即回滚"（掉线设备没有可回滚的可用状态）
            if (state == CameraState.Lost)
                return this.TearDownLostDevice();

            // 防御：Open/Grabbing 状态下 device 不应为空（ConnectCore 先赋值再置 Open），此处基本不可达
            var currentDevice = this.device;
            if (currentDevice == null)
                return CameraResult.Fail(-1, "Camera not initialized");

            var currentGrabber = this.CurrentGrabber();

            // 进入 Closing：立刻停止发布帧，等待在途回调排空后再释放设备。
            // 原子迁移：若 SDK 线程在读取状态之后已把它改为 Lost，则转入掉线释放路径
            if (!this.TryMoveState(state, CameraState.Closing))
                return this.TearDownLostDevice();

            try
            {
                // 注意：设备异常回调在关闭期间保持绑定（SDK 线程可把 Closing 迁为 Lost），仅在原生关闭成功后才解绑
                if (currentGrabber != null)
                    currentGrabber.FrameGrabedEvent -= this.ProcessFrameCallBack;
            }
            catch (Exception unbindError)
            {
                this.SetLastError(unbindError.Message);
            }

            var stopResult = this.StopGrabCore(currentGrabber, state == CameraState.Grabbing);
            if (!stopResult.IsSuccess)
                return this.RestoreAfterFailedClose(state, currentGrabber, currentDevice, stopResult);

            this.WaitForCallbacks();

            var closeCode = SafeClose(currentDevice, out var closeFailed);
            if (closeFailed)
            {
                this.SetLastError($"Close camera failed with error code {closeCode}.");
                return this.RestoreAfterFailedClose(state, currentGrabber, currentDevice, CameraResult.Fail(closeCode, "Close camera failed"));
            }

            // 正常路径：原生关闭成功后解绑设备异常回调。关闭期间若出现过 Lost，由下面的 Closed 覆盖（设备已释放，不再通知）
            try
            {
                currentDevice.DeviceExceptionEvent -= this.OnDeviceException;
            }
            catch (Exception unbindError)
            {
                this.SetLastError(unbindError.Message);
            }

            this.SetState(CameraState.Closed);
            this.SetLastError(null);
            return CameraResult.Success(closeCode);
        }

        /// <summary>
        /// 关闭失败后的统一回滚（只能在 <see cref="operationGate" /> 内调用）：恢复状态与事件订阅，使相机仍可被调用方重试关闭或继续使用。
        /// 任何一条关闭失败路径都必须经过这里，杜绝"状态位卡死 → 静默丢帧"。
        /// 判定在 <see cref="stateLock" /> 内完成：若关闭期间设备已掉线（SDK 线程已把 Closing 迁为 Lost），
        /// 不得回滚到 Open/Grabbing（否则会以"已连接"的假象永久卡住），改走释放路径并返回其结果。
        /// </summary>
        private CameraResult RestoreAfterFailedClose(CameraState previousState, IStreamGrabber currentGrabber, IDevice currentDevice, CameraResult failure)
        {
            bool lost;
            lock (this.stateLock)
            {
                lost = Volatile.Read(ref this.state) == (int)CameraState.Lost;
                if (!lost)
                    Volatile.Write(ref this.state, (int)previousState);
            }

            // 释放路径会等待在途回调，必须在 stateLock 之外执行（状态已为 Lost，不会再被回滚覆盖）
            if (lost)
                return this.TearDownLostDevice();

            this.RestoreEventSubscriptions(currentGrabber, currentDevice);
            return failure;
        }

        /// <summary>
        /// 释放已掉线设备的原生资源（只能在 <see cref="operationGate" /> 内调用）。
        /// 掉线句柄不可复用：解绑回调 → 尽力停流 → 等待在途回调 → 尽力关闭并销毁句柄 → 清空引用 → 回到 Closed，
        /// 下次 Connect 由 <c>deviceFactory</c> 重建。原生错误只记入 <see cref="LastError" />，不作为失败返回，保证相机随后可被再次打开。
        /// </summary>
        private CameraResult TearDownLostDevice()
        {
            var currentDevice = this.device;
            var currentGrabber = this.CurrentGrabber();
            string teardownError = null;

            try
            {
                if (currentGrabber != null)
                    currentGrabber.FrameGrabedEvent -= this.ProcessFrameCallBack;
                if (currentDevice != null)
                    currentDevice.DeviceExceptionEvent -= this.OnDeviceException;
            }
            catch (Exception unbindError)
            {
                teardownError = unbindError.Message;
            }

            try
            {
                // 设备已掉线，停流返回值没有意义（句柄可能已不可达），仅尽力调用
                currentGrabber?.StopGrabbing();
            }
            catch (Exception stopError)
            {
                teardownError = stopError.Message;
            }

            this.WaitForCallbacks();

            var closeCode = SafeClose(currentDevice, out var closeFailed);
            if (closeFailed)
                teardownError = $"Close lost camera failed with error code {closeCode}.";

            try
            {
                if (currentDevice is IDisposable disposable)
                    disposable.Dispose();
            }
            catch (Exception disposeError)
            {
                teardownError = disposeError.Message;
            }

            // 句柄已销毁：先清空引用，之后任何路径都不得再触达旧句柄
            lock (this.stateLock)
            {
                this.device = null;
                this.streamGrabber = null;
            }

            this.SetState(CameraState.Closed);
            this.SetLastError(teardownError);
            return CameraResult.Success(MvError.MV_OK);
        }

        private CameraResult StartGrabCore()
        {
            if (this.IsDisposed)
                return CameraResult.Fail(-1, "The camera has been disposed");

            if (!this.IsDeviceReady())
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

            // 原子迁移：StartGrabbing 期间若 SDK 线程已把状态改为 Lost，不得覆盖为 Grabbing
            if (!this.TryMoveState(CameraState.Open, CameraState.Grabbing, clearError: true))
                return CameraResult.Fail(-1, "Camera has been disconnected");

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

                // 只有真正的停流才回落到 Open：关闭路径上状态已是 Closing，掉线后已是 Lost，二者都不得被改回 Open。
                // 迁移与清除诊断在同一临界区内完成，不会覆盖掉线原因
                this.TryMoveState(CameraState.Grabbing, CameraState.Open, clearError: true);
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
                if (currentDevice != null)
                    currentDevice.DeviceExceptionEvent -= this.OnDeviceException;
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

            return this.ExecuteGuarded(() =>
            {
                // 写入与状态判定都在门内完成，与 Connect/Close 等状态迁移串行
                this.bufferCount = count;

                if (Volatile.Read(ref this.state) is not ((int)CameraState.Open or (int)CameraState.Grabbing))
                {
                    // 未打开：只记录配置，Connect 成功后应用
                    return CameraResult.Success(0);
                }

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
                if (!this.IsDeviceReady())
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
                () => this.IsDeviceReady() && ParameterReader.TryRead<T>(this, paramName, out var read)
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
                    var parameters = this.IsDeviceReady() ? this.CurrentParameters() : null;
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
                if (!this.IsDeviceReady())
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
        /// 关闭失败后恢复事件订阅（帧回调与设备异常回调），使相机仍可被调用方重试关闭或停止取流。
        /// 使用"先 -= 再 +="，避免重复订阅。
        /// </summary>
        private void RestoreEventSubscriptions(IStreamGrabber currentGrabber, IDevice currentDevice)
        {
            if (currentGrabber != null)
            {
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

            if (currentDevice != null)
            {
                try
                {
                    currentDevice.DeviceExceptionEvent -= this.OnDeviceException;
                    currentDevice.DeviceExceptionEvent += this.OnDeviceException;
                }
                catch (Exception e)
                {
                    this.SetLastError(e.Message);
                }
            }
        }

        /// <summary>
        /// 掉线后的停流（只能在 <see cref="operationGate" /> 内调用）：尽力通知 SDK 停止取流，
        /// 失败只记入 <see cref="LastError" />，仍返回成功以保持"停流幂等"的契约。
        /// 掉线原因保留在 LastError 中，直到 Close 或 Connect 完成释放。
        /// </summary>
        private CameraResult StopGrabOnLostDevice()
        {
            var currentGrabber = this.CurrentGrabber();
            if (currentGrabber == null)
                return CameraResult.Success(MvError.MV_OK);

            try
            {
                var code = currentGrabber.StopGrabbing();
                if (code != MvError.MV_OK)
                    this.SetLastError($"Device disconnected; stop grabbing failed with error code {code}.");
            }
            catch (Exception e)
            {
                this.SetLastError($"Device disconnected; stop grabbing failed: {e.Message}");
            }

            return CameraResult.Success(MvError.MV_OK);
        }

        /// <summary>
        /// 海康设备异常回调（DisConnect），运行在 SDK 线程。
        /// 并发约束：绝不能进入 <see cref="operationGate" />（门内的 device.Close() 可能正在等待本回调返回，否则死锁）。
        /// 只在 <see cref="stateLock" /> 内做 Open/Grabbing/Closing → Lost 的条件迁移并记录原因；
        /// 仅 Open/Grabbing 时事件才在线程池上异步派发（Closing 期间的掉线只改状态：关闭本身即释放设备，由关闭路径收尾）。
        /// 整个方法不抛出任何异常，避免中断 SDK 的事件派发。
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="e">设备异常参数</param>
        private void OnDeviceException(object sender, DeviceExceptionArgs e)
        {
            try
            {
                var msgType = e?.MsgType ?? DeviceExceptionType.DisConnect;
                if (msgType != DeviceExceptionType.DisConnect)
                    return;

                var reason = $"Device disconnected (MsgType={msgType})";
                var serialNumber = this.GetSerialNumber();
                var occurredAtUtc = DateTime.UtcNow;

                var notify = false;
                lock (this.stateLock)
                {
                    var currentState = Volatile.Read(ref this.state);
                    if (currentState is (int)CameraState.Open or (int)CameraState.Grabbing)
                    {
                        // 正常连接中的掉线：迁为 Lost 并通知订阅者（同一次连接最多一次，之后状态已非 Open/Grabbing）
                        Volatile.Write(ref this.state, (int)CameraState.Lost);
                        this.lastError = reason;
                        notify = true;
                    }
                    else if (currentState == (int)CameraState.Closing)
                    {
                        // 关闭期间的掉线：只改状态并记录原因，不派发事件；关闭失败的回滚会据此改走释放路径
                        Volatile.Write(ref this.state, (int)CameraState.Lost);
                        this.lastError = reason;
                    }

                    // Closed/Disposed/已 Lost：忽略
                }

                if (!notify)
                    return;

                // 锁外异步派发：不占用 SDK 线程，也不在回调上下文中运行用户代码
                ThreadPool.QueueUserWorkItem(_ => this.RaiseDisconnected(
                    new CameraDisconnectedEventArgs(serialNumber, reason, occurredAtUtc)));
            }
            catch
            {
                // 异常不得外泄到 SDK 线程
            }
        }

        /// <summary>
        /// 在线程池线程上逐个调用掉线处理程序；单个处理程序的异常被吞掉，不影响其余订阅者，也不拖垮线程池。
        /// </summary>
        private void RaiseDisconnected(CameraDisconnectedEventArgs args)
        {
            var handler = this.Disconnected;
            if (handler == null)
                return;

            foreach (var subscriber in handler.GetInvocationList())
            {
                try
                {
                    ((EventHandler<CameraDisconnectedEventArgs>)subscriber)(this, args);
                }
                catch
                {
                    // 线程池上未观察的异常会终止进程，用户代码异常在此截断
                }
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

        /// <summary>
        /// 设备是否可用于原生操作：状态为 Open/Grabbing，且原生句柄报告已连接。
        /// 只能在 <see cref="operationGate" /> 内调用（会访问原生设备）。
        /// 先判状态，再局部快照一次 device，避免与 DisposeCore 的置空产生 NRE。
        /// </summary>
        private bool IsDeviceReady()
        {
            var state = Volatile.Read(ref this.state);
            if (state is not ((int)CameraState.Open or (int)CameraState.Grabbing))
                return false;

            var currentDevice = this.device;
            return currentDevice != null && currentDevice.IsConnected;
        }

        /// <summary>
        /// 写入生命周期状态。与 <see cref="stateLock" /> 互斥，保证不会覆盖 SDK 线程刚完成的掉线迁移。
        /// </summary>
        private void SetState(CameraState newState)
        {
            lock (this.stateLock)
                Volatile.Write(ref this.state, (int)newState);
        }

        /// <summary>
        /// 条件迁移：仅当当前状态为 <paramref name="from" /> 时迁移到 <paramref name="to" />。
        /// 与掉线迁移在同一临界区内判定；<paramref name="clearError" /> 为 true 时在迁移成功的同一临界区内清空诊断信息。
        /// 返回 false 说明状态已被 SDK 线程改为 Lost，调用方应按掉线处理。
        /// </summary>
        private bool TryMoveState(CameraState from, CameraState to, bool clearError = false)
        {
            lock (this.stateLock)
            {
                if (Volatile.Read(ref this.state) != (int)from)
                    return false;

                Volatile.Write(ref this.state, (int)to);
                if (clearError)
                    this.lastError = null;
                return true;
            }
        }

        private IStreamGrabber CurrentGrabber()
        {
            lock (this.stateLock)
                return this.streamGrabber ?? this.device?.StreamGrabber;
        }

        /// <summary>
        /// 取当前参数节点；相机不可用或已释放时返回 <c>null</c>，由调用方按"相机不可用"表达失败。
        /// </summary>
        private IParameters CurrentParameters() => this.IsDeviceReady() ? this.device?.Parameters : null;

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
            if (!this.IsDeviceReady())
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
