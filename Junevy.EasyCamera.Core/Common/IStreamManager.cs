using Junevy.EasyCamera.Core.Abstractions;
using System;

namespace Junevy.EasyCamera.Core.Common
{
    /// <summary>
    /// 相机图像帧数据流管理接口，定义相机图像数据流的获取、创建与移除契约
    /// </summary>
    public interface IStreamManager : IDisposable
    {
        /// <summary>
        /// 获取或创建指定的相机的图像数据流
        /// </summary>
        /// <param name="cameraKey">相机Key</param>
        /// <returns>
        /// 相机图像数据流
        /// </returns>
        /// <exception cref="ArgumentNullException">cameraKey 为空</exception>
        ICameraStream GetOrCreateStream(string cameraKey);

        /// <summary>
        /// 获取指定的相机的图像数据流
        /// </summary>
        /// <param name="cameraKey">相机Key</param>
        /// <param name="stream">相机图像数据流</param>
        /// <returns>
        /// 是否成功获取到图像数据流
        /// </returns>
        bool GetStream(string cameraKey, out ICameraStream stream);

        /// <summary>
        /// 移除并释放指定相机的图像数据流
        /// </summary>
        /// <param name="cameraKey">相机Key</param>
        /// <returns>
        /// 是否成功移除图像数据流
        /// </returns>
        bool RemoveStream(string cameraKey);
    }
}
