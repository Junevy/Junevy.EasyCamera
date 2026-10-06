using Junevy.EasyCamera.Core.Abstractions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Junevy.EasyCamera.Common
{
    /// <summary>
    /// 聚合相机提供器，将多个厂商提供器聚合为一个统一入口：
    /// 枚举时合并所有厂商的设备；创建时按相机信息类型分发到对应厂商。
    /// </summary>
    /// <remarks>
    /// 厂商独有能力按"能力接口"探测（<see cref="ILinkStatusProbeProvider" />）：
    /// 没有厂商具备该能力时返回 <see cref="CameraLinkStatus.Unknown" />，
    /// 由调用方决定回退策略，新增厂商无需改动本类。
    /// </remarks>
    public sealed class AggregateCameraProvider : ICameraProvider, ILinkStatusProbeProvider
    {
        private readonly IVendorCameraProvider[] vendors;

        /// <summary>
        /// 已注册的厂商提供器列表
        /// </summary>
        public IReadOnlyList<IVendorCameraProvider> Vendors => this.vendors;

        /// <summary>
        /// 构造聚合相机提供器
        /// </summary>
        /// <param name="vendors">厂商提供器列表</param>
        /// <exception cref="ArgumentNullException">vendors 为 <c>null</c></exception>
        public AggregateCameraProvider(IEnumerable<IVendorCameraProvider> vendors)
        {
            this.vendors = (vendors ?? throw new ArgumentNullException(nameof(vendors))).ToArray();
        }

        /// <summary>
        /// 判断是否有厂商能够创建指定相机信息的相机实例
        /// </summary>
        /// <param name="info">相机信息</param>
        /// <returns>
        /// 存在支持的厂商返回 <c>true</c>；否则 <c>false</c>
        /// </returns>
        public bool Supports(ICameraInfo info)
        {
            return this.vendors.Any(v => v.Supports(info));
        }

        /// <summary>
        /// 创建指定相机信息的相机实例，自动分发到支持该相机信息的厂商
        /// </summary>
        /// <param name="info">相机信息</param>
        /// <param name="stream">相机帧数据流</param>
        /// <returns>
        /// 相机实例
        /// </returns>
        /// <exception cref="ArgumentNullException">info 为 <c>null</c></exception>
        /// <exception cref="ArgumentException">没有已注册厂商支持该相机信息</exception>
        public ICamera Create(ICameraInfo info, ICameraStream stream)
        {
            if (info == null)
                throw new ArgumentNullException(nameof(info));

            var vendor = this.vendors.FirstOrDefault(v => v.Supports(info));

            if (vendor == null)
                throw new ArgumentException($"No registered vendor supports the camera '{info.SerialNumber}'.", nameof(info));

            return vendor.Create(info, stream);
        }

        /// <summary>
        /// 枚举所有厂商的相机信息
        /// </summary>
        /// <returns>
        /// 相机信息列表
        /// </returns>
        public IEnumerable<ICameraInfo> Enumerate()
        {
            return Enumerate(CameraInterfaceType.All);
        }

        /// <summary>
        /// 枚举所有厂商指定接口类型的相机信息
        /// </summary>
        /// <param name="type">相机接口类型</param>
        /// <returns>
        /// 相机信息列表
        /// </returns>
        public IEnumerable<ICameraInfo> Enumerate(CameraInterfaceType type)
        {
            return this.vendors.SelectMany(v => v.Enumerate(type));
        }

        /// <summary>
        /// 探测指定相机的链路状态：按厂商支持关系分发到第一个具备
        /// <see cref="ILinkStatusProbeProvider" /> 能力的厂商提供器。
        /// 没有厂商具备该能力时返回 <see cref="CameraLinkStatus.Unknown"/>，由调用方决定回退策略
        /// </summary>
        /// <param name="info">相机信息</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>探测结果；无厂商具备探测能力时返回 <see cref="CameraLinkStatus.Unknown"/></returns>
        public CameraLinkStatus ProbeLinkStatus(ICameraInfo info, CancellationToken cancellationToken = default)
        {
            if (info == null)
                return CameraLinkStatus.Unknown;

            foreach (var vendor in this.vendors)
            {
                if (vendor is not ILinkStatusProbeProvider probeProvider || !vendor.Supports(info))
                    continue;

                return probeProvider.ProbeLinkStatus(info, cancellationToken);
            }

            return CameraLinkStatus.Unknown;
        }
    }
}
