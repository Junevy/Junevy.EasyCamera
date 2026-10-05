using Junevy.EasyCamera.Core.Abstractions;
using System;
using static MVSDK_Net.IMVDefine;

namespace Junevy.EasyCamera.Vendors.IRayple
{
    /// <summary>
    /// Irayple工业相机设备信息
    /// </summary>
    [Obsolete("未开发完毕", true)]
    public class IRaypleCameraInfo : ICameraInfo
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
        public CameraInterfaceType InterfaceType => this.GetCameraInterface();

        /// <summary>
        /// Irayple原生设备信息，仅供程序集内部创建相机实例使用
        /// </summary>
        internal IMV_DeviceInfo Native { get; }

        /// <summary>
        /// Irayple相机句柄标识，用于按句柄模式创建相机
        /// </summary>
        public string CameraKey { get; }

        /// <summary>
        /// 构造Irayple工业相机设备信息
        /// </summary>
        /// <param name="serialNumber">相机序列号</param>
        /// <param name="modelName">相机型号</param>
        /// <param name="userDefinedName">用户自定义名称</param>
        /// <param name="manufacturer">相机厂商</param>
        /// <param name="cameraVersion">相机设备版本</param>
        /// <param name="cameraKey">相机句柄标识</param>
        /// <param name="native">Irayple原生设备信息</param>
        public IRaypleCameraInfo(string serialNumber, string modelName, string userDefinedName,
            string manufacturer, string cameraVersion, string cameraKey, IMV_DeviceInfo native)
        {
            this.SerialNumber = serialNumber;
            this.ModelName = modelName;
            this.UserDefinedName = userDefinedName;
            this.Manufacturer = manufacturer;
            this.CameraVersion = cameraVersion;
            this.CameraKey = cameraKey;
            this.Native = native;
        }

        /// <summary>
        /// 更新本信息对象的用户自定义名称（仅本地副本，不下发相机设备）。
        /// 设备侧命名请通过相机参数接口设置。
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
        private CameraInterfaceType GetCameraInterface()
        {
            return this.Native.nInterfaceType switch
            {
                IMV_EInterfaceType.interfaceTypeGige => CameraInterfaceType.GigE,
                IMV_EInterfaceType.interfaceTypeUsb3 => CameraInterfaceType.Usb,
                IMV_EInterfaceType.interfaceTypeCL => CameraInterfaceType.CameraLink,
                IMV_EInterfaceType.interfaceTypePCIe => CameraInterfaceType.GenTL,
                _ => CameraInterfaceType.Unknown,
            };
        }
    }
}
