using System.Security.Cryptography;

namespace ArriOpto.BLE.Ota.Protocol;

/// <summary>
/// OTA challenge-response 所需的 AES-128 单块运算。
/// </summary>
/// <remarks>
/// 协议明确使用 ECB 且不填充，每次输入、输出和 key 都是 16 字节。该类型只实现
/// 设备认证算法，不应作为通用数据加密 API 使用。
/// </remarks>
public static class AesAuthenticator
{
    /// <summary>使用 AES-128 ECB 加密一个 16 字节 block。</summary>
    public static byte[] EncryptBlock(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> key)
    {
        ValidateBlockAndKey(plaintext, key);
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key.ToArray();
        return aes.EncryptEcb(plaintext, PaddingMode.None);
    }

    /// <summary>使用 AES-128 ECB 解密一个 16 字节 block。</summary>
    public static byte[] DecryptBlock(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> key)
    {
        ValidateBlockAndKey(ciphertext, key);
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key.ToArray();
        return aes.DecryptEcb(ciphertext, PaddingMode.None);
    }

    /// <summary>使用系统密码学随机数生成器创建 16 字节 challenge。</summary>
    public static byte[] CreateRandomBlock()
    {
        var result = new byte[16];
        RandomNumberGenerator.Fill(result);
        return result;
    }

    private static void ValidateBlockAndKey(ReadOnlySpan<byte> block, ReadOnlySpan<byte> key)
    {
        if (block.Length != 16)
        {
            throw new ArgumentException("AES authentication data must contain exactly 16 bytes.");
        }

        if (key.Length != 16)
        {
            throw new ArgumentException("AES-128 key must contain exactly 16 bytes.");
        }
    }
}
