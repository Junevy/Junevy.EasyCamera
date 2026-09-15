using Junevy.EasyCamera.Core.Abstractions;
using Junevy.EasyCamera.Common;
using Junevy.EasyCamera.Core.Common;
using Junevy.EasyCamera.Extensions;
using Junevy.EasyCamera.Vendors.HikVision;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;

namespace Junevy.EasyCamera.Tests.Extensions
{
    [TestClass]
    public class ServiceCollectionExtensionsTests
    {
        [TestMethod]
        public void AddEasyCamera_WithNullServices_ShouldThrowArgumentNullException()
        {
            Assert.ThrowsException<ArgumentNullException>(() =>
            {
                ServiceCollectionExtensions.AddEasyCamera(null);
            });
        }

        [TestMethod]
        public void AddEasyCamera_WithoutOptions_ShouldRegisterCoreOnly()
        {
            var services = new ServiceCollection();
            services.AddEasyCamera();

            var provider = services.BuildServiceProvider();

            Assert.IsNotNull(provider.GetService<StreamManager>());
            Assert.IsNotNull(provider.GetService<CameraManager>());
            Assert.IsNotNull(provider.GetService<CameraService>());
            Assert.IsInstanceOfType(provider.GetService<ICameraProvider>(), typeof(AggregateCameraProvider));

            // 接口与具体实现应解析为同一单例
            Assert.AreSame(provider.GetService<StreamManager>(), provider.GetService<IStreamManager>());
            Assert.AreSame(provider.GetService<CameraManager>(), provider.GetService<ICameraManager>());
            Assert.AreSame(provider.GetService<CameraService>(), provider.GetService<ICameraService>());
            Assert.AreSame(provider.GetService<StreamOptions>(), provider.GetService<IStreamOptions>());

            Assert.AreEqual(0, provider.GetServices<IVendorCameraProvider>().Count());
            Assert.IsNull(provider.GetService<ICameraSdkSystem>());
        }

        [TestMethod]
        public void AddEasyCamera_StreamOptionsConfigure_ShouldBeApplied()
        {
            var services = new ServiceCollection();
            services.AddEasyCamera(options => options.EnableHikVision = true,
                streamOptions => streamOptions.StreamCapacity = 9);

            var provider = services.BuildServiceProvider();

            Assert.AreEqual(9, provider.GetRequiredService<StreamOptions>().StreamCapacity);
        }

        [TestMethod]
        public void AddEasyCamera_WithHikVision_ShouldResolveVendorAndCompositeSdkSystem()
        {
            var services = new ServiceCollection();
            services.AddEasyCamera(options => options.EnableHikVision = true);

            var provider = services.BuildServiceProvider();

            var vendors = provider.GetServices<IVendorCameraProvider>().ToList();
            Assert.AreEqual(1, vendors.Count);
            Assert.IsInstanceOfType(vendors[0], typeof(HikCameraProvider));
            Assert.AreEqual("HikVision", vendors[0].VendorName);

            Assert.IsNotNull(provider.GetService<HikCameraSdkSystem>());
            Assert.IsInstanceOfType(provider.GetService<ICameraSdkSystem>(), typeof(CompositeCameraSdkSystem));

            Assert.IsNotNull(provider.GetService<StreamManager>());
            Assert.IsNotNull(provider.GetService<CameraManager>());
            Assert.IsNotNull(provider.GetService<CameraService>());
        }

        /// <summary>
        /// IRayple 厂商标记为 [Obsolete(..., true)]（未开发完毕），注册扩展按未实现厂商处理，
        /// 启用时与 Basler 一致抛出 NotImplementedException
        /// </summary>
        [TestMethod]
        public void AddEasyCamera_WithIRayple_ShouldThrowNotImplementedException()
        {
            var services = new ServiceCollection();

            Assert.ThrowsException<NotImplementedException>(() =>
            {
                services.AddEasyCamera(options => options.EnableIRayple = true);
            });
        }

        [TestMethod]
        public void AddEasyCamera_WithBothVendors_ShouldThrowNotImplementedExceptionForIRayple()
        {
            var services = new ServiceCollection();

            Assert.ThrowsException<NotImplementedException>(() =>
            {
                services.AddEasyCamera(options =>
                {
                    options.EnableHikVision = true;
                    options.EnableIRayple = true;
                });
            });
        }

        [TestMethod]
        public void AddEasyCamera_MultipleCalls_ShouldNotDuplicateVendors()
        {
            var services = new ServiceCollection();
            services.AddEasyCamera(options => options.EnableHikVision = true);
            services.AddEasyCamera(options => options.EnableHikVision = true);

            var provider = services.BuildServiceProvider();

            Assert.AreEqual(1, provider.GetServices<IVendorCameraProvider>().Count());
        }

        [TestMethod]
        public void AddHikVisionCamera_ShouldRegisterHikVisionVendor()
        {
            var services = new ServiceCollection();
            services.AddHikVisionCamera();

            var provider = services.BuildServiceProvider();

            var vendors = provider.GetServices<IVendorCameraProvider>().ToList();
            Assert.AreEqual(1, vendors.Count);
            Assert.IsInstanceOfType(vendors[0], typeof(HikCameraProvider));
            Assert.IsNotNull(provider.GetService<ICameraSdkSystem>());
        }

        /// <summary>
        /// IRayple 厂商标记为 [Obsolete(..., true)]（未开发完毕），AddIRaypleCamera 启用后应抛出未实现异常
        /// </summary>
        [TestMethod]
        public void AddIRaypleCamera_ShouldThrowNotImplementedException()
        {
            var services = new ServiceCollection();

            Assert.ThrowsException<NotImplementedException>(() =>
            {
                services.AddIRaypleCamera();
            });
        }

        [TestMethod]
        public void AddEasyCamera_WithBasler_ShouldThrowNotImplementedException()
        {
            var services = new ServiceCollection();

            Assert.ThrowsException<NotImplementedException>(() =>
            {
                services.AddEasyCamera(options => options.EnableBasler = true);
            });
        }

        [TestMethod]
        public void SingletonLifecycle_SameServiceInstanceOnMultipleResolve()
        {
            var services = new ServiceCollection();
            services.AddEasyCamera(options => options.EnableHikVision = true);

            var provider = services.BuildServiceProvider();

            Assert.AreSame(provider.GetService<CameraManager>(), provider.GetService<CameraManager>());
            Assert.AreSame(provider.GetService<StreamManager>(), provider.GetService<StreamManager>());
            Assert.AreSame(provider.GetService<CameraService>(), provider.GetService<CameraService>());
            Assert.AreSame(provider.GetService<ICameraProvider>(), provider.GetService<ICameraProvider>());
            Assert.AreSame(provider.GetService<ICameraManager>(), provider.GetService<ICameraManager>());
            Assert.AreSame(provider.GetService<IStreamManager>(), provider.GetService<IStreamManager>());
            Assert.AreSame(provider.GetService<ICameraService>(), provider.GetService<ICameraService>());
        }
    }
}
