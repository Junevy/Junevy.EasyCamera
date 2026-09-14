namespace Junevy.EasyCamera.Core.Abstractions
{
    /// <summary>
    /// Camera信息接口
    /// </summary>
    public interface ICameraInfo
    {
        string SerialNumber { get; }

        string ModelName { get; }

        string UserDefinedName { get; }

        string Manufacturer { get; }

        string CameraVersion { get; }

        CameraType InterfaceType { get; }
    }
}
