namespace Junevy.EasyCamera.Core.Abstractions
{
    /// <summary>
    /// 相机信息可命名能力接口（可选能力，接口隔离）。
    /// 注意：实现只更新本地副本，不下发相机设备；设备侧命名须通过相机参数接口设置。
    /// </summary>
    public interface INamedCameraInfo
    {
        /// <summary>更新本信息对象的用户自定义名称（仅本地副本）</summary>
        /// <param name="name">自定义名称，长度不得超过64个字符</param>
        /// <returns>是否设置成功</returns>
        bool SetDefinedName(string name);
    }
}
