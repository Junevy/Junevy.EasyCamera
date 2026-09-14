using Junevy.EasyCamera.Common;
using Junevy.EasyCamera.Core.Abstractions;
using Junevy.EasyCamera.Tests.Mocks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
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
    }
}
