using Junevy.EasyCamera.Vendors.HikVision;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Junevy.EasyCamera.Tests.Vendors.HikVision
{
    [TestClass]
    public class HikCameraSdkSystemTests
    {
        private int initCalls;
        private int finalizeCalls;
        private Func<int> originalInitialize;
        private Func<int> originalFinalize;

        [TestInitialize]
        public void ResetSeam()
        {
            this.initCalls = 0;
            this.finalizeCalls = 0;
            this.originalInitialize = HikCameraSdkSystem.SdkInitializeAction;
            this.originalFinalize = HikCameraSdkSystem.SdkFinalizeAction;
            HikCameraSdkSystem.SdkInitializeAction = () => Interlocked.Increment(ref this.initCalls);
            HikCameraSdkSystem.SdkFinalizeAction = () => Interlocked.Increment(ref this.finalizeCalls);
        }

        [TestCleanup]
        public void RestoreSeam()
        {
            // 恢复为真实 SDK 委托（仅赋值不调用），避免影响其他测试类
            HikCameraSdkSystem.SdkInitializeAction = this.originalInitialize;
            HikCameraSdkSystem.SdkFinalizeAction = this.originalFinalize;
        }

        [TestMethod]
        public void Initialize_Twice_HoldsSingleReference()
        {
            var system = new HikCameraSdkSystem();
            system.Initialize();
            system.Initialize();
            system.Dispose();

            Assert.AreEqual(1, this.initCalls, "同一实例重复 Initialize 必须只持有一个全局引用");
            Assert.AreEqual(1, this.finalizeCalls);
        }

        [TestMethod]
        public void Release_WithoutInitialize_DoesNotStealOtherInstanceReference()
        {
            var first = new HikCameraSdkSystem();
            var second = new HikCameraSdkSystem();

            first.Initialize();
            second.Release();   // 未初始化实例不得削减他人引用
            second.Dispose();

            Assert.AreEqual(1, this.initCalls);
            Assert.AreEqual(0, this.finalizeCalls, "SDK 仍被 first 持有，不得被 Finalize");

            first.Dispose();
            Assert.AreEqual(1, this.finalizeCalls);
        }

        [TestMethod]
        public void Initialize_AfterDispose_ShouldBeNoOp()
        {
            var system = new HikCameraSdkSystem();
            system.Dispose();
            system.Initialize();

            Assert.AreEqual(0, this.initCalls);
        }

        [TestMethod]
        public async Task ConcurrentInitializeAndRelease_EveryInitEventuallyFinalized()
        {
            for (var round = 0; round < 200; round++)
            {
                var first = new HikCameraSdkSystem();
                var second = new HikCameraSdkSystem();

                first.Initialize();
                await Task.WhenAll(
                    Task.Run(second.Initialize),
                    Task.Run(first.Release));

                first.Dispose();
                second.Dispose();
            }

            Assert.AreEqual(this.initCalls, this.finalizeCalls,
                "全部实例 Dispose 后，每次 SDK 初始化必须恰好对应一次 Finalize");
        }
    }
}
