namespace Junevy.EasyCamera.Core.Abstractions
{
    /// <summary>
    /// 相机内部图像缓冲区可配置能力接口（可选能力，接口隔离）。
    /// 仅部分厂商 SDK 支持配置采集端缓冲区；未实现该接口的相机会忽略
    /// <see cref="IStreamOptions.CameraBufferCapacity" /> 配置。
    /// </summary>
    public interface IBufferConfigurable
    {
        /// <summary>
        /// 设置相机内部图像缓冲区数量（帧数）。
        /// 相机已打开时立即生效，否则延迟到相机打开成功后应用。
        /// </summary>
        /// <param name="count">缓冲区数量，须大于0</param>
        /// <returns>相机操作结果</returns>
        CameraResult SetBufferCount(int count);
    }
}
