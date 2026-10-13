namespace ArriOpto.BLE.Mesh.Provisioning;

/// <summary>
/// Provisioning PDU 编解码（Mesh Protocol 1.1 §5.4.1）。
/// </summary>
/// <remarks>
/// PB-GATT 承载上，每条 Provisioning PDU 外层还要包一个 Proxy PDU
/// （MessageType=0x03，见 <see cref="ArriOpto.BLE.Mesh.MeshProxyPdu"/>）。
/// </remarks>
public static class ProvisioningPdu
{
    public const byte OpcodeInvite = 0x00;
    public const byte OpcodeCapabilities = 0x01;
    public const byte OpcodeStart = 0x02;
    public const byte OpcodePublicKey = 0x03;
    public const byte OpcodeInputComplete = 0x04;
    public const byte OpcodeConfirmation = 0x05;
    public const byte OpcodeRandom = 0x06;
    public const byte OpcodeData = 0x07;
    public const byte OpcodeComplete = 0x08;
    public const byte OpcodeFailed = 0x09;

    /// <summary>BTM_ECDH_P256_CMAC_AES128_AES_CCM 算法。</summary>
    public const byte AlgorithmP256CmacAes128AesCcm = 0x00;

    /// <summary>No OOB 认证方法。</summary>
    public const byte AuthenticationMethodNoOob = 0x00;

    /// <summary>邀请：Attention Duration(1)，最小闭环设为 0。</summary>
    public static byte[] CreateInvite(byte attentionDuration = 0) =>
        [OpcodeInvite, attentionDuration];

    /// <summary>Start：Algorithm(1) + PublicKey(1) + AuthMethod(1) + AuthAction(1) + AuthSize(1)。</summary>
    public static byte[] CreateStart(
        byte algorithm = AlgorithmP256CmacAes128AesCcm,
        byte publicKeyType = 0x00,
        byte authenticationMethod = AuthenticationMethodNoOob,
        byte authenticationAction = 0x00,
        byte authenticationSize = 0x00) =>
        [OpcodeStart, algorithm, publicKeyType, authenticationMethod, authenticationAction, authenticationSize];

    /// <summary>公钥：X(32) + Y(32)。</summary>
    public static byte[] CreatePublicKey(ReadOnlySpan<byte> x, ReadOnlySpan<byte> y)
    {
        if (x.Length != 32 || y.Length != 32)
            throw new ArgumentException("P-256 公钥 X/Y 必须各 32 字节。");
        var pdu = new byte[1 + 64];
        pdu[0] = OpcodePublicKey;
        x.CopyTo(pdu.AsSpan(1));
        y.CopyTo(pdu.AsSpan(33));
        return pdu;
    }

    /// <summary>确认值：Confirmation(16)。</summary>
    public static byte[] CreateConfirmation(ReadOnlySpan<byte> confirmation)
    {
        if (confirmation.Length != 16)
            throw new ArgumentException("确认值必须为 16 字节。");
        var pdu = new byte[17];
        pdu[0] = OpcodeConfirmation;
        confirmation.CopyTo(pdu.AsSpan(1));
        return pdu;
    }

    /// <summary>随机数：Random(16)。</summary>
    public static byte[] CreateRandom(ReadOnlySpan<byte> random)
    {
        if (random.Length != 16)
            throw new ArgumentException("随机数必须为 16 字节。");
        var pdu = new byte[17];
        pdu[0] = OpcodeRandom;
        random.CopyTo(pdu.AsSpan(1));
        return pdu;
    }

    /// <summary>
    /// 配网数据：AES-CCM(SessionKey, SessionNonce, Data, MIC=8) 加密后的密文+MIC。
    /// </summary>
    public static byte[] CreateData(ReadOnlySpan<byte> encryptedDataWithMic)
    {
        var pdu = new byte[1 + encryptedDataWithMic.Length];
        pdu[0] = OpcodeData;
        encryptedDataWithMic.CopyTo(pdu.AsSpan(1));
        return pdu;
    }

    /// <summary>解析 Capabilities PDU（11 字节参数）。</summary>
    public static bool TryParseCapabilities(
        ReadOnlySpan<byte> pdu,
        out ProvisioningCapabilities capabilities)
    {
        capabilities = default;
        if (pdu.Length != 12 || pdu[0] != OpcodeCapabilities)
            return false;

        capabilities = new ProvisioningCapabilities(
            pdu[1],
            (ushort)((pdu[2] << 8) | pdu[3]),
            pdu[4],
            pdu[5],
            pdu[6],
            (ushort)((pdu[7] << 8) | pdu[8]),
            pdu[9],
            (ushort)((pdu[10] << 8) | pdu[11]));
        return true;
    }

    /// <summary>解析公钥 PDU（X||Y，各 32 字节）。</summary>
    public static bool TryParsePublicKey(
        ReadOnlySpan<byte> pdu,
        out byte[] x,
        out byte[] y)
    {
        x = [];
        y = [];
        if (pdu.Length != 65 || pdu[0] != OpcodePublicKey)
            return false;

        x = pdu.Slice(1, 32).ToArray();
        y = pdu.Slice(33, 32).ToArray();
        return true;
    }

    /// <summary>解析 Confirmation PDU（16 字节确认值）。</summary>
    public static bool TryParseConfirmation(
        ReadOnlySpan<byte> pdu,
        out byte[] confirmation)
    {
        confirmation = [];
        if (pdu.Length != 17 || pdu[0] != OpcodeConfirmation)
            return false;
        confirmation = pdu.Slice(1, 16).ToArray();
        return true;
    }

    /// <summary>解析 Random PDU（16 字节随机数）。</summary>
    public static bool TryParseRandom(
        ReadOnlySpan<byte> pdu,
        out byte[] random)
    {
        random = [];
        if (pdu.Length != 17 || pdu[0] != OpcodeRandom)
            return false;
        random = pdu.Slice(1, 16).ToArray();
        return true;
    }

    /// <summary>解析 Complete PDU。</summary>
    public static bool IsComplete(ReadOnlySpan<byte> pdu) =>
        pdu.Length == 1 && pdu[0] == OpcodeComplete;

    /// <summary>解析 Failed PDU（错误码）。</summary>
    public static bool TryParseFailed(ReadOnlySpan<byte> pdu, out byte errorCode)
    {
        errorCode = 0;
        if (pdu.Length != 2 || pdu[0] != OpcodeFailed)
            return false;
        errorCode = pdu[1];
        return true;
    }
}

/// <summary>解析后的 Provisioning Capabilities（§5.4.2.2）。</summary>
public readonly record struct ProvisioningCapabilities(
    byte NumberOfElements,
    ushort SupportedAlgorithms,
    byte PublicKeyType,
    byte StaticOobType,
    byte OutputOobSize,
    ushort OutputOobAction,
    byte InputOobSize,
    ushort InputOobAction);
