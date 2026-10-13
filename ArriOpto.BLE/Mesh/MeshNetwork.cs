using ArriOpto.BLE.Mesh.Crypto;

namespace ArriOpto.BLE.Mesh;

/// <summary>
/// 网络中的一台设备（节点）。节点始终归属且仅归属一个分组：
/// 自建分组 / 默认分组。节点归属由所属网络的节点列表决定。
/// </summary>
public sealed class MeshNode
{
    /// <summary>BLE 地址（大写，与 App 设备列表一致）。</summary>
    public string BluetoothAddress { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>配网时分配的单播地址。</summary>
    public ushort UnicastAddress { get; set; }

    /// <summary>
    /// 仅用于旧版数据迁移：旧模型用该字段表示"Mesh 已关闭（无 Mesh 分组）"。
    /// 新模型网络内节点始终为 Mesh 已启用，迁移后该字段不再参与业务逻辑。
    /// </summary>
    public bool IsMeshEnabled { get; set; } = true;

    /// <summary>
    /// 兼容字段：订阅列表中的第一个分组地址（0 = 未加入任何子分组）。
    /// 新代码应使用 <see cref="SubscriptionAddresses"/>；该字段保留是为了旧数据
    /// 迁移和旧 JSON 兼容（保存时同步为第一个订阅地址）。
    /// </summary>
    public ushort CurrentGroupAddress { get; set; }

    /// <summary>
    /// 设备订阅的子分组地址列表：一台设备可同时加入多个设备组
    /// （设备侧模型订阅列表对应多个组地址，组播消息会全部到达）。
    /// </summary>
    public List<ushort> SubscriptionAddresses { get; set; } = [];

    /// <summary>配网产生的设备密钥（k1 派生），用于恢复设备密钥消息。</summary>
    public byte[] DeviceKey { get; set; } = [];
}

/// <summary>
/// 一个 Mesh 网络：一套 NetKey/AppKey、地址分配器、子分组与节点。
/// 网络是密钥与地址的隔离边界；多个网络可共存（默认网络 + 用户自建网络）。
/// </summary>
public sealed class MeshNetwork
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>
    /// 稳定网络 ID（分享/导入时用于去重与合并）。创建新网络时由 NetKey 派生
    /// （见 <see cref="EnsureStableId"/>），保证同一网络跨导入/重启身份稳定。
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>网络名称（默认网络/用户自定义）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>是否为默认网络：新建设备配网后自动进入；不可删除。</summary>
    public bool IsDefault { get; set; }

    /// <summary>网络密钥（16 字节）。</summary>
    public byte[] NetKey { get; set; } = [];

    /// <summary>应用密钥（16 字节）。</summary>
    public byte[] AppKey { get; set; } = [];

    public ushort NetKeyIndex { get; set; }

    public ushort AppKeyIndex { get; set; }

    /// <summary>IV Index（4 字节，大端）。</summary>
    public byte[] IvIndex { get; set; } = [0, 0, 0, 0];

    public ushort ProvisionerAddress { get; set; } = 0x0001;

    public byte Ttl { get; set; } = 7;

    /// <summary>
    /// 本机 Provisioner 在本网络的序列号。回放保护按 (SRC, SEQ) 判定：
    /// 不同手机共用同一网络时各自使用不同的 ProvisionerAddress，序列号互不影响。
    /// </summary>
    public uint SequenceNumber { get; set; }

    /// <summary>下一个待分配的单播地址。</summary>
    public ushort NextUnicastAddress { get; set; } = 0x0002;

    /// <summary>下一个待分配的分组地址。</summary>
    public ushort NextGroupAddress { get; set; } = MeshAddress.GroupAddressMin;

    public List<MeshGroup> Groups { get; set; } = [];

    public List<MeshNode> Nodes { get; set; } = [];

    public MeshGroup? DefaultGroup =>
        Groups.FirstOrDefault(group => group.Kind == MeshGroupKind.Default);

    /// <summary>
    /// 确保网络有稳定 ID：旧数据/旧分享码没有 ID 时，用 NetKey 派生确定性 ID，
    /// 使同一网络（同一 NetKey）在多次导入时能够去重合并。
    /// </summary>
    public void EnsureStableId()
    {
        if (!string.IsNullOrWhiteSpace(Id))
            return;

        var hash = System.Security.Cryptography.SHA256.HashData(NetKey);
        Id = "net-" + Convert.ToHexString(hash)[..16];
    }

