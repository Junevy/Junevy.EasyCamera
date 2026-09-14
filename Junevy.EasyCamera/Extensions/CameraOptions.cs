namespace Junevy.EasyCamera.Extensions
{
    /// <summary>
    /// 相机厂商启用配置
    /// </summary>
    public class CameraOptions
    {
        /// <summary>
        /// 是否启用海康（HikVision）相机
        /// </summary>
        public bool EnableHikVision { get; set; }

        /// <summary>
        /// 是否启用Irayple相机
        /// </summary>
        public bool EnableIRayple { get; set; }

        /// <summary>
        /// 是否启用巴斯勒（Basler）相机
        /// </summary>
        public bool EnableBasler { get; set; }
    }
}
