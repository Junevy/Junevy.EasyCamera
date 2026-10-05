using Junevy.EasyCamera.Common;
using Junevy.EasyCamera.Core.Abstractions;
using Junevy.EasyCamera.Core.Common;
using Junevy.EasyCamera.Tests.Mocks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Junevy.EasyCamera.Tests.Common
{
    [TestClass]
    public class CameraManagerServiceRegressionTests
    {
        [TestMethod]
        public void CameraManager_Remove_CloseFailure_ReturnsReleaseFailedButDisposesAndRemoves()
        {
            var manager = new CameraManager();
            var camera = new TrackingCamera { CloseResult = CameraResult.Fail(-9, "close failed") };
            manager.TryRegister("SN001", camera);

            var status = manager.Remove("SN001");

            Assert.AreEqual(CameraRemoveStatus.ReleaseFailed, status);
            Assert.IsTrue(camera.IsDisposed);
            Assert.IsFalse(manager.TryGet("SN001", out _));
            StringAssert.Contains(manager.LastError ?? string.Empty, "close failed");
        }

        [TestMethod]
        public void CameraManager_Remove_MissingKey_ReturnsNotFound()
        {
            var manager = new CameraManager();

            Assert.AreEqual(CameraRemoveStatus.NotFound, manager.Remove("missing"));
        }

        [TestMethod]
        public void CameraManager_Remove_AfterFailedRemove_SucceedingRemoveClearsLastError()
        {
            var manager = new CameraManager();
            var badCamera = new TrackingCamera { CloseResult = CameraResult.Fail(-9, "close failed") };
            manager.TryRegister("bad", badCamera);
            manager.Remove("bad");

            var goodCamera = new TrackingCamera();
            manager.TryRegister("good", goodCamera);

            Assert.AreEqual(CameraRemoveStatus.Removed, manager.Remove("good"));
            Assert.IsNull(manager.LastError, "成功清理后 LastError 必须清空，避免陈旧错误误导后续 Close");
        }

        [TestMethod]
        public void CameraService_Close_UnknownKey_ReportsNotFound()
        {
            var provider = new TrackingProvider();
            var manager = new CameraManager();
            using var streams = new StreamManager();
            var service = new CameraService(provider, manager, streams);

            var result = service.Close("missing-key");

            Assert.IsFalse(result.IsSuccess);
            StringAssert.Contains(result.Message, "not open or found");
        }

        [TestMethod]
        public void CameraService_StartGrab_AlreadyGrabbing_ReturnsSuccess()
        {
            var provider = new TrackingProvider();
            var manager = new CameraManager();
            using var streams = new StreamManager();
            var service = new CameraService(provider, manager, streams);

            Assert.IsTrue(service.OpenCamera(new MockCameraInfo { SerialNumber = "SN001" }, "key").IsSuccess);
            Assert.IsTrue(service.StartGrab("key").IsSuccess);

            var second = service.StartGrab("key");

            Assert.IsTrue(second.IsSuccess, "StartGrab 必须幂等，与 StopGrab 风格一致");
        }

        [TestMethod]
        public void CameraService_StopGrab_SuccessCodeIsZero()
        {
            var provider = new TrackingProvider();
            var manager = new CameraManager();
            using var streams = new StreamManager();
            var service = new CameraService(provider, manager, streams);

            Assert.IsTrue(service.OpenCamera(new MockCameraInfo { SerialNumber = "SN001" }, "key").IsSuccess);
            Assert.IsTrue(service.StartGrab("key").IsSuccess);

            var result = service.StopGrab("key");

            Assert.IsTrue(result.IsSuccess);
            Assert.AreEqual(0, result.Code, "成功结果 Code 约定为 0，不得使用魔数 1");
        }

        [TestMethod]
        public void CameraService_SubscribeFrameStream_WhenStreamDisposedMidFlight_ReturnsFalse()
        {
            // GetStream 通过后、Subscribe 执行前流被释放的竞态窗口：
            // 服务必须按 bool 契约返回 false，不得外泄 ObjectDisposedException
            var provider = new TrackingProvider();
            var manager = new CameraManager();
            var service = new CameraService(provider, manager, new DisposingStreamManager());

            Assert.IsFalse(service.SubscribeFrameStream("key", "sub", (_, _) => Task.CompletedTask));
        }

        [TestMethod]
        public void CameraService_TryGetParam_UnavailableCamera_ReturnsFalse()
        {
            var provider = new TrackingProvider();
            var manager = new CameraManager();
            using var streams = new StreamManager();
            var service = new CameraService(provider, manager, streams);

            Assert.IsFalse(service.TryGetParam<int>("missing", "Width", out var value));
            Assert.AreEqual(0, value);
        }

        [TestMethod]
        public void CameraService_TryGetParam_ReportsFailureInsteadOfDefault()
        {
            var provider = new TrackingProvider { TryGetParamResult = false };
            var manager = new CameraManager();
            using var streams = new StreamManager();
            var service = new CameraService(provider, manager, streams);

            Assert.IsTrue(service.OpenCamera(new MockCameraInfo { SerialNumber = "SN001" }, "key").IsSuccess);

            Assert.IsFalse(service.TryGetParam<int>("key", "Width", out _), "取参失败必须与 default 值可区分");
        }

        [TestMethod]
        public void CameraService_TryGetParam_Success_ReturnsValue()
        {
            var provider = new TrackingProvider { TryGetParamValue = 42 };
            var manager = new CameraManager();
            using var streams = new StreamManager();
            var service = new CameraService(provider, manager, streams);

            Assert.IsTrue(service.OpenCamera(new MockCameraInfo { SerialNumber = "SN001" }, "key").IsSuccess);

            Assert.IsTrue(service.TryGetParam<int>("key", "Width", out var value));
            Assert.AreEqual(42, value);
        }

        private sealed class DisposingStreamManager : IStreamManager
        {
            public ICameraStream GetOrCreateStream(string userDefinedName) => throw new ObjectDisposedException(nameof(StreamManager));

            public bool GetStream(string userDefinedName, out ICameraStream stream)
            {
                stream = new DisposedCameraStream();
                return true;
            }

            public bool RemoveStream(string userDefinedName) => false;

            public void Dispose()
            {
            }
        }

        private sealed class DisposedCameraStream : ICameraStream
        {
            public int SubscriberCount => 0;

            public void Dispose()
            {
            }

            public void Publish(IFrame frame)
            {
            }

            public void Subscribe(string subscriberKey, int capacity, Func<string, IFrame, Task> handler, Action<Exception> whenException = null)
                => throw new ObjectDisposedException(nameof(CameraStream));

            public bool Unsubscribe(string subscriberKey) => false;
        }

        [TestMethod]
        public void CameraManager_Dispose_ClearsRegistryAndIsIdempotent()
        {
            var manager = new CameraManager();
            var camera = new TrackingCamera();
            manager.TryRegister("SN001", camera);

            manager.Dispose();
            manager.Dispose();

            Assert.IsTrue(camera.IsDisposed);
            Assert.IsFalse(manager.TryGet("SN001", out _));
            Assert.ThrowsException<ObjectDisposedException>(() => manager.TryRegister("SN002", new TrackingCamera()));
        }

        [TestMethod]
        public void CameraManager_TryRegister_NullCamera_Throws()
        {
            var manager = new CameraManager();

            Assert.ThrowsException<ArgumentNullException>(() => manager.TryRegister("SN001", null));
        }

        [TestMethod]
        public async Task CameraService_OpenCamera_ConcurrentSameKey_CreatesOnlyOneCamera()
        {
            var provider = new TrackingProvider();
            var manager = new CameraManager();
            using var streams = new StreamManager();
            var service = new CameraService(provider, manager, streams);
            var info = new MockCameraInfo { SerialNumber = "SN001" };
            var tasks = new List<Task<CameraResult>>();

            for (var i = 0; i < 32; i++)
                tasks.Add(Task.Run(() => service.OpenCamera(info, "same-key")));

            await Task.WhenAll(tasks);

            Assert.AreEqual(1, provider.CreateCount);
            Assert.AreEqual(1, provider.CreatedCameras.Count);
            Assert.IsTrue(provider.CreatedCameras.TryPeek(out var created));
            Assert.IsFalse(created.IsDisposed);
        }

        [TestMethod]
        public void CameraService_OpenCamera_ConnectFailure_RollsBackCandidate()
        {
            var provider = new TrackingProvider
            {
                ConnectResult = CameraResult.Fail(-8, "connect failed")
            };
            var manager = new CameraManager();
            using var streams = new StreamManager();
            var service = new CameraService(provider, manager, streams);

            var result = service.OpenCamera(new MockCameraInfo { SerialNumber = "SN001" }, "key");

            Assert.IsFalse(result.IsSuccess);
            Assert.IsFalse(manager.TryGet("key", out _));
            Assert.IsTrue(provider.CreatedCameras.TryPeek(out var created));
            Assert.IsTrue(created.IsDisposed);
        }

        [TestMethod]
        public void CameraService_Close_ReflectsCleanupFailure()
        {
            var provider = new TrackingProvider
            {
                CloseResult = CameraResult.Fail(-9, "close failed")
            };
            var manager = new CameraManager();
            using var streams = new StreamManager();
            var service = new CameraService(provider, manager, streams);

            Assert.IsTrue(service.OpenCamera(new MockCameraInfo { SerialNumber = "SN001" }, "key").IsSuccess);
            var result = service.Close("key");

            Assert.IsFalse(result.IsSuccess);
            Assert.IsFalse(manager.TryGet("key", out _));
        }

        private sealed class TrackingProvider : ICameraProvider
        {
            public ConcurrentBag<TrackingCamera> CreatedCameras { get; } = new ConcurrentBag<TrackingCamera>();
            public int CreateCount => this.CreatedCameras.Count;
            public CameraResult ConnectResult { get; set; } = CameraResult.Success(0);
            public CameraResult CloseResult { get; set; } = CameraResult.Success(0);
            public bool TryGetParamResult { get; set; } = true;
            public int TryGetParamValue { get; set; } = 42;

            public bool Supports(ICameraInfo info) => true;

            public ICamera Create(ICameraInfo info, ICameraStream stream)
            {
                var camera = new TrackingCamera
                {
                    ConnectResult = this.ConnectResult,
                    CloseResult = this.CloseResult,
                    TryGetParamResult = this.TryGetParamResult,
                    TryGetParamValue = this.TryGetParamValue
                };
                this.CreatedCameras.Add(camera);
                return camera;
            }

            public IEnumerable<ICameraInfo> Enumerate() => Array.Empty<ICameraInfo>();

            public IEnumerable<ICameraInfo> Enumerate(CameraInterfaceType type) => Array.Empty<ICameraInfo>();
        }

        private sealed class TrackingCamera : ICamera
        {
            public CameraResult ConnectResult { get; set; } = CameraResult.Success(0);
            public CameraResult CloseResult { get; set; } = CameraResult.Success(0);
            public bool TryGetParamResult { get; set; } = true;
            public int TryGetParamValue { get; set; } = 42;
            public bool IsConnected { get; private set; }
            public bool IsGrabbing { get; private set; }
            public bool IsDisposed { get; private set; }

            public CameraResult Connect()
            {
                if (this.ConnectResult.IsSuccess)
                    this.IsConnected = true;
                return this.ConnectResult;
            }

            public CameraResult Close()
            {
                if (this.CloseResult.IsSuccess)
                    this.IsConnected = false;
                return this.CloseResult;
            }

            public CameraResult StartGrab()
            {
                // 与真实厂商相机（如 HikCamera）一致：重复取流在相机层返回失败
                if (this.IsGrabbing)
                    return CameraResult.Fail(-1, "Camera is already grabbing");
                this.IsGrabbing = true;
                return CameraResult.Success(0);
            }

            public void StopGrab() => this.IsGrabbing = false;
            public CameraResult SetParam(string paramName, int value) => CameraResult.Success(0);
            public CameraResult SetParam(string paramName, float value) => CameraResult.Success(0);
            public CameraResult SetParam(string paramName, bool value) => CameraResult.Success(0);
            public CameraResult SetParam(string paramName, string value) => CameraResult.Success(0);
            public CameraResult SetEnumParam(string paramName, string value) => CameraResult.Success(0);
            public T GetParam<T>(string paramName) => default;
            public string GetEnumParam(string paramName) => string.Empty;
            public bool TryGetParam<T>(string paramName, out T value)
            {
                if (this.TryGetParamResult && typeof(T) == typeof(int))
                {
                    value = (T)(object)this.TryGetParamValue;
                    return true;
                }

                value = default;
                return false;
            }

            public bool TryGetEnumParam(string paramName, out string value)
            {
                value = null;
                return false;
            }

            public CameraResult ExecuteCommand(string command) => CameraResult.Success(0);
            public string GetSerialNumber() => "SN001";
            public void Dispose()
            {
                this.IsDisposed = true;
                this.IsConnected = false;
            }
        }
    }
}
