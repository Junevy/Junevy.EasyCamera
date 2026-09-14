using Junevy.EasyCamera.Core.Abstractions;
using Junevy.EasyCamera.Core.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System;

namespace Junevy.EasyCamera.Core.Extensions
{
    /// <summary>
    /// 核心服务注册扩展，注册与具体厂商SDK无关的相机基础设施
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// 注册相机核心服务（流管理、相机缓存、聚合提供器、相机服务）
        /// </summary>
        /// <param name="services">服务集合</param>
        /// <returns>
        /// 服务集合
        /// </returns>
        /// <exception cref="ArgumentNullException">services 为 <c>null</c></exception>
        public static IServiceCollection AddEasyCameraCore(this IServiceCollection services)
        {
            if (services == null)
                throw new ArgumentNullException(nameof(services));

            services.TryAddSingleton<StreamOptions>();
            services.TryAddSingleton<StreamManager>();
            services.TryAddSingleton<CameraManager>();
            services.TryAddSingleton<ICameraProvider, AggregateCameraProvider>();
            services.TryAddSingleton<CameraService>();

            return services;
        }
    }
}
