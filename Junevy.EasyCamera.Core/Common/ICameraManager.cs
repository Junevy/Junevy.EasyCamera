using Junevy.EasyCamera.Core.Abstractions;
using System;

namespace Junevy.EasyCamera.Core.Common
{
    /// <summary>
    /// 相机缓存管理接口，定义相机实例的注册、获取与移除契约
    /// </summary>
    public interface ICameraManager : IDisposable
    {
        /// <summary>
        /// 注册相机实例
        /// </summary>
        /// <param name="cameraKey">相机序列号</param>
        /// <param name="camera">相机实例</param>
        /// <exception cref="ArgumentNullException">
        /// <c>cameraKey</c> 或 <c>camera</c> 为 <c>null</c>
        /// </exception>
        bool TryRegister(string cameraKey, ICamera camera);

        /// <summary>
        /// 尝试获取相机实例
        /// </summary>
        /// <param name="cameraKey">相机序列号</param>
        /// <param name="camera">相机实例</param>
        /// <returns>
        /// 是否成功获取相机实例
        /// </returns>
        bool TryGet(string cameraKey, out ICamera camera);

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
        bool TryRemove(string cameraKey);
    }
}
