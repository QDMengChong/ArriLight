using System.Security.Cryptography;

namespace ArriOpto.BLE.Mesh.Crypto;

/// <summary>
/// Mesh 网络层：加密并混淆 Network PDU（Mesh Protocol 1.1 §3.4.4 / §3.9.7）。
/// </summary>
/// <remarks>
/// 输出 PDU 布局（不含 Proxy 类型字节）：
/// IVI|NID(1) + 混淆头(6: CTL|TTL+SEQ+SRC) + 加密载荷(DST+TransportPDU+NetMIC)。
/// 算法与官方样例（§8.16.52）一致，见单元测试。
/// </remarks>
public static class MeshNetworkLayer
{
    /// <summary>网络层 NetMIC 长度：CTL=0 为 4 字节，CTL=1 为 8 字节。</summary>
    public static int GetNetMicLength(byte ctl) => ctl == 0 ? 4 : 8;

    /// <summary>
    /// 构建并加密一条 Network PDU。
    /// </summary>
    /// <param name="encryptionKey">k2 派生的 EncryptionKey。</param>
    /// <param name="privacyKey">k2 派生的 PrivacyKey。</param>
    /// <param name="nid">k2 派生的 NID（7 位）。</param>
    /// <param name="ivIndex">4 字节 IV Index；IVI 取其最低位。</param>
    /// <param name="ctl">0=访问消息，1=控制消息。</param>
    /// <param name="ttl">生存时间。</param>
    /// <param name="sequenceNumber">3 字节序列号。</param>
    /// <param name="src">源单播地址。</param>
    /// <param name="dst">目标地址。</param>
    /// <param name="transportPdu">下层传输 PDU。</param>
    /// <returns>完整 Network PDU（不含 Proxy 帧头）。</returns>
    public static byte[] BuildNetworkPdu(
        ReadOnlySpan<byte> encryptionKey,
        ReadOnlySpan<byte> privacyKey,
        byte nid,
        ReadOnlySpan<byte> ivIndex,
        byte ctl,
        byte ttl,
        ReadOnlySpan<byte> sequenceNumber,
        ushort src,
        ushort dst,
        ReadOnlySpan<byte> transportPdu)
        => BuildNetworkPduCore(
            encryptionKey,
            privacyKey,
            nid,
            ivIndex,
            ctl,
            ttl,
            sequenceNumber,
            src,
            dst,
            transportPdu,
            nonceOverride: null);

    /// <summary>
    /// 构建一条 Proxy Configuration 网络 PDU（CTL=1、TTL=0、SRC/DST=0x0000，
    /// 使用代理 nonce、NetMIC=8）。
    /// </summary>
    /// <param name="encryptionKey">K2 派生的 16 字节网络加密密钥。</param>
    /// <param name="privacyKey">K2 派生的 16 字节隐私密钥。</param>
    /// <param name="nid">K2 派生的 7 位 NID。</param>
    /// <param name="ivIndex">4 字节 IV Index。</param>
    /// <param name="sequenceNumber">3 字节网络序列号。</param>
    /// <param name="transportPdu">下层传输控制 PDU（SEG=0|AKF=0|AID=0 + opcode + 参数）。</param>
    public static byte[] BuildProxyConfigurationPdu(
        ReadOnlySpan<byte> encryptionKey,
        ReadOnlySpan<byte> privacyKey,
        byte nid,
        ReadOnlySpan<byte> ivIndex,
        ReadOnlySpan<byte> sequenceNumber,
        ReadOnlySpan<byte> transportPdu)
        => BuildNetworkPduCore(
            encryptionKey,
            privacyKey,
            nid,
            ivIndex,
            ctl: 1,
            ttl: 0,
            sequenceNumber,
            src: 0,
            dst: 0,
            transportPdu,
            MeshNonce.CreateProxyNonce(sequenceNumber, src: 0, ivIndex));

