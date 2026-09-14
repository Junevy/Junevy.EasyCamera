using Junevy.EasyCamera.Core.Abstractions;
using Junevy.EasyCamera.Core.Common;
using Junevy.EasyCamera.Tests.Mocks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Linq;

namespace Junevy.EasyCamera.Tests.Abstractions
{
    [TestClass]
    public class InterfaceCompatibilityTests
    {
        [TestMethod]
        public void MockCamera_ShouldImplementICamera()
        {
            ICamera camera = new MockCamera();
            Assert.IsNotNull(camera);
            Assert.IsInstanceOfType(camera, typeof(ICamera));
        }

        [TestMethod]
        public void MockCameraInfo_ShouldImplementICameraInfo()
        {
            ICameraInfo info = new MockCameraInfo
            {
                SerialNumber = "SN001",
                ModelName = "MockModel",
                Manufacturer = "MockVendor"
            };
            Assert.AreEqual("SN001", info.SerialNumber);
            Assert.AreEqual("MockModel", info.ModelName);
            Assert.AreEqual("MockVendor", info.Manufacturer);
        }

        [TestMethod]
        public void MockCameraProvider_ShouldImplementICameraProvider()
        {
            var infos = new[]
            {
                new MockCameraInfo { SerialNumber = "SN001" },
                new MockCameraInfo { SerialNumber = "SN002" }
            }.ToList();

            ICameraProvider provider = new MockCameraProvider(infos);

            var enumerated = provider.Enumerate().ToList();
            Assert.AreEqual(2, enumerated.Count);
        }

        [TestMethod]
        public void CameraManager_RegisterAndTryGet_ShouldCacheCamera()
        {
            var cameraManager = new CameraManager();
            var camera = new MockCamera();
            cameraManager.Register("SN001", camera);

            var found = cameraManager.TryGet("SN001", out var cached);
            Assert.IsTrue(found);
            Assert.AreSame(camera, cached);
        }

        [TestMethod]
        public void CameraManager_RegisterSameKeyTwice_ShouldOverwrite()
        {
            var cameraManager = new CameraManager();
            var camera1 = new MockCamera();
            var camera2 = new MockCamera();

            cameraManager.Register("SN001", camera1);
            cameraManager.Register("SN001", camera2);

            cameraManager.TryGet("SN001", out var cached);
            Assert.AreSame(camera2, cached);
        }

        [TestMethod]
        public void CameraManager_TryGetMissingKey_ShouldReturnFalse()
        {
            var cameraManager = new CameraManager();
            var found = cameraManager.TryGet("SN_Missing", out var camera);
            Assert.IsFalse(found);
            Assert.IsNull(camera);
        }

        [TestMethod]
        public void CameraManager_Remove_ShouldDisposeAndRemove()
        {
            var cameraManager = new CameraManager();
            var camera = new MockCamera();
            cameraManager.Register("SN_Remove", camera);

            var result = cameraManager.Remove("SN_Remove");
            Assert.IsTrue(result);
            Assert.IsTrue(camera.IsDisposed);

            var found = cameraManager.TryGet("SN_Remove", out _);
            Assert.IsFalse(found);
        }

        [TestMethod]
        public void CameraManager_RemoveMissingKey_ShouldReturnFalse()
        {
            var cameraManager = new CameraManager();
            var result = cameraManager.Remove("SN_NotExist");
            Assert.IsFalse(result);
        }

        [TestMethod]
        public void CameraManager_Dispose_ShouldDisposeAllCameras()
        {
            var cameraManager = new CameraManager();
            var camera1 = new MockCamera();
            var camera2 = new MockCamera();

            cameraManager.Register("SN001", camera1);
            cameraManager.Register("SN002", camera2);

            cameraManager.Dispose();

            Assert.IsTrue(camera1.IsDisposed);
            Assert.IsTrue(camera2.IsDisposed);
        }

        [TestMethod]
        public void CameraManager_RegisterWithNullArgs_ShouldThrow()
        {
            var cameraManager = new CameraManager();

            try { cameraManager.Register(null, new MockCamera()); Assert.Fail("Expected ArgumentNullException"); }
            catch (System.ArgumentNullException) { }

            try { cameraManager.Register("SN", null); Assert.Fail("Expected ArgumentNullException"); }
            catch (System.ArgumentNullException) { }
        }

        [TestMethod]
        public void CameraManager_RemoveWithNull_ShouldThrow()
        {
            var cameraManager = new CameraManager();
            try { cameraManager.Remove(null); Assert.Fail("Expected ArgumentNullException"); }
            catch (System.ArgumentNullException) { }
        }
    }
}
