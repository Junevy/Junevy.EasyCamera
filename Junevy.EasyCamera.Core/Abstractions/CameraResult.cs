namespace Junevy.EasyCamera.Core.Abstractions
{
    /// <summary>
    /// Camera的操作结果
    /// </summary>
    public class CameraResult
    {
        public bool IsSuccess { get; }

        public int Code { get; }

        public string Message { get; }

        public CameraResult(bool isSuccess, int code)
        {
            this.IsSuccess = isSuccess;
            this.Code = code;
        }

        public CameraResult(bool isSuccess, int code, string msg)
        {
            this.IsSuccess = isSuccess;
            this.Code = code;
            this.Message = msg;
        }

        public static CameraResult Result(bool result, int code, string message = "") => new (result, code, message);

        public static CameraResult Fail(int errorCode, string errMsg = "") => new (false, errorCode, errMsg);

        public static CameraResult Success(int code, string msg = "") => new (true, code, msg);
    }
}
