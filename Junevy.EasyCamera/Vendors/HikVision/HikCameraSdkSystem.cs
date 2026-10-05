using Junevy.EasyCamera.Core.Abstractions;
using MvCameraControl;
using System;
using System.Threading;

namespace Junevy.EasyCamera.Vendors.HikVision
{
    /// <summary>
    /// 海康相机SDK系统。
    /// SDK 的 Initialize/Finalize 是进程级全局操作，本类以全局引用计数管理：
    /// 每个实例无论 Initialize 几次都只持有一个引用（真正的按实例幂等），
    /// 引用计数归零时才真正 Finalize。全部计数转移在全局锁内完成，
    /// 消除"并发 Initialize 读到 0、Finalize 随后执行"的交叉窗口。
    /// </summary>
    public class HikCameraSdkSystem : ICameraSdkSystem
    {
        /// <summary>
        /// 全局初始化引用计数，归零时才真正释放SDK
        /// </summary>
        private static int initRefCount;

        /// <summary>串行化全局引用计数与 Initialize/Finalize 的转移（锁序 instanceLock → refCountLock，多实例间无反向持锁路径）</summary>
        private static readonly object refCountLock = new();

        /// <summary>本实例生命周期锁（初始化/释放/销毁互斥）</summary>
        private readonly object instanceLock = new();

        /// <summary>本实例是否已持有全局引用（按实例幂等的依据）</summary>
        private bool initialized;

        /// <summary>本实例是否已销毁</summary>
        private bool disposed;

        /// <summary>
        /// 测试 seam：默认指向真实 SDK（SDK 方法返回 int 错误码，此处忽略返回值）。
        /// 仅测试可通过 InternalsVisibleTo 替换，避免回归测试依赖相机运行时。
        /// </summary>
        internal static Func<int> SdkInitializeAction = SDKSystem.Initialize;

        /// <summary>测试 seam，见 <see cref="SdkInitializeAction" />。</summary>
        internal static Func<int> SdkFinalizeAction = SDKSystem.Finalize;

        /// <summary>
        /// 初始化相机SDK（按实例幂等：重复调用只持有一个全局引用）
        /// </summary>
        public void Initialize()
        {
            if (Volatile.Read(ref this.disposed))
                return;

            lock (this.instanceLock)
            {
                if (this.disposed || this.initialized)
                    return;

                lock (refCountLock)
                {
                    if (Interlocked.Increment(ref initRefCount) == 1)
                    {
                        try
                        {
                            SdkInitializeAction();
                        }
                        catch
                        {
                            // 初始化失败时回退引用计数，避免留下无法释放的悬挂引用
                            Interlocked.Decrement(ref initRefCount);
                            throw;
                        }
                    }
                }

                this.initialized = true;
            }
        }

        /// <summary>
        /// 释放本实例持有的SDK引用；未 Initialize 的实例不削减任何引用。
        /// 全局引用计数归零时才真正 Finalize SDK（幂等，可重复调用）
        /// </summary>
        public void Release()
        {
            lock (this.instanceLock)
            {
                if (!this.initialized)
                    return;

                lock (refCountLock)
                {
                    if (Interlocked.Decrement(ref initRefCount) == 0)
                        SdkFinalizeAction();
                }

                this.initialized = false;
            }
        }

        /// <summary>
        /// 释放相机SDK资源（幂等，可重复调用）
        /// </summary>
        public void Dispose()
        {
            lock (this.instanceLock)
            {
                if (this.disposed)
                    return;

                this.disposed = true;
            }

            this.Release();
        }
    }
}
