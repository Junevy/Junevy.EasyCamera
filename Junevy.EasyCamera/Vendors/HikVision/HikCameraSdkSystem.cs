using Junevy.EasyCamera.Core.Abstractions;
using MvCameraControl;
using System.Threading;

namespace Junevy.EasyCamera.Vendors.HikVision
{
    /// <summary>
    /// 海康相机SDK系统。
    /// SDK 的 Initialize/Finalize 是进程级全局操作，本类以静态引用计数管理：
    /// 每个实例的 Initialize 持有一个引用，引用计数归零时才真正 Finalize，
    /// 避免多个服务实例并存时一方 Release 导致其他在用实例失效。
    /// </summary>
    public class HikCameraSdkSystem : ICameraSdkSystem
    {
        /// <summary>
        /// 全局初始化引用计数，归零时才真正释放SDK
        /// </summary>
        private static int initRefCount;

        /// <summary>
        /// 是否已释放（释放后本实例不允许再参与初始化）
        /// </summary>
        private static int disposed;

        /// <summary>
        /// 初始化相机SDK（幂等，可重复调用；每实例持有一个全局引用）
        /// </summary>
        public void Initialize()
        {
            // 已释放的SDK系统不再允许重新初始化，避免句柄与原生资源失配
            if (Interlocked.CompareExchange(ref disposed, 1, 1) == 1)
                return;

            if (Interlocked.Increment(ref initRefCount) == 1)
            {
                try
                {
                    SDKSystem.Initialize();
                }
                catch
                {
                    // 初始化失败时回退引用计数，避免留下无法释放的悬挂引用
                    Interlocked.Decrement(ref initRefCount);
                    throw;
                }
            }
        }

        /// <summary>
        /// 释放本实例持有的SDK引用；全局引用计数归零时才真正 Finalize SDK（幂等，可重复调用）
        /// </summary>
        public void Release()
        {
            while (true)
            {
                var current = Volatile.Read(ref initRefCount);
                if (current <= 0)
                    return;

                if (Interlocked.CompareExchange(ref initRefCount, current - 1, current) == current)
                {
                    if (current == 1)
                        SDKSystem.Finalize();

                    return;
                }
            }
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