    /// <summary>
    /// 确保“默认分组”存在（首次使用网络时调用）。网络内不再有“无 Mesh 分组”。
    /// </summary>
    public void EnsureSystemGroups()
    {
        if (DefaultGroup is null)
        {
            var address = AllocateGroupAddress();
            Groups.Add(new MeshGroup
            {
                Name = "默认分组",
                Kind = MeshGroupKind.Default,
                Address = address
            });
        }
    }

    /// <summary>分配一个未被占用的分组地址（0xC000 起）。</summary>
    public ushort AllocateGroupAddress()
    {
        const int groupRange = MeshAddress.GroupAddressMax - MeshAddress.GroupAddressMin + 1;
        for (var probe = 0; probe < groupRange; probe++)
        {
            var offset = (NextGroupAddress - MeshAddress.GroupAddressMin + probe) % groupRange;
            var candidate = unchecked((ushort)(MeshAddress.GroupAddressMin + offset));
            if (Groups.All(group => group.Address != candidate))
            {
                NextGroupAddress = unchecked((ushort)(
                    MeshAddress.GroupAddressMin + ((offset + 1) % groupRange)));
                return candidate;
            }
        }

        throw new InvalidOperationException("分组地址已用尽。");
    }

    /// <summary>分配下一个单播地址并递增分配器。</summary>
    public ushort AllocateUnicastAddress()
    {
        if (NextUnicastAddress > MeshAddress.UnicastAddressMax)
            throw new InvalidOperationException("单播地址已用尽。");
        return NextUnicastAddress++;
    }

    public MeshNode? FindNode(string bluetoothAddress) =>
        Nodes.FirstOrDefault(node =>
            string.Equals(
                node.BluetoothAddress,
                bluetoothAddress,
                StringComparison.OrdinalIgnoreCase));

    public MeshNode? FindNodeByUnicast(ushort unicastAddress) =>
        Nodes.FirstOrDefault(node => node.UnicastAddress == unicastAddress);

    public MeshGroup? FindGroup(string groupId) =>
        Groups.FirstOrDefault(group => group.Id == groupId);

    public MeshGroup? FindGroupByAddress(ushort address) =>
        Groups.FirstOrDefault(group => group.Address == address);

    /// <summary>设备加入/退出子分组后保持兼容字段与订阅列表一致。</summary>
    public void SyncSubscriptionFields(MeshNode node)
    {
        node.SubscriptionAddresses.RemoveAll(address => address == 0);
        node.CurrentGroupAddress = node.SubscriptionAddresses.FirstOrDefault();
    }

    public string GroupNameFor(ushort address) =>
        FindGroupByAddress(address)?.Name ?? address.ToString("X4");

    /// <summary>
    /// 子分组内的设备：自定义分组按订阅地址匹配；默认分组是本地的"未归组"
    /// 容器（未订阅任何自定义子分组的设备）。
    /// </summary>
    public IReadOnlyList<MeshNode> NodesInGroup(MeshGroup group)
    {
        if (group.IsSystem)
        {
            return Nodes
                .Where(node => node.SubscriptionAddresses.Count == 0)
                .ToList();
        }

        return Nodes
            .Where(node => node.SubscriptionAddresses.Contains(group.Address))
            .ToList();
    }

    /// <summary>把设备加入子分组（仅更新模型，不下发订阅消息；可同时属于多个组）。</summary>
    public void AddNodeToGroup(MeshNode node, MeshGroup group)
    {
        if (!node.SubscriptionAddresses.Contains(group.Address))
            node.SubscriptionAddresses.Add(group.Address);
        SyncSubscriptionFields(node);
    }

    /// <summary>把设备移出子分组（仅更新模型，不下发退订消息）。</summary>
    public void RemoveNodeFromGroup(MeshNode node, MeshGroup group)
    {
        node.SubscriptionAddresses.RemoveAll(address => address == group.Address);
        SyncSubscriptionFields(node);
    }
}
