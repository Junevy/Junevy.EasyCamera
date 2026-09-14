using System;

namespace Junevy.EasyCamera.Core.Abstractions
{
    /// <summary>
    /// Camera接口，定义相机生命周期、取流与参数访问的统一抽象
    /// </summary>
    public interface ICamera : IDisposable
    {
        /// <summary>
        /// 是否已打开
        /// </summary>
        /// <value>
        /// <c>true</c> if已打开;否则，<c>false</c>。
        /// </value>
        bool IsOpen { get; }

        /// <summary>
        /// 是否正在取流
        /// </summary>
        /// <value>
        /// <c>true</c> if正在取流;否则，<c>false</c>。
        /// </value>
        bool IsGrabbing { get; }

        /// <summary>
        /// 打开Camera
        /// </summary>
        /// <returns>
        /// <see cref="CameraResult" />
        /// </returns>
        CameraResult Open();

        /// <summary>
        /// 关闭Camera
        /// </summary>
        /// <returns>
        /// <see cref="CameraResult" />
        /// </returns>
        CameraResult Close();

        /// <summary>
        /// 开始取流
        /// </summary>
        /// <returns>
        /// <see cref="CameraResult" />
        /// </returns>
        CameraResult Grab();

        /// <summary>
        /// 停止取流
        /// </summary>
        void StopGrab();

        /// <summary>
        /// 获取相机序列号
        /// </summary>
        /// <returns>
        /// 相机序列号，未知时返回空字符串
        /// </returns>
        string GetSerialNumber();

        /// <summary>
        /// 设置整型参数
        /// </summary>
        /// <param name="paramName">参数名</param>
        /// <param name="value">参数值</param>
        /// <returns>
        /// <see cref="CameraResult" />
        /// </returns>
        CameraResult SetParam(string paramName, int value);

        /// <summary>
        /// 设置浮点型参数
        /// </summary>
        /// <param name="paramName">参数名</param>
        /// <param name="value">参数值</param>
        /// <returns>
        /// <see cref="CameraResult" />
        /// </returns>
        CameraResult SetParam(string paramName, float value);

        /// <summary>
        /// 设置布尔型参数
        /// </summary>
        /// <param name="paramName">参数名</param>
        /// <param name="value">参数值</param>
        /// <returns>
        /// <see cref="CameraResult" />
        /// </returns>
        CameraResult SetParam(string paramName, bool value);

        /// <summary>
        /// 设置字符串参数
        /// </summary>
        /// <param name="paramName">参数名</param>
        /// <param name="value">参数值</param>
        /// <returns>
        /// <see cref="CameraResult" />
        /// </returns>
        CameraResult SetParam(string paramName, string value);

        /// <summary>
        /// 设置枚举参数
        /// </summary>
        /// <param name="paramName">参数名</param>
        /// <param name="value">枚举符号名</param>
        /// <returns>
        /// <see cref="CameraResult" />
        /// </returns>
        CameraResult SetEnumParam(string paramName, string value);

        /// <summary>
        /// 获取参数
        /// </summary>
        /// <param name="paramName">参数名</param>
        /// <returns>
        /// 参数值，类型不支持或相机不可用时返回 default(T)
        /// </returns>
        T GetParam<T>(string paramName);

        /// <summary>
        /// 获取枚举参数的符号名
        /// </summary>
        /// <param name="paramName">参数名</param>
        /// <returns>
        /// 枚举符号名，获取失败时返回空字符串
        /// </returns>
        string GetEnumValue(string paramName);

        /// <summary>
        /// 执行命令
        /// </summary>
        /// <param name="command">命令</param>
        /// <returns>
        /// <see cref="CameraResult" />
        /// </returns>
        CameraResult ExecuteCommand(string command);
    }
}
