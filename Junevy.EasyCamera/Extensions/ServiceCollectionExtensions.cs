using Junevy.EasyCamera.Core.Abstractions;
using Junevy.EasyCamera.Core.Common;
using Junevy.EasyCamera.Core.Extensions;
using Junevy.EasyCamera.Common;
using Junevy.EasyCamera.Vendors.HikVision;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Junevy.EasyCamera.Extensions
{
    /// <summary>
    /// 相机服务注册扩展
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// 注册相机服务。
        /// 核心基础设施始终注册；厂商提供器按 <see cref="CameraOptions" /> 启用。
        /// 新增相机品牌只需在此追加一个厂商注册方法，无需改动核心层
        /// </summary>
        /// <param name="services">服务集合</param>
        /// <param name="configure">厂商启用配置</param>
        /// <param name="streamConfigure">帧数据流配置</param>
        /// <returns>
        /// 服务集合
        /// </returns>
        /// <exception cref="ArgumentNullException">services 为 <c>null</c></exception>
        /// <exception cref="NotImplementedException">启用了尚未实现的厂商</exception>
        public static IServiceCollection AddEasyCamera(this IServiceCollection services,
            Action<CameraOptions> configure = null,
            Action<StreamOptions> streamConfigure = null)
        {
            if (services == null)
                throw new ArgumentNullException(nameof(services));

            var options = new CameraOptions();
            configure?.Invoke(options);

            var streamOptions = new StreamOptions();
            streamConfigure?.Invoke(streamOptions);

            // 先注册用户配置的实例，AddEasyCameraCore 内部的 TryAdd 不会覆盖它
            services.TryAddSingleton<StreamOptions>(streamOptions);
            services.AddEasyCameraCore();

            // 记录已启用厂商的SDK系统，用于组合为统一的SDK系统入口
            var sdkSystemFactories = new List<Func<IServiceProvider, ICameraSdkSystem>>();

            if (options.EnableHikVision)
            {
                RegisterHikVision(services);
                sdkSystemFactories.Add(provider => provider.GetRequiredService<HikCameraSdkSystem>());
            }

            if (options.EnableIRayple)
            {
                // IRayple 厂商标记为 [Obsolete(..., true)]（未开发完毕），与 Basler 一致，启用时抛出未实现异常
                RegisterIRayple();
            }

            if (options.EnableBasler)
            {
                RegisterBasler();
            }

            if (sdkSystemFactories.Count > 0)
            {
                // 组合SDK系统按工厂延迟构建，避免与 IEnumerable<ICameraSdkSystem> 形成自引用解析
                services.TryAddSingleton<ICameraSdkSystem>(provider =>
                    new CompositeCameraSdkSystem(sdkSystemFactories.Select(factory => factory(provider))));
            }

            return services;
        }

        /// <summary>
        /// 注册仅包含海康相机的相机服务
        /// </summary>
        /// <param name="services">服务集合</param>
        /// <returns>
        /// 服务集合
        /// </returns>
        /// <exception cref="ArgumentNullException">services 为 <c>null</c></exception>
        public static IServiceCollection AddHikVisionCamera(this IServiceCollection services)
        {
            return services.AddEasyCamera(options => options.EnableHikVision = true);
        }

        /// <summary>
        /// 注册仅包含Irayple相机的相机服务
        /// </summary>
        /// <param name="services">服务集合</param>
        /// <returns>
        /// 服务集合
        /// </returns>
        /// <exception cref="ArgumentNullException">services 为 <c>null</c></exception>
        public static IServiceCollection AddIRaypleCamera(this IServiceCollection services)
        {
            return services.AddEasyCamera(options => options.EnableIRayple = true);
        }

        /// <summary>
        /// 注册海康相机厂商服务
        /// </summary>
        /// <param name="services">服务集合</param>
        private static void RegisterHikVision(IServiceCollection services)
        {
            services.TryAddSingleton<HikCameraSdkSystem>();

            // 必须使用“服务类型+实现类型”方式注册，工厂委托注册会让 TryAddEnumerable
            // 按委托签名而非实现类型判重，导致多厂商场景下只注册成功第一个厂商
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IVendorCameraProvider, HikCameraProvider>());
        }

        /// <summary>
        /// 注册Irayple相机厂商服务
        /// </summary>
        /// <exception cref="NotImplementedException">
        /// Irayple 相机支持尚未开发完毕（与厂商实现类的 <c>[Obsolete(..., true)]</c> 标记保持一致）
        /// </exception>
        private static void RegisterIRayple()
        {
            throw new NotImplementedException("IRayple camera support is not yet implemented.");
        }

        /// <summary>
        /// 注册巴斯勒（Basler）相机厂商服务
        /// </summary>
        /// <exception cref="NotImplementedException">
        /// 巴斯勒相机支持尚未实现
        /// </exception>
        private static void RegisterBasler()
        {
            throw new NotImplementedException("Basler camera support is not yet implemented.");
        }
    }
}

namespace Junevy.EasyCamera.Core.Extensions
{
    /// <summary>
    /// 核心服务注册扩展，注册与具体厂商SDK无关的相机基础设施。
    /// 原位于 Junevy.EasyCamera.Core 程序集，随实现类迁移至本程序集；
    /// 保留原命名空间以维持对外契约，类名区分于厂商注册扩展避免同名混淆。
    /// 扩展方法调用点（services.AddEasyCameraCore()）不受类名影响。
    /// </summary>
    public static class CoreServiceCollectionExtensions
    {
        /// <summary>
        /// 注册相机核心服务（流管理、相机缓存、聚合提供器、相机服务）
        /// </summary>
        /// <param name="services">服务集合</param>
        /// <returns>服务集合</returns>
        /// <exception cref="ArgumentNullException">services 为 <c>null</c></exception>
        public static IServiceCollection AddEasyCameraCore(this IServiceCollection services)
        {
            if (services == null)
                throw new ArgumentNullException(nameof(services));

            services.TryAddSingleton<StreamOptions>();
            services.TryAddSingleton<IStreamOptions>(provider => provider.GetRequiredService<StreamOptions>());
            services.TryAddSingleton<StreamManager>();
            services.TryAddSingleton<IStreamManager>(provider => provider.GetRequiredService<StreamManager>());
            services.TryAddSingleton<CameraManager>();
            services.TryAddSingleton<ICameraManager>(provider => provider.GetRequiredService<CameraManager>());
            services.TryAddSingleton<ICameraProvider, AggregateCameraProvider>();
            services.TryAddSingleton<CameraService>();
            services.TryAddSingleton<ICameraService>(provider => provider.GetRequiredService<CameraService>());

            return services;
        }
    }
}
