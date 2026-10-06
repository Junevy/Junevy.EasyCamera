namespace Junevy.EasyCamera.Core.Abstractions
{
    /// <summary>
    /// 厂商参数读取原语接口。
    /// 厂商实现只需提供 6 个"读一个具体类型"的方法，泛型分发与失败语义
    /// （类型不支持、相机不可用一律返回 false）由 <see cref="Junevy.EasyCamera.Core.Common.ParameterReader" />
    /// 统一处理，避免每个厂商各写一遍 typeof 链。
    /// </summary>
    public interface IParameterSource
    {
        /// <summary>读取整型参数；厂商原生为 64 位时须做受检转换，越界按失败处理</summary>
        bool TryReadInt(string paramName, out int value);

        /// <summary>读取 64 位整型参数</summary>
        bool TryReadLong(string paramName, out long value);

        /// <summary>读取单精度浮点参数</summary>
        bool TryReadFloat(string paramName, out float value);

        /// <summary>读取双精度浮点参数（原生为单精度时可由单精度转换而来）</summary>
        bool TryReadDouble(string paramName, out double value);

        /// <summary>读取布尔参数</summary>
        bool TryReadBool(string paramName, out bool value);

        /// <summary>读取字符串参数；厂商返回 null 时统一为 <c>null</c>，由分发层兜底为空字符串</summary>
        bool TryReadString(string paramName, out string value);
    }
}
