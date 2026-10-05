using Junevy.EasyCamera.Common;
using Junevy.EasyCamera.Core.Abstractions;
using Junevy.EasyCamera.Core.Common;
using System;
using System.Threading;

namespace Junevy.EasyCamera
{
    /// <summary>
    /// 一次 <see cref="EasyCameraBuilder.Build"/> 产出的相机栈宿主：
    /// 持有 SDK 系统、服务门面与生效配置，并统一承担整栈的生命周期。
    /// 使用顺序：Sdk.Initialize() → Service 使用 → Dispose()（幂等；
    /// 释放顺序为 相机 → 帧流 → SDK，保证 Finalize 不早于任何相机释放）。
    /// </summary>
    public sealed class EasyCameraHost : IDisposable
    {
        private readonly CameraManager cameraManager;
        private readonly StreamManager streamManager;
        private int disposed;

        internal EasyCameraHost(
            ICameraSdkSystem sdk,
            ICameraService service,
            StreamOptions options,
            CameraManager cameraManager,
            StreamManager streamManager)
        {
            this.Sdk = sdk ?? throw new ArgumentNullException(nameof(sdk));
            this.Service = service ?? throw new ArgumentNullException(nameof(service));
            this.Options = options ?? throw new ArgumentNullException(nameof(options));
            this.cameraManager = cameraManager ?? throw new ArgumentNullException(nameof(cameraManager));
            this.streamManager = streamManager ?? throw new ArgumentNullException(nameof(streamManager));
        }

        /// <summary>相机 SDK 系统（进程级 Initialize/Dispose 由调用方驱动）</summary>
        public ICameraSdkSystem Sdk { get; }

        /// <summary>相机服务门面（枚举/开关/订阅/参数）</summary>
        public ICameraService Service { get; }

        /// <summary>本次组装生效的帧流配置（只读视图，修改不再影响已创建的栈）</summary>
        public StreamOptions Options { get; }

        /// <summary>
        /// 释放整栈：相机（Close+Dispose）→ 帧流 → SDK。幂等，可重复调用。
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref this.disposed, 1) == 1)
                return;

            this.cameraManager.Dispose();
            this.streamManager.Dispose();
            this.Sdk.Dispose();
        }
    }
}
