using Junevy.EasyCamera.Common;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;

namespace Junevy.EasyCamera.Tests.Common
{
    [TestClass]
    public class StreamManagerTests
    {
        [TestMethod]
        public void GetOrCreateStream_AfterDispose_ShouldThrowObjectDisposed()
        {
            var manager = new StreamManager();
            manager.Dispose();

            Assert.ThrowsException<ObjectDisposedException>(() => manager.GetOrCreateStream("SN001"));
        }

        [TestMethod]
        public void RemoveStream_AfterDispose_ShouldReturnFalse()
        {
            var manager = new StreamManager();
            manager.GetOrCreateStream("SN001");
            manager.Dispose();

            Assert.IsFalse(manager.RemoveStream("SN001"));
        }

        [TestMethod]
        public void GetOrCreateStream_RacingDispose_ShouldNotLeakStream()
        {
            // Dispose 与 GetOrCreate 竞态时，新 created 的流必须被就地释放而不是滞留字典外
            var manager = new StreamManager();

            Assert.ThrowsException<ObjectDisposedException>(() =>
            {
                manager.Dispose();
                manager.GetOrCreateStream("SN001");
            });
        }
    }
}
