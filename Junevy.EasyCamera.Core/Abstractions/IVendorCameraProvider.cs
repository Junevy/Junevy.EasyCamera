namespace Junevy.EasyCamera.Core.Abstractions
{
    /// <summary>
    /// 相机厂商提供器接口，用于多厂商聚合场景下标识提供器所属厂商。
    /// </summary>
    public interface IVendorCameraProvider : ICameraProvider
    {
        /// <summary>
        /// 厂商名称
        /// </summary>
        string VendorName { get; }
    }
}
