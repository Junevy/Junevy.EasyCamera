namespace Junevy.EasyCamera.Core.Abstractions
{
    /// <summary>
    /// 图像像素格式枚举
    /// </summary>
    public enum ImagePixelFormat
    {
        Unknown = 0,

        Mono8,
        Mono10,
        Mono12,
        Mono14,
        Mono16,

        BayerRG8,
        BayerRG10,
        BayerRG12,
        BayerGB8,
        BayerBG8,

        RGB8,
        BGR8,

        YUV422,
        YUV444,

        Mono10Packed,
        BayerRG12Packed
    }
}
