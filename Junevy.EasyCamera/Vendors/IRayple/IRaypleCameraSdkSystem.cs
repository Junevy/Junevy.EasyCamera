using Junevy.EasyCamera.Core.Abstractions;
using System;
using System.Threading;

namespace Junevy.EasyCamera.Vendors.IRayple
{
    /// <summary>
    /// Irayple相机SDK系统。
    /// Irayple SDK 未提供全局初始化/反初始化接口，因此 Initialize/Release 为空操作：
    /// 保留它们是为了让多厂商组合（CompositeCameraSdkSystem）对所有厂商使用同一条生命周期路径。
    /// </summary>

    [Obsolete("未开发完毕", true)]
    public class IRaypleCameraSdkSystem : ICameraSdkSystem
    {
        /// <summary>
        /// 是否已释放
        /// </summary>
        private int disposed;

        /// <summary>
        /// 初始化相机SDK（幂等，可重复调用）。Irayple SDK 无需全局初始化，空操作。
        /// </summary>
        public void Initialize()
        {
            // 已释放的SDK系统不再允许重新初始化
        }

        /// <summary>
        /// 释放相机SDK资源（幂等，可重复调用）。Irayple SDK 无需全局反初始化，空操作。
        /// </summary>
        public void Release()
        {
        }

        /// <summary>
        /// 释放相机SDK资源
        /// </summary>
        public void Dispose()
        {
            Interlocked.Exchange(ref this.disposed, 1);
        }
    }
}
