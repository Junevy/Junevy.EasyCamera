using Junevy.EasyCamera.Core.Abstractions;
using Junevy.EasyCamera.Common;
using Junevy.EasyCamera.Tests.Mocks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Junevy.EasyCamera.Tests.Abstractions
{
    /// <summary>
    /// 帧数据流的分发与释放行为测试，覆盖引用计数与缓冲帧释放
    /// </summary>
    [TestClass]
    public class CameraStreamTests
    {
        [TestMethod]
        public void Publish_WithoutSubscriber_ShouldDisposeFrame()
        {
            using var stream = new CameraStream("SN001");
            var frame = new MockFrame();

            stream.Publish(frame);

            Assert.IsTrue(frame.IsDisposed, "无订阅者时发布方持有的帧必须被释放，否则非托管内存泄漏");
        }

        [TestMethod]
        public void Publish_NullFrame_ShouldNotThrow()
        {
            using var stream = new CameraStream("SN001");
            stream.Publish(null);
        }

        [TestMethod]
        public async Task Publish_WithSubscriber_ShouldDeliverAndReleaseFrame()
        {
            using var stream = new CameraStream("SN001");
            var delivered = new TaskCompletionSource<IFrame>();

            stream.Subscribe("suber", 2, (name, frame) =>
            {
                delivered.TrySetResult(frame);
                return Task.CompletedTask;
            });

            var frame = new MockFrame();
            stream.Publish(frame);

            var completed = await Task.WhenAny(delivered.Task, Task.Delay(5000));
            Assert.AreSame(delivered.Task, completed, "帧未在预期时间内投递到订阅者");

            // 投递完成后再释放订阅者持有的引用
            delivered.Task.Result.Dispose();
        }

        [TestMethod]
        public void Subscribe_DuplicateKey_ShouldKeepSingleSubscriber()
        {
            using var stream = new CameraStream("SN001");

            stream.Subscribe("suber", 1, (name, frame) => Task.CompletedTask);
            stream.Subscribe("suber", 1, (name, frame) => Task.CompletedTask);

            Assert.AreEqual(1, stream.SubscriberCount, "相同订阅Key必须被替换而不是重复注册");
        }

        [TestMethod]
        public void Subscribe_NullKey_ShouldThrow()
        {
            using var stream = new CameraStream("SN001");

            Assert.ThrowsException<ArgumentException>(
                () => stream.Subscribe(null, 1, (name, frame) => Task.CompletedTask));
        }

        [TestMethod]
        public void Subscribe_NullHandler_ShouldThrow()
        {
            using var stream = new CameraStream("SN001");

            Assert.ThrowsException<ArgumentNullException>(
                () => stream.Subscribe("suber", 1, null));
        }

        [TestMethod]
        public void Unsubscribe_ExistingKey_ShouldReturnTrue()
        {
            using var stream = new CameraStream("SN001");
            stream.Subscribe("suber", 1, (name, frame) => Task.CompletedTask);

            Assert.IsTrue(stream.Unsubscribe("suber"));
            Assert.AreEqual(0, stream.SubscriberCount);
            Assert.IsFalse(stream.Unsubscribe("suber"));
        }

        [TestMethod]
        public void Unsubscribe_NullKey_ShouldReturnFalse()
        {
            using var stream = new CameraStream("SN001");
            Assert.IsFalse(stream.Unsubscribe(null));
        }

        [TestMethod]
        public void Subscribe_ZeroCapacity_ShouldFallbackToSingleSlot()
        {
            using var stream = new CameraStream("SN001");
            stream.Subscribe("suber", 0, (name, frame) => Task.CompletedTask);

            Assert.AreEqual(1, stream.SubscriberCount);
        }

        [TestMethod]
        public void Dispose_ShouldClearSubscribers()
        {
            var stream = new CameraStream("SN001");
            stream.Subscribe("suber", 1, (name, frame) => Task.CompletedTask);

            stream.Dispose();

            Assert.AreEqual(0, stream.SubscriberCount);
        }

        [TestMethod]
        public void Publish_OnceSubscriberUnsubscribed_ShouldDisposeFrame()
        {
            using var stream = new CameraStream("SN001");
            stream.Subscribe("suber", 1, (name, frame) =>
            {
                Thread.Sleep(20);
                return Task.CompletedTask;
            });

            stream.Unsubscribe("suber");

            var frame = new MockFrame();
            stream.Publish(frame);

            Assert.IsTrue(frame.IsDisposed, "订阅者已全部取消后，帧必须被释放");
        }
    }
}
