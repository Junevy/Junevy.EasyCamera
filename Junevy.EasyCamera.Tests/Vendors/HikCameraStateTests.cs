using Junevy.EasyCamera.Core.Abstractions;
using Junevy.EasyCamera.Tests.Mocks;
using Junevy.EasyCamera.Vendors.HikVision;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MvCameraControl;
using System;
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

        private static HikCamera CreateCamera(FakeDevice device, ICameraStream stream)
            => new HikCamera(new FakeDeviceInfo { SerialNumber = "HIK-STATE" }, stream, _ => device);

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
            public FakeStreamGrabber Stream { get; } = new FakeStreamGrabber();
            public IDeviceInfo DeviceInfo { get; } = new FakeDeviceInfo();
            public bool CloseThrows { get; set; }
            public bool IsConnected { get; private set; }

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
                this.IsConnected = true;
                return MvError.MV_OK;
            }

            public int Open(DeviceAccessMode accessMode, uint switchoverKey) => this.Open();

            public int Close()
            {
                if (this.CloseThrows)
                    throw new InvalidOperationException("native close failed");

                this.IsConnected = false;
                return MvError.MV_OK;
            }

            public int LocalUpgrade(string fileName) => MvError.MV_OK;
            public int GetUpgradeProcess(out uint process) { process = 0; return MvError.MV_OK; }
            public int EventNotificationOn(string eventName) => MvError.MV_OK;
            public int EventNotificationOff(string eventName) => MvError.MV_OK;
            public void Dispose() => this.IsConnected = false;
            public void RaiseDeviceException(DeviceExceptionArgs args) => this.DeviceExceptionEvent?.Invoke(this, args);
        }

        private sealed class FakeStreamGrabber : IStreamGrabber
        {
            public int StopResult { get; set; } = MvError.MV_OK;
            public event EventHandler<StreamExceptionEventArgs> StreamExceptionEvent;
            public event EventHandler<FrameGrabbedEventArgs> FrameGrabedEvent;

            public int SetImageNodeNum(uint nodeNum) => MvError.MV_OK;
            public int GetValidImageNum(out uint imageNum) { imageNum = 0; return MvError.MV_OK; }
            public int StartGrabbing() => MvError.MV_OK;
            public int StartGrabbing(StreamGrabStrategy strategy) => MvError.MV_OK;
            public int SetOutputQueueSize(uint queueSize) => MvError.MV_OK;
            public int StopGrabbing() => this.StopResult;
            public int GetImageBuffer(uint timeout, out IFrameOut frameOut) { frameOut = null; return MvError.MV_OK; }
            public int FreeImageBuffer(IFrameOut frameOut) => MvError.MV_OK;
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
