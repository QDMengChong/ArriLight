namespace ArriOpto.BLE.Ota;

/// <summary>
/// 从固件内部固定 marker 中提取的产品兼容信息。
/// </summary>
/// <param name="ProductId">两字节 Product ID 的四位十六进制字符串。</param>
/// <param name="BootVersion">六字节 boot version 的十二位十六进制字符串。</param>
public sealed record FirmwareMetadata(string? ProductId, string? BootVersion)
{
    /// <summary>Product ID 和 boot version 是否都已成功提取。</summary>
    public bool IsPresent =>
        !string.IsNullOrWhiteSpace(ProductId) && !string.IsNullOrWhiteSpace(BootVersion);
}

/// <summary>
/// 已解析并通过基础校验的固件镜像基类。
/// </summary>
public abstract class FirmwareImage
{
    /// <summary>初始化已解析固件的公共属性。</summary>
    /// <param name="path">规范化文件路径。</param>
    /// <param name="kind">固件内容格式。</param>
    /// <param name="protocol">目标 OTA 协议。</param>
    /// <param name="isSecure">是否为加密固件。</param>
    /// <param name="metadata">从镜像中提取的产品 metadata。</param>
    protected FirmwareImage(
        string path,
        FirmwareKind kind,
        OtaProtocol protocol,
        bool isSecure,
        FirmwareMetadata metadata)
    {
        Path = path;
        Kind = kind;
        Protocol = protocol;
        IsSecure = isSecure;
        Metadata = metadata;
    }

    /// <summary>
    /// 路径输入时为规范化绝对路径；Stream 输入时为调用方提供的逻辑文件名。
    /// </summary>
    public string Path { get; }

    /// <summary>固件内容格式。</summary>
    public FirmwareKind Kind { get; }

    /// <summary>该文件应使用的 OTA 协议族。</summary>
    public OtaProtocol Protocol { get; }

    /// <summary>文件名/扩展名是否表示加密固件。</summary>
    public bool IsSecure { get; }

    /// <summary>从固件内容提取的可选产品兼容信息。</summary>
    public FirmwareMetadata Metadata { get; }

    /// <summary>需要传输的有效载荷总字节数。</summary>
    public abstract long Length { get; }
}

/// <summary>
/// SLB 使用的连续二进制固件。
/// </summary>
public sealed class SlbFirmwareImage : FirmwareImage
{
    internal SlbFirmwareImage(
        string path,
        byte[] data,
        bool isSecure,
        string? resourceAddress,
        FirmwareMetadata metadata)
        : base(path, FirmwareKind.Binary, OtaProtocol.Slb, isSecure, metadata)
    {
        Data = data;
        ResourceAddress = resourceAddress;
    }

    /// <summary>完整且连续的固件字节。</summary>
    public ReadOnlyMemory<byte> Data { get; }

    /// <summary>
    /// SLB 资源文件名中提取的八位十六进制目标地址；普通固件为
    /// <see langword="null"/>。
    /// </summary>
    public string? ResourceAddress { get; }

    /// <inheritdoc />
    public override long Length => Data.Length;
}

/// <summary>
/// Single Bank 固件中的一段连续地址数据。
/// </summary>
public sealed class FirmwarePartition
{
    /// <summary>
    /// 创建分区。<paramref name="data"/> 应只包含从
    /// <paramref name="runAddress"/> 开始的连续字节。
    /// </summary>
    public FirmwarePartition(uint runAddress, byte[] data)
    {
        RunAddress = runAddress;
        Data = data;
    }

    /// <summary>固件在芯片地址空间中的运行地址。</summary>
    public uint RunAddress { get; }

    /// <summary>该分区的连续数据。</summary>
    public ReadOnlyMemory<byte> Data { get; }

    /// <summary>分区有效载荷长度，单位为字节。</summary>
    public int Length => Data.Length;
}

/// <summary>
/// 由一个或多个 <see cref="FirmwarePartition"/> 组成的 Single Bank 固件。
/// </summary>
public sealed class SingleBankFirmwareImage : FirmwareImage
{
    internal SingleBankFirmwareImage(
        string path,
        FirmwareKind kind,
        bool isSecure,
        IReadOnlyList<FirmwarePartition> partitions,
        FirmwareMetadata metadata)
        : base(path, kind, OtaProtocol.SingleBank, isSecure, metadata)
    {
        Partitions = partitions;
    }

    /// <summary>按照 Intel HEX 文件中地址出现顺序得到的连续分区。</summary>
    public IReadOnlyList<FirmwarePartition> Partitions { get; }

    /// <inheritdoc />
    public override long Length => Partitions.Sum(partition => (long)partition.Length);
}
