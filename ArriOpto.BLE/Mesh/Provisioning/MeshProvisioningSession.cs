using System.Security.Cryptography;
using ArriOpto.BLE.Mesh.Crypto;

namespace ArriOpto.BLE.Mesh.Provisioning;

/// <summary>
/// 配网会话：ECDH-P256 密钥交换与配网密钥派生（Mesh Protocol 1.1 §5.4.2/§5.4.3）。
/// </summary>
/// <remarks>
/// 使用经典算法 BTM_ECDH_P256_CMAC_AES128_AES_CCM（算法 0x00）与 No OOB
/// 认证。官方样例向量见单元测试（Mesh Protocol 1.1 §8.17.1）。
/// </remarks>
public sealed class MeshProvisioningSession : IDisposable
{
    private static readonly byte[] AuthValueNoOob = new byte[16];

    private readonly ECDiffieHellman _ecdh;

    public MeshProvisioningSession()
    {
        _ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var parameters = _ecdh.ExportParameters(false);
        PublicKeyX = parameters.Q.X!;
        PublicKeyY = parameters.Q.Y!;
    }

    /// <summary>
    /// 用指定密钥材料构造会话（主要用于单元测试与密钥恢复）。
    /// </summary>
    public static MeshProvisioningSession CreateWithKeyMaterial(
        ReadOnlySpan<byte> privateKeyD,
        ReadOnlySpan<byte> publicKeyX,
        ReadOnlySpan<byte> publicKeyY)
    {
        if (privateKeyD.Length != 32 || publicKeyX.Length != 32 || publicKeyY.Length != 32)
            throw new ArgumentException("P-256 密钥材料必须各 32 字节。");

        var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        ecdh.ImportParameters(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = privateKeyD.ToArray(),
            Q = new ECPoint
            {
                X = publicKeyX.ToArray(),
                Y = publicKeyY.ToArray()
            }
        });
        return new MeshProvisioningSession(ecdh);
    }

    private MeshProvisioningSession(ECDiffieHellman ecdh)
    {
        _ecdh = ecdh;
        var parameters = _ecdh.ExportParameters(false);
        PublicKeyX = parameters.Q.X!;
        PublicKeyY = parameters.Q.Y!;
    }

    /// <summary>本地公钥 X（32 字节）。</summary>
    public byte[] PublicKeyX { get; }

    /// <summary>本地公钥 Y（32 字节）。</summary>
    public byte[] PublicKeyY { get; }

    /// <summary>ECDH 共享秘密（P-256 的 X 坐标，32 字节）。</summary>
    public byte[] ComputeEcdhSecret(ReadOnlySpan<byte> peerX, ReadOnlySpan<byte> peerY)
    {
        if (peerX.Length != 32 || peerY.Length != 32)
            throw new ArgumentException("对端 P-256 公钥 X/Y 必须各 32 字节。");

        using var peer = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        peer.ImportParameters(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = peerX.ToArray(),
                Y = peerY.ToArray()
            }
        });
        return _ecdh.DeriveRawSecretAgreement(peer.PublicKey);
    }

    /// <summary>
    /// 计算配网所需的全部密钥与确认值。
    /// </summary>
    /// <param name="ecdhSecret">ECDH 共享秘密。</param>
    /// <param name="confirmationInputs">
    /// InviteValue || CapabilitiesValue || StartValue || PublicKeyProvisioner || PublicKeyDevice。
    /// </param>
    /// <param name="randomProvisioner">Provisioner 随机数（16 字节）。</param>
    /// <param name="randomDevice">设备随机数（16 字节）。</param>
    public static ProvisioningKeySet DeriveKeys(
        ReadOnlySpan<byte> ecdhSecret,
        ReadOnlySpan<byte> confirmationInputs,
        ReadOnlySpan<byte> randomProvisioner,
        ReadOnlySpan<byte> randomDevice)
    {
        var confirmationSalt = MeshKeyDerivation.S1(confirmationInputs);
        var confirmationKey = MeshKeyDerivation.K1(
            ecdhSecret,
            confirmationSalt,
            "prck"u8);

        var provisioningSaltInput = new byte[confirmationSalt.Length + randomProvisioner.Length + randomDevice.Length];
        confirmationSalt.CopyTo(provisioningSaltInput);
        randomProvisioner.CopyTo(provisioningSaltInput.AsSpan(confirmationSalt.Length));
        randomDevice.CopyTo(provisioningSaltInput.AsSpan(confirmationSalt.Length + randomProvisioner.Length));
        var provisioningSalt = MeshKeyDerivation.S1(provisioningSaltInput);

        var sessionKey = MeshKeyDerivation.K1(ecdhSecret, provisioningSalt, "prsk"u8);
        var sessionNonceFull = MeshKeyDerivation.K1(ecdhSecret, provisioningSalt, "prsn"u8);
        var sessionNonce = sessionNonceFull[^13..].ToArray();
        var deviceKey = MeshKeyDerivation.K1(ecdhSecret, provisioningSalt, "prdk"u8);

        // No OOB：AuthValue 为 16 字节全零
        var provisionerConfirmationInput = Concat(randomProvisioner, AuthValueNoOob);
        var deviceConfirmationInput = Concat(randomDevice, AuthValueNoOob);
        var provisionerConfirmation = MeshCmac.ComputeHash(confirmationKey, provisionerConfirmationInput);
        var deviceConfirmation = MeshCmac.ComputeHash(confirmationKey, deviceConfirmationInput);

        return new ProvisioningKeySet(
            confirmationSalt,
            confirmationKey,
            provisioningSalt,
            sessionKey,
            sessionNonce,
            deviceKey,
            provisionerConfirmation,
            deviceConfirmation);
    }

    /// <summary>
    /// 加密配网数据：NetKey(16) + NetKeyIndex(2, 大端) + Flags(1) +
    /// IVIndex(4, 大端) + UnicastAddress(2, 大端)，AES-CCM MIC=8。
    /// </summary>
    public static byte[] EncryptProvisioningData(
        ReadOnlySpan<byte> sessionKey,
        ReadOnlySpan<byte> sessionNonce,
        ProvisioningOptions options)
    {
        var data = new byte[16 + 2 + 1 + 4 + 2];
        options.NetKey.CopyTo(data);
        data[16] = (byte)(options.NetKeyIndex >> 8);
        data[17] = (byte)options.NetKeyIndex;
        data[18] = options.Flags;
        options.IvIndex.CopyTo(data.AsSpan(19));
        data[23] = (byte)(options.DeviceUnicastAddress >> 8);
        data[24] = (byte)options.DeviceUnicastAddress;
        return MeshCrypto.EncryptCcm(sessionKey, sessionNonce, data, micSize: 8);
    }

    public void Dispose() => _ecdh.Dispose();

    private static byte[] Concat(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var result = new byte[a.Length + b.Length];
        a.CopyTo(result);
        b.CopyTo(result.AsSpan(a.Length));
        return result;
    }
}

/// <summary>配网派生出的密钥与确认值集合。</summary>
public sealed record ProvisioningKeySet(
    byte[] ConfirmationSalt,
    byte[] ConfirmationKey,
    byte[] ProvisioningSalt,
    byte[] SessionKey,
    byte[] SessionNonce,
    byte[] DeviceKey,
    byte[] ProvisionerConfirmation,
    byte[] DeviceConfirmation);
