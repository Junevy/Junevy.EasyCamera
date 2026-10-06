using Junevy.EasyCamera.Core.Abstractions;
using Junevy.EasyCamera.Core.Common;
using Junevy.EasyCamera.Common;
using MvCameraControl;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Junevy.EasyCamera.Vendors.HikVision
{
    /// <summary>
    /// 海康工业相机提供器
    /// </summary>
    public class HikCameraProvider : IVendorCameraProvider, ILinkStatusProbeProvider
    {
        /// <summary>
        /// 海康工业相机提供器
        /// </summary>
        private readonly IStreamOptions streamOptions;

        /// <summary>
        /// 构造海康工业相机提供器
        /// </summary>
        /// <param name="streamOptions">相机帧数据流配置，为 <c>null</c> 时使用默认配置</param>
        public HikCameraProvider(IStreamOptions streamOptions = null)
        {
            this.streamOptions = streamOptions ?? new StreamOptions();
        }

        /// <summary>
        /// 厂商名称
        /// </summary>
        public string VendorName => "HikVision";

        /// <summary>
        /// 判断当前提供器能否创建指定相机信息的相机实例
        /// </summary>
        /// <param name="info">相机信息</param>
        /// <returns>
        /// 能创建返回 <c>true</c>；否则 <c>false</c>
        /// </returns>
        public bool Supports(ICameraInfo info) => info is HikCameraInfo;

        /// <summary>
        /// 创建指定相机信息的相机实例
        /// </summary>
        /// <param name="info">相机信息</param>
        /// <param name="stream">相机帧数据流</param>
        /// <returns>
        /// 相机实例
        /// </returns>
        /// <exception cref="ArgumentException">相机信息类型不被当前提供器支持</exception>
        /// <exception cref="ArgumentNullException">stream 为 <c>null</c></exception>
        public ICamera Create(ICameraInfo info, ICameraStream stream)
        {
            if (info is not HikCameraInfo hikInfo)
                throw new ArgumentException("Invalid camera info type.", nameof(info));

            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            var camera = new HikCamera(hikInfo.Native, stream);

            // 缓冲区配置是可选能力：厂商不支持时静默忽略，不影响相机可用性
            if (this.streamOptions.CameraBufferCapacity > 0 && camera is IBufferConfigurable configurable)
                configurable.SetBufferCount(this.streamOptions.CameraBufferCapacity);

            return camera;
        }

        /// <summary>
        /// 枚举所有相机信息
        /// </summary>
        /// <returns>
        /// 相机信息列表
        /// </returns>
        public IEnumerable<ICameraInfo> Enumerate() => this.Enumerate(CameraInterfaceType.All);

        /// <summary>
        /// 枚举指定接口类型的相机信息
        /// </summary>
        /// <param name="type">相机接口类型</param>
        /// <returns>
        /// 相机信息列表
        /// </returns>
        public IEnumerable<ICameraInfo> Enumerate(CameraInterfaceType type)
        {
            var result = DeviceEnumerator.EnumDevices(ToLayerType(type), out var deviceInfos);

            if (result != MvError.MV_OK || deviceInfos == null)
            {
                // 枚举失败目前只能以空集合表达；输出诊断便于现场排查设备缺失问题
                Trace.TraceWarning($"HikCameraProvider.Enumerate failed with code {result}.");
                yield break;
            }

            foreach (var device in deviceInfos)
            {
                if (device == null)
                    continue;

                yield return new HikCameraInfo(
                    serialNumber: device.SerialNumber,
                    modelName: device.ModelName,
                    userDefinedName: device.UserDefinedName,
                    manufacturer: this.VendorName,
                    cameraVersion: device.DeviceVersion,
                    native: device);
            }
        }

        /// <summary>
        /// 将统一的相机接口类型映射为海康设备传输层类型
        /// </summary>
        /// <param name="type">相机接口类型</param>
        /// <returns>
        /// 海康设备传输层类型
        /// </returns>
        private static DeviceTLayerType ToLayerType(CameraInterfaceType type)
        {
            return type switch
            {
                CameraInterfaceType.GigE => DeviceTLayerType.MvGigEDevice,
                CameraInterfaceType.Usb => DeviceTLayerType.MvUsbDevice,
                CameraInterfaceType.CameraLink => DeviceTLayerType.MvCameraLinkDevice,

                // 必须覆盖全部 GenTL 子类型，否则 Enumerate(GenTL) 的结果集合
                // 与 HikCameraInfo.InterfaceType 判定为 GenTL 的设备集合不一致
                CameraInterfaceType.GenTL => DeviceTLayerType.MvGenTLGigEDevice
                                  | DeviceTLayerType.MvGenTLCameraLinkDevice
                                  | DeviceTLayerType.MvGenTLCXPDevice
                                  | DeviceTLayerType.MvGenTLXoFDevice,

                // ALL 必须覆盖 SDK 支持的所有设备传输层。
                CameraInterfaceType.All => DeviceTLayerType.MvGigEDevice
                               | DeviceTLayerType.MvUsbDevice
                               | DeviceTLayerType.MvCameraLinkDevice
                               | DeviceTLayerType.MvVirGigEDevice
                               | DeviceTLayerType.MvVirUsbDevice
                               | DeviceTLayerType.MvGenTLGigEDevice
                               | DeviceTLayerType.MvGenTLCameraLinkDevice
                               | DeviceTLayerType.MvGenTLCXPDevice
                               | DeviceTLayerType.MvGenTLXoFDevice,

                // Unknown 代表调用方没有指定有效类型，不能扩大为 ALL。
                CameraInterfaceType.Unknown => (DeviceTLayerType)0,
                _ => (DeviceTLayerType)0,
            };
        }

        /// <summary>
        /// 探测指定相机的链路状态：按接口类型缩小枚举范围后重新枚举取新鲜设备信息，
        /// 再调用海康可达性查询。不信任调用方传入的原生引用（可能过期）；
        /// 查询使用独占模式，与 HikCamera 无参 Open 的默认权限一致，"可达"≈"可连接"。
        /// </summary>
        /// <param name="info">相机信息，至少需提供序列号</param>
        /// <param name="cancellationToken">取消令牌；枚举步骤间检查</param>
        /// <returns>
        /// 可达 → <see cref="CameraLinkStatus.Idle"/>；在线但不可达 → <see cref="CameraLinkStatus.Occupied"/>；
        /// 枚举中不存在（掉线/未上电）→ <see cref="CameraLinkStatus.Unreachable"/>
        /// </returns>
        public CameraLinkStatus ProbeLinkStatus(ICameraInfo info, CancellationToken cancellationToken = default)
        {
            if (info == null || string.IsNullOrEmpty(info.SerialNumber))
                return CameraLinkStatus.Unknown;

            cancellationToken.ThrowIfCancellationRequested();

            // 接口类型未知时只能全量枚举；已知类型可显著缩小单次探测成本
            var scope = info.InterfaceType == CameraInterfaceType.Unknown
                ? CameraInterfaceType.All
                : info.InterfaceType;

            HikCameraInfo fresh = null;
            foreach (var candidate in this.Enumerate(scope))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (candidate is HikCameraInfo hik
                    && string.Equals(hik.SerialNumber, info.SerialNumber, StringComparison.Ordinal))
                {
                    fresh = hik;
                    break;
                }
            }

            if (fresh == null)
                return CameraLinkStatus.Unreachable;

            var accessible = DeviceEnumerator.IsDeviceAccessible(fresh.Native, DeviceAccessMode.AccessExclusive);
            return accessible ? CameraLinkStatus.Idle : CameraLinkStatus.Occupied;
        }
    }
}
