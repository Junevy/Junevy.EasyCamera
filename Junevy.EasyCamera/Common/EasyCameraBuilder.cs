using Junevy.EasyCamera.Common;
using Junevy.EasyCamera.Core.Abstractions;
using Junevy.EasyCamera.Vendors.HikVision;
using System;
using System.Collections.Generic;

namespace Junevy.EasyCamera
{
    /// <summary>
    /// 非 DI 场景的一行式入口：组装并返回可直接使用的相机栈（SDK 系统 + 服务门面）。
    /// 与 <c>AddEasyCamera</c> 语义对齐，但不依赖 Microsoft.Extensions.DependencyInjection，
    /// 适配 Prism 等自带容器的框架：Build 后将 <see cref="EasyCameraHost.Service" /> /
    /// <see cref="EasyCameraHost.Sdk" /> 以单例实例注册进宿主容器即可。
    /// </summary>
    public static class EasyCamera
    {
        /// <summary>创建构建器，用于逐步配置</summary>
        public static EasyCameraBuilder CreateBuilder() => new EasyCameraBuilder();

        /// <summary>一行式创建：EasyCamera.Create(b => b.EnableHikVision())</summary>
        /// <param name="configure">构建器配置委托</param>
        /// <exception cref="ArgumentNullException">configure 为 <c>null</c></exception>
        /// <exception cref="InvalidOperationException">未启用任何厂商</exception>
        /// <exception cref="NotImplementedException">启用了尚未实现的厂商（IRayple/Basler）</exception>
        public static EasyCameraHost Create(Action<EasyCameraBuilder> configure)
        {
            if (configure == null)
                throw new ArgumentNullException(nameof(configure));

            var builder = new EasyCameraBuilder();
            configure(builder);
            return builder.Build();
        }
    }

    /// <summary>
    /// 相机栈构建器。Build 不会自动初始化 SDK（构造不做原生调用）；
    /// 拿到 <see cref="EasyCameraHost" /> 后调用 host.Sdk.Initialize() 启动。
    /// </summary>
    public sealed class EasyCameraBuilder
    {
        private readonly StreamOptions streamOptions = new StreamOptions();
        private readonly List<Func<StreamOptions, IVendorCameraProvider>> providerFactories = new List<Func<StreamOptions, IVendorCameraProvider>>();
        private readonly List<Func<StreamOptions, ICameraSdkSystem>> sdkFactories = new List<Func<StreamOptions, ICameraSdkSystem>>();

        /// <summary>启用海康（HikVision）相机支持</summary>
        public EasyCameraBuilder EnableHikVision()
        {
            this.providerFactories.Add(o => new HikCameraProvider(o));
            this.sdkFactories.Add(_ => new HikCameraSdkSystem());
            return this;
        }

        /// <summary>启用 IRayple 相机支持（未实现，与厂商类型的 Obsolete 标记一致）</summary>
        /// <exception cref="NotImplementedException">IRayple 相机支持尚未开发完毕</exception>
        public EasyCameraBuilder EnableIRayple()
            => throw new NotImplementedException("IRayple camera support is not yet implemented.");

        /// <summary>启用 Basler 相机支持（未实现）</summary>
        /// <exception cref="NotImplementedException">Basler 相机支持尚未实现</exception>
        public EasyCameraBuilder EnableBasler()
            => throw new NotImplementedException("Basler camera support is not yet implemented.");

        /// <summary>
        /// 配置帧数据流选项（可多次调用，效果累计；未配置项使用与 DI 一致的默认值）
        /// </summary>
        public EasyCameraBuilder WithStreamOptions(Action<StreamOptions> configure)
        {
            if (configure == null)
                throw new ArgumentNullException(nameof(configure));

            configure(this.streamOptions);
            return this;
        }

        /// <summary>
        /// 组装相机栈。至少须启用一个厂商；同一厂商重复启用会被去重为单实例语义
        /// （当前仅海康可用，重复 EnableHikVision 等价于一次）。
        /// </summary>
        public EasyCameraHost Build()
        {
            if (this.providerFactories.Count == 0)
                throw new InvalidOperationException("At least one camera vendor must be enabled before Build().");

            var providers = new List<IVendorCameraProvider>();
            var sdkSystems = new List<ICameraSdkSystem>();
            foreach (var factory in this.providerFactories)
                providers.Add(factory(this.streamOptions));
            foreach (var factory in this.sdkFactories)
                sdkSystems.Add(factory(this.streamOptions));

            var sdk = sdkSystems.Count == 1 ? sdkSystems[0] : new CompositeCameraSdkSystem(sdkSystems);
            var cameraManager = new CameraManager();

            // 帧流管理器必须拿到同一份配置，否则背压策略/容量不会生效
            var streamManager = new StreamManager(this.streamOptions);
            var service = new CameraService(
                new AggregateCameraProvider(providers),
                cameraManager,
                streamManager,
                this.streamOptions);

            return new EasyCameraHost(sdk, service, this.streamOptions, cameraManager, streamManager);
        }
    }
}
