namespace ArriOpto.BLE.Ota;

/// <summary>
/// 诊断事件严重程度。数值从低到高，便于日志接收器按阈值过滤。
/// </summary>
public enum OtaDiagnosticLevel
{
    /// <summary>高频、仅在分析协议细节时需要的事件。</summary>
    Trace,

    /// <summary>连接、分区和 offset 等调试信息。</summary>
    Debug,

    /// <summary>升级阶段开始、完成等正常生命周期事件。</summary>
    Information,

    /// <summary>可以恢复或已执行降级处理的异常情况。</summary>
    Warning,

    /// <summary>导致当前升级失败的错误。</summary>
    Error
}

/// <summary>诊断事件中 BLE 报文的方向。</summary>
public enum OtaDiagnosticDirection
{
    /// <summary>事件不对应一条 BLE 报文。</summary>
    None,

    /// <summary>手机发送到设备。</summary>
    Transmit,

    /// <summary>设备发送到手机。</summary>
    Receive
}

/// <summary>用于决定载荷是否可以写入日志的报文类别。</summary>
public enum OtaPayloadKind
{
    /// <summary>控制、认证、版本或校验命令。</summary>
    Command,

    /// <summary>设备 notification。</summary>
    Notification,

    /// <summary>固件正文数据。</summary>
    FirmwareData
}

/// <summary>
/// 控制诊断日志是否包含十六进制报文正文。
/// </summary>
public enum OtaPayloadLoggingMode
{
    /// <summary>只记录长度和结构化字段，不记录任何报文正文。</summary>
    None,

    /// <summary>记录命令和 notification，不记录大量固件正文。默认值。</summary>
    ProtocolOnly,

    /// <summary>记录包括固件正文在内的所有报文，仅建议在受控测试环境使用。</summary>
    All
}

/// <summary>
/// 一条可序列化的 OTA 结构化诊断事件。
/// </summary>
/// <param name="Timestamp">UTC 时间戳。</param>
/// <param name="Level">严重程度。</param>
/// <param name="EventName">稳定、可用于筛选的事件名，例如 <c>gatt.write</c>。</param>
/// <param name="Category">事件来源，例如 <c>connection</c> 或 <c>slb</c>。</param>
/// <param name="Message">供开发者阅读的说明。</param>
public sealed record OtaDiagnosticEvent(
    DateTimeOffset Timestamp,
    OtaDiagnosticLevel Level,
    string EventName,
    string Category,
    string Message)
{
    /// <summary>用于关联一次升级全部事件的会话 ID。</summary>
    public string? SessionId { get; init; }

    /// <summary>设备标识，仅用于本次日志关联，不保证是真实 MAC。</summary>
    public string? DeviceId { get; init; }

    /// <summary>固件/状态机使用的协议族。</summary>
    public OtaProtocol? Protocol { get; init; }

    /// <summary>当前发现的 GATT 模式。</summary>
    public OtaGattMode? Mode { get; init; }

    /// <summary>与事件对应的 OTA 生命周期状态。</summary>
    public OtaState? State { get; init; }

    /// <summary>报文方向；非报文事件为 <see cref="OtaDiagnosticDirection.None"/>。</summary>
    public OtaDiagnosticDirection Direction { get; init; }

    /// <summary>执行读写或收到通知的 characteristic UUID。</summary>
    public Guid? CharacteristicId { get; init; }

    /// <summary>原始报文长度，即使正文被隐藏也会记录。</summary>
    public int? PayloadLength { get; init; }

    /// <summary>按安全策略截取的十六进制正文；被禁用时为 <see langword="null"/>。</summary>
    public string? PayloadHex { get; init; }

    /// <summary><see cref="PayloadHex"/> 是否因最大记录长度而被截断。</summary>
    public bool PayloadTruncated { get; init; }

    /// <summary>事件发生时连接使用的 ATT MTU。</summary>
    public int? Mtu { get; init; }

    /// <summary>协议确认或准备发送的固件字节 offset。</summary>
    public long? Offset { get; init; }

    /// <summary>Single Bank 分区索引。</summary>
    public int? PartitionIndex { get; init; }

    /// <summary>操作耗时。</summary>
    public TimeSpan? Elapsed { get; init; }

    /// <summary>底层 API 返回的 GATT result；0 表示成功。</summary>
    public int? GattResult { get; init; }

    /// <summary>异常的完整类型名。</summary>
    public string? ExceptionType { get; init; }

    /// <summary>异常消息。为避免泄漏，不自动包含完整堆栈。</summary>
    public string? ExceptionMessage { get; init; }

    /// <summary>事件特有、适合 JSON 序列化的附加字段。</summary>
    public IReadOnlyDictionary<string, string>? Properties { get; init; }
}

