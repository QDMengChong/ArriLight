using System.Globalization;

namespace ArriOpto.BLE.Ota;

/// <summary>
/// 协议日志、固件 metadata 和配置使用的十六进制转换工具。
/// </summary>
public static class HexCodec
{
    /// <summary>
    /// 判断字符串是否为偶数长度的十六进制；指定
    /// <paramref name="byteCount"/> 时还会校验精确字节数。
    /// </summary>
    public static bool IsHex(string value, int? byteCount = null)
    {
        if (value.Length == 0
            || value.Length % 2 != 0
            || (byteCount.HasValue && value.Length != byteCount.Value * 2))
        {
            return false;
        }

        return value.All(Uri.IsHexDigit);
    }

    /// <summary>把不带 <c>0x</c> 前缀的十六进制字符串转换为字节。</summary>
    public static byte[] Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!IsHex(value))
        {
            throw new FormatException("Value must contain an even number of hexadecimal characters.");
        }

        return Convert.FromHexString(value);
    }

    /// <summary>把字节格式化为大写且不带分隔符的十六进制字符串。</summary>
    public static string Format(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(value);

    /// <summary>解析最多八位十六进制字符表示的无符号 32 位整数。</summary>
    public static uint ParseUInt32(string value)
    {
        if (value.Length > 8 || value.Length == 0 || !value.All(Uri.IsHexDigit))
        {
            throw new FormatException($"'{value}' is not a 32-bit hexadecimal value.");
        }

        return uint.Parse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
    }
}
