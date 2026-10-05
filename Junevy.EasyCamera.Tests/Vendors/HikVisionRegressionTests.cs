using Junevy.EasyCamera.Core.Abstractions;
using Junevy.EasyCamera.Vendors.HikVision;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MvCameraControl;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Junevy.EasyCamera.Tests.Vendors.HikVision
{
    [TestClass]
    public class HikVisionRegressionTests
    {
        [TestMethod]
        public void HikCamera_StopFailure_PreservesGrabbingStateAndDiagnostic()
        {
            var device = new FakeDevice { StopResult = -7 };
            var camera = CreateCamera(device);

            Assert.IsTrue(camera.Connect().IsSuccess);
            Assert.IsTrue(camera.StartGrab().IsSuccess);

            camera.StopGrab();

            Assert.IsTrue(camera.IsGrabbing);
            StringAssert.Contains(camera.LastError ?? string.Empty, "-7");

            camera.Dispose();
        }

        [TestMethod]
        public void HikCamera_Close_WhenStopFails_DoesNotReportSuccessOrCloseDevice()
        {
            var device = new FakeDevice { StopResult = -7 };
            var camera = CreateCamera(device);

            Assert.IsTrue(camera.Connect().IsSuccess);
            Assert.IsTrue(camera.StartGrab().IsSuccess);

            var result = camera.Close();

            Assert.IsFalse(result.IsSuccess);
            Assert.IsTrue(camera.IsConnected);
            Assert.AreEqual(0, device.CloseCalls);

            device.StopResult = MvError.MV_OK;
            camera.Dispose();
        }

        [TestMethod]
        public async Task HikCamera_Dispose_WaitsForInFlightCallbackAndReturnsBuffer()
        {
            var device = new FakeDevice();
            var stream = new BlockingStream();
            var camera = CreateCamera(device, stream);

            Assert.IsTrue(camera.Connect().IsSuccess);
            var callback = Task.Run(() => device.Stream.Raise(new FakeFrameOut()));
            await stream.Entered.Task;

            var disposeTask = Task.Run(camera.Dispose);
            await Task.Delay(100);

            Assert.IsFalse(disposeTask.IsCompleted, "Dispose 必须等待在途回调完成");
            Assert.AreEqual(0, device.Stream.FreeCalls);

            stream.Release.SetResult(null);
            await callback;
            await disposeTask;

            Assert.AreEqual(1, device.Stream.FreeCalls);
            Assert.IsTrue(device.Disposed);
        }

        [TestMethod]
        public void HikCamera_CallbackException_StillReturnsBuffer()
        {
            var device = new FakeDevice();
            var stream = new ThrowingStream();
            var camera = CreateCamera(device, stream);

            Assert.IsTrue(camera.Connect().IsSuccess);
            device.Stream.Raise(new FakeFrameOut());

            Assert.AreEqual(1, device.Stream.FreeCalls);
            camera.Dispose();
        }

        [TestMethod]
        public void HikFrameWrapper_CannotAddReferenceAfterDispose()
        {
            var frame = new HikFrameWrapper(new FakeFrameOut());

            frame.Dispose();
            frame.Dispose();

            Assert.ThrowsException<ObjectDisposedException>(() => frame.AddRef());
        }

        [TestMethod]
        public void HikCameraProvider_TransportMapping_CoversGenTlAndDoesNotExpandUnknown()
        {
            var method = typeof(HikCameraProvider).GetMethod(
                "ToLayerType", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method);

            var genTl = (DeviceTLayerType)method.Invoke(null, new object[] { CameraType.GenTL });
            var all = (DeviceTLayerType)method.Invoke(null, new object[] { CameraType.ALL });
            var unknown = (DeviceTLayerType)method.Invoke(null, new object[] { CameraType.Unknown });

            Assert.AreEqual(
                DeviceTLayerType.MvGenTLGigEDevice
                | DeviceTLayerType.MvGenTLCameraLinkDevice
                | DeviceTLayerType.MvGenTLCXPDevice
                | DeviceTLayerType.MvGenTLXoFDevice,
                genTl);
            Assert.AreEqual(
                DeviceTLayerType.MvGigEDevice
                | DeviceTLayerType.MvUsbDevice
                | DeviceTLayerType.MvCameraLinkDevice
                | DeviceTLayerType.MvVirGigEDevice
                | DeviceTLayerType.MvVirUsbDevice
                | DeviceTLayerType.MvGenTLGigEDevice
                | DeviceTLayerType.MvGenTLCameraLinkDevice
                | DeviceTLayerType.MvGenTLCXPDevice
                | DeviceTLayerType.MvGenTLXoFDevice,
                all);
            Assert.AreEqual((DeviceTLayerType)0, unknown);
        }

        [TestMethod]
        public void HikCamera_Int32Conversion_OutOfRangeReturnsFailure()
        {
            Assert.IsTrue(HikCamera.TryConvertToInt64ToInt32(12345L, out var value));
            Assert.AreEqual(12345, value);

            Assert.IsFalse(HikCamera.TryConvertToInt64ToInt32((long)int.MaxValue + 1, out var overflowHigh));
            Assert.AreEqual(0, overflowHigh);

            Assert.IsFalse(HikCamera.TryConvertToInt64ToInt32((long)int.MinValue - 1, out var overflowLow));
            Assert.AreEqual(0, overflowLow);
        }

        [TestMethod]
        public void HikFrameWrapper_Data_ReturnsSameInstanceOnRepeatedAccess()
        {
            var frame = new HikFrameWrapper(new FakeFrameOut());

            var first = frame.Data;
            var second = frame.Data;

            Assert.AreSame(first, second, "Data 必须缓存，禁止每次访问都触发 SDK 拷贝（5MB+/次）");
        }

        [TestMethod]
        public void HikFrameWrapper_Data_AfterDispose_ReturnsCachedOrEmpty()
        {
            var accessed = new HikFrameWrapper(new FakeFrameOut());
            var cached = accessed.Data;
            accessed.Dispose();
            Assert.AreSame(cached, accessed.Data, "已释放帧的已缓存托管副本必须仍可安全读取");

            var neverAccessed = new HikFrameWrapper(new FakeFrameOut());
            neverAccessed.Dispose();
            Assert.IsNotNull(neverAccessed.Data);
            Assert.AreEqual(0, neverAccessed.Data.Length, "未缓存且已释放的帧返回空数组，禁止触达已释放原生内存");
        }

        private static HikCamera CreateCamera(FakeDevice device, ICameraStream stream = null)
        {
            var info = new FakeDeviceInfo { SerialNumber = "HIK-001" };
            return new HikCamera(info, stream ?? new RecordingStream(), _ => device);
        }

        private class RecordingStream : ICameraStream
        {
            public int SubscriberCount => 0;

            public void Dispose()
            {
            }

            public virtual void Publish(IFrame frame) => frame.Dispose();

            public void Subscribe(string subberKey, int capacity, Func<string, IFrame, Task> handler, Action<Exception> whenException = null)
            {
            }

            public bool Unsubscribe(string subberKey) => false;
        }

        private sealed class ThrowingStream : RecordingStream
        {
            public override void Publish(IFrame frame) => throw new InvalidOperationException("publish failed");
        }

        private sealed class BlockingStream : RecordingStream
        {
            public TaskCompletionSource<object> Entered { get; } = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

            public TaskCompletionSource<object> Release { get; } = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

            public override void Publish(IFrame frame)
            {
                this.Entered.TrySetResult(null);
                this.Release.Task.GetAwaiter().GetResult();
                frame.Dispose();
            }
        }

        private sealed class FakeDeviceInfo : IDeviceInfo
        {
            public DeviceTLayerType TLayerType { get; set; } = DeviceTLayerType.MvGigEDevice;
            public string ManufacturerName { get; set; } = "HikVision";
            public string ModelName { get; set; } = "Fake";
            public string DeviceVersion { get; set; } = "1.0";
            public string SerialNumber { get; set; }
            public string UserDefinedName { get; set; } = "Fake";
            public uint DevTypeInfo { get; set; }
        }

        private sealed class FakeDevice : IDevice
        {
            public FakeDevice()
            {
                this.Stream = new FakeStreamGrabber();
                this.DeviceInfo = new FakeDeviceInfo();
            }

            public FakeStreamGrabber Stream { get; }
            public IDeviceInfo DeviceInfo { get; }
            public int OpenResult { get; set; } = MvError.MV_OK;
            public int CloseResult { get; set; } = MvError.MV_OK;
            public int StopResult { get => this.Stream.StopResult; set => this.Stream.StopResult = value; }
            public int CloseCalls { get; private set; }
            public bool Disposed { get; private set; }
            public bool IsConnected { get; private set; }
            public IStreamGrabber StreamGrabber => this.Stream;
            public IEventGrabber EventGrabber => null;
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
                if (this.OpenResult == MvError.MV_OK)
                    this.IsConnected = true;
                return this.OpenResult;
            }

            public int Open(DeviceAccessMode accessMode, uint switchoverKey) => this.Open();

            public int Close()
            {
                this.CloseCalls++;
                if (this.CloseResult == MvError.MV_OK)
                    this.IsConnected = false;
                return this.CloseResult;
            }

            public int LocalUpgrade(string filePath) => MvError.MV_OK;
            public int GetUpgradeProcess(out uint process) { process = 0; return MvError.MV_OK; }
            public int EventNotificationOn(string eventName) => MvError.MV_OK;
            public int EventNotificationOff(string eventName) => MvError.MV_OK;

            public void Dispose()
            {
                this.Disposed = true;
                this.IsConnected = false;
            }

            public void RaiseDeviceException(DeviceExceptionArgs args) => this.DeviceExceptionEvent?.Invoke(this, args);
        }

        private sealed class FakeStreamGrabber : IStreamGrabber
        {
            public int StopResult { get; set; } = MvError.MV_OK;
            public int FreeCalls { get; private set; }
            public event EventHandler<StreamExceptionEventArgs> StreamExceptionEvent;
            public event EventHandler<FrameGrabbedEventArgs> FrameGrabedEvent;

            public int SetImageNodeNum(uint nodeNum) => MvError.MV_OK;
            public int GetValidImageNum(out uint imageNum) { imageNum = 0; return MvError.MV_OK; }
            public int StartGrabbing() => MvError.MV_OK;
            public int StartGrabbing(StreamGrabStrategy strategy) => MvError.MV_OK;
            public int SetOutputQueueSize(uint queueSize) => MvError.MV_OK;
            public int StopGrabbing() => this.StopResult;
            public int GetImageBuffer(uint timeout, out IFrameOut frameOut) { frameOut = null; return MvError.MV_OK; }
            public int FreeImageBuffer(IFrameOut frameOut) { this.FreeCalls++; return MvError.MV_OK; }
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
            public System.Drawing.Bitmap ToBitmap() => null;
            public void Dispose()
            {
            }
        }
    }
}
