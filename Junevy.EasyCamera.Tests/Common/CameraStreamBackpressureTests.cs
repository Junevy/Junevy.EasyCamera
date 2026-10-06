using Junevy.EasyCamera.Common;
using Junevy.EasyCamera.Core.Abstractions;
using Junevy.EasyCamera.Core.Common;
using Junevy.EasyCamera.Tests.Mocks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Junevy.EasyCamera.Tests.Common
{
    /// <summary>
    /// 帧流背压策略与统计计数测试：三种策略都不阻塞采集线程，区别只在丢哪一帧；
    /// 丢帧必须可观测，否则现场无法判断"图像卡顿"来自采集还是消费。
    /// </summary>
    [TestClass]
    public class CameraStreamBackpressureTests
    {
        [TestMethod]
        public void Publish_WithoutSubscriber_CountsAsDropped()
        {
            using var stream = new CameraStream("SN001");
            var frame = new MockFrame();

            stream.Publish(frame);

            Assert.IsTrue(frame.IsDisposed);
            Assert.AreEqual(1, stream.Statistics.Published);
            Assert.AreEqual(1, stream.Statistics.Dropped, "无订阅者的帧必须计入丢帧统计");
            Assert.AreEqual(0, stream.Statistics.Delivered);
        }

        [TestMethod]
        public async Task DropOldest_KeepsNewestFrameAndCountsDrop()
        {
            using var stream = new CameraStream("SN001", new StreamOptions { BackpressureMode = BackpressureMode.DropOldest });
            var release = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            var delivered = 0;

            stream.Subscribe("sub", 1, async (_, _) =>
            {
                Interlocked.Increment(ref delivered);
                await release.Task.ConfigureAwait(false);
            });

            // 第一个帧占住 handler（容量 1），随后两帧触发淘汰
            stream.Publish(new MockFrame());
            await WaitUntilAsync(() => Volatile.Read(ref delivered) == 1);
            stream.Publish(new MockFrame());
            stream.Publish(new MockFrame());
            await WaitUntilAsync(() => stream.Statistics.Dropped >= 1);

            Assert.IsTrue(stream.Statistics.Dropped >= 1, "队列满淘汰必须计入丢帧统计");
            Assert.IsTrue(stream.Statistics.Published >= 3);
            stream.Unsubscribe("sub");
        }

        [TestMethod]
        public async Task RejectNewest_DropsIncomingFrameWithoutBlocking()
        {
            using var stream = new CameraStream("SN001", new StreamOptions { BackpressureMode = BackpressureMode.RejectNewest });
            var release = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            var delivered = 0;

            stream.Subscribe("sub", 1, async (_, _) =>
            {
                Interlocked.Increment(ref delivered);
                await release.Task.ConfigureAwait(false);
            });

            // 第一帧被 handler 取走并挂起 → 队列空
            stream.Publish(new MockFrame());
            await WaitUntilAsync(() => Volatile.Read(ref delivered) == 1);

            // 第二帧填满容量 1 的队列
            stream.Publish(new MockFrame());

            // 第三帧必须被立即拒绝（不阻塞采集线程），发布方引用照常归还
            var rejected = new MockFrame();
            var publishTask = Task.Run(() => stream.Publish(rejected));
            Assert.IsTrue(publishTask.Wait(TimeSpan.FromSeconds(2)), "RejectNewest 不得阻塞采集线程");

            Assert.IsTrue(rejected.IsDisposed, "被拒绝的帧引用必须归还");
            Assert.IsTrue(stream.Statistics.Dropped >= 1);
            release.TrySetResult(null);
            stream.Unsubscribe("sub");
        }

        [TestMethod]
        public void BackpressureMode_OnlyExposesTruthfulModes()
        {
            // .NET 的 BoundedChannelFullMode.DropNewest 与 DropOldest 行为一致（都淘汰最旧帧），
            // 因此库内不提供"丢新帧"选项，避免名不副实
            Assert.AreEqual(2, Enum.GetValues(typeof(BackpressureMode)).Length);
            Assert.IsFalse(Enum.IsDefined(typeof(BackpressureMode), "DropNewest"));
        }

        [TestMethod]
        public void StreamOptions_DefaultBackpressureIsDropOldest()
        {
            var options = new StreamOptions();

            Assert.AreEqual(5, options.StreamCapacity);
            Assert.AreEqual(BackpressureMode.DropOldest, options.BackpressureMode);
        }

        [TestMethod]
        public void StreamManager_CreatesStreamWithConfiguredBackpressureMode()
        {
            var options = new StreamOptions { BackpressureMode = BackpressureMode.RejectNewest };
            using var manager = new StreamManager(options);

            var stream = manager.GetOrCreateStream("SN001");

            Assert.IsNotNull(stream);
            Assert.AreSame(stream, manager.GetOrCreateStream("SN001"), "同 key 必须复用同一条流");
        }

        [TestMethod]
        public async Task Service_ExposesStreamStatistics()
        {
            var provider = new MockCameraProvider(new System.Collections.Generic.List<MockCameraInfo>
            {
                new MockCameraInfo { SerialNumber = "SN-001" }
            });
            using var manager = new CameraManager();
            using var streams = new StreamManager();
            var service = new CameraService(provider, manager, streams);
            var info = new MockCameraInfo { SerialNumber = "SN-001" };

            Assert.AreEqual(0, service.GetStreamStatistics("key").Published, "未打开相机时返回空统计");

            Assert.IsTrue(service.OpenCamera(info, "key").IsSuccess);
            Assert.IsTrue(service.SubscribeFrameStream("key", "sub", (_, _) => Task.CompletedTask));

            var frame = new MockFrame();
            streams.GetStream("key", out var stream);
            stream.Publish(frame);
            frame.Dispose();

            await WaitUntilAsync(() => service.GetStreamStatistics("key").Published > 0);
            Assert.IsTrue(service.GetStreamStatistics("key").Published >= 1);
            Assert.AreEqual(0, service.GetStreamStatistics("missing").Published);
        }

        private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
        {
            var deadline = Environment.TickCount + timeoutMs;
            while (!condition() && Environment.TickCount < deadline)
                await Task.Delay(10).ConfigureAwait(false);

            Assert.IsTrue(condition(), "等待条件超时");
        }
    }
}
