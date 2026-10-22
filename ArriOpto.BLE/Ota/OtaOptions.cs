
namespace ArriOpto.BLE.Ota;

/// <summary>
/// 与具体 BLE 库无关的 OTA 协议参数。
/// </summary>
/// <remarks>
/// 该记录应按一次升级会话创建。不要用全局可变对象在多个设备之间共享 MTU、
/// 密钥或超时设置。
/// </remarks>
public sealed record OtaOptions
{
    /// <summary>
    /// 希望向设备申请的 ATT MTU，范围为 23～517，默认 517。
    /// </summary>
    /// <remarks>
    /// 这是请求值而不是保证值。实际分片始终依据连接返回的 MTU 计算。
    /// </remarks>
    public int RequestedMtu { get; init; } = 517;

    /// <summary>
    /// 普通命令等待目标通知的最长时间，默认 10 秒。
    /// </summary>
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 发送一组固件数据后等待设备确认的最长时间，默认 15 秒。
    /// </summary>
    public TimeSpan TransferAcknowledgementTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// SLB 完成命令与最终校验响应的总等待预算，默认 45 秒。
    /// 与逐窗口 ACK 超时分开，允许设备分块执行整包和分区校验。
    /// </summary>
    public TimeSpan SlbVerificationTimeout { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>
    /// 加密固件使用的 AES-128 key，以 32 个十六进制字符表示。
    /// </summary>
    /// <remarks>
    /// 普通固件可为 <see langword="null"/>。SDK 不持久化密钥，调用方负责安全保管。
    /// </remarks>
    public string? AesKeyHex { get; init; }

    /// <summary>
    /// 是否在固件和设备都提供 Product ID 时拒绝型号不匹配的升级。
    /// </summary>
    /// <remarks>
    /// 量产环境应保持为 <see langword="true"/>。关闭后可能把固件写入错误产品。
    /// </remarks>
    public bool ValidateProductId { get; init; } = true;

    /// <summary>
    /// 可选的结构化诊断配置。为 <see langword="null"/> 时不产生诊断事件。
    /// </summary>
    public OtaDiagnostics? Diagnostics { get; init; }

    internal void Validate()
    {
        if (RequestedMtu is < 23 or > 517)
        {
            throw new ArgumentOutOfRangeException(
                nameof(RequestedMtu),
                "BLE ATT MTU must be between 23 and 517.");
        }

        if (CommandTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(CommandTimeout));
        }

        if (TransferAcknowledgementTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(TransferAcknowledgementTimeout));
        }

        if (SlbVerificationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(SlbVerificationTimeout));
        }

        if (AesKeyHex is not null && !HexCodec.IsHex(AesKeyHex, 16))
        {
            throw new ArgumentException(
                "AES key must contain exactly 16 bytes encoded as 32 hexadecimal characters.",
                nameof(AesKeyHex));
        }
    }
}
