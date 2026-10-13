using ArriOpto.BLE.Mesh.Crypto;

namespace ArriOpto.BLE.Mesh;

/// <summary>分组类型。</summary>
public enum MeshGroupKind
{
    /// <summary>默认分组：设备配网后的落点（未加入自定义子分组时所在位置），不可删除。</summary>
    Default,

    /// <summary>用户自建子分组。</summary>
    Custom
}

/// <summary>
/// 一个 Mesh 网络内的子分组，使用 0xC000-0xFEFF 的订阅地址。
/// 分组是网络内部的设备组织方式，不改变设备所在网络（密钥不变）。
/// </summary>
public sealed class MeshGroup
{
    /// <summary>稳定 ID（导入分享后保持不变）。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    /// <summary>订阅地址；无 Mesh 分组为 0。</summary>
    public ushort Address { get; set; }

    public MeshGroupKind Kind { get; set; } = MeshGroupKind.Custom;

    /// <summary>系统分组（默认分组）不可删除、不可重命名。</summary>
    public bool IsSystem => Kind == MeshGroupKind.Default;

    public string AddressText => MeshAddress.Format(Address);
}
