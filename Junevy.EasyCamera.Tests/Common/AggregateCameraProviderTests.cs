using Junevy.EasyCamera.Core.Abstractions;
using Junevy.EasyCamera.Common;
using Junevy.EasyCamera.Tests.Mocks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;

namespace Junevy.EasyCamera.Tests.Common
{
    /// <summary>
    /// 聚合相机提供器的多厂商枚举与分发测试
    /// </summary>
    [TestClass]
    public class AggregateCameraProviderTests
    {
        [TestMethod]
        public void Enumerate_ShouldMergeAllVendors()
        {
            var provider = new AggregateCameraProvider(new IVendorCameraProvider[]
            {
                new FakeVendorProvider("V1", new[] { "SN001" }),
                new FakeVendorProvider("V2", new[] { "SN002", "SN003" })
            });

            Assert.AreEqual(2, provider.Vendors.Count);
            Assert.AreEqual(3, provider.Enumerate().Count());
            Assert.AreEqual(3, provider.Enumerate(CameraInterfaceType.GigE).Count());
        }

        [TestMethod]
        public void Supports_ShouldReturnTrueWhenAnyVendorSupports()
        {
            var provider = new AggregateCameraProvider(new IVendorCameraProvider[]
            {
                new FakeVendorProvider("V1", new[] { "SN001" })
            });

            Assert.IsTrue(provider.Supports(new FakeCameraInfo { SerialNumber = "SN001" }));
            Assert.IsFalse(provider.Supports(new MockCameraInfo { SerialNumber = "SN001" }));
        }

        [TestMethod]
        public void Create_ShouldDispatchToSupportingVendor()
        {
            var vendor = new FakeVendorProvider("V1", new[] { "SN001" });
            var provider = new AggregateCameraProvider(new IVendorCameraProvider[] { vendor });

            using var stream = new CameraStream("SN001");
            var camera = provider.Create(new FakeCameraInfo { SerialNumber = "SN001" }, stream);

            Assert.IsNotNull(camera);
            Assert.AreEqual(1, vendor.CreateCount);
        }

        [TestMethod]
        public void Create_WithoutSupportingVendor_ShouldThrow()
        {
            var provider = new AggregateCameraProvider(new IVendorCameraProvider[0]);

            using var stream = new CameraStream("SN_UNKNOWN");
            Assert.ThrowsException<ArgumentException>(
                () => provider.Create(new MockCameraInfo { SerialNumber = "SN_UNKNOWN" }, stream));
        }

        [TestMethod]
        public void Create_NullInfo_ShouldThrow()
        {
            var provider = new AggregateCameraProvider(new IVendorCameraProvider[0]);

            using var stream = new CameraStream("SN001");
            Assert.ThrowsException<ArgumentNullException>(() => provider.Create(null, stream));
        }

        [TestMethod]
        public void Constructor_NullVendors_ShouldThrow()
        {
            Assert.ThrowsException<ArgumentNullException>(() => new AggregateCameraProvider(null));
        }
    }
}
