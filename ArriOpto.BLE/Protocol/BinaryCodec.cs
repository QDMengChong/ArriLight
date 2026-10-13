using System.Buffers.Binary;

namespace ArriOpto.BLE.Protocol;

/// <summary>
/// BLE 协议字段使用的显式小端序编解码工具，OTA 与 Mesh 共用。
/// </summary>
/// <remarks>
/// 不使用 <see cref="BitConverter"/>，以免结果依赖运行平台的 CPU 字节序。
/// 读取方法带长度校验（不足时抛 <see cref="ArgumentException"/>）；
/// 写入方法直接写入目标 span，零分配。
/// </remarks>
public static class BinaryCodec
{
    /// <summary>从至少两个字节的小端序数据读取无符号 16 位整数。</summary>
    public static ushort ReadUInt16LittleEndian(ReadOnlySpan<byte> value)
    {
        if (value.Length < 2)
        {
            throw new ArgumentException("At least two bytes are required.", nameof(value));
        }

        return BinaryPrimitives.ReadUInt16LittleEndian(value);
    }

    /// <summary>从至少四个字节的小端序数据读取无符号 32 位整数。</summary>
    public static uint ReadUInt32LittleEndian(ReadOnlySpan<byte> value)
    {
        if (value.Length < 4)
        {
            throw new ArgumentException("At least four bytes are required.", nameof(value));
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(value);
    }

    /// <summary>从至少八个字节的小端序数据读取无符号 64 位整数。</summary>
    public static ulong ReadUInt64LittleEndian(ReadOnlySpan<byte> value)
    {
        if (value.Length < 8)
        {
            throw new ArgumentException("At least eight bytes are required.", nameof(value));
        }

        return BinaryPrimitives.ReadUInt64LittleEndian(value);
    }

    /// <summary>把无符号 16 位整数以小端序写入目标 span 的前两个字节。</summary>
    public static void WriteUInt16LittleEndian(Span<byte> destination, ushort value)
    {
        if (destination.Length < 2)
        {
            throw new ArgumentException("At least two bytes are required.", nameof(destination));
        }

        BinaryPrimitives.WriteUInt16LittleEndian(destination, value);
    }

    /// <summary>把无符号 32 位整数以小端序写入目标 span 的前四个字节。</summary>
    public static void WriteUInt32LittleEndian(Span<byte> destination, uint value)
    {
        if (destination.Length < 4)
        {
            throw new ArgumentException("At least four bytes are required.", nameof(destination));
        }

        BinaryPrimitives.WriteUInt32LittleEndian(destination, value);
    }

    /// <summary>把无符号 64 位整数以小端序写入目标 span 的前八个字节。</summary>
    public static void WriteUInt64LittleEndian(Span<byte> destination, ulong value)
    {
        if (destination.Length < 8)
        {
            throw new ArgumentException("At least eight bytes are required.", nameof(destination));
        }

        BinaryPrimitives.WriteUInt64LittleEndian(destination, value);
    }
}
