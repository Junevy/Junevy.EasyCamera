using Junevy.EasyCamera.Common;
using Junevy.EasyCamera.Core.Abstractions;
using Junevy.EasyCamera.Tests.Mocks;
using Junevy.EasyCamera.Vendors.HikVision;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MvCameraControl;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Junevy.EasyCamera.Tests.Vendors.HikVision
{
    /// <summary>
    /// 海康相机生命周期状态机回归测试：
    /// 状态只能沿合法路径迁移，任何失败路径都必须回到"可用"状态（不得静默丢帧、不得抛异常穿透）。
    /// </summary>
    [TestClass]
    public class HikCameraStateTests
    {
        [TestMethod]
        public void StateTransitions_FollowConnectGrabStopCloseDispose()
        {
            var device = new FakeDevice();
            var camera = CreateCamera(device, new RecordingStream());

            Assert.IsFalse(camera.IsConnected);
            Assert.IsFalse(camera.IsGrabbing);

            Assert.IsTrue(camera.Connect().IsSuccess);
            Assert.IsTrue(camera.IsConnected);
            Assert.IsFalse(camera.IsGrabbing);

            Assert.IsTrue(camera.StartGrab().IsSuccess);
            Assert.IsTrue(camera.IsGrabbing);

            Assert.IsTrue(camera.StopGrab().IsSuccess, "StopGrab 幂等且必须返回成功");
            Assert.IsFalse(camera.IsGrabbing);

            Assert.IsTrue(camera.StopGrab().IsSuccess, "未取流时 StopGrab 仍返回成功（幂等）");

            Assert.IsTrue(camera.Close().IsSuccess);
            Assert.IsFalse(camera.IsConnected);

            // 关闭后必须能重新打开并取流（Close 不销毁设备句柄，只解除连接）
            Assert.IsTrue(camera.Connect().IsSuccess);
            Assert.IsTrue(camera.StartGrab().IsSuccess, "关闭后可重新打开取流");
            camera.Dispose();
            Assert.IsFalse(camera.IsConnected);
        }

        [TestMethod]
        public void Close_WhenNativeCloseThrows_RestoresUsableStateAndKeepsPublishing()
        {
            // 原生关闭抛异常时若不回滚状态，相机将永久停留在"关闭中"：
            // 连接看起来还在，帧却一帧都不下发（静默丢帧）
            var stream = new RecordingStream();
            var device = new FakeDevice { CloseThrows = true };
            var camera = CreateCamera(device, stream);

            Assert.IsTrue(camera.Connect().IsSuccess);
            Assert.IsTrue(camera.StopGrab().IsSuccess);

            var result = camera.Close();

            Assert.IsFalse(result.IsSuccess, "原生关闭失败必须如实上报");
            Assert.IsTrue(camera.IsConnected, "关闭失败后相机仍应保持已打开状态");
            Assert.IsTrue(camera.StartGrab().IsSuccess, "关闭失败后必须还能重新取流");
            Assert.IsTrue(camera.IsGrabbing);

            stream.Clear();
            device.Stream.Raise(new FakeFrameOut());

            Assert.AreEqual(1, stream.Published.Count, "关闭失败后帧回调必须继续发布，不能静默丢帧");
            camera.Dispose();
        }

        [TestMethod]
        public void Close_WhenStopFails_RestoresGrabbingState()
        {
            var device = new FakeDevice { StopResult = -7 };
            var camera = CreateCamera(device, new RecordingStream());

            Assert.IsTrue(camera.Connect().IsSuccess);
            Assert.IsTrue(camera.StartGrab().IsSuccess);

            var result = camera.Close();

            Assert.IsFalse(result.IsSuccess);
            Assert.IsTrue(camera.IsConnected);
            Assert.IsTrue(camera.IsGrabbing, "停流失败必须保留取流状态");
            camera.Dispose();
        }

        [TestMethod]
        public void ParameterAccess_AfterDispose_ReturnsFailureWithoutThrowing()
        {
            // 参数路径此前完全在互斥之外：与 Dispose 并发时会抛 NullReferenceException 穿透到调用方
            var device = new FakeDevice();
            var camera = CreateCamera(device, new RecordingStream());

            Assert.IsTrue(camera.Connect().IsSuccess);
            camera.Dispose();

            var setResult = camera.SetParam("ExposureTime", 5000f);
            Assert.IsFalse(setResult.IsSuccess, "释放后写参数必须走 CameraResult 失败通道，而不是抛异常");

            Assert.IsFalse(camera.SetEnumParam("TriggerMode", "On").IsSuccess);
            Assert.IsFalse(camera.ExecuteCommand("DeviceReset").IsSuccess);
            Assert.IsFalse(camera.TryGetParam<int>("Width", out _));
            Assert.IsFalse(camera.TryGetEnumParam("TriggerMode", out _));
            Assert.AreEqual(0, camera.GetParam<int>("Width"));
        }

        [TestMethod]
        public void ParameterAccess_WhenConnectedButVendorHasNoParameterNode_ReturnsFailure()
        {
            var device = new FakeDevice();
            var camera = CreateCamera(device, new RecordingStream());

            Assert.IsTrue(camera.Connect().IsSuccess);

            // FakeDevice.Parameters 为 null：不得报成功假象
            Assert.IsFalse(camera.SetParam("ExposureTime", 1).IsSuccess);

            camera.Dispose();
        }

        [TestMethod]
        public void Operations_AfterDispose_FailFast()
        {
            var camera = CreateCamera(new FakeDevice(), new RecordingStream());

            Assert.IsTrue(camera.Connect().IsSuccess);
            camera.Dispose();
            camera.Dispose();

            Assert.IsFalse(camera.Connect().IsSuccess);
            Assert.IsFalse(camera.Close().IsSuccess);
            Assert.IsFalse(camera.StartGrab().IsSuccess);
            Assert.IsTrue(camera.StopGrab().IsSuccess, "释放后停流仍是幂等成功");
        }

        [TestMethod]
        public void Callback_AfterDispose_DoesNotPublish()
        {
            var stream = new RecordingStream();
            var device = new FakeDevice();
            var camera = CreateCamera(device, stream);

            Assert.IsTrue(camera.Connect().IsSuccess);
            Assert.IsTrue(camera.StartGrab().IsSuccess);

            device.Stream.Raise(new FakeFrameOut());
            Assert.AreEqual(1, stream.Published.Count);

            camera.Dispose();
            device.Stream.Raise(new FakeFrameOut());

            Assert.AreEqual(1, stream.Published.Count, "释放后回调不得再发布帧");
        }

        [TestMethod]
        public void Camera_ImplementsOptionalCapabilityInterfaces()
        {
            var camera = CreateCamera(new FakeDevice(), new RecordingStream());

            Assert.IsInstanceOfType(camera, typeof(IParameterSource));
            Assert.IsInstanceOfType(camera, typeof(IBufferConfigurable));
        }

        [TestMethod]
        public void IsConnected_IsPureStateRead_NeverCallsNativeDevice()
        {
            // 公开属性可能被任意线程（含 UI 线程）读取：只能读状态，不得触达原生句柄
            var device = new FakeDevice();
            var camera = CreateCamera(device, new RecordingStream());

            Assert.IsTrue(camera.Connect().IsSuccess);

            var before = device.IsConnectedCalls;
            for (var i = 0; i < 10; i++)
                Assert.IsTrue(camera.IsConnected);

            Assert.AreEqual(before, device.IsConnectedCalls, "公开 IsConnected 只读状态，不得调用原生 IsConnected");
            camera.Dispose();
        }

        [TestMethod]
        public void IsConnectedAndParamAccess_RacingDispose_NeverThrow()
        {
            // 旧实现的 IsConnected 在门外读取 device 并调用原生 IsConnected：
            // 与 DisposeCore 的置空并发时会抛 NullReferenceException。
            var errors = new ConcurrentQueue<Exception>();

            for (var round = 0; round < 200; round++)
            {
                var camera = CreateCamera(new FakeDevice(), new RecordingStream());
                Assert.IsTrue(camera.Connect().IsSuccess);

                using var start = new ManualResetEventSlim(false);

                var reader = Task.Run(() =>
                {
                    start.Wait();
                    try
                    {
                        for (var i = 0; i < 50; i++)
                        {
                            _ = camera.IsConnected;
                            camera.SetParam("ExposureTime", 1f);
                        }
                    }
                    catch (Exception e)
                    {
                        errors.Enqueue(e);
                    }
                });

                var disposer = Task.Run(() =>
                {
                    start.Wait();
                    try
                    {
                        camera.Dispose();
                    }
                    catch (Exception e)
                    {
                        errors.Enqueue(e);
                    }
                });

                start.Set();
                Assert.IsTrue(Task.WaitAll(new[] { reader, disposer }, TimeSpan.FromSeconds(5)), "并发访问不得挂起");
            }

            Assert.AreEqual(0, errors.Count, "IsConnected/参数访问与 Dispose 并发时不得抛出异常");
        }

        [TestMethod]
        public void Close_WhenAlreadyClosed_IsIdempotentWithoutNativeClose()
        {
            // 重复 Close 若再调用原生 Close，大概率失败并进入回滚，使 CameraManager.Remove 误报 ReleaseFailed
            var device = new FakeDevice();
            var camera = CreateCamera(device, new RecordingStream());

            Assert.IsTrue(camera.Connect().IsSuccess);
            Assert.IsTrue(camera.Close().IsSuccess);
            Assert.AreEqual(1, device.CloseCalls);

            var second = camera.Close();

            Assert.IsTrue(second.IsSuccess, "已关闭状态下再次 Close 必须幂等成功");
            Assert.AreEqual(1, device.CloseCalls, "已关闭状态下不得再次调用原生 Close");
            Assert.IsFalse(camera.IsConnected);
            camera.Dispose();
        }

        [TestMethod]
        public void Close_WhenNeverConnected_IsIdempotentSuccess()
        {
            // ICamera.Close 幂等：从未打开过的相机也返回成功。CameraManager.DisposeCamera 会对 Connect 失败的候选先调用 Close，
            // 若此处报失败会制造假的 ReleaseFailed；且关闭不得为此创建原生句柄
            var factoryCalls = 0;
            var camera = new HikCamera(new FakeDeviceInfo { SerialNumber = "HIK-STATE" }, new RecordingStream(), _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                return new FakeDevice();
            });

            var result = camera.Close();

            Assert.IsTrue(result.IsSuccess, "从未 Connect 过的相机 Close 必须幂等成功");
            Assert.AreEqual(0, Volatile.Read(ref factoryCalls), "关闭不得为此创建原生句柄（无原生 Close 调用）");
            Assert.IsNull(camera.LastError);
        }

        [TestMethod]
        public void Disconnect_WhileGrabbing_MarksLostAndNotifiesOnce()
        {
            // SDK 线程上的掉线：状态必须立即不可用，通知在 2 秒内派发，连续两次掉线只通知一次
            var device = new FakeDevice();
            var camera = CreateCamera(device, new RecordingStream());
            var notifications = new ConcurrentQueue<CameraDisconnectedEventArgs>();
            using var notified = new ManualResetEventSlim(false);
            camera.Disconnected += (_, e) =>
            {
                notifications.Enqueue(e);
                notified.Set();
            };

            Assert.IsTrue(camera.Connect().IsSuccess);
            Assert.IsTrue(camera.StartGrab().IsSuccess);

            device.RaiseDeviceException(CreateDisconnectArgs());
            device.RaiseDeviceException(CreateDisconnectArgs());

            Assert.IsFalse(camera.IsConnected);
            Assert.IsFalse(camera.IsGrabbing);
            StringAssert.Contains(camera.LastError ?? string.Empty, "disconnected");
            Assert.IsFalse(camera.StartGrab().IsSuccess, "掉线后不得再取流");

            Assert.IsTrue(notified.Wait(TimeSpan.FromSeconds(2)), "掉线通知必须在 2 秒内派发");
            Assert.IsTrue(notifications.TryPeek(out var first));
            Assert.IsNull(first.CameraKey, "相机层通知不携带门面 Key");
            Assert.AreEqual("HIK-STATE", first.SerialNumber);
            Assert.AreEqual(1, notifications.Count, "同一次连接最多通知一次（第二次掉线被忽略）");
            camera.Dispose();
        }

        [TestMethod]
        public void Disconnect_WhileGrabbing_FrameCallbackStopsPublishingButReturnsBuffer()
        {
            var stream = new RecordingStream();
            var device = new FakeDevice();
            var camera = CreateCamera(device, stream);

            Assert.IsTrue(camera.Connect().IsSuccess);
            Assert.IsTrue(camera.StartGrab().IsSuccess);

            device.RaiseDeviceException(CreateDisconnectArgs());
            device.Stream.Raise(new FakeFrameOut());

            Assert.AreEqual(0, stream.Published.Count, "掉线后帧回调不得再发布帧");
            Assert.AreEqual(1, device.Stream.FreeCalls, "掉线后到达的帧仍必须归还 SDK 缓冲区（不泄漏）");
            camera.Dispose();
        }

        [TestMethod]
        public void StopGrab_AfterDisconnect_ReturnsSuccessAndAttemptsNativeStop()
        {
            var device = new FakeDevice();
            var camera = CreateCamera(device, new RecordingStream());

            Assert.IsTrue(camera.Connect().IsSuccess);
            Assert.IsTrue(camera.StartGrab().IsSuccess);
            device.RaiseDeviceException(CreateDisconnectArgs());

            var result = camera.StopGrab();

            Assert.IsTrue(result.IsSuccess, "掉线后停流仍是幂等成功");
            Assert.AreEqual(1, device.Stream.StopCalls, "掉线后仍应尽力通知 SDK 停止取流");
            StringAssert.Contains(camera.LastError ?? string.Empty, "disconnected", "掉线原因须保留到 Close 或 Connect 完成释放");
            camera.Dispose();
        }

        [TestMethod]
        public void Close_AfterDisconnect_ReleasesDeviceAndReturnsSuccess()
        {
            var device = new FakeDevice();
            var camera = CreateCamera(device, new RecordingStream());

            Assert.IsTrue(camera.Connect().IsSuccess);
            Assert.IsTrue(camera.StartGrab().IsSuccess);
            device.RaiseDeviceException(CreateDisconnectArgs());

            var result = camera.Close();

            Assert.IsTrue(result.IsSuccess, "掉线后 Close 必须成功（释放句柄），而不是停流失败后回滚");
            Assert.AreEqual(1, device.DisposeCalls, "掉线句柄必须被销毁");
            Assert.IsFalse(camera.IsConnected);
            Assert.IsNull(camera.LastError, "成功释放后诊断信息清空");
            camera.Dispose();
        }

        [TestMethod]
        public void Connect_AfterDisconnect_RebuildsDeviceHandleAndGrabs()
        {
            var devices = new List<FakeDevice>();
            var stream = new RecordingStream();
            var camera = new HikCamera(new FakeDeviceInfo { SerialNumber = "HIK-STATE" }, stream, _ =>
            {
                var created = new FakeDevice();
                devices.Add(created);
                return created;
            });

            Assert.IsTrue(camera.Connect().IsSuccess);
            Assert.IsTrue(camera.StartGrab().IsSuccess);
            devices[0].RaiseDeviceException(CreateDisconnectArgs());

            Assert.IsTrue(camera.Connect().IsSuccess, "掉线后可直接重连");
            Assert.AreEqual(2, devices.Count, "旧句柄不可复用，必须由 deviceFactory 重建");
            Assert.AreEqual(1, devices[0].DisposeCalls, "旧句柄已被销毁");
            Assert.IsTrue(camera.IsConnected);
            Assert.IsTrue(camera.StartGrab().IsSuccess, "重连后可重新取流");

            devices[1].Stream.Raise(new FakeFrameOut());
            Assert.AreEqual(1, stream.Published.Count, "重连后帧必须正常发布");
            camera.Dispose();
        }

        [TestMethod]
        public void DeviceException_AfterCloseOrDispose_RaisesNoNotification()
        {
            var device = new FakeDevice();
            var camera = CreateCamera(device, new RecordingStream());
            var notifications = 0;
            camera.Disconnected += (_, _) => Interlocked.Increment(ref notifications);

            Assert.IsTrue(camera.Connect().IsSuccess);
            Assert.IsTrue(camera.Close().IsSuccess);
            device.RaiseDeviceException(CreateDisconnectArgs());

            Assert.IsTrue(camera.Connect().IsSuccess);
            camera.Dispose();
            device.RaiseDeviceException(CreateDisconnectArgs());

            // 负向断言：正确实现下从不排队派发，因此无需等待即可断言为 0
            Assert.AreEqual(0, Volatile.Read(ref notifications), "关闭或释放后的设备异常不得产生通知");
        }

        [TestMethod]
        public void Disconnected_HandlerException_DoesNotBreakCloseOrReconnect()
        {
            var device = new FakeDevice();
            var camera = CreateCamera(device, new RecordingStream());
            using var handlerRan = new ManualResetEventSlim(false);
            camera.Disconnected += (_, _) =>
            {
                handlerRan.Set();
                throw new InvalidOperationException("subscriber failure");
            };

            Assert.IsTrue(camera.Connect().IsSuccess);
            Assert.IsTrue(camera.StartGrab().IsSuccess);
            device.RaiseDeviceException(CreateDisconnectArgs());

            Assert.IsTrue(handlerRan.Wait(TimeSpan.FromSeconds(2)), "处理程序应在 2 秒内被调用");
            Assert.IsTrue(camera.Close().IsSuccess, "处理程序异常不得影响后续 Close");
            Assert.IsTrue(camera.Connect().IsSuccess, "处理程序异常不得影响后续 Connect");
            Assert.IsTrue(camera.IsConnected);
            camera.Dispose();
        }

        [TestMethod]
        public async Task DeviceException_WhileDisposeWaitsForInFlightCallback_DoesNotDeadlock()
        {
            // 回归守卫：SDK 线程上的掉线处理绝不能进入 operationGate。
            // 此处 Dispose 持门并等待在途回调；若掉线处理试图取门，两者将互相等待。
            var stream = new BlockingStream();
            var device = new FakeDevice();
            var camera = CreateCamera(device, stream);

            Assert.IsTrue(camera.Connect().IsSuccess);
            Assert.IsTrue(camera.StartGrab().IsSuccess);

            var callback = Task.Run(() => device.Stream.Raise(new FakeFrameOut()));
            Assert.IsTrue(stream.Entered.Task.Wait(TimeSpan.FromSeconds(2)), "回调必须已进入 Publish（在途）");

            var disposeTask = Task.Run(camera.Dispose);
            await Task.Delay(100);
            Assert.IsFalse(disposeTask.IsCompleted, "Dispose 必须等待在途回调");

            var raiseTask = Task.Run(() => device.RaiseDeviceException(CreateDisconnectArgs()));
            Assert.IsTrue(raiseTask.Wait(TimeSpan.FromSeconds(2)), "掉线回调不得等待 operationGate（否则与 Dispose 死锁）");

            stream.Release.SetResult(null);
            Assert.IsTrue(Task.WaitAll(new Task[] { callback, disposeTask }, TimeSpan.FromSeconds(5)), "释放回调后 Dispose 必须完成");
        }

        [TestMethod]
        public void Close_AfterDisconnect_CalledTwice_BothSucceedAndReleaseOnce()
        {
            var device = new FakeDevice();
            var camera = CreateCamera(device, new RecordingStream());

            Assert.IsTrue(camera.Connect().IsSuccess);
            Assert.IsTrue(camera.StartGrab().IsSuccess);
            device.RaiseDeviceException(CreateDisconnectArgs());

            Assert.IsTrue(camera.Close().IsSuccess, "掉线后首次 Close 必须成功");
            Assert.IsTrue(camera.Close().IsSuccess, "释放后再次 Close 必须幂等成功");
            Assert.AreEqual(1, device.DisposeCalls, "句柄只释放一次");
            Assert.AreEqual(1, device.CloseCalls, "第二次 Close 不得再触达原生层");
            Assert.IsFalse(camera.IsConnected);
            camera.Dispose();
        }

        [TestMethod]
        public void Facade_ReconnectFailsWhenFactoryThrows_CloseByKeyStillRemovesCamera()
        {
            // 掉线后重连时设备已不可枚举（工厂抛异常）：Connect 失败，但门面 Close(key) 必须成功移除该相机，不得报 ReleaseFailed
            var device = new FakeDevice();
            var factoryCalls = 0;
            var provider = new SingleHikCameraProvider(_ =>
            {
                if (Interlocked.Increment(ref factoryCalls) > 1)
                    throw new InvalidOperationException("device is no longer enumerable");

                return device;
            });
            var manager = new CameraManager();
            using var streams = new StreamManager();
            var service = new CameraService(provider, manager, streams);

            Assert.IsTrue(service.OpenCamera(new MockCameraInfo { SerialNumber = "HIK-STATE" }, "cam1").IsSuccess);
            Assert.IsTrue(service.StartGrab("cam1").IsSuccess);
            device.RaiseDeviceException(CreateDisconnectArgs());

            Assert.IsFalse(service.OpenCamera(new MockCameraInfo { SerialNumber = "HIK-STATE" }, "cam1").IsSuccess, "工厂抛异常时重连必须失败");

            var close = service.Close("cam1");

            Assert.IsTrue(close.IsSuccess, "重连失败后门面 Close(key) 必须成功移除相机（不得报 ReleaseFailed）");
            Assert.IsFalse(manager.TryGet("cam1", out _));
        }

        [TestMethod]
        public void Close_WhenDisconnectedDuringStopGrab_ReleasesDeviceAndReturnsSuccess()
        {
            // 关闭过程中（停流期间）掉线并返回失败：不得回滚为"已连接"的假象，应释放设备并返回成功，且不派发掉线通知
            var devices = new List<FakeDevice>();
            var camera = CreateCameraWithFactory(devices, new RecordingStream());
            var notifications = 0;
            camera.Disconnected += (_, _) => Interlocked.Increment(ref notifications);

            Assert.IsTrue(camera.Connect().IsSuccess);
            Assert.IsTrue(camera.StartGrab().IsSuccess);

            var device = devices[0];
            device.Stream.StopResult = -7;
            device.Stream.StopHook = () => device.RaiseDeviceException(CreateDisconnectArgs());

            var result = camera.Close();

            Assert.IsTrue(result.IsSuccess, "关闭期间掉线必须按释放成功处理");
            Assert.IsFalse(camera.IsConnected);
            Assert.IsFalse(camera.IsGrabbing);
            Assert.AreEqual(1, device.DisposeCalls, "掉线句柄必须被释放");
            Assert.AreEqual(0, Volatile.Read(ref notifications), "关闭期间的掉线不派发 Disconnected");

            Assert.IsTrue(camera.Connect().IsSuccess, "之后必须能重建句柄重新连接");
            Assert.AreEqual(2, devices.Count, "句柄由 deviceFactory 重建");
            camera.Dispose();
        }

        [TestMethod]
        public void Close_WhenDisconnectedDuringNativeClose_ReleasesDeviceAndReturnsSuccess()
        {
            // 原生关闭期间掉线并返回失败：关闭期间的 Lost 不得被回滚覆盖，应走释放路径并返回成功，之后可重新连接
            var devices = new List<FakeDevice>();
            var camera = CreateCameraWithFactory(devices, new RecordingStream());
            var notifications = 0;
            camera.Disconnected += (_, _) => Interlocked.Increment(ref notifications);

            Assert.IsTrue(camera.Connect().IsSuccess);
            Assert.IsTrue(camera.StartGrab().IsSuccess);

            var device = devices[0];
            device.CloseResult = -9;
            device.CloseHook = () => device.RaiseDeviceException(CreateDisconnectArgs());

            var result = camera.Close();

            Assert.IsTrue(result.IsSuccess, "关闭期间掉线必须按释放成功处理");
            Assert.IsFalse(camera.IsConnected);
            Assert.AreEqual(1, device.DisposeCalls, "掉线句柄必须被释放");
            Assert.AreEqual(0, Volatile.Read(ref notifications), "关闭期间的掉线不派发 Disconnected");

            Assert.IsTrue(camera.Connect().IsSuccess, "之后必须能重建句柄重新连接");
            Assert.AreEqual(2, devices.Count, "句柄由 deviceFactory 重建");
            camera.Dispose();
        }

        /// <summary>每次工厂调用创建新的 FakeDevice 并记录，便于断言句柄是否被重建</summary>
        private static HikCamera CreateCameraWithFactory(List<FakeDevice> devices, ICameraStream stream)
            => new HikCamera(new FakeDeviceInfo { SerialNumber = "HIK-STATE" }, stream, _ =>
            {
                var created = new FakeDevice();
                devices.Add(created);
                return created;
            });

        private static HikCamera CreateCamera(FakeDevice device, ICameraStream stream)
            => new HikCamera(new FakeDeviceInfo { SerialNumber = "HIK-STATE" }, stream, _ => device);

        /// <summary>
        /// 构造 SDK 的 DisConnect 设备异常参数。SDK（4.5.0.2）中 <c>DeviceExceptionArgs(DeviceExceptionType)</c>
        /// 为 internal，因此经反射调用 SDK 自身的构造函数，得到与运行时一致的真实对象。
        /// </summary>
        private static DeviceExceptionArgs CreateDisconnectArgs()
            => (DeviceExceptionArgs)Activator.CreateInstance(
                typeof(DeviceExceptionArgs),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new object[] { DeviceExceptionType.DisConnect },
                null);

        /// <summary>门面测试用提供器：每次 Create 都包装为一台使用指定设备工厂的 HikCamera</summary>
        private sealed class SingleHikCameraProvider : ICameraProvider
        {
            private readonly Func<IDeviceInfo, IDevice> deviceFactory;

            public SingleHikCameraProvider(Func<IDeviceInfo, IDevice> deviceFactory)
            {
                this.deviceFactory = deviceFactory;
            }

            public bool Supports(ICameraInfo info) => true;

            public ICamera Create(ICameraInfo info, ICameraStream stream)
                => new HikCamera(new FakeDeviceInfo { SerialNumber = info.SerialNumber }, stream, this.deviceFactory);

            public IEnumerable<ICameraInfo> Enumerate() => Array.Empty<ICameraInfo>();

            public IEnumerable<ICameraInfo> Enumerate(CameraInterfaceType type) => Array.Empty<ICameraInfo>();
        }

        /// <summary>发布帧时阻塞，直到测试显式放行：用于制造"回调在途"的窗口</summary>
        private sealed class BlockingStream : ICameraStream
        {
            public TaskCompletionSource<object> Entered { get; } = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

            public TaskCompletionSource<object> Release { get; } = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

            public int SubscriberCount => 0;

            public FrameStreamStatistics Statistics => FrameStreamStatistics.Empty;

            public void Dispose()
            {
            }

            public void Publish(IFrame frame)
            {
                this.Entered.TrySetResult(null);
                this.Release.Task.GetAwaiter().GetResult();
                frame.Dispose();
            }

            public void Subscribe(string subscriberKey, int capacity, Func<string, IFrame, Task> handler, Action<Exception> whenException = null)
            {
            }

            public bool Unsubscribe(string subscriberKey) => false;
        }

        private sealed class RecordingStream : ICameraStream
        {
            public List<IFrame> Published { get; } = new List<IFrame>();

            public int SubscriberCount => 0;

            public FrameStreamStatistics Statistics => FrameStreamStatistics.Empty;

            public void Clear() => this.Published.Clear();

            public void Dispose()
            {
            }

            public void Publish(IFrame frame)
            {
                lock (this.Published)
                    this.Published.Add(frame);

                frame.Dispose();
            }

            public void Subscribe(string subscriberKey, int capacity, Func<string, IFrame, Task> handler, Action<Exception> whenException = null)
            {
            }

            public bool Unsubscribe(string subscriberKey) => false;
        }

        private sealed class FakeDeviceInfo : IDeviceInfo
        {
            public DeviceTLayerType TLayerType { get; set; } = DeviceTLayerType.MvGigEDevice;
            public string ManufacturerName => "HikVision";
            public string ModelName => "Fake";
            public string DeviceVersion => "1.0";
            public string SerialNumber { get; set; }
            public string UserDefinedName => "Fake";
            public uint DevTypeInfo => 0;
        }

        private sealed class FakeDevice : IDevice
        {
            private volatile bool opened;
            private int isConnectedCalls;
            private int closeCalls;
            private int disposeCalls;

            public FakeStreamGrabber Stream { get; } = new FakeStreamGrabber();
            public IDeviceInfo DeviceInfo { get; } = new FakeDeviceInfo();
            public bool CloseThrows { get; set; }

            /// <summary>原生 Close 的返回码：非 MV_OK 时设备保持打开（模拟关闭失败）</summary>
            public int CloseResult { get; set; } = MvError.MV_OK;

            /// <summary>一次性钩子：下一次原生 Close 期间执行（用于模拟关闭过程中掉线）</summary>
            public Action CloseHook { get; set; }

            /// <summary>原生 IsConnected 被调用的次数：用于证明公开属性不做原生调用</summary>
            public int IsConnectedCalls => Volatile.Read(ref this.isConnectedCalls);

            /// <summary>原生 Close 被调用的次数：用于证明已关闭状态下 Close 不再触达原生层</summary>
            public int CloseCalls => Volatile.Read(ref this.closeCalls);

            /// <summary>原生句柄被销毁（Dispose）的次数：用于证明掉线后句柄被释放</summary>
            public int DisposeCalls => Volatile.Read(ref this.disposeCalls);

            public bool IsConnected
            {
                get
                {
                    Interlocked.Increment(ref this.isConnectedCalls);
                    return this.opened;
                }
            }

            public int StopResult
            {
                get => this.Stream.StopResult;
                set => this.Stream.StopResult = value;
            }

            public IStreamGrabber StreamGrabber => this.Stream;
            public IEventGrabber EventGrabber => null;

            // 故意为 null：验证"已连接但厂商未暴露参数节点"必须报失败而不是成功
            public IParameters Parameters => null;
            public IPixelTypeConverter PixelTypeConverter => null;
            public IImageProcess ImageProcess => null;
            public IImageSaver ImageSaver => null;
            public IImageDecoder ImageDecoder => null;
            public IVideoRecorder VideoRecorder => null;
            public IImageRender ImageRender => null;
            public event EventHandler<DeviceExceptionArgs> DeviceExceptionEvent;

            public int Open()
            {
                this.opened = true;
                return MvError.MV_OK;
            }

            public int Open(DeviceAccessMode accessMode, uint switchoverKey) => this.Open();

            public int Close()
            {
                Interlocked.Increment(ref this.closeCalls);

                var hook = this.CloseHook;
                this.CloseHook = null;
                hook?.Invoke();

                if (this.CloseThrows)
                    throw new InvalidOperationException("native close failed");

                if (this.CloseResult != MvError.MV_OK)
                    return this.CloseResult;

                this.opened = false;
                return MvError.MV_OK;
            }

            public int LocalUpgrade(string fileName) => MvError.MV_OK;
            public int GetUpgradeProcess(out uint process) { process = 0; return MvError.MV_OK; }
            public int EventNotificationOn(string eventName) => MvError.MV_OK;
            public int EventNotificationOff(string eventName) => MvError.MV_OK;
            public void Dispose()
            {
                Interlocked.Increment(ref this.disposeCalls);
                this.opened = false;
            }
            public void RaiseDeviceException(DeviceExceptionArgs args) => this.DeviceExceptionEvent?.Invoke(this, args);
        }

        private sealed class FakeStreamGrabber : IStreamGrabber
        {
            private int stopCalls;
            private int freeCalls;

            public int StopResult { get; set; } = MvError.MV_OK;

            /// <summary>原生 StopGrabbing 被调用的次数（掉线后的尽力停流也计入）</summary>
            public int StopCalls => Volatile.Read(ref this.stopCalls);

            /// <summary>归还 SDK 缓冲区（FreeImageBuffer）的次数：用于证明掉线后到达的帧仍被归还</summary>
            public int FreeCalls => Volatile.Read(ref this.freeCalls);

            public event EventHandler<StreamExceptionEventArgs> StreamExceptionEvent;
            public event EventHandler<FrameGrabbedEventArgs> FrameGrabedEvent;

            public int SetImageNodeNum(uint nodeNum) => MvError.MV_OK;
            public int GetValidImageNum(out uint imageNum) { imageNum = 0; return MvError.MV_OK; }
            public int StartGrabbing() => MvError.MV_OK;
            public int StartGrabbing(StreamGrabStrategy strategy) => MvError.MV_OK;
            public int SetOutputQueueSize(uint queueSize) => MvError.MV_OK;

            /// <summary>一次性钩子：下一次原生 StopGrabbing 期间执行（用于模拟停流过程中掉线）</summary>
            public Action StopHook { get; set; }

            public int StopGrabbing()
            {
                Interlocked.Increment(ref this.stopCalls);

                var hook = this.StopHook;
                this.StopHook = null;
                hook?.Invoke();

                return this.StopResult;
            }

            public int GetImageBuffer(uint timeout, out IFrameOut frameOut) { frameOut = null; return MvError.MV_OK; }

            public int FreeImageBuffer(IFrameOut frameOut)
            {
                Interlocked.Increment(ref this.freeCalls);
                return MvError.MV_OK;
            }

            public int ClearImageBuffer() => MvError.MV_OK;

            public void Raise(IFrameOut frameOut)
            {
                var args = (FrameGrabbedEventArgs)FormatterServices.GetUninitializedObject(typeof(FrameGrabbedEventArgs));
                typeof(FrameGrabbedEventArgs).GetField("_frame", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(args, frameOut);
                this.FrameGrabedEvent?.Invoke(this, args);
            }
        }

        private sealed class FakeFrameOut : IFrameOut
        {
            public IImage Image { get; } = new FakeImage();
            public uint FrameNum => 1;
            public ulong DevTimeStamp => 0;
            public ulong HostTimeStamp => 0;
            public ulong FrameLen => 1;
            public uint SecondCount => 0;
            public uint CycleCount => 0;
            public uint CycleOffset => 0;
            public float Gain => 0;
            public float ExposureTime => 0;
            public uint AverageBrightness => 0;
            public uint Red => 0;
            public uint Green => 0;
            public uint Blue => 0;
            public uint FrameCount => 1;
            public uint TriggerIndex => 0;
            public uint Input => 0;
            public uint Output => 0;
            public uint OffsetX => 0;
            public uint OffsetY => 0;
            public uint LostPacket => 0;
            public IChunkInfo ChunkInfo => null;
            public bool Disposed { get; private set; }

            public object Clone() => new FakeFrameOut();

            public void Dispose() => this.Disposed = true;
        }

        private sealed class FakeImage : IImage
        {
            public IntPtr PixelDataPtr => IntPtr.Zero;
            public byte[] PixelData => new byte[1];
            public uint Width => 1;
            public uint Height => 1;
            public MvGvspPixelType PixelType => MvGvspPixelType.PixelType_Gvsp_Mono8;
            public ulong ImageSize => 1;

            public object Clone() => new FakeImage();
            public Bitmap ToBitmap() => null;
            public void Dispose()
            {
            }
        }
    }
}
