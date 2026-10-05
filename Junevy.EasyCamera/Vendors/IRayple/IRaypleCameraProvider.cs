using Junevy.EasyCamera.Core.Abstractions;
using Junevy.EasyCamera.Common;
using MVSDK_Net;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using static MVSDK_Net.IMVDefine;

namespace Junevy.EasyCamera.Vendors.IRayple
{
    /// <summary>
    /// Irayple工业相机提供器
    /// </summary>
    [Obsolete("未开发完毕", true)]
    public class IRaypleCameraProvider : IVendorCameraProvider
    {
        /// <summary>
        /// 相机帧数据流配置
        /// </summary>
        private readonly StreamOptions streamOptions;

        /// <summary>
        /// 构造Irayple工业相机提供器
        /// </summary>
        /// <param name="streamOptions">相机帧数据流配置，为 <c>null</c> 时使用默认配置</param>
        public IRaypleCameraProvider(StreamOptions streamOptions = null)
        {
            this.streamOptions = streamOptions ?? new StreamOptions();
        }

        /// <summary>
        /// 厂商名称
        /// </summary>
        public string VendorName => "iRAYPLE";

        /// <summary>
        /// 判断当前提供器能否创建指定相机信息的相机实例
        /// </summary>
        /// <param name="info">相机信息</param>
        /// <returns>
        /// 能创建返回 <c>true</c>；否则 <c>false</c>
        /// </returns>
        public bool Supports(ICameraInfo info) => info is IRaypleCameraInfo;

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
            if (info is not IRaypleCameraInfo iraypleInfo)
                throw new ArgumentException("Invalid camera info type.", nameof(info));

            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            var camera = new IRaypleCamera(iraypleInfo.Native, stream);

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
            var deviceList = new IMV_DeviceList();
            var result = MyCamera.IMV_EnumDevices(ref deviceList, (uint)ToInterfaceType(type));

            if (result != IMV_OK || deviceList.nDevNum == 0 || deviceList.pDevInfo == IntPtr.Zero)
                yield break;

            var structureSize = Marshal.SizeOf(typeof(IMV_DeviceInfo));

            for (var i = 0; i < deviceList.nDevNum; i++)
            {
                var native = (IMV_DeviceInfo)Marshal.PtrToStructure(
                    IntPtr.Add(deviceList.pDevInfo, structureSize * i),
                    typeof(IMV_DeviceInfo));

                var cameraInfo = ToCameraInfo(native);

                // 单个设备信息解析失败不应中断整个枚举
                if (cameraInfo != null)
                    yield return cameraInfo;
            }
        }

        /// <summary>
        /// 将原生设备信息转换为相机信息
        /// </summary>
        /// <param name="native">Irayple原生设备信息</param>
        /// <returns>
        /// 相机信息，解析失败返回 <c>null</c>
        /// </returns>
        private IRaypleCameraInfo ToCameraInfo(IMV_DeviceInfo native)
        {
            try
            {
                return new IRaypleCameraInfo(
                    serialNumber: native.serialNumber,
                    modelName: native.modelName,
                    userDefinedName: native.cameraName,
                    manufacturer: string.IsNullOrEmpty(native.vendorName) ? this.VendorName : native.vendorName,
                    cameraVersion: native.deviceVersion,
                    cameraKey: native.cameraKey,
                    native: native);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 将统一的相机接口类型映射为Irayple接口类型
        /// </summary>
        /// <param name="type">相机接口类型</param>
        /// <returns>
        /// Irayple接口类型
        /// </returns>
        private static IMV_EInterfaceType ToInterfaceType(CameraInterfaceType type)
        {
            return type switch
            {
                CameraInterfaceType.GigE => IMV_EInterfaceType.interfaceTypeGige,
                CameraInterfaceType.Usb => IMV_EInterfaceType.interfaceTypeUsb3,
                CameraInterfaceType.CameraLink => IMV_EInterfaceType.interfaceTypeCL,
                CameraInterfaceType.GenTL => IMV_EInterfaceType.interfaceTypePCIe,
                _ => IMV_EInterfaceType.interfaceTypeAll
            };
        }
    }
}
