using ArriOpto.BLE.Protocol;

namespace ArriOpto.BLE.Mesh;

/// <summary>
/// Little-endian reader for Bluetooth Mesh access-layer message payloads.
/// </summary>
public sealed class MeshMessageReader
{
    private readonly byte[] _data;
    private int _position;

    public MeshMessageReader(ReadOnlySpan<byte> data) => _data = data.ToArray();

    public int Position => _position;

    public int Remaining => _data.Length - _position;

    public bool HasRemaining => Remaining > 0;

    /// <summary>读取 1 个字节。</summary>
    public byte ReadU8()
    {
        Ensure(1);
        return _data[_position++];
    }

    /// <summary>以小端读取 2 字节无符号整数。</summary>
    public ushort ReadU16()
    {
        Ensure(2);
        var value = BinaryCodec.ReadUInt16LittleEndian(
            _data.AsSpan(_position, 2));
        _position += 2;
        return value;
    }

    /// <summary>以小端读取 3 字节无符号整数。</summary>
    public uint ReadU24()
    {
        Ensure(3);
        var value = (uint)_data[_position]
            | ((uint)_data[_position + 1] << 8)
            | ((uint)_data[_position + 2] << 16);
        _position += 3;
        return value;
    }

    /// <summary>以小端读取 4 字节无符号整数。</summary>
    public uint ReadU32()
    {
        Ensure(4);
        var value = BinaryCodec.ReadUInt32LittleEndian(
            _data.AsSpan(_position, 4));
        _position += 4;
        return value;
    }

    /// <summary>Reads a 40-bit value (5 octets, little-endian).</summary>
    public ulong ReadU40()
    {
        Ensure(5);
        ulong value = _data[_position]
            | ((ulong)_data[_position + 1] << 8)
            | ((ulong)_data[_position + 2] << 16)
            | ((ulong)_data[_position + 3] << 24)
            | ((ulong)_data[_position + 4] << 32);
        _position += 5;
        return value;
    }

    /// <summary>以小端读取 8 字节无符号整数。</summary>
    public ulong ReadU64()
    {
        Ensure(8);
        var value = BinaryCodec.ReadUInt64LittleEndian(
            _data.AsSpan(_position, 8));
        _position += 8;
        return value;
    }

    /// <summary>以二进制补码小端读取 2 字节有符号整数。</summary>
    public short ReadI16() => unchecked((short)ReadU16());

    /// <summary>以二进制补码小端读取 4 字节有符号整数。</summary>
    public int ReadI32() => unchecked((int)ReadU32());

    /// <summary>读取指定数量的原始字节。</summary>
    /// <exception cref="InvalidOperationException">剩余长度不足时抛出。</exception>
    public byte[] ReadBytes(int count)
    {
        Ensure(count);
        var value = _data.AsSpan(_position, count).ToArray();
        _position += count;
        return value;
    }

    /// <summary>读取剩余全部字节并前进到末尾。</summary>
    public byte[] ReadRemaining()
    {
        var value = _data.AsSpan(_position).ToArray();
        _position = _data.Length;
        return value;
    }

    /// <summary>消费并校验 1 字节 opcode；不匹配或越界返回 false。</summary>
    public bool TryConsumeOpcode(byte expected)
    {
        if (!HasRemaining || _data[_position] != expected)
            return false;
        _position++;
        return true;
    }

    /// <summary>消费并校验 2 字节 opcode（大端）；不匹配或越界返回 false。</summary>
    public bool TryConsumeOpcode(ushort expected)
    {
        if (Remaining < 2)
            return false;
        var actual = (ushort)((_data[_position] << 8) | _data[_position + 1]);
        if (actual != expected)
            return false;
        _position += 2;
        return true;
    }

    private void Ensure(int count)
    {
        if (_position + count > _data.Length)
        {
            throw new InvalidOperationException(
                $"Mesh message is truncated: need {count} more byte(s) at offset {_position}.");
        }
    }
}
