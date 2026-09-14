using Junevy.EasyCamera.Core.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Junevy.EasyCamera.Tests.Abstractions
{
    [TestClass]
    public class CameraResultTests
    {
        [TestMethod]
        public void CameraResult_Success_ShouldHaveIsSuccessTrue()
        {
            var result = CameraResult.Success(0);
            Assert.IsTrue(result.IsSuccess);
            Assert.AreEqual(0, result.Code);
        }

        [TestMethod]
        public void CameraResult_Fail_ShouldHaveIsSuccessFalse()
        {
            var result = CameraResult.Fail(-1, "error");
            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual(-1, result.Code);
            Assert.AreEqual("error", result.Message);
        }

        [TestMethod]
        public void CameraResult_Result_ShouldMatchBool()
        {
            var successResult = CameraResult.Result(true, 100);
            Assert.IsTrue(successResult.IsSuccess);
            Assert.AreEqual(100, successResult.Code);

            var failResult = CameraResult.Result(false, 200);
            Assert.IsFalse(failResult.IsSuccess);
            Assert.AreEqual(200, failResult.Code);
        }
    }
}