/// <summary>
/// 结构化诊断事件接收器。实现必须快速返回，耗时 I/O 应转交后台队列。
/// </summary>
public interface IOtaDiagnosticSink
{
    /// <summary>接收一条已经应用载荷安全策略的诊断事件。</summary>
    void Write(OtaDiagnosticEvent diagnosticEvent);
}

/// <summary>把委托包装成诊断接收器，适合 MAUI 页面或测试快速接入。</summary>
public sealed class DelegateOtaDiagnosticSink : IOtaDiagnosticSink
{
    private readonly Action<OtaDiagnosticEvent> _writer;

    /// <summary>创建委托接收器。</summary>
    public DelegateOtaDiagnosticSink(Action<OtaDiagnosticEvent> writer)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    /// <inheritdoc />
    public void Write(OtaDiagnosticEvent diagnosticEvent) => _writer(diagnosticEvent);
}

/// <summary>
/// 一次 OTA 升级使用的诊断策略和安全写入入口。
/// </summary>
/// <remarks>
/// SDK 会吞掉接收器自身抛出的异常，避免日志系统故障中断固件升级。接收器不应在
/// BLE 回调线程执行磁盘或网络阻塞操作；推荐写入无界/有界 Channel 后异步落盘。
/// </remarks>
public sealed class OtaDiagnostics
{
    private readonly IOtaDiagnosticSink _sink;

    /// <summary>
    /// 创建诊断配置。
    /// </summary>
    /// <param name="sink">结构化事件接收器。</param>
    /// <param name="payloadLogging">报文正文记录策略。</param>
    /// <param name="maxPayloadBytes">每条事件最多记录的正文字节数。</param>
    /// <param name="minimumLevel">接收器收到的最低严重程度。</param>
    /// <param name="sessionId">
    /// 可选会话 ID；未指定时自动生成。一次升级应复用同一个实例。
    /// </param>
    public OtaDiagnostics(
        IOtaDiagnosticSink sink,
        OtaPayloadLoggingMode payloadLogging = OtaPayloadLoggingMode.ProtocolOnly,
        int maxPayloadBytes = 128,
        OtaDiagnosticLevel minimumLevel = OtaDiagnosticLevel.Information,
        string? sessionId = null)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        if (maxPayloadBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPayloadBytes));
        }

        PayloadLogging = payloadLogging;
        MaxPayloadBytes = maxPayloadBytes;
        MinimumLevel = minimumLevel;
        SessionId = string.IsNullOrWhiteSpace(sessionId)
            ? Guid.NewGuid().ToString("N")
            : sessionId;
    }

    /// <summary>自动附加到每条事件的升级会话 ID。</summary>
    public string SessionId { get; }

    /// <summary>当前的报文正文记录策略。</summary>
    public OtaPayloadLoggingMode PayloadLogging { get; }

    /// <summary>每条事件最多保存的正文字节数。</summary>
    public int MaxPayloadBytes { get; }

    /// <summary>接收器会收到的最低严重程度。</summary>
    public OtaDiagnosticLevel MinimumLevel { get; }

    /// <summary>
    /// 安全地提交一条诊断事件。接收器异常不会传播到 OTA 状态机。
    /// </summary>
    public void Report(OtaDiagnosticEvent diagnosticEvent)
    {
        ArgumentNullException.ThrowIfNull(diagnosticEvent);
        if (diagnosticEvent.Level < MinimumLevel)
        {
            return;
        }

        try
        {
            _sink.Write(
                diagnosticEvent.SessionId is null
                    ? diagnosticEvent with { SessionId = SessionId }
                    : diagnosticEvent);
        }
        catch
        {
            // 诊断系统不能改变 OTA 协议行为。
        }
    }

    /// <summary>
    /// 根据当前安全策略生成可写入日志的报文快照。
    /// </summary>
    public OtaPayloadCapture CapturePayload(
        ReadOnlySpan<byte> payload,
        OtaPayloadKind kind)
    {
        var allowed = PayloadLogging == OtaPayloadLoggingMode.All
            || PayloadLogging == OtaPayloadLoggingMode.ProtocolOnly
            && kind != OtaPayloadKind.FirmwareData;
        if (!allowed)
        {
            return new OtaPayloadCapture(payload.Length, null, false);
        }

        var count = Math.Min(payload.Length, MaxPayloadBytes);
        return new OtaPayloadCapture(
            payload.Length,
            Convert.ToHexString(payload[..count]),
            count < payload.Length);
    }
}

/// <summary>
/// 应用诊断安全策略后的报文快照。
/// </summary>
/// <param name="Length">原始字节数。</param>
/// <param name="Hex">允许记录的十六进制正文。</param>
/// <param name="IsTruncated">正文是否被截断。</param>
public readonly record struct OtaPayloadCapture(int Length, string? Hex, bool IsTruncated);
