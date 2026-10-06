namespace Junevy.EasyCamera.Core.Common
{
    /// <summary>
    /// 订阅者有界队列的背压策略。
    /// </summary>
    /// <remarks>
    /// 两种策略都不会阻塞 SDK 采集线程（宁可丢帧，绝不拖慢采集），区别只在"丢哪一帧"：
    /// <list type="bullet">
    /// <item><see cref="DropOldest" />：保留最新画面，适合实时预览与状态判定。</item>
    /// <item><see cref="RejectNewest" />：保留已入队的旧帧，按序处理；新帧被拒收并计入丢帧统计。
    /// 适合"每帧都必须按顺序测量/计数、不能丢帧也不能乱序"的场景（如线阵测量、缺陷计数）。</item>
    /// </list>
    /// 刻意不提供"丢新帧"模式：<c>System.Threading.Channels</c> 的
    /// <see cref="System.Threading.Channels.BoundedChannelFullMode.DropNewest" /> 与
    /// <see cref="System.Threading.Channels.BoundedChannelFullMode.DropOldest" /> 行为完全一致
    /// （实测都会淘汰最旧帧并保留最新帧），提供该选项只会名不副实。
    /// </remarks>
    public enum BackpressureMode
    {
        /// <summary>队列满时淘汰最旧的一帧，保留最新画面（默认）</summary>
        DropOldest = 0,

        /// <summary>队列满时拒绝新到的帧（不覆盖已入队帧），并计入丢帧统计</summary>
        RejectNewest = 1
    }
}
