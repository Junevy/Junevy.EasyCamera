namespace Junevy.EasyCamera.Core.Abstractions
{
    /// <summary>
    /// 相机链路状态：描述"本进程视角"下该相机当前的可连接性。
    /// 身份为稳定枚举，值只增不改；显示文本由应用侧本地化。
    /// </summary>
    /// <remarks>
    /// <see cref="Unknown" /> 恒为 0，务必不要把其它成员排到 0：
    /// 未初始化/漏赋值的变量必须落在"未知"而不是"已连接"上。
    /// </remarks>
    public enum CameraLinkStatus
    {
        /// <summary>未能判定（未探测、探测被取消，或缺少可用的探测手段）。</summary>
        Unknown = 0,

        /// <summary>本进程已持有该相机的连接（任意 cameraKey 注册且序列号匹配）。</summary>
        Connected = 1,

        /// <summary>相机在线、未被本进程持有且可达——当前可以打开。</summary>
        Idle = 2,

        /// <summary>相机在线但不可达：可达性检查未通过，通常是被其它客户端独占。</summary>
        Occupied = 3,

        /// <summary>相机不可达且在重新枚举中不存在（掉线、被拔出、未上电）。</summary>
        Unreachable = 4
    }
}
