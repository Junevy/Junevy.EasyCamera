using Junevy.EasyCamera.Core.Abstractions;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Junevy.EasyCamera.Core.Common
{
    /// <summary>
    /// 相机服务类，提供相机枚举、打开关闭、取流订阅与参数访问的门面入口
    /// </summary>
    public class CameraService(ICameraProvider provider, CameraManager cameraManager, StreamManager streamManager, StreamOptions streamOptions = null)
    {
        private const string ErrorMsg = "Camera not open or found";

        private readonly ICameraProvider provider = provider;
        private readonly CameraManager cameraManager = cameraManager;
        private readonly StreamManager streamManager = streamManager;
        private readonly StreamOptions streamOptions = streamOptions ?? new StreamOptions();

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
            if (info == null)
                return CameraResult.Fail(-1, "The camera info is null");

            if (string.IsNullOrEmpty(cameraKey))
                return CameraResult.Fail(-1, "The camera key is empty");

            try
            {
                if (!cameraManager.TryGet(cameraKey, out var camera))
                {
                    // 流与相机使用相同的 Key，保证订阅入口与打开入口一致
                    var stream = streamManager.GetOrCreateStream(cameraKey);
                    camera = provider.Create(info, stream);
                    cameraManager.Register(cameraKey, camera);
                }

                if (camera.IsOpen)
                    return CameraResult.Fail(-1, "The camera has been opened");

                return camera.Open();
            }
            catch (Exception e)
            {
                return CameraResult.Fail(-2, e.Message);
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

            stream.Subscribe(subKey, capacity, processFrame, whenException);
            return true;
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
            if (!cameraManager.TryGet(cameraKey ?? "", out var camera) || !camera.IsOpen)
                return CameraResult.Fail(-1, ErrorMsg);

            try
            {
                return camera.Grab();
            }
            catch (Exception e)
            {
                camera?.StopGrab();
                return CameraResult.Fail(-2, e.Message);
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
            if (!cameraManager.TryGet(cameraKey ?? "", out var camera) || !camera.IsOpen)
                return CameraResult.Fail(-1, ErrorMsg);

            camera.StopGrab();
            return CameraResult.Success(1);
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
            if (!cameraManager.TryGet(cameraKey ?? "", out var camera))
                return CameraResult.Fail(-1, ErrorMsg);

            var closeResult = camera.Close();
            cameraManager.Remove(cameraKey);

            return closeResult;
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
            if (!cameraManager.TryGet(cameraKey ?? "", out var camera) || !camera.IsOpen)
                return CameraResult.Fail(-1, ErrorMsg);

            return camera.SetParam(paramName, value);
        }

        public CameraResult SetParam(string cameraKey, string paramName, int value)
        {
            if (!cameraManager.TryGet(cameraKey ?? "", out var camera) || !camera.IsOpen)
                return CameraResult.Fail(-1, ErrorMsg);

            return camera.SetParam(paramName, value);
        }

        public CameraResult SetParam(string cameraKey, string paramName, bool value)
        {
            if (!cameraManager.TryGet(cameraKey ?? "", out var camera) || !camera.IsOpen)
                return CameraResult.Fail(-1, ErrorMsg);

            return camera.SetParam(paramName, value);
        }

        public CameraResult SetParam(string cameraKey, string paramName, float value)
        {
            if (!cameraManager.TryGet(cameraKey ?? "", out var camera) || !camera.IsOpen)
                return CameraResult.Fail(-1, ErrorMsg);

            return camera.SetParam(paramName, value);
        }

        public CameraResult SetEnumParam(string cameraKey, string paramName, string value)
        {
            if (!cameraManager.TryGet(cameraKey ?? "", out var camera) || !camera.IsOpen)
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
            if (!cameraManager.TryGet(cameraKey ?? "", out var camera) || !camera.IsOpen)
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
            if (!cameraManager.TryGet(cameraKey ?? "", out var camera) || !camera.IsOpen)
                return CameraResult.Fail(-1, ErrorMsg);

            if (string.IsNullOrEmpty(triggerWay))
                return CameraResult.Fail(-1, "Check the trigger source or trigger way");

            camera.StopGrab();

            string acq = isAcquisition ? "On" : "Off";
            var acqResult = camera.SetEnumParam("TriggerMode", acq);
            if (!acqResult.IsSuccess) return acqResult;

            return camera.SetEnumParam("TriggerSource", triggerWay);
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
            if (!cameraManager.TryGet(cameraKey ?? "", out var camera) || !camera.IsOpen)
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
            if (!cameraManager.TryGet(cameraKey ?? "", out var camera) || !camera.IsOpen)
                return string.Empty;

            return camera.GetEnumValue(paramName);
        }
    }
}
