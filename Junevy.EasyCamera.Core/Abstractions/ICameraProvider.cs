using System.Collections.Generic;

namespace Junevy.EasyCamera.Core.Abstractions
{
    /// <summary>
    /// Camera提供器接口，负责枚举设备并创建相机实例。
    /// </summary>
    /// <remarks>
    /// 该接口只保留"所有厂商都必须能做的事"。厂商独有能力（可达性探测、内部缓冲区配置、
    /// 设备命名等）一律通过独立的能力接口表达（如 <see cref="ILinkStatusProbeProvider" />、
    /// <see cref="IBufferConfigurable" />、<see cref="INamedCameraInfo" />），
    /// 避免每加一个能力就破坏所有实现者。
    /// </remarks>
    public interface ICameraProvider
    {
        /// <summary>
        /// 判断当前提供器能否创建指定相机信息的相机实例
        /// </summary>
        /// <param name="info">相机信息</param>
        /// <returns>
        /// 能创建返回 <c>true</c>；否则 <c>false</c>
        /// </returns>
        bool Supports(ICameraInfo info);

        /// <summary>
        /// 创建指定相机信息的相机实例
        /// </summary>
        /// <param name="info">相机信息</param>
        /// <param name="stream">相机帧数据流，由调用方创建并管理生命周期</param>
        /// <returns>
        /// 相机实例
        /// </returns>
        /// <exception cref="System.ArgumentException">
        /// 相机信息类型不被当前提供器支持
        /// </exception>
        ICamera Create(ICameraInfo info, ICameraStream stream);

        /// <summary>
        /// 枚举所有相机信息
        /// </summary>
        /// <returns>
        /// 相机信息列表
        /// </returns>
        IEnumerable<ICameraInfo> Enumerate();

        /// <summary>
        /// 枚举指定接口类型的相机信息
        /// </summary>
        /// <param name="type">相机接口类型</param>
        /// <returns>
        /// 相机信息列表
        /// </returns>
        IEnumerable<ICameraInfo> Enumerate(CameraInterfaceType type);
    }
}
