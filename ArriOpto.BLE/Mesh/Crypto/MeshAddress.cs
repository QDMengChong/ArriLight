namespace ArriOpto.BLE.Mesh.Crypto;

/// <summary>
/// Mesh 地址分类与格式化工具（Mesh Protocol 1.1, Section 3.4.2）。
/// </summary>
public static class MeshAddress
{
    public const ushort UnicastAddressMin = 0x0001;
    public const ushort UnicastAddressMax = 0x7FFF;
    public const ushort VirtualAddressMin = 0x8000;
    public const ushort VirtualAddressMax = 0xBFFF;
    public const ushort GroupAddressMin = 0xC000;
    public const ushort GroupAddressMax = 0xFEFF;
    public const ushort AllNodesAddress = 0xFFFF;

    /// <summary>单播地址（0x0001-0x7FFF）。</summary>
    public static bool IsUnicast(ushort address) =>
        address is >= UnicastAddressMin and <= UnicastAddressMax;

    /// <summary>虚拟地址（0x8000-0xBFFF）。</summary>
    public static bool IsVirtual(ushort address) =>
        address is >= VirtualAddressMin and <= VirtualAddressMax;

    /// <summary>分组（订阅）地址（0xC000-0xFEFF）。</summary>
    public static bool IsGroup(ushort address) =>
        address is >= GroupAddressMin and <= GroupAddressMax;

    /// <summary>所有节点地址。</summary>
    public static bool IsAllNodes(ushort address) => address == AllNodesAddress;

    /// <summary>
    /// 是否可作为订阅地址：分组地址或虚拟地址（Mesh Protocol 1.1
    /// Section 3.4.2.2/3.4.2.3）。单播与 All Nodes 不允许出现在订阅列表。
    /// </summary>
    public static bool IsValidSubscriptionAddress(ushort address) =>
        IsGroup(address) || IsVirtual(address);

    /// <summary>格式化为 4 位十六进制地址（如 C000）。</summary>
    public static string Format(ushort address) => address.ToString("X4");
}
