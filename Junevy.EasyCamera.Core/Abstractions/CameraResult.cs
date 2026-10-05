namespace Junevy.EasyCamera.Core.Abstractions
{
    /// <summary>
    /// Camera的操作结果
    /// </summary>
    /// <remarks>
    /// <see cref="Code"/> 约定：成功时为厂商原生成功码（通常为 0，本库自身产生且无原生码时恒为 0）；
    /// 失败时为厂商原生错误码，无原生码时为 -1。调用方只应以 <see cref="IsSuccess"/> 判定成败，
    /// <see cref="Code"/> 用于诊断与厂商错误对照。
    /// </remarks>
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
