using Junevy.EasyCamera.Core.Abstractions;
using System;
using System.Collections.Concurrent;

namespace Junevy.EasyCamera.Core.Common
{
    /// <summary>
    /// 相机图像帧数据流管理类，用于管理相机的图像数据流
    /// </summary>
    public class StreamManager : IDisposable
    {
        private readonly ConcurrentDictionary<string, ICameraStream> streams = new();

        /// <summary>
        /// 释放所有图像数据流
        /// </summary>
        public void Dispose()
        {
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
        /// <param name="userDefinedName">相机自定义名称</param>
        /// <returns>
        /// 相机图像数据流
        /// </returns>
        /// <exception cref="ArgumentNullException">userDefinedName 为空</exception>
        public ICameraStream GetOrCreateStream(string userDefinedName)
        {
            if (string.IsNullOrEmpty(userDefinedName))
                throw new ArgumentNullException(nameof(userDefinedName));

            return streams.GetOrAdd(userDefinedName, _ => new CameraStream(userDefinedName));
        }

        /// <summary>
        /// 获取指定的相机的图像数据流
        /// </summary>
        /// <param name="userDefinedName">相机自定义名称</param>
        /// <param name="stream">相机图像数据流</param>
        /// <returns>
        /// 是否成功获取到图像数据流
        /// </returns>
        public bool GetStream(string userDefinedName, out ICameraStream stream)
        {
            stream = null;

            if (string.IsNullOrEmpty(userDefinedName))
                return false;

            return streams.TryGetValue(userDefinedName, out stream);
        }

        /// <summary>
        /// 移除并释放指定相机的图像数据流
        /// </summary>
        /// <param name="userDefinedName">相机自定义名称</param>
        /// <returns>
        /// 是否成功移除图像数据流
        /// </returns>
        public bool RemoveStream(string userDefinedName)
        {
            if (string.IsNullOrEmpty(userDefinedName))
                return false;

            if (streams.TryRemove(userDefinedName, out var stream))
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
