namespace ArriOpto.BLE.Mesh.Crypto;

/// <summary>
/// Mesh nonce 构造（Mesh Protocol 1.1 §3.9.5）。
/// 所有 nonce 均为 13 字节，字段按规范顺序排列。
/// </summary>
public static class MeshNonce
{
    /// <summary>
    /// 网络 nonce：0x00 | CTL|TTL | SEQ(3) | SRC(2) | PAD(2) | IVIndex(4)。
    /// </summary>
    public static byte[] CreateNetworkNonce(
        byte ctlTtl,
        ReadOnlySpan<byte> sequenceNumber,
        ushort src,
        ReadOnlySpan<byte> ivIndex)
    {
        var nonce = new byte[13];
        nonce[0] = 0x00;
        nonce[1] = ctlTtl;
        sequenceNumber[..3].CopyTo(nonce.AsSpan(2));
        nonce[5] = (byte)(src >> 8);
        nonce[6] = (byte)src;
        nonce[7] = 0x00;
        nonce[8] = 0x00;
        ivIndex[..4].CopyTo(nonce.AsSpan(9));
        return nonce;
    }

    /// <summary>
    /// 应用 nonce：0x01 | ASZMIC+Pad(1) | SEQ(3) | SRC(2) | DST(2) | IVIndex(4)。
    /// </summary>
    public static byte[] CreateApplicationNonce(
        bool aszmic,
        ReadOnlySpan<byte> sequenceNumber,
        ushort src,
        ushort dst,
        ReadOnlySpan<byte> ivIndex)
    {
        var nonce = new byte[13];
        nonce[0] = 0x01;
        nonce[1] = (byte)(aszmic ? 0x01 : 0x00);
        sequenceNumber[..3].CopyTo(nonce.AsSpan(2));
        nonce[5] = (byte)(src >> 8);
        nonce[6] = (byte)src;
        nonce[7] = (byte)(dst >> 8);
        nonce[8] = (byte)dst;
        ivIndex[..4].CopyTo(nonce.AsSpan(9));
        return nonce;
    }

    /// <summary>
    /// 设备 nonce：0x02 | ASZMIC+Pad(1) | SEQ(3) | SRC(2) | DST(2) | IVIndex(4)。
    /// 用于设备密钥消息（AKF=0，如 Config 消息）。
    /// </summary>
    public static byte[] CreateDeviceNonce(
        bool aszmic,
        ReadOnlySpan<byte> sequenceNumber,
        ushort src,
        ushort dst,
        ReadOnlySpan<byte> ivIndex)
    {
        var nonce = new byte[13];
        nonce[0] = 0x02;
        nonce[1] = (byte)(aszmic ? 0x01 : 0x00);
        sequenceNumber[..3].CopyTo(nonce.AsSpan(2));
        nonce[5] = (byte)(src >> 8);
        nonce[6] = (byte)src;
        nonce[7] = (byte)(dst >> 8);
        nonce[8] = (byte)dst;
        ivIndex[..4].CopyTo(nonce.AsSpan(9));
        return nonce;
    }

    /// <summary>
    /// 代理 nonce：0x03 | PAD(1) | SEQ(3) | SRC(2) | PAD(2) | IVIndex(4)。
    /// 用于 Proxy Configuration 消息（CTL=1，SRC/DST 通常为 0x0000）。
    /// </summary>
    public static byte[] CreateProxyNonce(
        ReadOnlySpan<byte> sequenceNumber,
        ushort src,
        ReadOnlySpan<byte> ivIndex)
    {
        var nonce = new byte[13];
        nonce[0] = 0x03;
        nonce[1] = 0x00;
        sequenceNumber[..3].CopyTo(nonce.AsSpan(2));
        nonce[5] = (byte)(src >> 8);
        nonce[6] = (byte)src;
        nonce[7] = 0x00;
        nonce[8] = 0x00;
        ivIndex[..4].CopyTo(nonce.AsSpan(9));
        return nonce;
    }
}
