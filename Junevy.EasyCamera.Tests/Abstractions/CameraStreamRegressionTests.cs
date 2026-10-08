using Junevy.EasyCamera.Common;
using Junevy.EasyCamera.Core.Abstractions;
using Junevy.EasyCamera.Tests.Mocks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Junevy.EasyCamera.Tests.Abstractions
{
    [TestClass]
    public class CameraStreamRegressionTests
    {
        [TestMethod]
        public async Task Subscribe_WithCapacityOne_DropsOldestFrameAndDisposesIt()
        {
            using var stream = new CameraStream("SN001");
            var firstStarted = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirst = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            var delivered = new List<IFrame>();

            stream.Subscribe("sub", 1, async (_, frame) =>
            {
                lock (delivered)
                    delivered.Add(frame);
                if (delivered.Count == 1)
                {
                    firstStarted.TrySetResult(null);
                    await releaseFirst.Task;
                }
            });

            var first = new MockFrame();
            var dropped = new MockFrame();
            var newest = new MockFrame();
            stream.Publish(first);
            await firstStarted.Task;
            stream.Publish(dropped);
            stream.Publish(newest);

            Assert.IsTrue(SpinWait.SpinUntil(() => dropped.IsDisposed, 5000), "有界队列淘汰的帧必须立即释放");

            releaseFirst.TrySetResult(null);
            Assert.IsTrue(SpinWait.SpinUntil(() =>
            {
                lock (delivered)
                    return delivered.Count == 2;
            }, 5000), "队列中的最新帧未被投递");

            Assert.AreSame(newest, delivered[1]);
            stream.Unsubscribe("sub");
        }

        [TestMethod]
        public void Dispose_BlocksFutureSubscribeAndDisposesPublishedFrame()
        {
            using var stream = new CameraStream("SN001");
            stream.Dispose();

            Assert.ThrowsException<ObjectDisposedException>(() =>
                stream.Subscribe("sub", 1, (_, _) => Task.CompletedTask));

            var frame = new MockFrame();
            stream.Publish(frame);

            Assert.IsTrue(frame.IsDisposed);
        }

        [TestMethod]
        public async Task Dispose_RacingSubscribe_DoesNotLeaveAWorkerRegistered()
        {
            var stream = new CameraStream("SN001");
            var gate = new ManualResetEventSlim(false);
            var tasks = new List<Task>();

            for (var i = 0; i < 16; i++)
            {
                tasks.Add(Task.Run(() =>
                {
                    gate.Wait();
                    try
                    {
                        stream.Subscribe("sub-" + Guid.NewGuid().ToString("N"), 1, (_, _) => Task.CompletedTask);
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }));
            }

            stream.Dispose();
            gate.Set();
            await Task.WhenAll(tasks);

            Assert.AreEqual(0, stream.SubscriberCount);
            stream.Dispose();
        }

        [TestMethod]
        public async Task HandlerFailure_WithoutExceptionCallback_DoesNotInvokeNullCallback()
        {
            using var stream = new CameraStream("SN001");
            var handlerEntered = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            stream.Subscribe("sub", 1, (_, _) =>
            {
                handlerEntered.TrySetResult(null);
                throw new InvalidOperationException("handler failed");
            });

            var frame = new MockFrame();
            stream.Publish(frame);
            await handlerEntered.Task;

            Assert.IsTrue(SpinWait.SpinUntil(() => frame.IsDisposed, 5000));
            stream.Unsubscribe("sub");
        }

        [TestMethod]
        public async Task ConcurrentSubscribe_SameKey_LeavesOnlyOneSubscription()
        {
            using var stream = new CameraStream("SN001");
            var tasks = new List<Task>();
            var callbacks = 0;

            for (var i = 0; i < 32; i++)
            {
                tasks.Add(Task.Run(() => stream.Subscribe(
                    "same",
                    1,
                    (_, _) =>
                    {
                        Interlocked.Increment(ref callbacks);
                        return Task.CompletedTask;
                    })));
            }

            await Task.WhenAll(tasks);
            Assert.AreEqual(1, stream.SubscriberCount);

            var frame = new MockFrame();
            stream.Publish(frame);
            Assert.IsTrue(SpinWait.SpinUntil(() => Volatile.Read(ref callbacks) == 1, 5000));
        }

        [TestMethod]
        public void DeadSubscriber_FramesPublishedAroundWorkerDeath_AreAllReleased()
        {
            // 订阅者首帧即抛异常且不提供 whenException：worker 终止，随后被移除。
            // 发布线程持续发布直到死订阅者消失，覆盖"终止前、排空中、移除前"的全部窗口：
            // 任何成功入队的帧都必须被消费或释放，不得滞留在无人读取的通道中。
            using var stream = new CameraStream("SN001");
            stream.Subscribe("dead", 4, (_, _) => throw new InvalidOperationException("handler failed"));

            var frames = new List<MockFrame>();
            var publisher = Task.Run(() =>
            {
                // 至少 500 帧；之后若死订阅者仍在则继续发布（5000 帧上限，防止缺陷下挂死）
                for (var i = 0; i < 5000 && (i < 500 || stream.SubscriberCount > 0); i++)
                {
                    var frame = new MockFrame();
                    frames.Add(frame);
                    stream.Publish(frame);
                    Thread.Yield();
                }
            });

            Assert.IsTrue(publisher.Wait(TimeSpan.FromSeconds(5)), "发布线程必须在 5 秒内完成");
            Assert.AreEqual(0, stream.SubscriberCount, "死订阅者必须被移除");
            Assert.IsTrue(frames.Count >= 500, "必须覆盖足够多的发布");
            Assert.IsTrue(frames.All(f => f.IsDisposed), "worker 终止前后发布的每一帧都必须被释放");
        }
    }
}
