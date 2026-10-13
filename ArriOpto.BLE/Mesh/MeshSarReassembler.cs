namespace ArriOpto.BLE.Mesh;

/// <summary>
/// 把 Proxy 通道收到的 SAR 分段重组成完整 Proxy PDU（Mesh Protocol 1.1 §6.3.1）。
/// 与 <see cref="MeshProxyPdu.Segment"/>（发送分段）对应，是接收端的逆操作。
/// </summary>
/// <remarks>
/// 每个 GATT 连接应使用一个实例。连接断开或模式切换时应调用
/// <see cref="Reset"/>，避免上一段未完成的消息污染下一次重组。
/// </remarks>
public sealed class MeshSarReassembler
{
    private readonly object _syncRoot = new();
    private byte[]? _buffer;
    private byte _messageType;

    /// <summary>
    /// 喂入一个分段（首字节为 SAR + MessageType），返回重组完成的完整 Proxy
    /// PDU；消息尚未收完时返回 <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// SarComplete 直接透传；SarFirst 开始新消息；SarContinuation / SarLast
    /// 追加到当前累积缓冲。续段/末段在没有进行中的消息时被丢弃。
    /// </remarks>
    public byte[]? Append(ReadOnlySpan<byte> segment)
    {
        if (segment.Length < 1)
        {
            return null;
        }

        var (sar, messageType) = MeshProxyPdu.ParseHeader(segment[0]);
        lock (_syncRoot)
        {
            switch (sar)
            {
                case MeshProxyPdu.SarComplete:
                    return segment.ToArray();
                case MeshProxyPdu.SarFirst:
                    _buffer = segment[1..].ToArray();
                    _messageType = messageType;
                    return null;
                case MeshProxyPdu.SarContinuation:
                    if (_buffer is null)
                    {
                        return null;
                    }

                    _buffer = [.. _buffer, .. segment[1..]];
                    return null;
                case MeshProxyPdu.SarLast:
                    if (_buffer is null)
                    {
                        return null;
                    }

                    _buffer = [.. _buffer, .. segment[1..]];
                    var full = new byte[1 + _buffer.Length];
                    full[0] = _messageType;
                    _buffer.CopyTo(full, 1);
                    _buffer = null;
                    return full;
                default:
                    return null;
            }
        }
    }

    /// <summary>丢弃未完成的重组状态（连接断开或模式切换时调用）。</summary>
    public void Reset()
    {
        lock (_syncRoot)
        {
            _buffer = null;
        }
    }
}
