using ArriOpto.BLE.Mesh.Crypto;

namespace ArriOpto.BLE.Mesh;

/// <summary>
/// 上层/下层传输层（Mesh Protocol 1.1 §3.5/§3.6）。
/// 当前实现覆盖非分段访问消息（最小闭环所需）；
/// 分段消息（每段 12 字节）在后续迭代补充。
/// </summary>
public static class MeshTransportLayer
{
    /// <summary>非分段上层传输载荷上限（含 4 字节 MIC 共 15 字节）。</summary>
    public const int MaxUnsegmentedUpperTransportPayload = 15;

    /// <summary>分段消息每段载荷上限。</summary>
    public const int MaxSegmentedPayload = 12;

    /// <summary>分段消息传输头长度（4 字节）。</summary>
    public const int SegmentedHeaderLength = 4;

    /// <summary>应用层 MIC 长度：非分段为 4，分段且 SZMIC=1 为 8。</summary>
    public static int GetTransMicLength(bool aszmic) => aszmic ? 8 : 4;

    /// <summary>
    /// 构造并加密上层传输 PDU。
    /// </summary>
    /// <param name="appKey">应用密钥（16 字节）。</param>
    /// <param name="aszmic">是否使用 8 字节 MIC（分段且 SZMIC=1 时）。</param>
    /// <param name="sequenceNumber">3 字节序列号。</param>
    /// <param name="src">源地址。</param>
    /// <param name="dst">目标地址。</param>
    /// <param name="ivIndex">4 字节 IV Index。</param>
    /// <param name="accessPdu">访问层消息（opcode + 参数）。</param>
    /// <returns>加密后的上层传输 PDU（密文 + MIC）。</returns>
    public static byte[] EncryptUpperTransportPdu(
        ReadOnlySpan<byte> appKey,
        bool aszmic,
        ReadOnlySpan<byte> sequenceNumber,
        ushort src,
        ushort dst,
        ReadOnlySpan<byte> ivIndex,
        ReadOnlySpan<byte> accessPdu)
    {
        var nonce = MeshNonce.CreateApplicationNonce(
            aszmic,
            sequenceNumber,
            src,
            dst,
            ivIndex);
        return MeshCrypto.EncryptCcm(
            appKey,
            nonce,
            accessPdu,
            micSize: GetTransMicLength(aszmic));
    }

    /// <summary>
    /// 使用设备密钥构造上层传输 PDU（用于 Config 等设备密钥消息，AKF=0）。
    /// </summary>
    public static byte[] EncryptDeviceKeyUpperTransportPdu(
        ReadOnlySpan<byte> deviceKey,
        bool aszmic,
        ReadOnlySpan<byte> sequenceNumber,
        ushort src,
        ushort dst,
        ReadOnlySpan<byte> ivIndex,
        ReadOnlySpan<byte> accessPdu)
    {
        var nonce = MeshNonce.CreateDeviceNonce(
            aszmic,
            sequenceNumber,
            src,
            dst,
            ivIndex);
        return MeshCrypto.EncryptCcm(
            deviceKey,
            nonce,
            accessPdu,
            micSize: GetTransMicLength(aszmic));
    }

    /// <summary>
    /// 构造非分段下层传输 PDU：SEG=0 | AKF | AID(6 位) + 加密上层传输 PDU。
    /// </summary>
    /// <param name="akf">1=应用密钥，0=设备密钥。</param>
    /// <param name="aid">k4(AppKey) 派生的 6 位 AID。</param>
    /// <param name="encryptedUpperTransportPdu">上层传输 PDU。</param>
    public static byte[] CreateUnsegmentedLowerTransportPdu(
        bool akf,
        byte aid,
        ReadOnlySpan<byte> encryptedUpperTransportPdu)
    {
        var header = (byte)(((akf ? 1 : 0) << 6) | (aid & 0x3F));
        var pdu = new byte[1 + encryptedUpperTransportPdu.Length];
        pdu[0] = header;
        encryptedUpperTransportPdu.CopyTo(pdu.AsSpan(1));
        return pdu;
    }

    /// <summary>
    /// 构造分段下层传输 PDU 列表。
    /// </summary>
    /// <param name="akf">1=应用密钥，0=设备密钥。</param>
    /// <param name="aid">6 位 AID（设备密钥消息为 0）。</param>
    /// <param name="aszmic">SZMIC 状态（默认 0 → MIC 4 字节）。</param>
    /// <param name="sequenceNumber">首段序列号（3 字节），SeqZero 取其低 13 位。</param>
    /// <param name="encryptedUpperTransportPdu">加密后的上层传输 PDU。</param>
    public static IReadOnlyList<byte[]> CreateSegmentedLowerTransportPdus(
        bool akf,
        byte aid,
        bool aszmic,
        ReadOnlySpan<byte> sequenceNumber,
        ReadOnlySpan<byte> encryptedUpperTransportPdu)
    {
        var akfAid = (byte)(((akf ? 1 : 0) << 6) | (aid & 0x3F));
        // SeqZero = 序列号低 13 位
        var seqValue = ((uint)sequenceNumber[0] << 16)
                       | ((uint)sequenceNumber[1] << 8)
                       | sequenceNumber[2];
        var seqZero = (ushort)(seqValue & 0x1FFF);

        var segmentCount = (encryptedUpperTransportPdu.Length + MaxSegmentedPayload - 1)
                           / MaxSegmentedPayload;
        var segN = (byte)(segmentCount - 1);
        var result = new List<byte[]>(segmentCount);

        for (var segO = 0; segO < segmentCount; segO++)
        {
            var offset = segO * MaxSegmentedPayload;
            var length = Math.Min(
                MaxSegmentedPayload,
                encryptedUpperTransportPdu.Length - offset);
            var pdu = new byte[SegmentedHeaderLength + length];
            pdu[0] = (byte)((1 << 7) | akfAid);
            pdu[1] = (byte)(((aszmic ? 1 : 0) << 7) | ((seqZero >> 6) & 0x7F));
            pdu[2] = (byte)(((seqZero << 2) & 0xFC) | ((segO >> 3) & 0x03));
            pdu[3] = (byte)(((segO << 5) & 0xE0) | (segN & 0x1F));
            encryptedUpperTransportPdu
                .Slice(offset, length)
                .CopyTo(pdu.AsSpan(SegmentedHeaderLength));
            result.Add(pdu);
        }

        return result;
    }
}
