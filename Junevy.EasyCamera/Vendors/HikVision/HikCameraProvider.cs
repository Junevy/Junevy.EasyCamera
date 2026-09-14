using Junevy.EasyCamera.Core.Abstractions;
using Junevy.EasyCamera.Core.Common;
using MvCameraControl;
using System;
using System.Collections.Generic;

namespace Junevy.EasyCamera.Vendors.HikVision
{
    /// <summary>
    /// 海康工业相机提供器
    /// </summary>
    public class HikCameraProvider : IVendorCameraProvider
    {
        /// <summary>
        /// 相机帧数据流配置
        /// </summary>
        private readonly StreamOptions streamOptions;

        /// <summary>
        /// 构造海康工业相机提供器
        /// </summary>
        /// <param name="streamOptions">相机帧数据流配置，为 <c>null</c> 时使用默认配置</param>
        public HikCameraProvider(StreamOptions streamOptions = null)
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

            if (this.streamOptions.CameraBufferCapacity > 0)
                camera.SetBufferCount(this.streamOptions.CameraBufferCapacity);

            return camera;
        }

        /// <summary>
        /// 枚举所有相机信息
        /// </summary>
        /// <returns>
        /// 相机信息列表
        /// </returns>
        public IEnumerable<ICameraInfo> Enumerate() => this.Enumerate(CameraType.ALL);

        /// <summary>
        /// 枚举指定接口类型的相机信息
        /// </summary>
        /// <param name="type">相机接口类型</param>
        /// <returns>
        /// 相机信息列表
        /// </returns>
        public IEnumerable<ICameraInfo> Enumerate(CameraType type)
        {
            var result = DeviceEnumerator.EnumDevices(ToLayerType(type), out var deviceInfos);

            if (result != MvError.MV_OK || deviceInfos == null)
                yield break;

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
        private static DeviceTLayerType ToLayerType(CameraType type)
        {
            return type switch
            {
                CameraType.GigE => DeviceTLayerType.MvGigEDevice,
                CameraType.Usb => DeviceTLayerType.MvUsbDevice,
                CameraType.CameraLink => DeviceTLayerType.MvCameraLinkDevice,

                // 必须覆盖全部 GenTL 子类型，否则 Enumerate(GenTL) 的结果集合
                // 与 HikCameraInfo.InterfaceType 判定为 GenTL 的设备集合不一致
                CameraType.GenTL => DeviceTLayerType.MvGenTLGigEDevice
                                  | DeviceTLayerType.MvGenTLCameraLinkDevice
                                  | DeviceTLayerType.MvGenTLCXPDevice
                                  | DeviceTLayerType.MvGenTLXoFDevice,

                // ALL/Unknown：必须覆盖上面所有具体类型，否则 Enumerate(ALL) 与 Enumerate(具体类型)
                // 的结果集合不一致（历史上漏掉了 MvCameraLinkDevice）
                _ => DeviceTLayerType.MvGigEDevice
                     | DeviceTLayerType.MvUsbDevice
                     | DeviceTLayerType.MvCameraLinkDevice
                     | DeviceTLayerType.MvGenTLCXPDevice
                     | DeviceTLayerType.MvGenTLCameraLinkDevice
                     | DeviceTLayerType.MvGenTLXoFDevice
            };
        }
    }
}
