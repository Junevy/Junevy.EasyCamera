using Junevy.EasyCamera.Core.Abstractions;
using MvCameraControl;
using System;

namespace Junevy.EasyCamera.Vendors.HikVision
{
    /// <summary>
    /// 海康工业相机设备信息
    /// </summary>
    public class HikCameraInfo : ICameraInfo
    {
        /// <summary>
        /// 相机序列号
        /// </summary>
        public string SerialNumber { get; }

        /// <summary>
        /// 相机型号
        /// </summary>
        public string ModelName { get; }

        /// <summary>
        /// 用户自定义名称
        /// </summary>
        public string UserDefinedName { get; private set; }

        /// <summary>
        /// 相机厂商
        /// </summary>
        public string Manufacturer { get; }

        /// <summary>
        /// 相机设备版本
        /// </summary>
        public string CameraVersion { get; }

        /// <summary>
        /// 相机接口类型
        /// </summary>
        /// <value>
        /// 相机接口类型
        /// </value>
        public CameraType InterfaceType => this.GetCameraType();

        /// <summary>
        /// 海康原生设备信息，仅供程序集内部创建相机实例使用
        /// </summary>
        internal IDeviceInfo Native { get; }

        /// <summary>
        /// 构造海康相机设备信息
        /// </summary>
        /// <param name="serialNumber">相机序列号</param>
        /// <param name="modelName">相机型号</param>
        /// <param name="userDefinedName">用户自定义名称</param>
        /// <param name="manufacturer">相机厂商</param>
        /// <param name="cameraVersion">相机设备版本</param>
        /// <param name="native">海康原生设备信息</param>
        /// <exception cref="ArgumentNullException">
        /// <c>native</c> 为 <c>null</c>
        /// </exception>
        public HikCameraInfo(string serialNumber, string modelName, string userDefinedName,
            string manufacturer, string cameraVersion, IDeviceInfo native)
        {
            this.SerialNumber = serialNumber;
            this.ModelName = modelName;
            this.UserDefinedName = userDefinedName;
            this.Manufacturer = manufacturer;
            this.CameraVersion = cameraVersion;
            this.Native = native ?? throw new ArgumentNullException(nameof(native));
        }

        /// <summary>
        /// 更新本信息对象的用户自定义名称（仅本地副本，不下发相机设备）。
        /// 设备侧命名请通过相机参数接口设置（如海康 DeviceUserID）。
        /// </summary>
        /// <param name="name">自定义名称，长度不得超过64个字符</param>
        /// <returns>
        /// 是否设置成功
        /// </returns>
        public bool SetDefinedName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;

            if (name.Length > 64)
                return false;

            this.UserDefinedName = name;
            return true;
        }

        /// <summary>
        /// 解析相机接口类型
        /// </summary>
        /// <returns>
        /// 相机接口类型
        /// </returns>
        private CameraType GetCameraType()
        {
            if (this.Native == null)
                return CameraType.Unknown;

            return this.Native.TLayerType switch
            {
                DeviceTLayerType.MvGigEDevice => CameraType.GigE,
                DeviceTLayerType.MvVirGigEDevice => CameraType.GigE,
                DeviceTLayerType.MvUsbDevice => CameraType.Usb,
                DeviceTLayerType.MvVirUsbDevice => CameraType.Usb,
                DeviceTLayerType.MvCameraLinkDevice => CameraType.CameraLink,
                DeviceTLayerType.MvGenTLGigEDevice => CameraType.GenTL,
                DeviceTLayerType.MvGenTLCameraLinkDevice => CameraType.GenTL,
                DeviceTLayerType.MvGenTLCXPDevice => CameraType.GenTL,
                DeviceTLayerType.MvGenTLXoFDevice => CameraType.GenTL,
                _ => CameraType.Unknown,
            };
        }
    }
}
