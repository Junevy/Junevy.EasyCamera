using Junevy.EasyCamera.Core.Abstractions;
using System;
using System.Collections.Generic;

namespace Junevy.EasyCamera.Core.Common
{
    /// <summary>
    /// 组合相机SDK系统，将多个厂商的SDK系统聚合为一个统一入口，
    /// 初始化/释放时按注册顺序统一调度所有厂商SDK。
    /// </summary>
    public sealed class CompositeCameraSdkSystem : ICameraSdkSystem
    {
        private readonly ICameraSdkSystem[] systems;

        /// <summary>
        /// 构造组合相机SDK系统
        /// </summary>
        /// <param name="systems">厂商SDK系统列表</param>
        /// <exception cref="ArgumentNullException">systems 为 <c>null</c></exception>
        public CompositeCameraSdkSystem(IEnumerable<ICameraSdkSystem> systems)
        {
            if (systems == null)
                throw new ArgumentNullException(nameof(systems));

            this.systems = new List<ICameraSdkSystem>(systems).ToArray();
        }

        /// <summary>
        /// 按注册顺序初始化所有厂商SDK
        /// </summary>
        public void Initialize()
        {
            foreach (var system in this.systems)
                system.Initialize();
        }

        /// <summary>
        /// 按注册顺序释放所有厂商SDK
        /// </summary>
        public void Release()
        {
            foreach (var system in this.systems)
                system.Release();
        }

        /// <summary>
        /// 释放所有厂商SDK资源
        /// </summary>
        public void Dispose()
        {
            foreach (var system in this.systems)
            {
                try
                {
                    system.Dispose();
                }
                catch
                {
                    // 释放阶段不向上抛出异常，避免中断其余SDK的清理
                }
            }
        }
    }
}
