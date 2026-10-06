using System.Threading;

namespace Junevy.EasyCamera.Core.Abstractions
{
    /// <summary>
    /// 相机链路可达性探测能力接口（可选能力，接口隔离）。
    /// 仅支持非侵入式可达性查询的厂商提供器才实现；未实现的提供器由调用方
    /// 决定回退策略（如侵入式打开/关闭探测）。
    /// </summary>
    /// <remarks>
    /// 该能力刻意不放进 <see cref="ICameraProvider" />：基础接口每加一个成员，
    /// 所有外部实现者（含第三方品牌）都会被迫修改，属于破坏性变更。
    /// </remarks>
    public interface ILinkStatusProbeProvider
    {
        /// <summary>
        /// 探测指定相机的链路状态（非侵入优先：厂商可达性查询）。
        /// 实现方不应信任 <paramref name="info"/> 携带的原生引用，需自行重新枚举取新鲜设备信息。
        /// </summary>
        /// <param name="info">相机信息，至少需提供序列号</param>
        /// <param name="cancellationToken">取消令牌；厂商调用本身不可中途取消，仅在步骤间检查</param>
        /// <returns>
        /// 探测结果：<see cref="CameraLinkStatus.Idle"/> 可达、
        /// <see cref="CameraLinkStatus.Occupied"/> 在线但不可达、
        /// <see cref="CameraLinkStatus.Unreachable"/> 重新枚举中不存在。
        /// 无法判定时返回 <see cref="CameraLinkStatus.Unknown"/>。
        /// </returns>
        CameraLinkStatus ProbeLinkStatus(ICameraInfo info, CancellationToken cancellationToken = default);
    }
}
