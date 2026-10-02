using System.Diagnostics;
using System.Globalization;

namespace ArriLight;

/// <summary>Monotonic BLE timing with an operation ID shared by nested async stages.</summary>
public sealed class BleTimingScope : IDisposable
{
#if DEBUG
    private static readonly AsyncLocal<BleTimingScope?> Active = new();
    private static long _nextOperationId;
    private static long _nextSpanId;
    private readonly AppDiagnosticService _diagnostics;
    private readonly BleTimingScope? _parent;
    private readonly long _started;
    private readonly long _operationStarted;
    private readonly string _stage;
    private readonly string? _address;
    private string? _result;
    private int _disposed;

    internal static BleTimingScope? Current => Active.Value;

    internal BleTimingScope(
        AppDiagnosticService diagnostics, string stage, string? address, string? detail)
    {
        _diagnostics = diagnostics;
        _stage = stage;
        _parent = Active.Value;
        while (_parent is not null && Volatile.Read(ref _parent._disposed) != 0)
            _parent = _parent._parent;
        _address = address ?? _parent?._address;
        OperationId = _parent?.OperationId ?? Interlocked.Increment(ref _nextOperationId);
        SpanId = Interlocked.Increment(ref _nextSpanId);
        _started = Stopwatch.GetTimestamp();
        _operationStarted = _parent?._operationStarted ?? _started;
        Active.Value = this;
        Write("start", detail);
    }

    public long OperationId { get; }
    public long SpanId { get; }
#else
    internal static BleTimingScope Disabled { get; } = new();
    internal static BleTimingScope? Current => null;
    private BleTimingScope() { }
    public long OperationId => 0;
    public long SpanId => 0;
#endif

    [Conditional("DEBUG")]
    public void Mark(string detail)
    {
#if DEBUG
        Write("mark", detail);
#endif
    }

    [Conditional("DEBUG")]
    public void SetResult(string result)
    {
#if DEBUG
        _result = result;
#endif
    }

    public void Dispose()
    {
#if DEBUG
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (ReferenceEquals(Active.Value, this))
            Active.Value = _parent;
        Write("end", _result is null ? null : $"result={_result}");
#endif
    }

#if DEBUG
    private void Write(string eventName, string? detail)
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(_started, now).TotalMilliseconds;
        var total = Stopwatch.GetElapsedTime(_operationStarted, now).TotalMilliseconds;
        _diagnostics.Information(
            "BLE/Timing",
            string.Create(CultureInfo.InvariantCulture,
                $"id={OperationId:D6} span={SpanId:D6} parent={_parent?.SpanId.ToString("D6", CultureInfo.InvariantCulture) ?? "-"} stage={_stage} event={eventName} elapsed={elapsed:0.0}ms total={total:0.0}ms") +
            (string.IsNullOrWhiteSpace(_address) ? string.Empty : $" device={_address}") +
            (string.IsNullOrWhiteSpace(detail) ? string.Empty : $" {detail}"));
    }
#endif
}
