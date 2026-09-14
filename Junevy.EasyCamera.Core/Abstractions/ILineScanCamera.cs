namespace Junevy.EasyCamera.Core.Abstractions
{
    /// <summary>
    /// 线阵相机接口，标记扩展点。
    /// 线阵相机可在此接口上追加行触发、行计数等线阵特有操作。
    /// </summary>
    public interface ILineScanCamera : ICamera
    {
    }
}
