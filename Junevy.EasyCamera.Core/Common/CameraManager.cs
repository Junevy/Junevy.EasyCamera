using Junevy.EasyCamera.Core.Abstractions;
using System;
using System.Collections.Concurrent;
using System.Linq;

namespace Junevy.EasyCamera.Core.Common
{
    /// <summary>
    /// 相机缓存管理
    /// </summary>
    public class CameraManager : IDisposable
    {
        /// <summary>
        /// 缓存的相机实例
        /// </summary>
        private readonly ConcurrentDictionary<string, ICamera> cameras = new();

        private bool disposed;

        /// <summary>
        /// 注册相机实例
        /// </summary>
        /// <param name="cameraKey">相机序列号</param>
        /// <param name="camera">相机实例</param>
        /// <exception cref="ArgumentNullException">
        /// <c>cameraKey</c> 或 <c>camera</c> 为 <c>null</c>
        /// </exception>
        public void Register(string cameraKey, ICamera camera)
        {
            if (string.IsNullOrEmpty(cameraKey))
                throw new ArgumentNullException(nameof(cameraKey));

            cameras[cameraKey] = camera ?? throw new ArgumentNullException(nameof(camera));
        }

        /// <summary>
        /// 尝试获取相机实例
        /// </summary>
        /// <param name="cameraKey">相机序列号</param>
        /// <param name="camera">相机实例</param>
        /// <returns>
        /// 是否成功获取相机实例
        /// </returns>
        public bool TryGet(string cameraKey, out ICamera camera)
        {
            camera = null;

            if (string.IsNullOrEmpty(cameraKey))
                return false;

            return cameras.TryGetValue(cameraKey, out camera);
        }

        /// <summary>
        /// 移除并释放相机实例
        /// </summary>
        /// <param name="cameraKey">相机序列号</param>
        /// <returns>
        /// 是否成功移除相机实例
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// <c>cameraKey</c> 为 <c>null</c>
        /// </exception>
        public bool Remove(string cameraKey)
        {
            if (string.IsNullOrEmpty(cameraKey))
                throw new ArgumentNullException(nameof(cameraKey));

            if (cameras.TryRemove(cameraKey, out var cam))
            {
                DisposeCamera(cam);
                return true;
            }
            return false;
        }

        /// <summary>
        /// 释放所有相机实例
        /// </summary>
        public void Dispose()
        {
            if (this.disposed)
                return;

            this.disposed = true;

            // 先取出快照再逐个释放，避免释放过程中集合被修改，
            // 且单个相机释放失败不影响其余相机
            var snapshot = cameras.Values.ToArray();
            cameras.Clear();

            foreach (var cam in snapshot)
                DisposeCamera(cam);
        }

        /// <summary>
        /// 释放单个相机实例，尽力而为，不抛出异常
        /// </summary>
        /// <param name="camera">相机实例</param>
        private static void DisposeCamera(ICamera camera)
        {
            try
            {
                camera.Dispose();
            }
            catch
            {
                // 释放阶段不向上抛出异常，避免中断其余相机的清理
            }
        }
    }
}
