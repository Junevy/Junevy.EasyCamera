using Junevy.EasyCamera.Vendors.HikVision;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MvCameraControl;
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
            // 桩返回 MV_OK：seam 返回值即 SDK 错误码，计数值不能被当作错误码
            HikCameraSdkSystem.SdkInitializeAction = () =>
            {
                Interlocked.Increment(ref this.initCalls);
                return MvError.MV_OK;
            };
            HikCameraSdkSystem.SdkFinalizeAction = () =>
            {
                Interlocked.Increment(ref this.finalizeCalls);
                return MvError.MV_OK;
            };
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

        [TestMethod]
        public void Initialize_WhenSdkReturnsError_ThrowsAndRollsBackReference()
        {
            HikCameraSdkSystem.SdkInitializeAction = () =>
            {
                Interlocked.Increment(ref this.initCalls);
                return unchecked((int)0x80000001);
            };

            var system = new HikCameraSdkSystem();
            try
            {
                var ex = Assert.ThrowsException<InvalidOperationException>(() => system.Initialize());
                StringAssert.Contains(ex.Message, "0x80000001");
                Assert.AreEqual(1, this.initCalls);

                // 失败后引用计数必须已回退：再次 Initialize 必须重新调用 SDK，而不是被"计数非零"误判为已初始化
                HikCameraSdkSystem.SdkInitializeAction = () =>
                {
                    Interlocked.Increment(ref this.initCalls);
                    return MvError.MV_OK;
                };

                system.Initialize();
                Assert.AreEqual(2, this.initCalls, "初始化失败后引用计数必须回退，再次 Initialize 必须重新调用 SDK");
            }
            finally
            {
                // 即使断言失败也释放本实例持有的引用，避免全局计数泄漏污染后续测试
                system.Dispose();
            }

            Assert.AreEqual(1, this.finalizeCalls);
        }

        [TestMethod]
        public void Release_WhenSdkFinalizeThrows_RethrowsOnceAndKeepsCountBalanced()
        {
            HikCameraSdkSystem.SdkFinalizeAction = () =>
            {
                Interlocked.Increment(ref this.finalizeCalls);
                throw new InvalidOperationException("finalize failed");
            };

            var system = new HikCameraSdkSystem();
            HikCameraSdkSystem next = null;
            system.Initialize();
            try
            {
                Assert.ThrowsException<InvalidOperationException>(() => system.Release());
                Assert.AreEqual(1, this.finalizeCalls);

                // 第二次 Release：实例已不再持有引用，既不得再次 Finalize，也不得抛异常
                system.Release();
                Assert.AreEqual(1, this.finalizeCalls, "Finalize 抛异常后第二次 Release 不得再次调用 Finalize");

                // 计数必须仍然平衡：全局计数此时应为 0，新实例 Initialize 必须真正调用 SDK
                HikCameraSdkSystem.SdkFinalizeAction = () =>
                {
                    Interlocked.Increment(ref this.finalizeCalls);
                    return MvError.MV_OK;
                };

                next = new HikCameraSdkSystem();
                next.Initialize();
                Assert.AreEqual(2, this.initCalls, "异常路径不得让全局引用计数错乱（二次递减会导致 SDK 不再被初始化）");
            }
            finally
            {
                // 无论断言成败都释放全部持有的引用，避免污染后续测试
                next?.Dispose();
                system.Dispose();
            }

            Assert.AreEqual(2, this.finalizeCalls);
        }
    }
}
