using System;
using System.Threading.Tasks;

namespace Junevy.EasyCamera.Core.Abstractions
{
    /// <summary>
    /// Camera流接口，在Camera取流时，如何处理流数据
    /// </summary>
    public interface ICameraStream : IDisposable
    {
        /// <summary>
        /// 订阅者数量
        /// </summary>
        /// <value>
        /// 订阅者数量
        /// </value>
        int SubscriberCount { get; }

        /// <summary>
        /// 发布一帧图像
        /// </summary>
        /// <param name="frame">一帧图像，发布方保留初始引用，由流负责分发与释放</param>
        void Publish(IFrame frame);

        /// <summary>
        /// 订阅指定相机的帧图像数据
        /// </summary>
        /// <param name="subberKey">订阅自定义名称</param>
        /// <param name="capacity">图像缓存容量，小于1时按1处理</param>
        /// <param name="handler">帧处理回调处理方法，参数1：发布帧数据的相机自定义名称；参数2：帧数据；参数3：异步Task</param>
        /// <param name="whenException">异常发生处理回调方法，当不提供异常处理回调时，订阅工作线程将终止</param>
        void Subscribe(string subberKey, int capacity, Func<string, IFrame, Task> handler, Action<Exception> whenException = null);

        /// <summary>
        /// 取消订阅指定相机的帧的处理数据
        /// </summary>
        /// <param name="subberKey">订阅自定义名称</param>
        /// <returns>
        /// 是否成功取消订阅
        /// </returns>
        bool Unsubscribe(string subberKey);
    }
}
