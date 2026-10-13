namespace ArriOpto.BLE.Mesh;

/// <summary>
/// Proxy 协议 PDU（Mesh Protocol 1.1 §6.3）。
/// </summary>
/// <remarks>
/// 首字节：SAR(2 位，bit6-7) + MessageType(6 位，bit0-5)。
/// 对 GATT 承载，整条消息不超过 MTU-3 时 SAR=0b00 单包发送；
/// 超过时按 SAR 0b01/0b10/0b11 分段。
/// </remarks>
public static class MeshProxyPdu
{
    /// <summary>完整消息（不分段）。</summary>
    public const byte SarComplete = 0b00;
    /// <summary>首段。</summary>
    public const byte SarFirst = 0b01;
    /// <summary>续段。</summary>
    public const byte SarContinuation = 0b10;
    /// <summary>末段。</summary>
    public const byte SarLast = 0b11;

    /// <summary>Network PDU。</summary>
    public const byte MessageTypeNetwork = 0x00;
    /// <summary>Mesh Beacon。</summary>
    public const byte MessageTypeBeacon = 0x01;
    /// <summary>Proxy Configuration。</summary>
    public const byte MessageTypeProxyConfiguration = 0x02;
    /// <summary>Provisioning PDU。</summary>
    public const byte MessageTypeProvisioning = 0x03;

    /// <summary>
    /// 构造单包 Proxy PDU：首字节 = (SAR &lt;&lt; 6) | MessageType。
    /// </summary>
    public static byte[] Create(byte messageType, ReadOnlySpan<byte> data, byte sar = SarComplete)
    {
        var pdu = new byte[1 + data.Length];
        pdu[0] = (byte)(((sar & 0x03) << 6) | (messageType & 0x3F));
        data.CopyTo(pdu.AsSpan(1));
        return pdu;
    }

    /// <summary>
    /// 把消息拆成若干 Proxy PDU 分段。
    /// </summary>
    /// <param name="messageType">消息类型。</param>
    /// <param name="data">完整消息。</param>
    /// <param name="maxPayload">每个 Proxy PDU 的 Data 上限（GATT 承载为 MTU-3-1）。</param>
    public static IReadOnlyList<byte[]> Segment(
        byte messageType,
        ReadOnlySpan<byte> data,
        int maxPayload)
    {
        if (maxPayload <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxPayload));

        if (data.Length <= maxPayload)
            return [Create(messageType, data, SarComplete)];

        var result = new List<byte[]>();
        var offset = 0;
        var segmentCount = (data.Length + maxPayload - 1) / maxPayload;
        for (var index = 0; index < segmentCount; index++)
        {
            var length = Math.Min(maxPayload, data.Length - offset);
            var sar = index == 0 ? SarFirst
                : index == segmentCount - 1 ? SarLast
                : SarContinuation;
            result.Add(Create(messageType, data.Slice(offset, length), sar));
            offset += length;
        }

        return result;
    }

    /// <summary>解析首字节，得到 SAR 与 MessageType。</summary>
    public static (byte Sar, byte MessageType) ParseHeader(byte firstByte) =>
        ((byte)((firstByte >> 6) & 0x03), (byte)(firstByte & 0x3F));
}
