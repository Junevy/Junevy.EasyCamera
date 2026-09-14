using Junevy.EasyCamera.Core.Abstractions;

namespace Junevy.EasyCamera.Tests.Mocks
{
    public class MockCamera : ICamera
    {
        public bool IsConnected { get; private set; }
        public bool IsDisposed { get; private set; }
        public bool IsGrabbing { get; private set; }

        public CameraResult Connect()
        {
            IsConnected = true;
            return CameraResult.Success(0);
        }

        public CameraResult Close()
        {
            IsConnected = false;
            return CameraResult.Success(0);
        }

        public CameraResult StartGrab()
        {
            IsGrabbing = true;
            return CameraResult.Success(0);
        }

        public void StopGrab()
        {
            IsGrabbing = false;
        }

        public CameraResult SetParam(string paramName, int value)
        {
            return CameraResult.Success(0);
        }

        public CameraResult SetParam(string paramName, float value)
        {
            return CameraResult.Success(0);
        }

        public CameraResult SetParam(string paramName, bool value)
        {
            return CameraResult.Success(0);
        }

        public CameraResult SetParam(string paramName, string value)
        {
            return CameraResult.Success(0);
        }

        public CameraResult SetEnumParam(string paramName, string value)
        {
            return CameraResult.Success(0);
        }

        public T GetParam<T>(string paramName)
        {
            return default;
        }

        public string GetEnumParam(string paramName)
        {
            return string.Empty;
        }

        public CameraResult ExecuteCommand(string command)
        {
            return CameraResult.Success(0);
        }

        public string GetSerialNumber()
        {
            return string.Empty;
        }

        public void Dispose()
        {
            IsDisposed = true;
            IsConnected = false;
        }
    }
}
