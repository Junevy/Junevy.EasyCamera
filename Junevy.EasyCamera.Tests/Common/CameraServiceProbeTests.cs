using Junevy.EasyCamera.Common;
using Junevy.EasyCamera.Core.Abstractions;
using Junevy.EasyCamera.Core.Common;
using Junevy.EasyCamera.Tests.Mocks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Junevy.EasyCamera.Tests.Common
{
    /// <summary>
    /// 相机链路状态探测测试：自持早退、厂商可达性路由、异常降级、侵入式回退与探测资源清理。
    /// 厂商真实可达性查询依赖硬件，由 HikCameraProvider 覆盖；本文件用桩提供器验证门面策略。
    /// </summary>
    [TestClass]
    public class CameraServiceProbeTests
    {
        [TestMethod]
        public void Probe_SelfHeldCamera_ReturnsConnectedWithoutVendorProbe()
        {
            var provider = new ProbeRecordingProvider { ProbeResult = CameraLinkStatus.Occupied };
            var service = new CameraService(provider, new CameraManager(), new StreamManager());
            var info = new MockCameraInfo { SerialNumber = "SN-001" };
            Assert.IsTrue(service.OpenCamera(info, "cam:SN-001").IsSuccess);

            // 自持早退：即使厂商会报不可达（独占持有的正常表现），也必须返回 Connected
            Assert.AreEqual(CameraLinkStatus.Connected, service.ProbeCameraLinkStatus(info));
            Assert.AreEqual(0, provider.ProbeCalls);
        }

        [TestMethod]
        public void Probe_VendorAccessibilityReportedIdle()
        {
            var provider = new ProbeRecordingProvider { ProbeResult = CameraLinkStatus.Idle };
            var service = new CameraService(provider, new CameraManager(), new StreamManager());
            var info = new MockCameraInfo { SerialNumber = "SN-001" };

            Assert.AreEqual(CameraLinkStatus.Idle, service.ProbeCameraLinkStatus(info));
            Assert.AreEqual(1, provider.ProbeCalls);
        }

        [TestMethod]
        public void Probe_VendorAccessibilityReportedOccupied()
        {
            var provider = new ProbeRecordingProvider { ProbeResult = CameraLinkStatus.Occupied };
            var service = new CameraService(provider, new CameraManager(), new StreamManager());
            var info = new MockCameraInfo { SerialNumber = "SN-001" };

            Assert.AreEqual(CameraLinkStatus.Occupied, service.ProbeCameraLinkStatus(info));
            Assert.AreEqual(1, provider.ProbeCalls);
        }

        [TestMethod]
        public void Probe_VendorReportsUnreachable_DistinctFromOccupied()
        {
            // 掉线/未上电必须与"被其它客户端占用"区分开，否则界面会误导用户
            var provider = new ProbeRecordingProvider { ProbeResult = CameraLinkStatus.Unreachable };
            var service = new CameraService(provider, new CameraManager(), new StreamManager());
            var info = new MockCameraInfo { SerialNumber = "SN-001" };

            Assert.AreEqual(CameraLinkStatus.Unreachable, service.ProbeCameraLinkStatus(info));
        }

        [TestMethod]
        public void Probe_NullInfoOrEmptySerial_ReturnsUnknown()
        {
            var provider = new ProbeRecordingProvider { ProbeResult = CameraLinkStatus.Idle };
            var service = new CameraService(provider, new CameraManager(), new StreamManager());

            Assert.AreEqual(CameraLinkStatus.Unknown, service.ProbeCameraLinkStatus(null));
            Assert.AreEqual(CameraLinkStatus.Unknown, service.ProbeCameraLinkStatus(new MockCameraInfo()));
            Assert.AreEqual(0, provider.ProbeCalls, "无法判定时不得触发厂商探测");
        }

        [TestMethod]
        public void CameraLinkStatus_UnknownIsTheDefaultMember()
        {
            // default 必须落在"未知"上：未初始化变量不得谎报已连接
            Assert.AreEqual(CameraLinkStatus.Unknown, default(CameraLinkStatus));
            Assert.AreEqual(0, (int)CameraLinkStatus.Unknown);
        }

        [TestMethod]
        public void Probe_VendorThrows_FallsBackInsteadOfPropagating()
        {
            // SDK 未初始化等厂商异常不得外泄到界面线程，必须降级为侵入式回退
            var provider = new ProbeRecordingProvider { ProbeThrows = true };
            var service = new CameraService(provider, new CameraManager(), new StreamManager());
            var info = new MockCameraInfo { SerialNumber = "SN-001" };

            Assert.AreEqual(CameraLinkStatus.Idle, service.ProbeCameraLinkStatus(info));
            Assert.AreEqual(1, provider.ProbeCalls);
        }

        [TestMethod]
        public void Probe_VendorWithoutCapability_FallsBackToInvasiveProbeAndCleansUp()
        {
            // MockCameraProvider 未实现 ILinkStatusProbeProvider（无探测能力）→ 走侵入式回退；
            // MockCamera.Connect 恒成功 → Idle，且探测连接用后即关、不留在注册表与帧流表
            var provider = new MockCameraProvider(new List<MockCameraInfo>
            {
                new MockCameraInfo { SerialNumber = "SN-001" }
            });
            var manager = new CameraManager();
            var streams = new StreamManager();
            var service = new CameraService(provider, manager, streams);
            var info = new MockCameraInfo { SerialNumber = "SN-001" };

            Assert.AreEqual(CameraLinkStatus.Idle, service.ProbeCameraLinkStatus(info));

            // 探测连接不残留：注册表为空，序列号也不再命中
            Assert.IsFalse(manager.Snapshot().Any());
            Assert.IsFalse(service.IsSerialConnected("SN-001"));

            // 帧流也不残留：否则每次探测都会在流管理器里永久留下一条流
            Assert.IsFalse(streams.GetStream("probe:SN-001", out _), "侵入式探测用过的帧流必须被清理");

            streams.Dispose();
        }

        [TestMethod]
        public void Probe_FallbackReportsOccupiedWhenOpenFails()
        {
            // Create 抛异常模拟"打开被其它客户端占用"，无厂商探测能力 → 回退报 Occupied
            var service = new CameraService(new FailingCreateProvider(), new CameraManager(), new StreamManager());
            var info = new MockCameraInfo { SerialNumber = "SN-001" };

            Assert.AreEqual(CameraLinkStatus.Occupied, service.ProbeCameraLinkStatus(info));
        }

        [TestMethod]
        public void Probe_Cancelled_ThrowsOperationCanceled()
        {
            var provider = new ProbeRecordingProvider { ProbeResult = CameraLinkStatus.Idle };
            var service = new CameraService(provider, new CameraManager(), new StreamManager());
            using var cts = new System.Threading.CancellationTokenSource();
            cts.Cancel();

            Assert.ThrowsException<OperationCanceledException>(
                () => service.ProbeCameraLinkStatus(new MockCameraInfo { SerialNumber = "SN-001" }, cts.Token));
        }

        [TestMethod]
        public void IsSerialConnected_MatchesRegisteredSerialOnly()
        {
            var service = new CameraService(new SerialReportingProvider(), new CameraManager(), new StreamManager());
            var info = new MockCameraInfo { SerialNumber = "SN-001" };

            Assert.IsFalse(service.IsSerialConnected("SN-001"));

            Assert.IsTrue(service.OpenCamera(info, "cam:SN-001").IsSuccess);
            Assert.IsTrue(service.IsSerialConnected("SN-001"));
            Assert.IsFalse(service.IsSerialConnected("SN-002"));
            Assert.IsFalse(service.IsSerialConnected(string.Empty));
        }

        [TestMethod]
        public void AggregateProvider_WithoutProbeCapability_ReportsUnknown()
        {
            var provider = new Junevy.EasyCamera.Common.AggregateCameraProvider(new IVendorCameraProvider[]
            {
                new FakeVendorProvider("V1", new[] { "SN-001" })
            });

            Assert.IsInstanceOfType(provider, typeof(ILinkStatusProbeProvider));
            Assert.AreEqual(
                CameraLinkStatus.Unknown,
                ((ILinkStatusProbeProvider)provider).ProbeLinkStatus(new FakeCameraInfo { SerialNumber = "SN-001" }));
        }

        [TestMethod]
        public void AggregateProvider_DispatchesProbeToCapableVendor()
        {
            var capable = new ProbeCapableVendorProvider();
            var provider = new Junevy.EasyCamera.Common.AggregateCameraProvider(new IVendorCameraProvider[]
            {
                new FakeVendorProvider("V1", new[] { "SN-001" }),
                capable
            });

            Assert.AreEqual(
                CameraLinkStatus.Idle,
                ((ILinkStatusProbeProvider)provider).ProbeLinkStatus(new ProbeCapableVendorProvider.ProbeInfo()));
            Assert.AreEqual(1, capable.ProbeCalls);
        }

        [TestMethod]
        public void Probe_AggregateWithoutCapability_UnknownFallsBackToInvasiveProbe()
        {
            // 生产环境 provider 恒为 AggregateCameraProvider：它总实现 ILinkStatusProbeProvider，
            // 无厂商具备能力时返回 Unknown。门面必须把 Unknown 视作回退信号，改走侵入式探测，
            // 而不是把 Unknown 当作探测结论返回（否则回退路径永远不可达）。
            var vendor = new FakeVendorProvider("V1", new[] { "SN-001" });
            var aggregate = new Junevy.EasyCamera.Common.AggregateCameraProvider(new IVendorCameraProvider[] { vendor });
            var manager = new CameraManager();
            var streams = new StreamManager();
            var service = new CameraService(aggregate, manager, streams);

            Assert.AreEqual(
                CameraLinkStatus.Idle,
                service.ProbeCameraLinkStatus(new FakeCameraInfo { SerialNumber = "SN-001" }));
            Assert.AreEqual(1, vendor.CreateCount, "侵入式回退必须真正尝试打开一次相机");

            // 探测连接与探测帧流都不得残留
            Assert.IsFalse(manager.TryGet("probe:SN-001", out _), "探测连接不得残留在注册表中");
            Assert.IsFalse(streams.GetStream("probe:SN-001", out _), "探测帧流不得残留");

            streams.Dispose();
        }

        private sealed class ProbeCapableVendorProvider : IVendorCameraProvider, ILinkStatusProbeProvider
        {
            public string VendorName => "ProbeVendor";

            public int ProbeCalls { get; private set; }

            public bool Supports(ICameraInfo info) => info is ProbeInfo;

            public CameraLinkStatus ProbeLinkStatus(ICameraInfo info, System.Threading.CancellationToken cancellationToken = default)
            {
                this.ProbeCalls++;
                return CameraLinkStatus.Idle;
            }

            public ICamera Create(ICameraInfo info, ICameraStream stream) => new MockCamera();

            public IEnumerable<ICameraInfo> Enumerate() => Array.Empty<ICameraInfo>();

            public IEnumerable<ICameraInfo> Enumerate(CameraInterfaceType type) => Array.Empty<ICameraInfo>();

            public sealed class ProbeInfo : ICameraInfo
            {
                public string SerialNumber => "SN-PROBE";
                public string ModelName => "Probe";
                public string UserDefinedName => "Probe";
                public string Manufacturer => "ProbeVendor";
                public string CameraVersion => "1.0";
                public CameraInterfaceType InterfaceType => CameraInterfaceType.GigE;
            }
        }

        private sealed class ProbeRecordingProvider : ICameraProvider, ILinkStatusProbeProvider
        {
            public int ProbeCalls { get; private set; }

            public CameraLinkStatus? ProbeResult { get; set; }

            /// <summary>模拟厂商探测抛出异常（如 SDK 未初始化），验证门面降级而不是外泄</summary>
            public bool ProbeThrows { get; set; }

            public bool Supports(ICameraInfo info) => true;

            public ICamera Create(ICameraInfo info, ICameraStream stream)
                => new MockCamera { SerialNumberToReport = info.SerialNumber };

            public IEnumerable<ICameraInfo> Enumerate() => System.Array.Empty<ICameraInfo>();

            public IEnumerable<ICameraInfo> Enumerate(CameraInterfaceType type) => System.Array.Empty<ICameraInfo>();

            public CameraLinkStatus ProbeLinkStatus(ICameraInfo info, System.Threading.CancellationToken cancellationToken = default)
            {
                this.ProbeCalls++;

                if (this.ProbeThrows)
                    throw new InvalidOperationException("vendor probe failed");

                return this.ProbeResult ?? CameraLinkStatus.Unknown;
            }
        }

        private sealed class SerialReportingProvider : ICameraProvider
        {
            public bool Supports(ICameraInfo info) => true;

            public ICamera Create(ICameraInfo info, ICameraStream stream)
                => new MockCamera { SerialNumberToReport = info.SerialNumber };

            public IEnumerable<ICameraInfo> Enumerate() => System.Array.Empty<ICameraInfo>();

            public IEnumerable<ICameraInfo> Enumerate(CameraInterfaceType type) => System.Array.Empty<ICameraInfo>();
        }

        private sealed class FailingCreateProvider : ICameraProvider
        {
            public bool Supports(ICameraInfo info) => true;

            public ICamera Create(ICameraInfo info, ICameraStream stream)
                => throw new InvalidOperationException("occupied by another client");

            public IEnumerable<ICameraInfo> Enumerate() => System.Array.Empty<ICameraInfo>();

            public IEnumerable<ICameraInfo> Enumerate(CameraInterfaceType type) => System.Array.Empty<ICameraInfo>();
        }
    }
}
