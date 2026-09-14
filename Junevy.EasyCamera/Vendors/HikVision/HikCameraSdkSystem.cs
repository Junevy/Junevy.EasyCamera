using Junevy.EasyCamera.Core.Abstractions;
using MvCameraControl;
using System.Threading;

namespace Junevy.EasyCamera.Vendors.HikVision
{
    /// <summary>
    /// 海康相机SDK系统
    /// </summary>
    public class HikCameraSdkSystem : ICameraSdkSystem
    {
        /// <summary>
        /// 相机SDK系统是否已初始化
        /// </summary>
        private static int isInitialized;

        /// <summary>
        /// 是否已释放
        /// </summary>
        private static int disposed;

        /// <summary>
        /// 初始化相机SDK（幂等，可重复调用）
        /// </summary>
        public void Initialize()
        {
            // 已释放的SDK系统不再允许重新初始化，避免句柄与原生资源失配
            if (Interlocked.CompareExchange(ref disposed, 1, 1) == 1)
                return;

            if (Interlocked.CompareExchange(ref isInitialized, 1, 0) == 0) SDKSystem.Initialize();
        }

        /// <summary>
        /// 释放相机SDK资源（幂等，可重复调用）
        /// </summary>
        public void Release()
        {
            if (Interlocked.CompareExchange(ref isInitialized, 0, 1) == 1)
                SDKSystem.Finalize();
        }

        /// <summary>
        /// 释放相机SDK资源
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref disposed, 1, 0) == 0)
            {
                this.Release();
            }
        }
    }
}
