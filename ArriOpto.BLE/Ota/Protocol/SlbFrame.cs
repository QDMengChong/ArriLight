namespace ArriOpto.BLE.Ota.Protocol;

/// <summary>
/// 已解析的 SLB 四字节头消息。
/// </summary>
/// <param name="MessageId">低四位消息序号。</param>
/// <param name="Opcode">消息操作码。</param>
/// <param name="TotalFrames">当前窗口总帧数，范围 1～16。</param>
/// <param name="FrameIndex">当前帧在窗口中的索引。</param>
/// <param name="Payload">不包含四字节头的有效载荷。</param>
public readonly record struct SlbFrame(
    byte MessageId,
    byte Opcode,
    int TotalFrames,
    int FrameIndex,
    ReadOnlyMemory<byte> Payload)
{
    /// <summary>SLB 固定消息头长度。</summary>
    public const int HeaderLength = 4;

    /// <summary>
    /// 解析并验证一条完整 SLB 消息。
    /// </summary>
    /// <exception cref="InvalidDataException">消息不足四字节或 payload 长度不一致。</exception>
    public static SlbFrame Parse(ReadOnlyMemory<byte> message)
    {
        var span = message.Span;
        if (span.Length < HeaderLength)
        {
            throw new InvalidDataException("SLB message is shorter than its four-byte header.");
        }

        var payloadLength = span[3];
        if (span.Length != HeaderLength + payloadLength)
        {
            throw new InvalidDataException("SLB payload length does not match its header.");
        }

        return new SlbFrame(
            (byte)(span[0] & 0x0F),
            span[1],
            ((span[2] >> 4) & 0x0F) + 1,
            span[2] & 0x0F,
            message[HeaderLength..]);
    }
}

/// <summary>
/// 按 MTU 编码 SLB 消息并维护 1～15 循环消息号。
/// </summary>
/// <remarks>实例属于单次会话，不应在多个设备或并发升级间共享。</remarks>
public sealed class SlbFrameEncoder
{
    private byte _messageId = 1;

    /// <summary>
    /// 编码一条 SLB 帧。调用方负责保证同一窗口的
    /// <paramref name="totalFrames"/> 和 <paramref name="frameIndex"/> 一致。
    /// </summary>
    public byte[] Encode(
        byte opcode,
        ReadOnlySpan<byte> payload,
        int mtu,
        int totalFrames = 1,
        int frameIndex = 0)
    {
        var payloadCapacity = mtu - 3 - SlbFrame.HeaderLength;
        if (payloadCapacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(mtu), "MTU is too small for an SLB frame.");
        }

        if (payload.Length > payloadCapacity)
        {
            throw new ArgumentException(
                $"Payload exceeds the {payloadCapacity}-byte capacity of the negotiated MTU.",
                nameof(payload));
        }

        if (totalFrames is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(totalFrames));
        }

        if (frameIndex < 0 || frameIndex >= totalFrames)
        {
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        }

        var message = new byte[SlbFrame.HeaderLength + payload.Length];
        message[0] = _messageId;
        message[1] = opcode;
        message[2] = (byte)(((totalFrames - 1) << 4) | frameIndex);
        message[3] = checked((byte)payload.Length);
        payload.CopyTo(message.AsSpan(SlbFrame.HeaderLength));

        _messageId = _messageId == 15 ? (byte)1 : (byte)(_messageId + 1);
        return message;
    }
}
