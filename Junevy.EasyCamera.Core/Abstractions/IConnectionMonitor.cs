using System;

namespace Junevy.EasyCamera.Core.Abstractions
{
    /// <summary>
    /// 相机连接监视能力接口（可选能力，接口隔离）：相机检测到物理掉线时通知订阅者。
    /// 仅能感知掉线的厂商相机实现此接口；门面据此把掉线转发为 <c>ICameraService.CameraDisconnected</c>。
    /// </summary>
    /// <remarks>
    /// 事件在线程池线程上触发，处理程序不得假定 UI 线程。同一次连接最多触发一次。
    /// 掉线后相机不会自动重连，需由调用方重新 <c>Connect</c>（门面为再次 <c>OpenCamera</c>）。
    /// </remarks>
    public interface IConnectionMonitor
    {
        /// <summary>
        /// 相机掉线时触发（线程池线程；同一次连接最多一次）。
        /// </summary>
        event EventHandler<CameraDisconnectedEventArgs> Disconnected;
    }
}
