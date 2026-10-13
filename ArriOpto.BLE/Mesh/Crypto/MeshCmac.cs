namespace ArriOpto.BLE.Mesh.Crypto;

/// <summary>
/// AES-CMAC（RFC 4493 / NIST SP 800-38B）实现，输出与蓝牙 Mesh 官方参考实现一致。
/// </summary>
/// <remarks>
/// .NET 运行时没有内置 AES-CMAC（AesCmac 类型不存在），因此基于
/// <see cref="MeshCrypto.EncryptAesEcbBlock"/> 实现标准 CBC-MAC 变体：
/// 子密钥 K1/K2 的派生与末块补位（0x80 后补零）均按 RFC 4493。
/// </remarks>
public static class MeshCmac
{
    /// <summary>计算 16 字节 AES-CMAC。</summary>
    public static byte[] ComputeHash(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data)
    {
        if (key.Length != 16)
            throw new ArgumentException("AES-CMAC 密钥必须为 16 字节。", nameof(key));

        // 子密钥：L = AES_K(0^128)，K1 = (L<<1) | MSB?Rb，K2 = (K1<<1) | MSB?Rb。
        var k1 = GenerateSubkey(MeshCrypto.EncryptAesEcbBlock(key, new byte[16]));
        var k2 = GenerateSubkey(k1);

        // 分块 CBC-MAC；空消息按单个补位块处理。
        var blockCount = Math.Max(1, (data.Length + 15) / 16);
        var block = new byte[16];
        var previous = new byte[16]; // 上一轮密文，初始为 0^128

        for (var i = 0; i < blockCount; i++)
        {
            var offset = i * 16;
            var remaining = data.Length - offset;
            var isLast = i == blockCount - 1;

            if (isLast && remaining == 16)
            {
                // 完整末块：M XOR K1。
                for (var j = 0; j < 16; j++)
                    block[j] = (byte)(data[offset + j] ^ k1[j]);
            }
            else if (isLast)
            {
                // 不完整末块：M || 0x80 || 0^pad，再 XOR K2。
                for (var j = 0; j < remaining; j++)
                    block[j] = data[offset + j];
                block[remaining] = 0x80;
                for (var j = remaining + 1; j < 16; j++)
                    block[j] = 0;
                for (var j = 0; j < 16; j++)
                    block[j] ^= k2[j];
            }
            else
            {
                // 中间完整块。
                for (var j = 0; j < 16; j++)
                    block[j] = data[offset + j];
            }

            for (var j = 0; j < 16; j++)
                block[j] ^= previous[j];

            previous = MeshCrypto.EncryptAesEcbBlock(key, block);
        }

        return previous;
    }

    /// <summary>左移一位；最高位为 1 时末字节异或 Rb = 0x87。</summary>
    private static byte[] GenerateSubkey(ReadOnlySpan<byte> input)
    {
        var output = new byte[16];
        var carry = 0;
        for (var i = 15; i >= 0; i--)
        {
            output[i] = (byte)((input[i] << 1) | carry);
            carry = input[i] >> 7;
        }

        if (carry == 1)
        {
            output[^1] ^= 0x87;
        }

        return output;
    }
}
