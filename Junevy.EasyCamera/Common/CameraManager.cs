using Junevy.EasyCamera.Core.Abstractions;
using Junevy.EasyCamera.Core.Common;
using System;
using System.Collections.Concurrent;
using System.Linq;

namespace Junevy.EasyCamera.Common
{
    /// <summary>
    /// 相机缓存管理。字典锁只保护注册表状态，原生 Close/Dispose 在锁外执行，
    /// 避免阻塞其他相机访问或在回调代码中形成锁反转。
    /// </summary>
    public class CameraManager : ICameraManager
    {
        private readonly object operationLock = new();
        private readonly ConcurrentDictionary<string, ICamera> cameras = new();
        private bool disposed;
        private string lastError;

        /// <summary>
        /// 最近一次清理失败的诊断信息。Remove 返回 <see cref="CameraRemoveStatus.ReleaseFailed"/> 时可据此定位原因。
        /// </summary>
        public string LastError
        {
            get
            {
                lock (this.operationLock)
                    return this.lastError;
            }
        }

        public bool TryRegister(string cameraKey, ICamera camera)
        {
            if (string.IsNullOrEmpty(cameraKey))
                throw new ArgumentNullException(nameof(cameraKey));
            if (camera == null)
                throw new ArgumentNullException(nameof(camera));

            lock (this.operationLock)
            {
                if (this.disposed)
                    throw new ObjectDisposedException(nameof(CameraManager));

                return this.cameras.TryAdd(cameraKey, camera);
            }
        }

        public bool TryGet(string cameraKey, out ICamera camera)
        {
            camera = null;
            if (string.IsNullOrEmpty(cameraKey))
                return false;

            lock (this.operationLock)
            {
                if (this.disposed)
                    return false;

                return this.cameras.TryGetValue(cameraKey, out camera);
            }
        }

        public CameraRemoveStatus Remove(string cameraKey)
        {
            if (string.IsNullOrEmpty(cameraKey))
                throw new ArgumentNullException(nameof(cameraKey));

            ICamera camera;
            lock (this.operationLock)
            {
                if (this.disposed || !this.cameras.TryRemove(cameraKey, out camera))
                    return CameraRemoveStatus.NotFound;
            }

            return this.DisposeCamera(camera) ? CameraRemoveStatus.Removed : CameraRemoveStatus.ReleaseFailed;
        }

        public void Dispose()
        {
            ICamera[] snapshot;
            lock (this.operationLock)
            {
                if (this.disposed)
                    return;

                this.disposed = true;
                snapshot = this.cameras.Values.ToArray();
                this.cameras.Clear();
            }

            foreach (var camera in snapshot)
                DisposeCamera(camera);
        }

        /// <summary>
        /// 真实执行相机关闭和释放，任何一步失败都会反映到返回值和 LastError；
        /// 即使 Close 失败也继续 Dispose，确保资源尽量回收。
        /// </summary>
        private bool DisposeCamera(ICamera camera)
        {
            var success = true;
            string error = null;

            try
            {
                var closeResult = camera.Close();
                if (closeResult == null || !closeResult.IsSuccess)
                {
                    success = false;
                    error = closeResult?.Message ?? "Camera close returned no result.";
                }
            }
            catch (Exception ex)
            {
                success = false;
                error = ex.Message;
            }

            try
            {
                camera.Dispose();
            }
            catch (Exception ex)
            {
                success = false;
                error = string.IsNullOrEmpty(error) ? ex.Message : error + "; " + ex.Message;
            }

            if (success)
            {
                // 成功清理后清空 LastError，避免陈旧错误误导后续诊断
                lock (this.operationLock)
                    this.lastError = null;
            }
            else
            {
                lock (this.operationLock)
                    this.lastError = error;
            }

            return success;
        }
    }
}
