using Junevy.EasyCamera.Core.Abstractions;
using Junevy.EasyCamera.Core.Common;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Junevy.EasyCamera.Common
{
    /// <summary>
    /// 相机服务类，提供相机枚举、打开关闭、取流订阅与参数访问的门面入口
    /// </summary>
    public class CameraService(ICameraProvider provider, ICameraManager cameraManager, IStreamManager streamManager, StreamOptions streamOptions = null) : ICameraService
    {
        private const string ErrorMsg = "Camera not open or found";
        private readonly ConcurrentDictionary<string, object> cameraKeyLocks = new();

        private readonly ICameraProvider provider = provider;
        private readonly ICameraManager cameraManager = cameraManager;
        private readonly IStreamManager streamManager = streamManager;
        private readonly StreamOptions streamOptions = streamOptions ?? new StreamOptions();

        /// <summary>
        /// 相机 key 级操作锁：OpenCamera/StartGrab/StopGrab/SetTrigger 按 key 串行。
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
        public IEnumerable<ICameraInfo> EnumerateCameras(CameraType type = CameraType.ALL) => provider.Enumerate(type);

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
        /// <param name="subKey">订阅者标识</param>
        /// <param name="processFrame">处理图像帧的回调函数</param>
        /// <param name="whenException">异常发生处理回调方法，当不提供异常处理回调时，订阅工作线程将终止</param>
        /// <param name="capacity">订阅Channel的边界（容量），小于等于0时使用 <see cref="StreamOptions.StreamCapacity" /></param>
        /// <returns>
        /// 是否成功订阅
        /// </returns>
        public bool SubscribeFrameStream(string cameraKey, string subKey, Func<string, IFrame, Task> processFrame, Action<Exception> whenException = null, int capacity = 0)
        {
            if (processFrame == null) return false;
            if (string.IsNullOrEmpty(cameraKey) || string.IsNullOrEmpty(subKey)) return false;

            if (!streamManager.GetStream(cameraKey, out var stream)) return false;

            if (capacity <= 0)
                capacity = this.streamOptions.StreamCapacity;

            try
            {
                stream.Subscribe(subKey, capacity, processFrame, whenException);
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
        /// <param name="subKey">订阅者标识符</param>
        /// <returns>
        /// 是否成功取消订阅
        /// </returns>
        public bool UnsubscribeFrameStream(string cameraKey, string subKey)
        {
            if (string.IsNullOrEmpty(cameraKey) || string.IsNullOrEmpty(subKey)) return false;

            if (!streamManager.GetStream(cameraKey, out var stream)) return false;

            return stream.Unsubscribe(subKey);
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
                    camera?.StopGrab();
                    return CameraResult.Fail(-2, e.Message);
                }
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

                camera.StopGrab();
                return camera.IsGrabbing
                    ? CameraResult.Fail(-1, "Camera stop grabbing failed")
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

            return this.cameraManager.Remove(cameraKey) switch
            {
                CameraRemoveStatus.Removed => CameraResult.Success(0),
                CameraRemoveStatus.NotFound => CameraResult.Fail(-1, ErrorMsg),
                _ => CameraResult.Fail(-1, this.cameraManager.LastError ?? "Dispose camera error")
            };
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
        /// 获取在线相机的序列号
        /// </summary>
        /// <param name="cameraKey">相机Key</param>
        /// <returns>
        /// 相机序列号，未找到时返回空字符串
        /// </returns>
        public string GetOnlineCameraSerialNumber(string cameraKey)
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
        /// <param name="triggerWay">触发方式</param>
        /// <param name="isAcquisition">是否打开触发</param>
        /// <returns>
        /// 相机操作结果
        /// </returns>
        public CameraResult SetTrigger(string cameraKey, string triggerWay, bool isAcquisition)
        {
            if (string.IsNullOrEmpty(cameraKey))
                return CameraResult.Fail(-1, ErrorMsg);

            lock (this.GetKeyLock(cameraKey))
            {
                if (!cameraManager.TryGet(cameraKey, out var camera) || !camera.IsConnected)
                    return CameraResult.Fail(-1, ErrorMsg);

                if (string.IsNullOrEmpty(triggerWay))
                    return CameraResult.Fail(-1, "Check the trigger source or trigger way");

                camera.StopGrab();
                if (camera.IsGrabbing)
                    return CameraResult.Fail(-1, "Camera stop grabbing failed");

                string acq = isAcquisition ? "On" : "Off";
                var acqResult = camera.SetEnumParam("TriggerMode", acq);
                if (!acqResult.IsSuccess) return acqResult;

                return camera.SetEnumParam("TriggerSource", triggerWay);
            }
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
        {
            if (!cameraManager.TryGet(cameraKey ?? "", out var camera) || !camera.IsConnected)
                return default;

            return camera.GetParam<T>(paramName);
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
        {
            if (!cameraManager.TryGet(cameraKey ?? "", out var camera) || !camera.IsConnected)
                return string.Empty;

            return camera.GetEnumParam(paramName);
        }
    }
}