    private static byte[] BuildNetworkPduCore(
        ReadOnlySpan<byte> encryptionKey,
        ReadOnlySpan<byte> privacyKey,
        byte nid,
        ReadOnlySpan<byte> ivIndex,
        byte ctl,
        byte ttl,
        ReadOnlySpan<byte> sequenceNumber,
        ushort src,
        ushort dst,
        ReadOnlySpan<byte> transportPdu,
        byte[]? nonceOverride)
    {
        if (ivIndex.Length != 4)
            throw new ArgumentException("IV Index 必须为 4 字节。", nameof(ivIndex));
        if (sequenceNumber.Length != 3)
            throw new ArgumentException("序列号必须为 3 字节。", nameof(sequenceNumber));

        var ctlTtl = (byte)(((ctl & 0x01) << 7) | (ttl & 0x7F));
        var ivi = (byte)(ivIndex[3] & 0x01);
        var iviNid = (byte)((ivi << 7) | (nid & 0x7F));

        // 加密载荷 = DST(2) + TransportPDU，AES-CCM，MIC=NetMIC
        var plaintext = new byte[2 + transportPdu.Length];
        plaintext[0] = (byte)(dst >> 8);
        plaintext[1] = (byte)dst;
        transportPdu.CopyTo(plaintext.AsSpan(2));

        var nonce = nonceOverride
            ?? MeshNonce.CreateNetworkNonce(ctlTtl, sequenceNumber, src, ivIndex);
        var encrypted = MeshCrypto.EncryptCcm(
            encryptionKey,
            nonce,
            plaintext,
            micSize: GetNetMicLength(ctl));

        // 隐私混淆：PECB = AES-ECB(PrivacyKey, 0x00*5 || IV || privacyRandom(7))
        var privacyRandom = encrypted.AsSpan(0, 7);
        var pecbInput = new byte[16];
        ivIndex.CopyTo(pecbInput.AsSpan(5, 4));
        privacyRandom.CopyTo(pecbInput.AsSpan(9));
        var pecb = MeshCrypto.EncryptAesEcbBlock(privacyKey, pecbInput);

        // 混淆头 = (CTL|TTL || SEQ || SRC) XOR PECB[0..6]
        var header = new byte[6];
        header[0] = ctlTtl;
        sequenceNumber[..3].CopyTo(header.AsSpan(1));
        header[4] = (byte)(src >> 8);
        header[5] = (byte)src;
        for (var i = 0; i < 6; i++)
            header[i] ^= pecb[i];

        var pdu = new byte[1 + header.Length + encrypted.Length];
        pdu[0] = iviNid;
        header.CopyTo(pdu.AsSpan(1));
        encrypted.CopyTo(pdu.AsSpan(1 + header.Length));
        return pdu;
    }

    /// <summary>
    /// 解析并解密一条 Network PDU（用于状态订阅与回包接收）。
    /// </summary>
    /// <param name="networkPdu">完整 Network PDU（不含 Proxy 帧头）。</param>
    /// <param name="encryptionKey">k2 派生的 EncryptionKey。</param>
    /// <param name="privacyKey">k2 派生的 PrivacyKey。</param>
    /// <param name="nid">期望的 NID（7 位）。</param>
    /// <param name="ivIndex">4 字节 IV Index。</param>
    /// <param name="result">解密结果。</param>
    public static bool TryDecryptNetworkPdu(
        ReadOnlySpan<byte> networkPdu,
        ReadOnlySpan<byte> encryptionKey,
        ReadOnlySpan<byte> privacyKey,
        byte nid,
        ReadOnlySpan<byte> ivIndex,
        out DecryptedNetworkPdu result)
    {
        result = default;
        if (networkPdu.Length < 15)
            return false;

        var iviNid = networkPdu[0];
        if ((iviNid & 0x7F) != (nid & 0x7F))
            return false;

        var obfuscated = networkPdu.Slice(1, 6);
        var encrypted = networkPdu.Slice(7);

        // 隐私随机数 = 加密载荷前 7 字节
        var privacyRandom = encrypted.Slice(0, 7);
        var pecbInput = new byte[16];
        ivIndex.CopyTo(pecbInput.AsSpan(5, 4));
        privacyRandom.CopyTo(pecbInput.AsSpan(9));
        var pecb = MeshCrypto.EncryptAesEcbBlock(privacyKey, pecbInput);

        var header = new byte[6];
        for (var i = 0; i < 6; i++)
            header[i] = (byte)(obfuscated[i] ^ pecb[i]);

        var ctlTtl = header[0];
        var ctl = (byte)((ctlTtl >> 7) & 0x01);
        var ttl = (byte)(ctlTtl & 0x7F);
        var sequenceNumber = new byte[] { header[1], header[2], header[3] };
        var src = (ushort)((header[4] << 8) | header[5]);

        var nonce = MeshNonce.CreateNetworkNonce(ctlTtl, sequenceNumber, src, ivIndex);
        byte[] plaintext;
        try
        {
            plaintext = MeshCrypto.DecryptCcm(
                encryptionKey,
                nonce,
                encrypted,
                micSize: GetNetMicLength(ctl));
        }
        catch (CryptographicException)
        {
            return false;
        }

        if (plaintext.Length < 2)
            return false;

        var dst = (ushort)((plaintext[0] << 8) | plaintext[1]);
        result = new DecryptedNetworkPdu(
            src,
            dst,
            ttl,
            sequenceNumber,
            plaintext.AsSpan(2).ToArray());
        return true;
    }
}

/// <summary>解密后的网络层消息。</summary>
public readonly record struct DecryptedNetworkPdu(
    ushort Src,
    ushort Dst,
    byte Ttl,
    byte[] SequenceNumber,
    byte[] TransportPdu);
