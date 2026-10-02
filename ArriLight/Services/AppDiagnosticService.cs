using System.Threading.Channels;
using ArriOpto.BLE.Ota;

namespace ArriLight;

public enum AppDiagnosticLevel
{
    Trace,
    Debug,
    Information,
    Warning,
    Error
}

public sealed record AppDiagnosticEntry(
    DateTimeOffset Timestamp,
    AppDiagnosticLevel Level,
    string Source,
    string Message)
{
    public string DisplayText =>
        $"{Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff} [{Level}] [{Source}] {Message}";
}

public sealed class AppDiagnosticService
{
    private const string EnabledPreferenceKey =
        "arrilight_diagnostics_enabled_v2";

    private readonly Channel<AppDiagnosticEntry> _fileQueue =
        Channel.CreateUnbounded<AppDiagnosticEntry>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });

    private bool _isEnabled = Preferences.Default.Get(
        EnabledPreferenceKey,
        false);

    public AppDiagnosticService()
    {
        _ = Task.Run(WriteFileQueueAsync);
#if DEBUG
        Information(
            "BLE/Test",
            $"BLE 实体测试环境：app={AppInfo.Current.VersionString}，build={AppInfo.Current.BuildString}，platform={DeviceInfo.Current.Platform}，os={DeviceInfo.Current.VersionString}，manufacturer={DeviceInfo.Current.Manufacturer}，model={DeviceInfo.Current.Model}。计时使用单调时钟；所有 BLE/Timing 日志自动显示在调试输出中。");
#endif
        if (IsEnabled)
        {
            Write(
                AppDiagnosticLevel.Information,
                "Diagnostics",
                $"ArriLight 调试会话开始，app={AppInfo.Current.VersionString}，build={AppInfo.Current.BuildString}，platform={DeviceInfo.Current.Platform}。",
                force: true);
        }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value)
                return;

            _isEnabled = value;
            Preferences.Default.Set(EnabledPreferenceKey, value);
            Write(
                AppDiagnosticLevel.Information,
                "Diagnostics",
                value ? "调试信息输出已开启。" : "调试信息输出已关闭。",
                force: true);
        }
    }

    public string LogFilePath =>
        Path.Combine(FileSystem.AppDataDirectory, "arrilight_debug.txt");

    public BleTimingScope BeginBleTiming(
        string stage, string? address = null, string? detail = null)
    {
#if DEBUG
        return new(this, stage, address, detail);
#else
        return BleTimingScope.Disabled;
#endif
    }

    public void Trace(string source, string message) =>
        Write(AppDiagnosticLevel.Trace, source, message);

    public void Debug(string source, string message) =>
        Write(AppDiagnosticLevel.Debug, source, message);

    public void Information(string source, string message) =>
        Write(AppDiagnosticLevel.Information, source, message);

    public void Warning(string source, string message) =>
        Write(AppDiagnosticLevel.Warning, source, message);

    public void Error(string source, string message, Exception? exception = null)
    {
        var detail = exception is null
            ? message
            : $"{message} {exception.GetType().Name}: {exception.Message}";
        Write(AppDiagnosticLevel.Error, source, detail);
    }

    public OtaDiagnostics CreateOtaDiagnostics()
    {
        var sink = new DelegateOtaDiagnosticSink(WriteOtaEvent);
        return new OtaDiagnostics(
            sink,
            payloadLogging: OtaPayloadLoggingMode.ProtocolOnly,
            maxPayloadBytes: 128,
            minimumLevel: OtaDiagnosticLevel.Trace);
    }

    private void WriteOtaEvent(OtaDiagnosticEvent value)
    {
        var details = new List<string>
        {
            $"{value.EventName}: {value.Message}"
        };

        if (value.State is not null)
            details.Add($"state={value.State}");
        if (value.Mode is not null)
            details.Add($"mode={value.Mode}");
        if (value.Mtu is not null)
            details.Add($"mtu={value.Mtu}");
        if (value.Offset is not null)
            details.Add($"offset={value.Offset}");
        if (value.PayloadLength is not null)
            details.Add($"length={value.PayloadLength}");
        if (!string.IsNullOrWhiteSpace(value.PayloadHex))
            details.Add($"payload={value.PayloadHex}");
        if (value.Elapsed is not null)
            details.Add($"elapsed={value.Elapsed.Value.TotalMilliseconds:0}ms");
        if (!string.IsNullOrWhiteSpace(value.ExceptionMessage))
            details.Add($"error={value.ExceptionMessage}");

        Write(
            value.Level switch
            {
                OtaDiagnosticLevel.Trace => AppDiagnosticLevel.Trace,
                OtaDiagnosticLevel.Debug => AppDiagnosticLevel.Debug,
                OtaDiagnosticLevel.Information => AppDiagnosticLevel.Information,
                OtaDiagnosticLevel.Warning => AppDiagnosticLevel.Warning,
                OtaDiagnosticLevel.Error => AppDiagnosticLevel.Error,
                _ => AppDiagnosticLevel.Debug
            },
            $"OTA/{value.Category}",
            string.Join(" | ", details));
    }

    private void Write(
        AppDiagnosticLevel level,
        string source,
        string message,
        bool force = false)
    {
        var entry = new AppDiagnosticEntry(
            DateTimeOffset.Now,
            level,
            source,
            message);

        // Debug.WriteLine 带有 DEBUG 条件特性：Debug 构建始终进入
        // Visual Studio“输出 → 调试”，Release 构建会在编译时移除。
        System.Diagnostics.Debug.WriteLine(entry.DisplayText);

        // 隐藏开关只控制文件写入，不影响 Debug 构建的 VS 输出。
        if (!force && !IsEnabled)
            return;

        _fileQueue.Writer.TryWrite(entry);
    }

    private async Task WriteFileQueueAsync()
    {
        await foreach (var entry in _fileQueue.Reader.ReadAllAsync())
        {
            try
            {
                Directory.CreateDirectory(FileSystem.AppDataDirectory);
                await File.AppendAllTextAsync(
                    LogFilePath,
                    entry.DisplayText + Environment.NewLine);
            }
            catch (Exception exception)
            {
                // 文件日志失败不能影响蓝牙回调或 OTA 状态机。
                System.Diagnostics.Debug.WriteLine(
                    $"ArriLight file logger failed: {exception}");
            }
        }
    }
}
