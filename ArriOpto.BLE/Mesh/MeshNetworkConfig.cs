using System.Security.Cryptography;
using ArriOpto.BLE.Mesh.Crypto;
using ArriOpto.BLE.Mesh.Provisioning;

namespace ArriOpto.BLE.Mesh;

/// <summary>
/// 一个已配网节点的网络配置：密钥、索引、地址与 IV，用于后续收发消息。
/// 属性可读写，便于宿主应用序列化持久化（如 Preferences/JSON）。
/// </summary>
public sealed class MeshNetworkConfig
{
    private uint _sequenceNumber;

    /// <summary>创建空配置（供反序列化使用，请用 <see cref="FromProvisioning"/> 生成）。</summary>
    public MeshNetworkConfig() { }

    /// <summary>
    /// 从持久化的网络与节点构建配置（本地配置丢失后的恢复路径）。
    /// 与 <see cref="FromProvisioning"/> 的区别：不重新执行配网，直接复制
    /// 网络级字段并派生 k2/k4，同时恢复网络级序列号——回放保护按 SRC+SEQ
    /// 判定，恢复出的配置若从 0 重新计数会被设备当作重放消息丢弃。
    /// 节点可以没有 DeviceKey（分享导入的"仅控制"模式）：应用密钥消息
    /// （单播/组播控制）不需要 DeviceKey，只有配置消息需要。
    /// </summary>
    public static MeshNetworkConfig FromNetwork(
        MeshNetwork network,
        MeshNode node)
    {
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(node);
        if (node.DeviceKey.Length is not (0 or 16))
        {
            throw new InvalidOperationException(
                "设备密钥长度非法（必须为空或 16 字节）。");
        }

        var k2 = MeshKeyDerivation.K2(network.NetKey, [0x00]);
        return new MeshNetworkConfig
        {
            NetKey = network.NetKey,
            AppKey = network.AppKey,
            NetKeyIndex = network.NetKeyIndex,
            AppKeyIndex = network.AppKeyIndex,
            ProvisionerAddress = network.ProvisionerAddress,
            DeviceUnicastAddress = node.UnicastAddress,
            IvIndex = network.IvIndex,
            Flags = 0,
            Ttl = network.Ttl,
            Nid = k2.Nid,
            EncryptionKey = k2.EncryptionKey,
            PrivacyKey = k2.PrivacyKey,
            Aid = MeshKeyDerivation.K4(network.AppKey),
            DeviceKey = node.DeviceKey,
            SequenceNumber = network.SequenceNumber
        };
    }

    /// <summary>从配网选项与配网结果构建配置。</summary>
    public static MeshNetworkConfig FromProvisioning(
        ProvisioningOptions options,
        ProvisioningResult result)
    {
        var k2 = MeshKeyDerivation.K2(options.NetKey, [0x00]);
        return new MeshNetworkConfig
        {
            NetKey = options.NetKey,
            AppKey = options.AppKey,
            NetKeyIndex = options.NetKeyIndex,
            AppKeyIndex = options.AppKeyIndex,
            ProvisionerAddress = options.ProvisionerAddress,
            DeviceUnicastAddress = options.DeviceUnicastAddress,
            IvIndex = options.IvIndex,
            Flags = options.Flags,
            Ttl = options.Ttl,
            Nid = k2.Nid,
            EncryptionKey = k2.EncryptionKey,
            PrivacyKey = k2.PrivacyKey,
            Aid = MeshKeyDerivation.K4(options.AppKey),
            DeviceKey = result.DeviceKey
        };
    }

    /// <summary>网络密钥。</summary>
    public byte[] NetKey { get; set; } = [];

    /// <summary>应用密钥。</summary>
    public byte[] AppKey { get; set; } = [];

    /// <summary>NetKey 索引。</summary>
    public ushort NetKeyIndex { get; set; }

    /// <summary>AppKey 索引。</summary>
    public ushort AppKeyIndex { get; set; }

    /// <summary>Provisioner 自身单播地址（SRC）。</summary>
    public ushort ProvisionerAddress { get; set; }

    /// <summary>设备单播地址（DST）。</summary>
    public ushort DeviceUnicastAddress { get; set; }

    /// <summary>IV Index（4 字节，大端）。</summary>
    public byte[] IvIndex { get; set; } = [0, 0, 0, 0];

    /// <summary>配网 Flags。</summary>
    public byte Flags { get; set; }

    /// <summary>TTL。</summary>
    public byte Ttl { get; set; }

    /// <summary>k2 派生的 NID（7 位）。</summary>
    public byte Nid { get; set; }

    /// <summary>k2 派生的网络加密密钥。</summary>
    public byte[] EncryptionKey { get; set; } = [];

    /// <summary>k2 派生的网络隐私密钥。</summary>
    public byte[] PrivacyKey { get; set; } = [];

    /// <summary>k4 派生的 AID（6 位）。</summary>
    public byte Aid { get; set; }

    /// <summary>设备密钥（k1 派生，用于设备密钥消息）。</summary>
    public byte[] DeviceKey { get; set; } = [];

    /// <summary>
    /// 是否拥有设备密钥：有密钥可发送配置消息（订阅/Node Reset 等）；
    /// 无密钥只能发送应用密钥消息（单播/组播控制），即"仅控制"模式。
    /// </summary>
    public bool HasDeviceKey => DeviceKey.Length == 16;

    /// <summary>
    /// 最后使用的 24 位网络序列号。必须随配置持久化：设备按 (SRC, SEQ) 做
    /// 回放保护，App 重启后若序列号回退会被设备当作重放消息丢弃。
    /// </summary>
    public uint SequenceNumber
    {
        get => _sequenceNumber;
        set => _sequenceNumber = value;
    }

    /// <summary>
    /// 每次分配新序列号后触发（参数为分配后的值），用于宿主持久化。
    /// 序列号在构造 PDU 时即被消耗，写入失败、取消或断连都不会退回。
    /// 宿主通常不需要直接设置：改用 <see cref="MeshClient.SequenceNumberAllocated"/>，
    /// 它会在配网与恢复配置后自动传导到这里。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Action<uint>? SequenceNumberAllocated { get; set; }

    /// <summary>取下一个 24 位序列号并递增，递增后立即通知持久化回调。</summary>
    public byte[] NextSequenceNumber()
    {
        var seq = unchecked((uint)Interlocked.Increment(ref _sequenceNumber)) & 0x00FFFFFF;
        SequenceNumberAllocated?.Invoke(seq);
        return [(byte)(seq >> 16), (byte)(seq >> 8), (byte)seq];
    }
}
