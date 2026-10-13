namespace ArriOpto.BLE.Agp;

/// <summary>AGP 可选 TLV。Type 的 bit7 表示 Critical。</summary>
public sealed record AgpTlv(byte Type, ReadOnlyMemory<byte> Value)
{
    public bool IsCritical => (Type & AgpTlvCodec.CriticalMask) != 0;
    public byte TypeId => (byte)(Type & AgpTlvCodec.TypeIdMask);
}

public static class AgpTlvCodec
{
    public const byte CriticalMask = 0x80;
    public const byte TypeIdMask = 0x7F;
    public const byte TransitionTimeType = 0x81;
    public const byte ExecutionDelayType = 0x82;
    public const byte ColorTransitionType = 0x83;

    public static byte[] Encode(IEnumerable<AgpTlv> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var result = new List<byte>();
        foreach (var value in values)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Value.Length > byte.MaxValue)
            {
                throw new ArgumentException("AGP TLV values cannot exceed 255 bytes.", nameof(values));
            }

            result.Add(value.Type);
            result.Add((byte)value.Value.Length);
            result.AddRange(value.Value.ToArray());
        }

        return [.. result];
    }

    public static IReadOnlyList<AgpTlv> Decode(ReadOnlySpan<byte> data)
    {
        var result = new List<AgpTlv>();
        var offset = 0;
        while (offset < data.Length)
        {
            if (data.Length - offset < 2)
            {
                throw new AgpProtocolException(
                    AgpResultCode.MalformedTlv,
                    "AGP TLV header is truncated.");
            }

            var type = data[offset++];
            var length = data[offset++];
            if (length > data.Length - offset)
            {
                throw new AgpProtocolException(
                    AgpResultCode.MalformedTlv,
                    "AGP TLV value extends beyond the payload.");
            }

            result.Add(new AgpTlv(type, data.Slice(offset, length).ToArray()));
            offset += length;
        }

        return result;
    }
}
