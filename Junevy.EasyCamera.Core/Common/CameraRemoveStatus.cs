namespace Junevy.EasyCamera.Core.Common
{
    /// <summary>
    /// 相机从缓存移除的结果状态
    /// </summary>
    public enum CameraRemoveStatus
    {
        /// <summary>已移除并成功释放</summary>
        Removed = 0,

        /// <summary>相机Key不存在（或管理器已释放）</summary>
        NotFound = 1,

        /// <summary>已移除，但关闭或释放失败（诊断见 ICameraManager 实现的 LastError）</summary>
        ReleaseFailed = 2
    }
}
