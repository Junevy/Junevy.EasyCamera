using Junevy.EasyCamera.Core.Abstractions;
using Junevy.EasyCamera.Common;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;

namespace Junevy.EasyCamera.Tests.Common
{
    /// <summary>
    /// 组合相机SDK系统的统一调度与释放测试
    /// </summary>
    [TestClass]
    public class CompositeCameraSdkSystemTests
    {
        [TestMethod]
        public void Initialize_Release_ShouldDispatchToAllSystems()
        {
            var first = new FakeSdkSystem("first");
            var second = new FakeSdkSystem("second");

            var composite = new CompositeCameraSdkSystem(new ICameraSdkSystem[] { first, second });

            composite.Initialize();
            Assert.AreEqual("first:init", first.CallLog);
            Assert.AreEqual("second:init", second.CallLog);

            composite.Release();
            Assert.AreEqual("first:init,first:release", first.CallLog);
            Assert.AreEqual("second:init,second:release", second.CallLog);
        }

        [TestMethod]
        public void Dispose_ShouldDisposeAllSystems()
        {
            var first = new FakeSdkSystem("first");
            var second = new FakeSdkSystem("second");

            var composite = new CompositeCameraSdkSystem(new ICameraSdkSystem[] { first, second });
            composite.Dispose();

            Assert.IsTrue(first.Disposed);
            Assert.IsTrue(second.Disposed);
        }

        [TestMethod]
        public void Dispose_WhenOneThrows_ShouldStillDisposeOthers()
        {
            var throwing = new FakeSdkSystem("throwing") { ThrowOnDispose = true };
            var healthy = new FakeSdkSystem("healthy");

            var composite = new CompositeCameraSdkSystem(new ICameraSdkSystem[] { throwing, healthy });
            composite.Dispose();

            Assert.IsTrue(healthy.Disposed, "单个SDK释放失败不应中断其余SDK的清理");
        }

        [TestMethod]
        public void Constructor_NullSystems_ShouldThrow()
        {
            Assert.ThrowsException<ArgumentNullException>(() => new CompositeCameraSdkSystem(null));
        }

        [TestMethod]
        public void Initialize_WhenOneThrows_ShouldStillInitializeOthers()
        {
            var throwing = new FakeSdkSystem("throwing") { ThrowOnInitialize = true };
            var healthy = new FakeSdkSystem("healthy");

            var composite = new CompositeCameraSdkSystem(new ICameraSdkSystem[] { throwing, healthy });

            Assert.ThrowsException<AggregateException>(() => composite.Initialize());
            Assert.IsTrue(healthy.CallLog.Contains("healthy:init"), "单个SDK初始化失败不应中断其余SDK");
        }

        [TestMethod]
        public void Release_WhenOneThrows_ShouldStillReleaseOthers()
        {
            var throwing = new FakeSdkSystem("throwing") { ThrowOnRelease = true };
            var healthy = new FakeSdkSystem("healthy");

            var composite = new CompositeCameraSdkSystem(new ICameraSdkSystem[] { throwing, healthy });
            composite.Initialize();
            Assert.ThrowsException<AggregateException>(() => composite.Release());
            Assert.IsTrue(healthy.CallLog.Contains("healthy:release"), "单个SDK释放失败不应中断其余SDK");
        }

        private class FakeSdkSystem : ICameraSdkSystem
        {
            private readonly List<string> calls = new List<string>();

            public FakeSdkSystem(string name)
            {
                this.Name = name;
            }

            public string Name { get; }

            public bool Disposed { get; private set; }

            public bool ThrowOnDispose { get; set; }

            public bool ThrowOnInitialize { get; set; }

            public bool ThrowOnRelease { get; set; }

            public string CallLog => string.Join(",", this.calls);

            public void Initialize()
            {
                if (this.ThrowOnInitialize)
                    throw new InvalidOperationException("initialize failed");
                this.calls.Add(this.Name + ":init");
            }

            public void Release()
            {
                if (this.ThrowOnRelease)
                    throw new InvalidOperationException("release failed");
                this.calls.Add(this.Name + ":release");
            }

            public void Dispose()
            {
                if (this.ThrowOnDispose)
                    throw new InvalidOperationException("dispose failed");

                this.Disposed = true;
            }
        }
    }
}
