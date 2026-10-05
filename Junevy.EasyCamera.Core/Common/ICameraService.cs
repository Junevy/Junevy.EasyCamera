using Junevy.EasyCamera.Core.Abstractions;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Junevy.EasyCamera.Core.Common
{
    /// <summary>
    /// 相机服务接口，定义相机枚举、打开关闭、取流订阅与参数访问的门面契约
    /// </summary>
    public interface ICameraService
    {
        /// <summary>
        /// 枚举相机
        /// </summary>
        /// <param name="type">相机类型</param>
        /// <returns>
        /// 相机信息列表
        /// </returns>
        IEnumerable<ICameraInfo> EnumerateCameras(CameraInterfaceType type = CameraInterfaceType.All);

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
        CameraResult OpenCamera(ICameraInfo info, string cameraKey);

        /// <summary>
        /// 订阅指定相机的图像帧数据
        /// </summary>
        /// <param name="cameraKey">打开相机时使用的Key</param>
        /// <param name="subKey">订阅者标识</param>
        /// <param name="processFrame">处理图像帧的回调函数</param>
        /// <param name="whenException">异常发生处理回调方法，当不提供异常处理回调时，订阅工作线程将终止</param>
        /// <param name="capacity">订阅Channel的边界（容量），小于等于0时使用默认流配置容量</param>
        /// <returns>
        /// 是否成功订阅
        /// </returns>
        bool SubscribeFrameStream(string cameraKey, string subKey, Func<string, IFrame, Task> processFrame, Action<Exception> whenException = null, int capacity = 0);

        /// <summary>
        /// 取消订阅指定相机的图像帧数据
        /// </summary>
        /// <param name="cameraKey">打开相机时使用的Key</param>
        /// <param name="subKey">订阅者标识符</param>
        /// <returns>
        /// 是否成功取消订阅
        /// </returns>
        bool UnsubscribeFrameStream(string cameraKey, string subKey);

        /// <summary>
        /// 打开指定相机的图像采集功能
        /// </summary>
        /// <param name="cameraKey">注册的相机名称</param>
        /// <returns>
        /// 开始取流操作结果
        /// </returns>
        CameraResult StartGrab(string cameraKey);

        /// <summary>
        /// 停止指定相机的图像采集功能
        /// </summary>
        /// <param name="cameraKey">注册的相机名称</param>
        /// <returns>
        /// 相机操作结果
        /// </returns>
        CameraResult StopGrab(string cameraKey);

        /// <summary>
        /// 断开连接指定相机并从缓存中移除释放。
        /// 注意：相机对应的帧数据流不会自动移除，以便重新打开相机后继续订阅
        /// </summary>
        /// <param name="cameraKey">相机Key</param>
        /// <returns>
        /// 相机操作结果
        /// </returns>
        CameraResult Close(string cameraKey);

        /// <summary>
        /// 获取在线相机的序列号
        /// </summary>
        /// <param name="cameraKey">相机Key</param>
        /// <returns>
        /// 相机序列号，未找到时返回空字符串
        /// </returns>
        string GetSerialNumber(string cameraKey);

        /// <summary>
        /// 设置相机的指定参数
        /// </summary>
        /// <param name="cameraKey">相机Key</param>
        /// <param name="paramName">参数键</param>
        /// <param name="value">参数值</param>
        /// <returns>
        /// 相机操作结果
        /// </returns>
        CameraResult SetParam(string cameraKey, string paramName, string value);

        /// <summary>
        /// 设置相机的指定参数
        /// </summary>
        /// <param name="cameraKey">相机Key</param>
        /// <param name="paramName">参数键</param>
        /// <param name="value">参数值</param>
        /// <returns>
        /// 相机操作结果
        /// </returns>
        CameraResult SetParam(string cameraKey, string paramName, int value);

        /// <summary>
        /// 设置相机的指定参数
        /// </summary>
        /// <param name="cameraKey">相机Key</param>
        /// <param name="paramName">参数键</param>
        /// <param name="value">参数值</param>
        /// <returns>
        /// 相机操作结果
        /// </returns>
        CameraResult SetParam(string cameraKey, string paramName, bool value);

        /// <summary>
        /// 设置相机的指定参数
        /// </summary>
        /// <param name="cameraKey">相机Key</param>
        /// <param name="paramName">参数键</param>
        /// <param name="value">参数值</param>
        /// <returns>
        /// 相机操作结果
        /// </returns>
        CameraResult SetParam(string cameraKey, string paramName, float value);

        /// <summary>
        /// 设置相机的枚举参数
        /// </summary>
        /// <param name="cameraKey">相机Key</param>
        /// <param name="paramName">参数键</param>
        /// <param name="value">枚举符号名</param>
        /// <returns>
        /// 相机操作结果
        /// </returns>
        CameraResult SetEnumParam(string cameraKey, string paramName, string value);

        /// <summary>
        /// 执行相机的指定命令
        /// </summary>
        /// <param name="cameraKey">相机Key</param>
        /// <param name="command">命令</param>
        /// <returns>
        /// 相机操作结果
        /// </returns>
        CameraResult ExecuteCommand(string cameraKey, string command);

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
        CameraResult SetTrigger(string cameraKey, string triggerSource, bool enableTrigger);

        /// <summary>
        /// 获取相机的指定参数
        /// </summary>
        /// <param name="cameraKey">相机Key</param>
        /// <param name="paramName">参数键</param>
        /// <returns>
        /// 参数值，相机不可用或类型不支持时返回 default(T)
        /// </returns>
        T GetParam<T>(string cameraKey, string paramName);

        /// <summary>
        /// 尝试获取相机的指定参数，可区分"参数值恰为 default"与"获取失败"
        /// </summary>
        /// <param name="cameraKey">相机Key</param>
        /// <param name="paramName">参数键</param>
        /// <param name="value">获取成功时的参数值</param>
        /// <returns>
        /// 获取成功返回 <c>true</c>；相机不可用、参数名无效、类型不支持或读取失败返回 <c>false</c>
        /// </returns>
        bool TryGetParam<T>(string cameraKey, string paramName, out T value);

        /// <summary>
        /// 获取相机的枚举参数符号名
        /// </summary>
        /// <param name="cameraKey">相机Key</param>
        /// <param name="paramName">参数键</param>
        /// <returns>
        /// 枚举符号名，相机不可用时返回空字符串
        /// </returns>
        string GetEnumParam(string cameraKey, string paramName);

        /// <summary>尝试获取相机的枚举参数符号名，语义同 <see cref="TryGetParam{T}" /></summary>
        bool TryGetEnumParam(string cameraKey, string paramName, out string value);
    }
}
