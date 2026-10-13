using ArriOpto.BLE.Ota;

namespace ArriOpto.BLE.Ota.Protocol;

/// <summary>
/// OTA 状态机的公共基础设施，保存单次连接配置并统一报告进度。
/// </summary>
public abstract class OtaSessionBase
{
    /// <summary>保存并校验单次状态机运行需要的连接、配置和进度接收器。</summary>
    protected OtaSessionBase(
        IOtaGattConnection connection,
        OtaOptions options,
        IProgress<OtaProgress>? progress)
    {
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Options.Validate();
        Progress = progress;
    }

    /// <summary>已经完成通知订阅的 GATT 连接。</summary>
    protected IOtaGattConnection Connection { get; }

    /// <summary>本次会话的协议参数。</summary>
    protected OtaOptions Options { get; }

    /// <summary>可选的状态和进度接收器。</summary>
    protected IProgress<OtaProgress>? Progress { get; }

    /// <summary>
    /// 报告状态，并根据 <paramref name="transferred"/> 和
    /// <paramref name="total"/> 计算 0～100 的百分比。
    /// </summary>
    protected void Report(
        OtaState state,
        string message,
        long transferred = 0,
        long total = 0)
    {
        var percentage = total <= 0
            ? 0
            : Math.Clamp((double)transferred * 100 / total, 0, 100);
        Progress?.Report(new OtaProgress(state, percentage, transferred, total, message));
    }

    /// <summary>
    /// 等待一条满足条件的设备 notification；超时抛出 <see cref="TimeoutException"/>，
    /// notification 流结束抛出 <see cref="EndOfStreamException"/>。
    /// </summary>
    private protected static async Task<ReadOnlyMemory<byte>> WaitForNotificationAsync(
        NotificationInbox inbox,
        Func<ReadOnlyMemory<byte>, bool> predicate,
        TimeSpan timeout,
        string description,
        CancellationToken cancellationToken)
    {
        return await inbox.WaitAsync(predicate, timeout, description, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Android 在设备收到协议允许的立即重启/切换命令后上报的 GATT 错误 133。
    /// </summary>
    protected static bool IsGatt133(IOException exception) =>
        exception.Message.Contains("GATT result 133", StringComparison.OrdinalIgnoreCase);

    /// <summary>提交自动附带当前设备、模式和 MTU 的结构化诊断事件。</summary>
    protected void Diagnostic(
        OtaDiagnosticLevel level,
        string eventName,
        string category,
        string message,
        OtaProtocol? protocol = null,
        OtaState? state = null,
        long? offset = null,
        int? partitionIndex = null,
        IReadOnlyDictionary<string, string>? properties = null)
    {
        Options.Diagnostics?.Report(
            new OtaDiagnosticEvent(
                DateTimeOffset.UtcNow,
                level,
                eventName,
                category,
                message)
            {
                DeviceId = Connection.DeviceId,
                Protocol = protocol,
                Mode = Connection.Mode,
                State = state,
                Mtu = Connection.Mtu,
                Offset = offset,
                PartitionIndex = partitionIndex,
                Properties = properties
            });
    }
}
