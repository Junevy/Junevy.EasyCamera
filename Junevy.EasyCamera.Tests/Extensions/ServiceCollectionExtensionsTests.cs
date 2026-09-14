using Junevy.EasyCamera.Core.Abstractions;
using Junevy.EasyCamera.Common;
using Junevy.EasyCamera.Core.Common;
using Junevy.EasyCamera.Extensions;
using Junevy.EasyCamera.Vendors.HikVision;
using Junevy.EasyCamera.Vendors.IRayple;
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

        [TestMethod]
        public void AddEasyCamera_WithIRayple_ShouldResolveVendorAndCompositeSdkSystem()
        {
            var services = new ServiceCollection();
            services.AddEasyCamera(options => options.EnableIRayple = true);

            var provider = services.BuildServiceProvider();

            var vendors = provider.GetServices<IVendorCameraProvider>().ToList();
            Assert.AreEqual(1, vendors.Count);
            Assert.IsInstanceOfType(vendors[0], typeof(IRaypleCameraProvider));
            Assert.AreEqual("iRAYPLE", vendors[0].VendorName);

            Assert.IsNotNull(provider.GetService<IRaypleCameraSdkSystem>());
            Assert.IsInstanceOfType(provider.GetService<ICameraSdkSystem>(), typeof(CompositeCameraSdkSystem));
        }

        [TestMethod]
        public void AddEasyCamera_WithBothVendors_ShouldRegisterBothVendors()
        {
            var services = new ServiceCollection();
            services.AddEasyCamera(options =>
            {
                options.EnableHikVision = true;
                options.EnableIRayple = true;
            });

            var provider = services.BuildServiceProvider();

            var vendors = provider.GetServices<IVendorCameraProvider>().ToList();
            Assert.AreEqual(2, vendors.Count);
            CollectionAssert.AreEquivalent(
                new[] { "HikVision", "iRAYPLE" },
                vendors.Select(vendor => vendor.VendorName).ToArray());

            var aggregate = provider.GetService<ICameraProvider>() as AggregateCameraProvider;
            Assert.IsNotNull(aggregate);
            Assert.AreEqual(2, aggregate.Vendors.Count);
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

        [TestMethod]
        public void AddIRaypleCamera_ShouldRegisterIRaypleVendor()
        {
            var services = new ServiceCollection();
            services.AddIRaypleCamera();

            var provider = services.BuildServiceProvider();

            var vendors = provider.GetServices<IVendorCameraProvider>().ToList();
            Assert.AreEqual(1, vendors.Count);
            Assert.IsInstanceOfType(vendors[0], typeof(IRaypleCameraProvider));
            Assert.IsNotNull(provider.GetService<ICameraSdkSystem>());
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
