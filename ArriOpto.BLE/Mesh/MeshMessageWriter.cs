using ArriOpto.BLE.Protocol;

namespace ArriOpto.BLE.Mesh;

/// <summary>
/// Little-endian writer for Bluetooth Mesh access-layer message payloads.
/// Mesh multi-octet fields are transmitted least-significant octet first.
/// </summary>
public sealed class MeshMessageWriter
{
    private readonly List<byte> _buffer = [];

    /// <summary>Starts a message with a 1-byte opcode.</summary>
    public static MeshMessageWriter Create(byte opcode)
    {
        var writer = new MeshMessageWriter();
        writer.AddU8(opcode);
        return writer;
    }

    /// <summary>Starts a message with a 2-byte opcode.</summary>
    /// <param name="opcode">2 字节 opcode，按大端写入（先高字节）。</param>
    public static MeshMessageWriter Create(ushort opcode)
    {
        var writer = new MeshMessageWriter();
        writer.AddBytes(MeshOpcode.Encode(opcode));
        return writer;
    }

    /// <summary>追加 1 个字节。</summary>
    public MeshMessageWriter AddU8(byte value)
    {
        _buffer.Add(value);
        return this;
    }

    /// <summary>以小端追加 2 字节无符号整数。</summary>
    public MeshMessageWriter AddU16(ushort value)
    {
        Span<byte> span = stackalloc byte[2];
        BinaryCodec.WriteUInt16LittleEndian(span, value);
        return AddBytes(span);
    }

    /// <summary>以小端追加 3 字节无符号整数（如 Time to Discharge）。</summary>
    public MeshMessageWriter AddU24(uint value)
    {
        Span<byte> span = stackalloc byte[3];
        span[0] = (byte)value;
        span[1] = (byte)(value >> 8);
        span[2] = (byte)(value >> 16);
        return AddBytes(span);
    }

    /// <summary>以小端追加 4 字节无符号整数。</summary>
    public MeshMessageWriter AddU32(uint value)
    {
        Span<byte> span = stackalloc byte[4];
        BinaryCodec.WriteUInt32LittleEndian(span, value);
        return AddBytes(span);
    }

    /// <summary>Adds a 40-bit value (5 octets, little-endian).</summary>
    public MeshMessageWriter AddU40(ulong value)
    {
        Span<byte> span = stackalloc byte[5];
        span[0] = (byte)value;
        span[1] = (byte)(value >> 8);
        span[2] = (byte)(value >> 16);
        span[3] = (byte)(value >> 24);
        span[4] = (byte)(value >> 32);
        return AddBytes(span);
    }

    /// <summary>以小端追加 8 字节无符号整数。</summary>
    public MeshMessageWriter AddU64(ulong value)
    {
        Span<byte> span = stackalloc byte[8];
        BinaryCodec.WriteUInt64LittleEndian(span, value);
        return AddBytes(span);
    }

    /// <summary>以二进制补码小端追加 2 字节有符号整数。</summary>
    public MeshMessageWriter AddI16(short value) =>
        AddU16(unchecked((ushort)value));

    /// <summary>以二进制补码小端追加 4 字节有符号整数。</summary>
    public MeshMessageWriter AddI32(int value) =>
        AddU32(unchecked((uint)value));

    /// <summary>追加原始字节序列（如 Label UUID、Key 值）。</summary>
    public MeshMessageWriter AddBytes(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
            _buffer.Add(b);
        return this;
    }

    /// <summary>返回完整消息（opcode + 参数）的字节数组。</summary>
    public byte[] ToArray() => [.. _buffer];
}
