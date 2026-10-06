namespace Junevy.EasyCamera.Core.Common
{
    /// <summary>
    /// 相机帧数据流配置接口
    /// </summary>
    public interface IStreamOptions
    {
        /// <summary>
        /// 订阅者图像缓存容量（帧数），小于1时按1处理
        /// </summary>
        int StreamCapacity { get; set; }

        /// <summary>
        /// 相机内部缓存队列容量（帧数）。
        /// 仅部分厂商SDK支持配置（未实现 <see cref="Junevy.EasyCamera.Core.Abstractions.IBufferConfigurable" /> 的相机会忽略）；0表示使用SDK默认值
        /// </summary>
        int CameraBufferCapacity { get; set; }

        /// <summary>
        /// 订阅者队列满时的背压策略，默认 <see cref="BackpressureMode.DropOldest" />
        /// </summary>
        BackpressureMode BackpressureMode { get; set; }
    }
}
