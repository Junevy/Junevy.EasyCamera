namespace Junevy.EasyCamera.Core.Abstractions
{
    /// <summary>
    /// 相机物理接口类型（GigE/USB/CameraLink/GenTL 等），表示设备如何连接主机，
    /// 与相机品牌无关。品牌由 <see cref="Junevy.EasyCamera.Core.Abstractions.ICameraInfo.Manufacturer" /> 表达。
    /// </summary>
    public enum CameraInterfaceType
    {
        GigE,
        Usb,
        GenTL,
        CameraLink,

        /// <summary>全部接口类型</summary>
        All,
        Unknown
    }
}
