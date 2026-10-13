namespace ArriOpto.BLE.Mesh.Crypto;

/// <summary>
/// Bluetooth Mesh 密钥派生函数（Mesh Protocol 1.1 §3.9.2）。
/// </summary>
/// <remarks>
/// 实现与蓝牙 SIG 规范及 Nordic/ESP-IDF 参考实现一致，并用官方样例数据
/// （Mesh Protocol 1.1 §8.16/§8.17）做单元测试验证：
/// - s1(M) 使用全零密钥的 AES-CMAC；
/// - k1(N, SALT, P) = AES-CMAC(P, key=AES-CMAC(N, key=SALT))；
/// - k2(N, P) 输出 NID(7 位) + EncryptionKey(16) + PrivacyKey(16)；
/// - k3/k4 用于 Network ID / AID。
/// </remarks>
public static class MeshKeyDerivation
{
    private static readonly byte[] ZeroKey = new byte[16];

    /// <summary>s1(M)：以全零密钥计算 AES-CMAC。</summary>
    public static byte[] S1(ReadOnlySpan<byte> message) =>
        MeshCmac.ComputeHash(ZeroKey, message);

    /// <summary>
    /// k1(N, SALT, P)：先用 SALT 做键对 N 计算 AES-CMAC 得到 T，
    /// 再用 T 做键对 P 计算 AES-CMAC。
    /// </summary>
    public static byte[] K1(
        ReadOnlySpan<byte> n,
        ReadOnlySpan<byte> salt,
        ReadOnlySpan<byte> p)
    {
        var t = MeshCmac.ComputeHash(salt, n);
        return MeshCmac.ComputeHash(t, p);
    }

    /// <summary>
    /// k2(N, P)：派生网络层的 NID / EncryptionKey / PrivacyKey。
    /// 用于 <c>NID || EncryptionKey || PrivacyKey = k2(NetKey, 0x00)</c>。
    /// </summary>
    public static K2Output K2(ReadOnlySpan<byte> networkKey, ReadOnlySpan<byte> p)
    {
        var salt = S1("smk2"u8);
        var t = MeshCmac.ComputeHash(salt, networkKey);

        // T1 = AES-CMAC_T(P || 0x01)，NID 取 T1 最低 7 位
        var t1Input = Concat(p, [0x01]);
        var t1 = MeshCmac.ComputeHash(t, t1Input);
        var nid = (byte)(t1[^1] & 0x7F);

        // T2 = AES-CMAC_T(T1 || P || 0x02) -> EncryptionKey
        var t2Input = Concat(t1, p, [0x02]);
        var encryptionKey = MeshCmac.ComputeHash(t, t2Input);

        // T3 = AES-CMAC_T(T2 || P || 0x03) -> PrivacyKey
        var t3Input = Concat(encryptionKey, p, [0x03]);
        var privacyKey = MeshCmac.ComputeHash(t, t3Input);

        return new K2Output(nid, encryptionKey, privacyKey);
    }

    /// <summary>k3(N)：取 AES-CMAC("id64" || 0x01) 的最低 8 字节作为 Network ID。</summary>
    public static byte[] K3(ReadOnlySpan<byte> networkKey)
    {
        var salt = S1("smk3"u8);
        var t = MeshCmac.ComputeHash(salt, networkKey);
        var input = Concat("id64"u8, [0x01]);
        var result = MeshCmac.ComputeHash(t, input);
        return result[^8..].ToArray();
    }

    /// <summary>k4(N)：取 AES-CMAC("id6" || 0x01) 的最低 6 位作为 AID。</summary>
    public static byte K4(ReadOnlySpan<byte> key)
    {
        var salt = S1("smk4"u8);
        var t = MeshCmac.ComputeHash(salt, key);
        var input = Concat("id6"u8, [0x01]);
        var result = MeshCmac.ComputeHash(t, input);
        return (byte)(result[^1] & 0x3F);
    }

    /// <summary>拼接多个字节序列（最多 3 段）。</summary>
    private static byte[] Concat(
        ReadOnlySpan<byte> first,
        ReadOnlySpan<byte> second,
        ReadOnlySpan<byte> third = default)
    {
        var result = new byte[first.Length + second.Length + third.Length];
        first.CopyTo(result);
        second.CopyTo(result.AsSpan(first.Length));
        if (third.Length > 0)
            third.CopyTo(result.AsSpan(first.Length + second.Length));
        return result;
    }
}

/// <summary>k2 派生结果：NID（7 位）与 16 字节加密/隐私密钥。</summary>
public readonly record struct K2Output(byte Nid, byte[] EncryptionKey, byte[] PrivacyKey);
