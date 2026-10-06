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
        /// 帧流统计快照（累计发布/投递/丢弃帧数）。
        /// 用于现场排查"图像卡顿、丢帧"：<see cref="FrameStreamStatistics.Dropped" />
        /// 持续增长说明下游消费慢于采集，应加大 <see cref="Junevy.EasyCamera.Core.Common.IStreamOptions.StreamCapacity" />
        /// 或优化 handler。
        /// </summary>
        FrameStreamStatistics Statistics { get; }

        /// <summary>
        /// 发布一帧图像。
        /// 发布方将帧的初始引用转移给流：流为每个成功入队的订阅者增加一个引用，
        /// 并负责在消费、淘汰、取消或流释放时释放；Publish 返回后发布方不得再访问该帧。
        /// </summary>
        /// <param name="frame">一帧图像，所有权随调用转移给流</param>
        void Publish(IFrame frame);

        /// <summary>
        /// 订阅指定相机的帧图像数据。同 Key 重复订阅时原子替换旧订阅者。
        /// </summary>
        /// <param name="subscriberKey">订阅者标识，流内唯一</param>
        /// <param name="capacity">图像缓存容量，小于1时按1处理</param>
        /// <param name="handler">帧处理回调处理方法，参数1：发布帧数据的相机Key；参数2：帧数据；参数3：异步Task</param>
        /// <param name="whenException">异常发生处理回调方法，当不提供异常处理回调时，订阅工作线程将终止</param>
        void Subscribe(string subscriberKey, int capacity, Func<string, IFrame, Task> handler, Action<Exception> whenException = null);

        /// <summary>
        /// 取消订阅指定相机的帧的处理数据
        /// </summary>
        /// <param name="subscriberKey">订阅者标识</param>
        /// <returns>
        /// 是否成功取消订阅
        /// </returns>
        bool Unsubscribe(string subscriberKey);
    }
}
