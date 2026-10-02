#if DEBUG
using System.Diagnostics;
using System.Globalization;

namespace ArriLight;

// Collect first, print after the measured navigation preparation. Logging
// itself is measured separately and is included in the enclosing interval.
internal sealed class DebugOperationTiming : IDisposable
{
    private static readonly AsyncLocal<DebugOperationTiming?> Active = new();
    internal static DebugOperationTiming? Current => Active.Value;
    private readonly DebugOperationTiming? _previous = Current;
    private readonly List<(string Stage, double Elapsed, double Logging)> _steps = [];
    private double _logging;
    private int _logCount;
    private readonly object _sync = new();
    private bool _collecting = true;
    internal DebugOperationTiming() => Active.Value = this;

    internal void Run(string stage, Action action)
    {
        var started = Stopwatch.GetTimestamp();
        double logging;
        lock (_sync) logging = _logging;
        try { action(); }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            lock (_sync) _steps.Add((stage, elapsed, _logging - logging));
        }
    }

    internal void RecordLog(double milliseconds)
    {
        lock (_sync)
        {
            if (!_collecting) return;
            _logging += milliseconds;
            _logCount++;
        }
    }

    internal void Emit(AppDiagnosticService diagnostics, string address)
    {
        Active.Value = _previous;
        lock (_sync) _collecting = false;
        var outputStarted = Stopwatch.GetTimestamp();
        foreach (var step in _steps)
            diagnostics.Information("UI/Timing", string.Create(CultureInfo.InvariantCulture,
                $"stage=Home.BeforeNavigation.{step.Stage} elapsed={step.Elapsed:0.0}ms loggingIncluded={step.Logging:0.0}ms device={address}"));
        var outputElapsed = Stopwatch.GetElapsedTime(outputStarted).TotalMilliseconds;
        diagnostics.Information("UI/Timing", string.Create(CultureInfo.InvariantCulture,
            $"stage=Home.BeforeNavigation.LogOutput count={_logCount} existingLogs={_logging:0.0}ms timingReportOutput={outputElapsed:0.0}ms device={address}; 嵌套区间不能相加，报告输出不计入上面的操作区间"));
    }

    public void Dispose() => Active.Value = _previous;
}
#endif
