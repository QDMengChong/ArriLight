using System.Security.Cryptography;
using ArriOpto.BLE.Mesh;

namespace ArriOpto.BLE.Mesh.Provisioning;

/// <summary>
/// 配网参数：由 Provisioner 生成并下发给设备的网络/应用密钥与地址。
/// </summary>
public sealed class ProvisioningOptions
{
    /// <summary>自动生成随机 NetKey / AppKey / IV Index，并分配地址。</summary>
    public static ProvisioningOptions CreateRandom(
        ushort netKeyIndex = 0x0000,
        ushort appKeyIndex = 0x0000,
        ushort provisionerAddress = 0x0001,
        ushort deviceUnicastAddress = 0x0002)
    {
        var options = new ProvisioningOptions
        {
            NetKey = RandomNumberGenerator.GetBytes(16),
            AppKey = RandomNumberGenerator.GetBytes(16),
            NetKeyIndex = netKeyIndex,
            AppKeyIndex = appKeyIndex,
            ProvisionerAddress = provisionerAddress,
            DeviceUnicastAddress = deviceUnicastAddress,
            IvIndex = [0x00, 0x00, 0x00, 0x00]
        };
        return options;
    }

    /// <summary>
    /// 使用共享网络的密钥与 IV 生成配网参数：新设备加入现有网络而不是
    /// 自成一网。设备单播地址由调用方（网络地址分配器）保证唯一。
    /// </summary>
    public static ProvisioningOptions FromNetwork(
        MeshNetwork network,
        ushort deviceUnicastAddress)
    {
        ArgumentNullException.ThrowIfNull(network);
        return new ProvisioningOptions
        {
            NetKey = network.NetKey,
            AppKey = network.AppKey,
            NetKeyIndex = network.NetKeyIndex,
            AppKeyIndex = network.AppKeyIndex,
            ProvisionerAddress = network.ProvisionerAddress,
            DeviceUnicastAddress = deviceUnicastAddress,
            IvIndex = network.IvIndex,
            Flags = 0,
            Ttl = network.Ttl
        };
    }

    /// <summary>网络密钥（16 字节）。</summary>
    public required byte[] NetKey { get; init; }

    /// <summary>应用密钥（16 字节）。</summary>
    public required byte[] AppKey { get; init; }

    /// <summary>NetKey 索引（12 位）。</summary>
    public ushort NetKeyIndex { get; init; }

    /// <summary>AppKey 索引（12 位）。</summary>
    public ushort AppKeyIndex { get; init; }

    /// <summary>Provisioner 自身单播地址（SRC）。</summary>
    public ushort ProvisionerAddress { get; init; }

    /// <summary>下发给设备的单播地址（DST）。</summary>
    public ushort DeviceUnicastAddress { get; init; }

    /// <summary>IV Index（4 字节，大端）。</summary>
    public required byte[] IvIndex { get; init; }

    /// <summary>Flags：bit1=Key Refresh，bit0=IV Update。</summary>
    public byte Flags { get; init; }

    /// <summary>默认 TTL。</summary>
    public byte Ttl { get; init; } = 7;
}
