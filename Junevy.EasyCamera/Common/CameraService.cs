using Junevy.EasyCamera.Core.Abstractions;
using Junevy.EasyCamera.Core.Common;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Junevy.EasyCamera.Common
{
    /// <summary>
    /// 相机服务类，提供相机枚举、打开关闭、取流订阅与参数访问的门面入口
    /// </summary>
    public class CameraService(ICameraProvider provider, ICameraManager cameraManager, IStreamManager streamManager, IStreamOptions streamOptions = null) : ICameraService
    {
        private const string ErrorMsg = "Camera not open or found";
        private readonly ConcurrentDictionary<string, object> cameraKeyLocks = new();

        private readonly ICameraProvider provider = provider;
        private readonly ICameraManager cameraManager = cameraManager;
        private readonly IStreamManager streamManager = streamManager;
        private readonly IStreamOptions streamOptions = streamOptions ?? new StreamOptions();

        /// <summary>
        /// 相机 key 级操作锁：OpenCamera/Close/StartGrab/StopGrab/SetTrigger 按 key 串行。
        /// 锁对象按 key 只增不减：key 数量量级 ≈ 相机数（小且稳定），可接受；
        /// 按引用计数删除会在 Close/Open 竞态窗口重开两个锁对象，得不偿失。
        /// </summary>
        private object GetKeyLock(string cameraKey) => this.cameraKeyLocks.GetOrAdd(cameraKey, _ => new object());

        /// <summary>
        /// 枚举相机
        /// </summary>
        /// <param name="type">相机类型</param>
        /// <returns>
        /// 相机信息列表
        /// </returns>
        public IEnumerable<ICameraInfo> EnumerateCameras(CameraInterfaceType type = CameraInterfaceType.All) => provider.Enumerate(type);

        /// <summary>
        /// 打开相机。
        /// 相机以 cameraKey 注册；其帧数据流同样以 cameraKey 创建，
        /// 因此后续订阅/取消订阅必须使用相同的 cameraKey。
        /// </summary>
        /// <param name="info">相机信息</param>
        /// <param name="cameraKey">相机的操作Key，用于获取相机实例及其数据流</param>
        /// <returns>
        /// 相机操作结果
        /// </returns>
        public CameraResult OpenCamera(ICameraInfo info, string cameraKey)
        {
            if (info == null || string.IsNullOrEmpty(cameraKey))
                return CameraResult.Fail(-1, "The camera info or camera key is null");

            var keyLock = this.GetKeyLock(cameraKey);
            lock (keyLock)
            {
                ICamera camera = null;
                var created = false;
                var registered = false;
                try
                {
                    if (!cameraManager.TryGet(cameraKey, out camera))
                    {
                        // 流与相机使用相同的 Key，保证订阅入口与打开入口一致。
                        var stream = streamManager.GetOrCreateStream(cameraKey);
                        camera = provider.Create(info, stream);
                        created = true;

                        // 其他服务实例可能已经先注册；失败候选必须在离开竞争路径前清理。
                        if (!cameraManager.TryRegister(cameraKey, camera))
                        {
                            DisposeCandidate(camera);
                            camera = null;
                            if (!cameraManager.TryGet(cameraKey, out camera))
                                return CameraResult.Fail(-2, "Another camera won registration but is no longer available");

                            created = false;
                        }
                        else
                        {
                            registered = true;
                        }
                    }

                    if (camera.IsConnected)
                        return CameraResult.Fail(-1, "The camera has been opened");

                    var result = camera.Connect();
                    if (!result.IsSuccess && registered)
                    {
                        // Connect 失败的候选不应留在缓存中阻塞后续重试。
                        this.cameraManager.Remove(cameraKey);
                    }

                    return result;
                }
                catch (Exception e)
                {
                    if (registered)
                        this.cameraManager.Remove(cameraKey);
                    else if (created)
                        DisposeCandidate(camera);

                    return CameraResult.Fail(-2, e.Message);
                }
            }
        }

        /// <summary>
        /// 订阅指定相机的图像帧数据
        /// </summary>
        /// <param name="cameraKey">打开相机时使用的Key</param>
        /// <param name="subscriberKey">订阅者标识</param>
        /// <param name="processFrame">处理图像帧的回调函数</param>
        /// <param name="whenException">异常发生处理回调方法，当不提供异常处理回调时，订阅工作线程将终止</param>
        /// <param name="capacity">订阅Channel的边界（容量），小于等于0时使用 <see cref="IStreamOptions.StreamCapacity" /></param>
        /// <returns>
        /// 是否成功订阅
        /// </returns>
        public bool SubscribeFrameStream(string cameraKey, string subscriberKey, Func<string, IFrame, Task> processFrame, Action<Exception> whenException = null, int capacity = 0)
        {
            if (processFrame == null) return false;
            if (string.IsNullOrEmpty(cameraKey) || string.IsNullOrEmpty(subscriberKey)) return false;

            if (!streamManager.GetStream(cameraKey, out var stream)) return false;

            if (capacity <= 0)
                capacity = this.streamOptions.StreamCapacity;

            try
            {
                stream.Subscribe(subscriberKey, capacity, processFrame, whenException);
                return true;
            }
            catch (ObjectDisposedException)
            {
                // 流随服务释放的竞态窗口：按"订阅失败"表达，不外泄异常
                return false;
            }
        }

        /// <summary>
        /// 取消订阅指定相机的图像帧数据
        /// </summary>
        /// <param name="cameraKey">打开相机时使用的Key</param>
        /// <param name="subscriberKey">订阅者标识符</param>
        /// <returns>
        /// 是否成功取消订阅
        /// </returns>
        public bool UnsubscribeFrameStream(string cameraKey, string subscriberKey)
        {
            if (string.IsNullOrEmpty(cameraKey) || string.IsNullOrEmpty(subscriberKey)) return false;

            if (!streamManager.GetStream(cameraKey, out var stream)) return false;

            return stream.Unsubscribe(subscriberKey);
        }

        /// <summary>
        /// 打开指定相机的图像采集功能
        /// </summary>
        /// <param name="cameraKey">注册的相机名称</param>
        /// <returns>
        /// 开始取流操作结果
        /// </returns>
        public CameraResult StartGrab(string cameraKey)
        {
            if (string.IsNullOrEmpty(cameraKey))
                return CameraResult.Fail(-1, ErrorMsg);

            lock (this.GetKeyLock(cameraKey))
            {
                if (!cameraManager.TryGet(cameraKey, out var camera) || !camera.IsConnected)
                    return CameraResult.Fail(-1, ErrorMsg);

                // 幂等：已在取流时直接成功，与 StopGrab 的幂等风格一致
                if (camera.IsGrabbing)
                    return CameraResult.Success(0);

                try
                {
                    return camera.StartGrab();
                }
                catch (Exception e)
                {
                    SafeStopGrab(camera);
                    return CameraResult.Fail(-2, e.Message);
                }
            }
        }

        /// <summary>
        /// 异常补偿路径上的停流：失败不再向上抛出，避免掩盖原始异常
        /// </summary>
        private static void SafeStopGrab(ICamera camera)
        {
            try
            {
                camera.StopGrab();
            }
            catch
            {
            }
        }

        /// <summary>
        /// 停止指定相机的图像采集功能
        /// </summary>
        /// <param name="cameraKey">注册的相机名称</param>
        /// <returns>
        /// 相机操作结果
        /// </returns>
        public CameraResult StopGrab(string cameraKey)
        {
            if (string.IsNullOrEmpty(cameraKey))
                return CameraResult.Fail(-1, ErrorMsg);

            lock (this.GetKeyLock(cameraKey))
            {
                if (!cameraManager.TryGet(cameraKey, out var camera) || !camera.IsConnected)
                    return CameraResult.Fail(-1, ErrorMsg);

                var result = camera.StopGrab();
                if (!result.IsSuccess)
                    return result;

                return camera.IsGrabbing
                    ? CameraResult.Fail(-1, camera.LastError ?? "Camera stop grabbing failed")
                    : CameraResult.Success(0);
            }
        }

        /// <summary>
        /// 断开连接指定相机并从缓存中移除释放。
        /// 注意：相机对应的帧数据流不会自动移除，以便重新打开相机后继续订阅
        /// </summary>
        /// <param name="cameraKey">相机Key</param>
        /// <returns>
        /// 相机操作结果
        /// </returns>
        public CameraResult Close(string cameraKey)
        {
            if (string.IsNullOrEmpty(cameraKey))
                return CameraResult.Fail(-1, "Camera key is empty");

            // 必须持 per-key 锁：否则 Close 会在 OpenCamera 的 Connect()、
            // StartGrab/StopGrab 的厂商调用中途摘走并释放实例，形成 use-after-free 竞态
            lock (this.GetKeyLock(cameraKey))
            {
                return this.cameraManager.Remove(cameraKey) switch
                {
                    CameraRemoveStatus.Removed => CameraResult.Success(0),
                    CameraRemoveStatus.NotFound => CameraResult.Fail(-1, ErrorMsg),
                    _ => CameraResult.Fail(-1, this.cameraManager.LastError ?? "Dispose camera error")
                };
            }
        }

        private static void DisposeCandidate(ICamera camera)
        {
            if (camera == null)
                return;

            try
            {
                camera.Close();
            }
            catch
            {
            }

            try
            {
                camera.Dispose();
            }
            catch
            {
            }
        }

        /// <summary>
        /// 获取已注册（已打开）相机的序列号
        /// </summary>
        /// <param name="cameraKey">相机Key</param>
        /// <returns>
        /// 相机序列号，未找到时返回空字符串
        /// </returns>
        public string GetSerialNumber(string cameraKey)
        {
            if (cameraManager.TryGet(cameraKey, out var camera))
                return camera.GetSerialNumber();
            return string.Empty;
        }

        /// <summary>
        /// 设置相机的指定参数
        /// </summary>
        /// <param name="cameraKey">相机Key</param>
        /// <param name="paramName">参数键</param>
        /// <param name="value">参数值</param>
        /// <returns>
        /// 相机操作结果
        /// </returns>
        public CameraResult SetParam(string cameraKey, string paramName, string value)
        {
            if (!cameraManager.TryGet(cameraKey ?? "", out var camera) || !camera.IsConnected)
                return CameraResult.Fail(-1, ErrorMsg);

            return camera.SetParam(paramName, value);
        }

        public CameraResult SetParam(string cameraKey, string paramName, int value)
        {
            if (!cameraManager.TryGet(cameraKey ?? "", out var camera) || !camera.IsConnected)
                return CameraResult.Fail(-1, ErrorMsg);

            return camera.SetParam(paramName, value);
        }

        public CameraResult SetParam(string cameraKey, string paramName, bool value)
        {
            if (!cameraManager.TryGet(cameraKey ?? "", out var camera) || !camera.IsConnected)
                return CameraResult.Fail(-1, ErrorMsg);

            return camera.SetParam(paramName, value);
        }

        public CameraResult SetParam(string cameraKey, string paramName, float value)
        {
            if (!cameraManager.TryGet(cameraKey ?? "", out var camera) || !camera.IsConnected)
                return CameraResult.Fail(-1, ErrorMsg);

            return camera.SetParam(paramName, value);
        }

        public CameraResult SetEnumParam(string cameraKey, string paramName, string value)
        {
            if (!cameraManager.TryGet(cameraKey ?? "", out var camera) || !camera.IsConnected)
                return CameraResult.Fail(-1, ErrorMsg);

            return camera.SetEnumParam(paramName, value);
        }

        /// <summary>
        /// 执行相机的指定命令
        /// </summary>
        /// <param name="cameraKey">相机Key</param>
        /// <param name="command">命令</param>
        /// <returns>
        /// 相机操作结果
        /// </returns>
        public CameraResult ExecuteCommand(string cameraKey, string command)
        {
            if (!cameraManager.TryGet(cameraKey ?? "", out var camera) || !camera.IsConnected)
                return CameraResult.Fail(-1, ErrorMsg);

            return camera.ExecuteCommand(command);
        }

        /// <summary>
        /// 设置相机的触发方式。
        /// 注意：设置前会停止取流，设置完成后需重新调用 <see cref="StartGrab(string)" />
        /// </summary>
        /// <param name="cameraKey">相机Key</param>
        /// <param name="triggerSource">触发源（TriggerSource 枚举符号名，如 Line1/Software）</param>
        /// <param name="enableTrigger">是否打开触发（TriggerMode On/Off）</param>
        /// <returns>
        /// 相机操作结果
        /// </returns>
        public CameraResult SetTrigger(string cameraKey, string triggerSource, bool enableTrigger)
        {
            if (string.IsNullOrEmpty(cameraKey))
                return CameraResult.Fail(-1, ErrorMsg);

            lock (this.GetKeyLock(cameraKey))
            {
                if (!cameraManager.TryGet(cameraKey, out var camera) || !camera.IsConnected)
                    return CameraResult.Fail(-1, ErrorMsg);

                if (string.IsNullOrEmpty(triggerSource))
                    return CameraResult.Fail(-1, "Check the trigger source or trigger way");

                var stopResult = camera.StopGrab();
                if (!stopResult.IsSuccess)
                    return stopResult;

                if (camera.IsGrabbing)
                    return CameraResult.Fail(-1, camera.LastError ?? "Camera stop grabbing failed");

                string acq = enableTrigger ? "On" : "Off";
                var acqResult = camera.SetEnumParam("TriggerMode", acq);
                if (!acqResult.IsSuccess) return acqResult;

                return camera.SetEnumParam("TriggerSource", triggerSource);
            }
        }

        public bool TryGetParam<T>(string cameraKey, string paramName, out T value)
        {
            value = default;
            if (!cameraManager.TryGet(cameraKey ?? "", out var camera) || !camera.IsConnected)
                return false;

            return camera.TryGetParam(paramName, out value);
        }

        /// <summary>
        /// 获取相机的指定参数
        /// </summary>
        /// <param name="cameraKey">相机Key</param>
        /// <param name="paramName">参数键</param>
        /// <returns>
        /// 参数值，相机不可用或类型不支持时返回 default(T)
        /// </returns>
        public T GetParam<T>(string cameraKey, string paramName)
            => this.TryGetParam<T>(cameraKey, paramName, out var value) ? value : default;

        public bool TryGetEnumParam(string cameraKey, string paramName, out string value)
        {
            value = null;
            if (!cameraManager.TryGet(cameraKey ?? "", out var camera) || !camera.IsConnected)
                return false;

            return camera.TryGetEnumParam(paramName, out value);
        }

        /// <summary>
        /// 获取相机的枚举参数符号名
        /// </summary>
        /// <param name="cameraKey">相机Key</param>
        /// <param name="paramName">参数键</param>
        /// <returns>
        /// 枚举符号名，相机不可用时返回空字符串
        /// </returns>
        public string GetEnumParam(string cameraKey, string paramName)
            => this.TryGetEnumParam(cameraKey, paramName, out var value) ? value : string.Empty;

        /// <summary>
        /// 侵入式探测使用的临时 cameraKey 前缀；探测完成后连接即关闭，不留在注册表
        /// </summary>
        private const string ProbeKeyPrefix = "probe:";

        /// <inheritdoc />
        public bool IsSerialConnected(string serial)
        {
            if (string.IsNullOrEmpty(serial))
                return false;

            foreach (var entry in cameraManager.Snapshot())
            {
                try
                {
                    if (string.Equals(entry.Value.GetSerialNumber(), serial, StringComparison.Ordinal))
                        return true;
                }
                catch
                {
                    // 单个实例取序列号失败不影响整体扫描
                }
            }

            return false;
        }

        /// <inheritdoc />
        public CameraLinkStatus ProbeCameraLinkStatus(ICameraInfo info, CancellationToken cancellationToken = default)
        {
            // 无序列号可匹配时无法判定任何链路状态：必须返回 Unknown 而不是谎称"被占用"
            if (info == null || string.IsNullOrEmpty(info.SerialNumber))
                return CameraLinkStatus.Unknown;

            // 自持优先：本进程连接在独占模式下同样会令可达性检查失败，必须先查注册表
            if (IsSerialConnected(info.SerialNumber))
                return CameraLinkStatus.Connected;

            cancellationToken.ThrowIfCancellationRequested();

            // 非侵入优先：厂商可达性查询（实现方自行重新枚举取新鲜设备信息）
            var vendorStatus = TryProbeByVendor(info, cancellationToken);
            if (vendorStatus.HasValue)
                return vendorStatus.Value;

            // 回退：侵入式探测（清理残留 → Open → 成功即 Idle 并立即 Close）
            return ProbeByOpenClose(info, cancellationToken);
        }

        /// <summary>
        /// 调用厂商非侵入探测：厂商未实现能力或调用失败（SDK 未初始化、设备枚举异常等）
        /// 一律返回 <c>null</c>，由调用方回退到侵入式探测，绝不把异常抛给界面线程
        /// </summary>
        private CameraLinkStatus? TryProbeByVendor(ICameraInfo info, CancellationToken cancellationToken)
        {
            if (this.provider is not ILinkStatusProbeProvider probeProvider)
                return null;

            try
            {
                return probeProvider.ProbeLinkStatus(info, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 侵入式探测回退：以 probe:{serial} 为临时 key 尝试打开，
        /// 成功说明当前可连接（Idle）并立即关闭；失败无法区分"被占用"与"设备不存在"，
        /// 按"被占用"保守表达。
        /// 探测用的帧流与相机连接都不留在注册表中。
        /// </summary>
        private CameraLinkStatus ProbeByOpenClose(ICameraInfo info, CancellationToken cancellationToken)
        {
            var probeKey = ProbeKeyPrefix + info.SerialNumber;

            // 清理上次残留；NotFound 属正常路径
            this.cameraManager.Remove(probeKey);

            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var opened = OpenCamera(info, probeKey);
                if (!opened.IsSuccess)
                    return CameraLinkStatus.Occupied;

                Close(probeKey);
                return CameraLinkStatus.Idle;
            }
            finally
            {
                // Close 失败/抛异常时也要清理：探测连接绝不能留在相机注册表或帧流表中
                this.cameraManager.Remove(probeKey);
                this.streamManager.RemoveStream(probeKey);
            }
        }

        /// <inheritdoc />
        public FrameStreamStatistics GetStreamStatistics(string cameraKey)
        {
            if (string.IsNullOrEmpty(cameraKey))
                return FrameStreamStatistics.Empty;

            if (!streamManager.GetStream(cameraKey, out var stream))
                return FrameStreamStatistics.Empty;

            return stream.Statistics;
        }
    }
}
