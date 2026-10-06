using Junevy.EasyCamera.Core.Abstractions;
using Junevy.EasyCamera.Core.Common;
using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Junevy.EasyCamera.Common
{
    /// <summary>
    /// 相机图像帧数据流管理类，用于管理相机的图像数据流
    /// </summary>
    public class StreamManager : IStreamManager
    {
        private readonly ConcurrentDictionary<string, ICameraStream> streams = new();
        private readonly IStreamOptions options;
        private int disposed;

        /// <summary>
        /// 构造帧流管理器
        /// </summary>
        /// <param name="options">
        /// 帧流配置（容量、背压策略）；为 <c>null</c> 时使用默认配置。
        /// 依赖抽象而非具体 <see cref="StreamOptions" />，便于容器注入与测试替换
        /// </param>
        public StreamManager(IStreamOptions options = null)
        {
            this.options = options ?? new StreamOptions();
        }

        /// <summary>
        /// 释放所有图像数据流
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref this.disposed, 1) == 1)
                return;

            foreach (var stream in streams.Values)
            {
                try
                {
                    stream.Dispose();
                }
                catch
                {
                    // 释放阶段不向上抛出异常，避免中断其余流的清理
                }
            }

            streams.Clear();
        }

        /// <summary>
        /// 获取或创建指定的相机的图像数据流
        /// </summary>
        /// <param name="cameraKey">相机Key</param>
        /// <returns>
        /// 相机图像数据流
        /// </returns>
        /// <exception cref="ArgumentNullException">cameraKey 为空</exception>
        public ICameraStream GetOrCreateStream(string cameraKey)
        {
            if (string.IsNullOrEmpty(cameraKey))
                throw new ArgumentNullException(nameof(cameraKey));

            var stream = streams.GetOrAdd(cameraKey, _ => new CameraStream(cameraKey, this.options));

            // 与 Dispose 竞态时，字典外的新建流必须就地释放，避免无人持有的流常驻
            if (Volatile.Read(ref this.disposed) == 1)
            {
                stream.Dispose();
                throw new ObjectDisposedException(nameof(StreamManager));
            }

            return stream;
        }

        /// <summary>
        /// 获取指定的相机的图像数据流
        /// </summary>
        /// <param name="cameraKey">相机Key</param>
        /// <param name="stream">相机图像数据流</param>
        /// <returns>
        /// 是否成功获取到图像数据流
        /// </returns>
        public bool GetStream(string cameraKey, out ICameraStream stream)
        {
            stream = null;

            if (string.IsNullOrEmpty(cameraKey))
                return false;

            if (Volatile.Read(ref this.disposed) == 1)
                return false;

            return streams.TryGetValue(cameraKey, out stream);
        }

        /// <summary>
        /// 移除并释放指定相机的图像数据流
        /// </summary>
        /// <param name="cameraKey">相机Key</param>
        /// <returns>
        /// 是否成功移除图像数据流
        /// </returns>
        public bool RemoveStream(string cameraKey)
        {
            if (string.IsNullOrEmpty(cameraKey))
                return false;

            if (Volatile.Read(ref this.disposed) == 1)
                return false;

            if (streams.TryRemove(cameraKey, out var stream))
            {
                try
                {
                    stream.Dispose();
                }
                catch
                {
                    // 释放阶段不向上抛出异常
                }

                return true;
            }

            return false;
        }
    }
}
