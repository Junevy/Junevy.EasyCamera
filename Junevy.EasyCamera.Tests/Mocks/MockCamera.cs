using Junevy.EasyCamera.Core.Abstractions;

namespace Junevy.EasyCamera.Tests.Mocks
{
    public class MockCamera : ICamera
    {
        public bool IsConnected { get; private set; }
        public bool IsDisposed { get; private set; }
        public bool IsGrabbing { get; private set; }
        public string LastError { get; set; }

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

        public CameraResult StopGrab()
        {
            IsGrabbing = false;
            return CameraResult.Success(0);
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

        public bool TryGetParam<T>(string paramName, out T value)
        {
            value = default;
            return false;
        }

        public string GetEnumParam(string paramName)
        {
            return string.Empty;
        }

        public bool TryGetEnumParam(string paramName, out string value)
        {
            value = null;
            return false;
        }

        public CameraResult ExecuteCommand(string command)
        {
            return CameraResult.Success(0);
        }

        /// <summary>GetSerialNumber 的可编程返回值，供按序列号匹配的用例使用</summary>
        public string SerialNumberToReport { get; set; } = string.Empty;

        public string GetSerialNumber()
        {
            return this.SerialNumberToReport;
        }

        public void Dispose()
        {
            IsDisposed = true;
            IsConnected = false;
        }
    }
}
