using System;

namespace Junevy.EasyCamera.Core.Abstractions
{
    /// <summary>
    /// 相机掉线通知的事件参数（相机层与门面层共用）。
    /// </summary>
    public sealed class CameraDisconnectedEventArgs : EventArgs
    {
        /// <summary>
        /// 构造相机层的掉线通知参数（<see cref="CameraKey" /> 为 <c>null</c>，由门面通过 <see cref="WithCameraKey" /> 填充）。
        /// </summary>
        /// <param name="serialNumber">掉线相机的序列号，未知时为空字符串</param>
        /// <param name="reason">掉线原因的诊断文本</param>
        /// <param name="occurredAtUtc">检测到掉线的 UTC 时间</param>
        public CameraDisconnectedEventArgs(string serialNumber, string reason, DateTime occurredAtUtc)
            : this(null, serialNumber, reason, occurredAtUtc)
        {
        }

        private CameraDisconnectedEventArgs(string cameraKey, string serialNumber, string reason, DateTime occurredAtUtc)
        {
            this.CameraKey = cameraKey;
            this.SerialNumber = serialNumber ?? string.Empty;
            this.Reason = reason ?? string.Empty;
            this.OccurredAtUtc = occurredAtUtc;
        }

        /// <summary>
        /// 门面层中打开该相机时使用的操作 Key；相机层触发时为 <c>null</c>。
        /// </summary>
        public string CameraKey { get; }

        /// <summary>
        /// 掉线相机的序列号，未知时为空字符串。
        /// </summary>
        public string SerialNumber { get; }

        /// <summary>
        /// 掉线原因的诊断文本（英文）。
        /// </summary>
        public string Reason { get; }

        /// <summary>
        /// 检测到掉线的 UTC 时间。
        /// </summary>
        public DateTime OccurredAtUtc { get; }

        /// <summary>
        /// 复制一份参数并填入门面层的相机 Key；不修改当前实例。
        /// </summary>
        /// <param name="cameraKey">门面层的相机操作 Key</param>
        /// <returns>带有相机 Key 的新参数实例</returns>
        public CameraDisconnectedEventArgs WithCameraKey(string cameraKey)
            => new CameraDisconnectedEventArgs(cameraKey, this.SerialNumber, this.Reason, this.OccurredAtUtc);
    }
}
