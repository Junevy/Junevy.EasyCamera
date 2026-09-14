using System;

namespace Junevy.EasyCamera.Core.Abstractions
{
    /// <summary>
    /// 相机SDK系统接口, 用于初始化相机SDK和释放SDK资源
    /// </summary>
    public interface ICameraSdkSystem : IDisposable
    {
        /// <summary>
        /// 初始化相机SDK（幂等，可重复调用）
        /// </summary>
        void Initialize();

        /// <summary>
        /// 释放相机SDK资源
        /// <returns></returns>
        /// </summary>
        void Release();
    }
}
