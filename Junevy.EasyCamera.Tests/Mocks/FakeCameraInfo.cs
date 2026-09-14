using Junevy.EasyCamera.Core.Abstractions;

namespace Junevy.EasyCamera.Tests.Mocks
{
    /// <summary>
    /// 模拟厂商相机信息，用于聚合提供器的分发测试
    /// </summary>
    public class FakeCameraInfo : ICameraInfo
    {
        public string SerialNumber { get; set; }

        public string ModelName { get; set; }

        public string UserDefinedName { get; set; }

        public string Manufacturer { get; set; }

        public string CameraVersion { get; set; }

        public CameraType InterfaceType { get; set; } = CameraType.GigE;
    }
}
