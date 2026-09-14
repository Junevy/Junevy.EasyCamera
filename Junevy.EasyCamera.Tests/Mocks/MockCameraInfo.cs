using Junevy.EasyCamera.Core.Abstractions;

namespace Junevy.EasyCamera.Tests.Mocks
{
    public class MockCameraInfo : ICameraInfo
    {
        public string SerialNumber { get; set; }

        public string ModelName { get; set; }

        public string UserDefinedName { get; set; }

        public string Manufacturer { get; set; }

        public string CameraVersion { get; set; }

        public CameraType InterfaceType { get; set; } = CameraType.Unknown;
    }
}
