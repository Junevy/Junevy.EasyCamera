namespace Junevy.EasyCamera.Core.Abstractions
{
    /// <summary>
    /// 帧数据流统计快照（只读值拷贝，读取时刻的计数）。
    /// </summary>
    public struct FrameStreamStatistics
    {
        public FrameStreamStatistics(long published, long delivered, long dropped)
        {
            this.Published = published;
            this.Delivered = delivered;
            this.Dropped = dropped;
        }

        /// <summary>累计收到并尝试分发的帧数（不含空帧）</summary>
        public long Published { get; }

        /// <summary>累计投递给订阅者 handler 的帧数</summary>
        public long Delivered { get; }

        /// <summary>
        /// 累计未能送达订阅者的帧数：无订阅者、背压淘汰（队列满）、
        /// 订阅者已取消订阅三种情况合计。
        /// </summary>
        public long Dropped { get; }

        /// <summary>静态空统计（相机未打开或流不存在时返回）</summary>
        public static FrameStreamStatistics Empty => default;
    }
}
