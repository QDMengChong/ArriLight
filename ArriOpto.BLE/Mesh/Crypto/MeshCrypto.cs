using System.Security.Cryptography;

namespace ArriOpto.BLE.Mesh.Crypto;

/// <summary>
/// Bluetooth Mesh 使用的对称加密原语封装：
/// AES-CMAC、AES-CCM（RFC 3610）、单块 AES-ECB（用于网络层隐私混淆）。
/// </summary>
public static class MeshCrypto
{
    /// <summary>
    /// AES-CCM 加密并追加 MIC。
    /// </summary>
    /// <param name="key">16 字节密钥（网络/应用/会话密钥）。</param>
    /// <param name="nonce">13 字节 nonce（网络/应用/代理/会话 nonce）。</param>
    /// <param name="plaintext">明文载荷。</param>
    /// <param name="additionalData">CCM 关联数据（网络层为 DST，其余为空）。</param>
    /// <param name="micSize">MIC 长度（网络层 4/8，应用层 4/8，配网数据 8）。</param>
    /// <returns>密文 + MIC。</returns>
    public static byte[] EncryptCcm(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> additionalData = default,
        int micSize = 4)
    {
        using var ccm = new AesCcm(key);
        var result = new byte[plaintext.Length + micSize];
        ccm.Encrypt(
            nonce,
            plaintext,
            result.AsSpan(0, plaintext.Length),
            result.AsSpan(plaintext.Length, micSize),
            additionalData);
        return result;
    }

    /// <summary>
    /// AES-CCM 解密并校验 MIC；失败抛出 <see cref="CryptographicException"/>。
    /// </summary>
    /// <returns>明文（不含 MIC）。</returns>
    public static byte[] DecryptCcm(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> additionalData = default,
        int micSize = 4)
    {
        using var ccm = new AesCcm(key);
        var plaintext = new byte[ciphertext.Length - micSize];
        ccm.Decrypt(
            nonce,
            ciphertext.Slice(0, ciphertext.Length - micSize),
            ciphertext.Slice(ciphertext.Length - micSize, micSize),
            plaintext,
            additionalData);
        return plaintext;
    }

    /// <summary>
    /// 单块 AES-ECB 加密（16 字节输入，用于网络层 PECB 计算）。
    /// </summary>
    public static byte[] EncryptAesEcbBlock(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> block)
    {
        if (block.Length != 16)
            throw new ArgumentException("AES-ECB 仅支持 16 字节单块。", nameof(block));

        using var aes = Aes.Create();
        aes.Key = key.ToArray();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(block.ToArray(), 0, 16);
    }
}
