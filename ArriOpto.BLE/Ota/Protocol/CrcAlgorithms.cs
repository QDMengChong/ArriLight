namespace ArriOpto.BLE.Ota.Protocol;

/// <summary>
/// 奉加微两种 OTA 协议使用的 CRC16 实现。
/// </summary>
public static class CrcAlgorithms
{
    /// <summary>
    /// 计算 SLB 使用的 CRC-16/CCITT（poly 0x1021，默认 seed 0xFFFF）。
    /// </summary>
    public static ushort Crc16Ccitt(ReadOnlySpan<byte> data, ushort seed = 0xFFFF)
    {
        var crc = seed;
        foreach (var value in data)
        {
            crc ^= (ushort)(value << 8);
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (ushort)((crc & 0x8000) != 0
                    ? (crc << 1) ^ 0x1021
                    : crc << 1);
            }
        }

        return crc;
    }

    /// <summary>
    /// 计算 Single Bank 分区使用的反射 CRC16（poly 0xA001，默认 seed 0）。
    /// </summary>
    public static ushort Crc16A001(ReadOnlySpan<byte> data, ushort seed = 0)
    {
        var crc = seed;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (ushort)((crc & 1) != 0
                    ? (crc >> 1) ^ 0xA001
                    : crc >> 1);
            }
        }

        return crc;
    }
}
