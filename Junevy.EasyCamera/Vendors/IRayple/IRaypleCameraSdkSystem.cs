using Junevy.EasyCamera.Core.Abstractions;
using System.Threading;

namespace Junevy.EasyCamera.Vendors.IRayple
{
    /// <summary>
    /// Irayple相机SDK系统。
    /// Irayple SDK 未提供全局初始化/反初始化接口，此处仅维护幂等的初始化状态标记
    /// </summary>
    public class IRaypleCameraSdkSystem : ICameraSdkSystem
    {
        /// <summary>
        /// 相机SDK系统是否已初始化
        /// </summary>
        private int isInitialized;

        /// <summary>
        /// 是否已释放
        /// </summary>
        private int disposed;

        /// <summary>
        /// 初始化相机SDK（幂等，可重复调用）
        /// </summary>
        public void Initialize()
        {
            // 已释放的SDK系统不再允许重新初始化
            if (Volatile.Read(ref this.disposed) == 1)
                return;

            Interlocked.Exchange(ref this.isInitialized, 1);
        }

        /// <summary>
        /// 释放相机SDK资源（幂等，可重复调用）
        /// </summary>
        public void Release()
        {
            Interlocked.Exchange(ref this.isInitialized, 0);
        }

        /// <summary>
        /// 释放相机SDK资源
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref this.disposed, 1, 0) == 1)
                return;

            this.Release();
        }
    }
}
