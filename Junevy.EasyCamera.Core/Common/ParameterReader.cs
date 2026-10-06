using Junevy.EasyCamera.Core.Abstractions;
using System;

namespace Junevy.EasyCamera.Core.Common
{
    /// <summary>
    /// 参数泛型分发器：把 <c>TryGetParam&lt;T&gt;</c> 的 typeof 分发与失败语义集中到一处，
    /// 厂商只需实现 <see cref="IParameterSource" /> 的 6 个原语。
    /// </summary>
    /// <remarks>
    /// 统一失败语义：类型不支持、参数名无效、相机不可用、厂商读取失败一律返回 <c>false</c>；
    /// 调用方据此区分"取值失败"与"取值恰为 default"。
    /// </remarks>
    public static class ParameterReader
    {
        /// <summary>
        /// long → int 的受检转换：越界按"取值失败"处理，禁止静默回绕。
        /// </summary>
        public static bool TryConvertToInt64ToInt32(long value, out int result)
        {
            if (value < int.MinValue || value > int.MaxValue)
            {
                result = 0;
                return false;
            }

            result = (int)value;
            return true;
        }

        /// <summary>
        /// 按目标类型从参数源读取参数值。
        /// </summary>
        /// <typeparam name="T">支持的类型：int、long、float、double、bool、string</typeparam>
        /// <param name="source">厂商参数读取原语</param>
        /// <param name="paramName">参数名</param>
        /// <param name="value">读取成功时的参数值</param>
        /// <returns>读取成功返回 <c>true</c>，否则 <c>false</c> 且 <paramref name="value"/> 为 default</returns>
        public static bool TryRead<T>(IParameterSource source, string paramName, out T value)
        {
            value = default;

            if (source == null || string.IsNullOrEmpty(paramName))
                return false;

            var type = typeof(T);

            try
            {
                if (type == typeof(int))
                {
                    if (!source.TryReadInt(paramName, out var intValue))
                        return false;

                    value = (T)(object)intValue;
                    return true;
                }

                if (type == typeof(long))
                {
                    if (!source.TryReadLong(paramName, out var longValue))
                        return false;

                    value = (T)(object)longValue;
                    return true;
                }

                if (type == typeof(float))
                {
                    if (!source.TryReadFloat(paramName, out var floatValue))
                        return false;

                    value = (T)(object)floatValue;
                    return true;
                }

                if (type == typeof(double))
                {
                    if (!source.TryReadDouble(paramName, out var doubleValue))
                        return false;

                    value = (T)(object)doubleValue;
                    return true;
                }

                if (type == typeof(bool))
                {
                    if (!source.TryReadBool(paramName, out var boolValue))
                        return false;

                    value = (T)(object)boolValue;
                    return true;
                }

                if (type == typeof(string))
                {
                    if (!source.TryReadString(paramName, out var stringValue))
                        return false;

                    value = (T)(object)(stringValue ?? string.Empty);
                    return true;
                }

                return false;
            }
            catch
            {
                // 取参失败必须与"值恰为 default"可区分：异常一律按失败处理
                value = default;
                return false;
            }
        }
    }
}
