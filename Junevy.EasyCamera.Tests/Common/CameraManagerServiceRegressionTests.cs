using Junevy.EasyCamera.Common;
using Junevy.EasyCamera.Core.Abstractions;
using Junevy.EasyCamera.Core.Common;
using Junevy.EasyCamera.Tests.Mocks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
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

        [TestMethod]
        public async Task CameraService_Close_WaitsForInFlightOpenOnSameKey()
        {
            // Close 必须与 OpenCamera 走同一把 per-key 锁：
            // 否则 Close 会在 Connect() 中途把实例摘走并释放，随后对已释放实例 Connect/StartGrab
            var provider = new TrackingProvider();
            var manager = new CameraManager();
            using var streams = new StreamManager();
            var service = new CameraService(provider, manager, streams);
            var info = new MockCameraInfo { SerialNumber = "SN001" };

            provider.ConnectGate = new SemaphoreSlim(0, 1);

            var openTask = Task.Run(() => service.OpenCamera(info, "key"));
            await provider.ConnectEntered.Task;

            var closeTask = Task.Run(() => service.Close("key"));
            await Task.Delay(150);

            Assert.IsFalse(closeTask.IsCompleted, "同 key 的 Close 必须等待进行中的 OpenCamera");

            provider.ConnectGate.Release();
            await openTask;

            Assert.IsTrue(closeTask.Wait(TimeSpan.FromSeconds(5)));
            Assert.IsTrue(closeTask.Result.IsSuccess);
            Assert.IsFalse(manager.TryGet("key", out _), "关闭后注册表必须为空");
        }

        [TestMethod]
        public async Task CameraService_StartGrab_WaitsForInFlightCloseOnSameKey()
        {
            var provider = new TrackingProvider();
            var manager = new CameraManager();
            using var streams = new StreamManager();
            var service = new CameraService(provider, manager, streams);
            var info = new MockCameraInfo { SerialNumber = "SN001" };

            // 阻塞门必须在 OpenCamera 之前挂上：相机实例是创建时从提供器取走这些设置的
            provider.CloseGate = new SemaphoreSlim(0, 1);

            Assert.IsTrue(service.OpenCamera(info, "key").IsSuccess);

            var closeTask = Task.Run(() => service.Close("key"));
            await provider.CloseEntered.Task;

            var startTask = Task.Run(() => service.StartGrab("key"));
            await Task.Delay(150);
            Assert.IsFalse(startTask.IsCompleted, "同 key 的 StartGrab 必须等待进行中的 Close");

            provider.CloseGate.Release();
            await closeTask;
            await startTask;

            Assert.IsFalse(startTask.Result.IsSuccess, "相机已关闭后 StartGrab 必须失败而不是拿到已释放实例");
        }

        [TestMethod]
        public void CameraService_CameraDisconnected_ForwardsCameraKey()
        {
            var provider = new TrackingProvider();
            var manager = new CameraManager();
            using var streams = new StreamManager();
            var service = new CameraService(provider, manager, streams);
            CameraDisconnectedEventArgs received = null;
            service.CameraDisconnected += (_, e) => received = e;

            Assert.IsTrue(service.OpenCamera(new MockCameraInfo { SerialNumber = "SN001" }, "cam1").IsSuccess);
            Assert.IsTrue(provider.CreatedCameras.TryPeek(out var camera));

            camera.RaiseDisconnected();

            Assert.IsNotNull(received, "门面必须转发相机的掉线事件");
            Assert.AreEqual("cam1", received.CameraKey, "门面事件必须带上操作 Key");
            Assert.AreEqual("SN001", received.SerialNumber);
            StringAssert.Contains(received.Reason, "disconnected");
            Assert.IsTrue(manager.TryGet("cam1", out _), "掉线后相机仍保持注册，便于重连");
        }

        [TestMethod]
        public void CameraService_CameraDisconnected_SubscriberException_DoesNotStopOtherSubscribers()
        {
            var provider = new TrackingProvider();
            var manager = new CameraManager();
            using var streams = new StreamManager();
            var service = new CameraService(provider, manager, streams);
            string notifiedKey = null;
            service.CameraDisconnected += (_, _) => throw new InvalidOperationException("subscriber failure");
            service.CameraDisconnected += (_, e) => notifiedKey = e.CameraKey;

            Assert.IsTrue(service.OpenCamera(new MockCameraInfo { SerialNumber = "SN001" }, "cam1").IsSuccess);
            Assert.IsTrue(provider.CreatedCameras.TryPeek(out var camera));

            camera.RaiseDisconnected();

            Assert.AreEqual("cam1", notifiedKey, "单个订阅者的异常不得影响其他订阅者，也不得外泄到相机派发线程");
        }

        [TestMethod]
        public void CameraService_StopGrab_DisconnectedButRegistered_ReturnsSuccess()
        {
            var provider = new TrackingProvider();
            var manager = new CameraManager();
            using var streams = new StreamManager();
            var service = new CameraService(provider, manager, streams);

            Assert.IsTrue(service.OpenCamera(new MockCameraInfo { SerialNumber = "SN001" }, "cam1").IsSuccess);
            Assert.IsTrue(provider.CreatedCameras.TryPeek(out var camera));
            camera.RaiseDisconnected();

            var result = service.StopGrab("cam1");

            Assert.IsTrue(result.IsSuccess, "相机已注册但掉线时，门面停流必须交给相机层并返回成功");
            Assert.IsFalse(service.StopGrab("missing-key").IsSuccess, "未注册的 key 仍必须返回失败");
        }

        [TestMethod]
        public void CameraService_OpenCamera_AfterDisconnect_ReconnectsSameKeyWithoutNewCamera()
        {
            var provider = new TrackingProvider();
            var manager = new CameraManager();
            using var streams = new StreamManager();
            var service = new CameraService(provider, manager, streams);

            Assert.IsTrue(service.OpenCamera(new MockCameraInfo { SerialNumber = "SN001" }, "cam1").IsSuccess);
            Assert.IsTrue(provider.CreatedCameras.TryPeek(out var camera));
            camera.RaiseDisconnected();

            var reopened = service.OpenCamera(new MockCameraInfo { SerialNumber = "SN001" }, "cam1");

            Assert.IsTrue(reopened.IsSuccess, "掉线后对同一 key 再次 OpenCamera 即为重连");
            Assert.IsTrue(camera.IsConnected);
            Assert.AreEqual(1, provider.CreatedCameras.Count, "重连复用已注册的相机实例，不新建相机");
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

            public FrameStreamStatistics Statistics => FrameStreamStatistics.Empty;

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

            /// <summary>非空时 Connect 会在此阻塞，用于制造"打开进行中"的窗口</summary>
            public SemaphoreSlim ConnectGate { get; set; }

            /// <summary>非空时 Close 会在此阻塞，用于制造"关闭进行中"的窗口</summary>
            public SemaphoreSlim CloseGate { get; set; }

            public TaskCompletionSource<object> ConnectEntered { get; } = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

            public TaskCompletionSource<object> CloseEntered { get; } = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

            public bool Supports(ICameraInfo info) => true;

            public ICamera Create(ICameraInfo info, ICameraStream stream)
            {
                var camera = new TrackingCamera
                {
                    ConnectResult = this.ConnectResult,
                    CloseResult = this.CloseResult,
                    TryGetParamResult = this.TryGetParamResult,
                    TryGetParamValue = this.TryGetParamValue,
                    ConnectGate = this.ConnectGate,
                    ConnectEntered = this.ConnectEntered,
                    CloseGate = this.CloseGate,
                    CloseEntered = this.CloseEntered
                };
                this.CreatedCameras.Add(camera);
                return camera;
            }

            public IEnumerable<ICameraInfo> Enumerate() => Array.Empty<ICameraInfo>();

            public IEnumerable<ICameraInfo> Enumerate(CameraInterfaceType type) => Array.Empty<ICameraInfo>();
        }

        private sealed class TrackingCamera : ICamera, IConnectionMonitor
        {
            public CameraResult ConnectResult { get; set; } = CameraResult.Success(0);
            public CameraResult CloseResult { get; set; } = CameraResult.Success(0);
            public CameraResult StopGrabResult { get; set; } = CameraResult.Success(0);
            public bool TryGetParamResult { get; set; } = true;
            public int TryGetParamValue { get; set; } = 42;
            public bool IsConnected { get; private set; }
            public bool IsGrabbing { get; private set; }
            public bool IsDisposed { get; private set; }
            public string LastError { get; set; }

            public event EventHandler<CameraDisconnectedEventArgs> Disconnected;

            /// <summary>模拟掉线（与 HikCamera 的 Lost 语义一致：不再连接、不再取流，并同步触发掉线事件）</summary>
            public void RaiseDisconnected()
            {
                this.IsConnected = false;
                this.IsGrabbing = false;
                this.Disconnected?.Invoke(this, new CameraDisconnectedEventArgs("SN001", "Device disconnected (MsgType=DisConnect)", DateTime.UtcNow));
            }

            public SemaphoreSlim ConnectGate { get; set; }
            public TaskCompletionSource<object> ConnectEntered { get; set; }
            public SemaphoreSlim CloseGate { get; set; }
            public TaskCompletionSource<object> CloseEntered { get; set; }

            public CameraResult Connect()
            {
                this.ConnectEntered?.TrySetResult(null);
                this.ConnectGate?.Wait();

                if (this.ConnectResult.IsSuccess)
                    this.IsConnected = true;
                return this.ConnectResult;
            }

            public CameraResult Close()
            {
                this.CloseEntered?.TrySetResult(null);
                this.CloseGate?.Wait();

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

            public CameraResult StopGrab()
            {
                if (this.StopGrabResult.IsSuccess)
                    this.IsGrabbing = false;
                return this.StopGrabResult;
            }
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
