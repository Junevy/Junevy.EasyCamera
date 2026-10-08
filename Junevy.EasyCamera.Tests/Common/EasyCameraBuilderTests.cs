using Junevy.EasyCamera.Core.Abstractions;
using Junevy.EasyCamera.Vendors.HikVision;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MvCameraControl;
using System;
using System.Threading;

namespace Junevy.EasyCamera.Tests.Common
{
    /// <summary>
    /// 非 DI 入口（EasyCamera.Create/Builder）的组装与生命周期测试。
    /// SDK 初始化经 HikCameraSdkSystem 的 internal seam 替换，不依赖真实相机运行时。
    /// </summary>
    [TestClass]
    public class EasyCameraBuilderTests
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
            HikCameraSdkSystem.SdkInitializeAction = this.originalInitialize;
            HikCameraSdkSystem.SdkFinalizeAction = this.originalFinalize;
        }

        [TestMethod]
        public void Create_WithHikVision_ReturnsReadyComponents()
        {
            using var host = EasyCamera.Create(b => b.EnableHikVision());

            Assert.IsNotNull(host.Service);
            Assert.IsNotNull(host.Sdk);
            Assert.AreEqual(5, host.Options.StreamCapacity, "未配置时使用与 DI 一致的默认容量");
        }

        [TestMethod]
        public void Create_WithStreamOptions_AppliesConfiguration()
        {
            using var host = EasyCamera.Create(b => b.EnableHikVision()
                .WithStreamOptions(o =>
                {
                    o.StreamCapacity = 9;
                    o.CameraBufferCapacity = 3;
                }));

            Assert.AreEqual(9, host.Options.StreamCapacity);
            Assert.AreEqual(3, host.Options.CameraBufferCapacity);
        }

        [TestMethod]
        public void Create_WithoutVendor_Throws()
        {
            Assert.ThrowsException<InvalidOperationException>(
                () => EasyCamera.Create(_ => { }));
        }

        [TestMethod]
        public void EnableIRayple_ThrowsNotImplementedException()
        {
            Assert.ThrowsException<NotImplementedException>(
                () => EasyCamera.Create(b => b.EnableIRayple()));
        }

        [TestMethod]
        public void EnableBasler_ThrowsNotImplementedException()
        {
            Assert.ThrowsException<NotImplementedException>(
                () => EasyCamera.Create(b => b.EnableBasler()));
        }

        [TestMethod]
        public void Create_ConfigureNull_ThrowsArgumentNullException()
        {
            Assert.ThrowsException<ArgumentNullException>(() => EasyCamera.Create(null));
        }

        [TestMethod]
        public void Create_WithHikVision_SdkLifecycleThroughSeam()
        {
            var host = EasyCamera.Create(b => b.EnableHikVision());

            host.Sdk.Initialize();
            host.Dispose();

            Assert.AreEqual(1, this.initCalls, "host.Sdk.Initialize 恰好触发一次 SDK 初始化");
            Assert.AreEqual(1, this.finalizeCalls, "host.Dispose 必须释放 SDK 引用并触发 Finalize");

            host.Dispose();   // 幂等：二次 Dispose 不再触发任何 SDK 调用
            Assert.AreEqual(1, this.finalizeCalls);
        }

        [TestMethod]
        public void Create_TwoHosts_AreIndependentStacks()
        {
            using var first = EasyCamera.Create(b => b.EnableHikVision());
            using var second = EasyCamera.Create(b => b.EnableHikVision());

            Assert.AreNotSame(first.Service, second.Service, "每次 Create 必须是独立的相机栈");
            Assert.AreNotSame(first.Sdk, second.Sdk);
        }

        [TestMethod]
        public void EnableHikVision_CalledTwice_RegistersSingleVendor()
        {
            // 重复启用同一厂商必须去重：否则会追加两个提供器与两个 SDK 系统，枚举结果重复
            var builder = EasyCamera.CreateBuilder().EnableHikVision().EnableHikVision();

            Assert.AreEqual(1, builder.EnabledVendorCount, "同一厂商重复启用必须去重为单实例语义");

            var host = builder.Build();
            Assert.AreEqual(0, this.initCalls, "Build 不得触碰 SDK");

            host.Dispose();
            Assert.AreEqual(0, this.finalizeCalls, "未 Initialize 的宿主释放时不得触发 SDK Finalize");
        }
    }
}
