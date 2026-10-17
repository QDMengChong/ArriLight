using Plugin.BLE.Abstractions.Contracts;

namespace ArriLight;

/// <summary>Application-owned connection slots and recovery requests, shared by AGP and Mesh.</summary>
public sealed class DeviceReconnectQueue : IDisposable
{
    private static readonly TimeSpan StartSpacing = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan SlotSpacing = TimeSpan.FromMilliseconds(2000);
    private readonly object _sync = new();
    private readonly TimeProvider _clock;
    private readonly AppSettingsService? _settings;
    private readonly ITimer _timer;
    private readonly List<Attempt> _pending = [];
    private readonly Dictionary<string, Attempt> _attempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Recovery> _recoveries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _addressStarts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _priority = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _ordinaryRequests = new(StringComparer.OrdinalIgnoreCase);
    private readonly Slot[] _slots = [new(), new()];
    private readonly AsyncLocal<bool> _automatic = new();
    private readonly AsyncLocal<bool> _recovery = new();
    private long? _lastAutomaticStart;
    private bool _disposed;
    private bool _dispatchPending;
    internal bool Enabled { get; private set; }

    public DeviceReconnectQueue(TimeProvider? clock = null, AppSettingsService? settings = null)
    {
        _clock = clock ?? TimeProvider.System;
        _settings = settings;
        _timer = _clock.CreateTimer(_ => Tick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Only offline retries and unexpected-disconnect recovery use this deadline.</summary>
    private TimeSpan _recoveryConnectionTimeout = TimeSpan.FromMilliseconds(1200);
    public TimeSpan RecoveryConnectionTimeout
    {
        get => _recoveryConnectionTimeout;
        set
        {
            if (value.TotalMilliseconds is < 100 or > 30000) throw new ArgumentOutOfRangeException(nameof(value));
            _recoveryConnectionTimeout = value;
        }
    }

    internal bool IsAutomatic => _automatic.Value;
    internal IDisposable AutomaticPreparation(bool recovery = false)
    {
        var previous = _automatic.Value;
        var previousRecovery = _recovery.Value;
        _automatic.Value = true;
        _recovery.Value = recovery;
        return new Scope(() => { _automatic.Value = previous; _recovery.Value = previousRecovery; });
    }

    // Recovery registration owns no control command. The runtime owns and deduplicates
    // the preparation task; successful partial steps and authentication stay there.
    internal void Register(string address, Func<bool> needsPreparation, Action request, CancellationToken lifetime)
    {
        lock (_sync)
        {
            if (_disposed) return;
            Enabled = true;
            _recoveries[address] = new(needsPreparation, request, lifetime, _clock.GetTimestamp());
            Arm();
        }
    }

    internal void Remove(string address)
    {
        lock (_sync) { _recoveries.Remove(address); _addressStarts.Remove(address); _priority.Remove(address); _ordinaryRequests.Remove(address); Arm(); }
    }

    internal void Prioritize(string address, bool ordinaryConnection = false)
    {
        lock (_sync)
        {
            if (_attempts.TryGetValue(address, out var attempt))
            {
                attempt.Priority = true;
                if (ordinaryConnection)
                {
                    // A manual request joins the same native task using its original
                    // caller/session deadline, without inheriting the recovery budget.
                    attempt.Recovery = false;
                    attempt.RecoveryDeadline?.CancelAfter(Timeout.InfiniteTimeSpan);
                }
            }
            else
            {
                _priority.Add(address);
                if (ordinaryConnection) _ordinaryRequests.Add(address);
            }
            Pump();
        }
    }

    internal void ClearOrdinaryRequest(string address)
    { lock (_sync) _ordinaryRequests.Remove(address); }

    internal Task<IDevice> ConnectAsync(string address, Func<CancellationToken, Task<IDevice>> connect,
        CancellationToken token, Func<Task>? prepare = null)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_attempts.TryGetValue(address, out var running)) return running.Completion.Task.WaitAsync(token);
            var attempt = new Attempt(address, connect, token, _automatic.Value,
                _recovery.Value && !_ordinaryRequests.Remove(address), ExecutionContext.Capture(), prepare);
            if (_priority.Remove(address)) attempt.Priority = true;
            _attempts.Add(address, attempt);
            _pending.Add(attempt);
            attempt.Registration = token.Register(() => { lock (_sync) Pump(); });
            Pump();
            return attempt.Completion.Task;
        }
    }

    private void Tick()
    {
        Recovery[] recoveries;
        lock (_sync)
        {
            if (_disposed) return;
            var now = _clock.GetTimestamp();
            foreach (var address in _recoveries.Where(pair => pair.Value.Token.IsCancellationRequested).Select(pair => pair.Key).ToArray())
                _recoveries.Remove(address);
            recoveries = _recoveries.Values.Where(recovery => _clock.GetElapsedTime(recovery.LastCheck, now) >= SlotSpacing).ToArray();
            foreach (var recovery in recoveries) recovery.LastCheck = now;
            Pump();
        }
        // Never call back into a runtime while holding the queue lock.
        foreach (var recovery in recoveries)
        {
            try
            {
                if (!recovery.Token.IsCancellationRequested && recovery.NeedsPreparation()) recovery.Request();
            }
            catch (Exception exception)
            {
                // A concurrent lifecycle/exclusive transition can reject a request.
                System.Diagnostics.Debug.WriteLine($"[BLE/Recovery] {exception.Message}");
            }
        }
    }

    private void Pump()
    {
        if (_disposed) return;
        foreach (var canceled in _pending.Where(attempt => attempt.Token.IsCancellationRequested).ToArray())
        {
            _pending.Remove(canceled); _attempts.Remove(canceled.Address);
            canceled.Registration.Unregister(); canceled.Completion.TrySetCanceled(canceled.Token);
        }
        var now = _clock.GetTimestamp();
        foreach (var slot in _slots.Where(slot => !slot.Active).OrderBy(slot => slot.LastStart))
        {
            if (_dispatchPending) break;
            var next = _pending.OrderByDescending(attempt => attempt.Priority)
                .FirstOrDefault(attempt => !attempt.Automatic || Remaining(attempt, slot, now) <= TimeSpan.Zero);
            if (next is null) continue;
            _pending.Remove(next);
            next.Registration.Unregister();
            slot.Active = true;
            _dispatchPending = true;
            // Isolate workers from cancellation/preparation AsyncLocals of the worker
            // that released the previous slot, restoring only the submitter's context.
            if (ExecutionContext.IsFlowSuppressed()) _ = Task.Run(() => ExecuteAsync(next, slot));
            else using (ExecutionContext.SuppressFlow()) _ = Task.Run(() => ExecuteAsync(next, slot));
        }
        Arm();
    }

    private TimeSpan Remaining(Attempt attempt, Slot slot, long now)
    {
        var delay = TimeSpan.Zero;
        if (_lastAutomaticStart is { } start) delay = Max(delay, StartSpacing - _clock.GetElapsedTime(start, now));
        if (slot.LastStart is { } last) delay = Max(delay, SlotSpacing - _clock.GetElapsedTime(last, now));
        if (_addressStarts.TryGetValue(attempt.Address, out var addressStart)) delay = Max(delay, SlotSpacing - _clock.GetElapsedTime(addressStart, now));
        return delay;
    }

    private async Task ExecuteAsync(Attempt attempt, Slot slot)
    {
        IDevice? device = null;
        Exception? failure = null;
        try
        {
            Task<IDevice>? execution = null;
            if (attempt.Context is { } context)
                ExecutionContext.Run(context, _ => execution = ConnectWithDeadlineAsync(attempt, slot), null);
            else execution = ConnectWithDeadlineAsync(attempt, slot);
            device = await execution!;
        }
        catch (Exception exception) { failure = exception; }
        finally
        {
            lock (_sync)
            {
                // Connect includes native cancellation and late-success cleanup.
                // No WaitAsync timeout may release this slot before that completes.
                slot.Active = false;
                if (!attempt.Started) _dispatchPending = false;
                _attempts.Remove(attempt.Address);
                Pump();
            }
        }
        if (failure is OperationCanceledException canceled) attempt.Completion.TrySetCanceled(canceled.CancellationToken);
        else if (failure is not null) attempt.Completion.TrySetException(failure);
        else attempt.Completion.TrySetResult(device!);
    }

    private async Task<IDevice> ConnectWithDeadlineAsync(Attempt attempt, Slot slot)
    {
        attempt.Token.ThrowIfCancellationRequested();
        if (attempt.Prepare is { } prepare) await prepare();
        attempt.Token.ThrowIfCancellationRequested();
        // Scheduling is shared by every preparation; this budget is exclusive to
        // recovery. Startup, foreground resume and manual work keep their deadlines.
        var duration = !attempt.Recovery || _settings is null ? RecoveryConnectionTimeout : await _settings.LoadReconnectTimeoutAsync(attempt.Token);
        CancellationTokenSource? timeout = null;
        lock (_sync)
        {
            var now = _clock.GetTimestamp();
            slot.LastStart = now;
            if (attempt.Automatic) _lastAutomaticStart = now;
            _addressStarts[attempt.Address] = now;
            attempt.Started = true;
            if (attempt.Recovery) attempt.RecoveryDeadline = timeout = new CancellationTokenSource(duration, _clock);
            _dispatchPending = false;
            Pump();
        }
        using (timeout)
        using (var linked = timeout is null ? null : CancellationTokenSource.CreateLinkedTokenSource(attempt.Token, timeout.Token))
        {
            try { return await attempt.Connect(linked?.Token ?? attempt.Token); }
            finally { lock (_sync) attempt.RecoveryDeadline = null; }
        }
    }

    private void Arm()
    {
        if (_disposed) return;
        var now = _clock.GetTimestamp();
        var delay = Timeout.InfiniteTimeSpan;
        foreach (var attempt in _pending.Where(_ => !_dispatchPending))
            foreach (var slot in _slots.Where(slot => !slot.Active))
                delay = Min(delay, attempt.Automatic ? Remaining(attempt, slot, now) : TimeSpan.Zero);
        foreach (var recovery in _recoveries.Values)
            delay = Min(delay, SlotSpacing - _clock.GetElapsedTime(recovery.LastCheck, now));
        _timer.Change(delay == Timeout.InfiniteTimeSpan ? delay : Max(TimeSpan.FromMilliseconds(1), delay), Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true; _recoveries.Clear(); _timer.Dispose();
            foreach (var attempt in _pending)
            { attempt.Registration.Unregister(); attempt.Completion.TrySetCanceled(new CancellationToken(true)); _attempts.Remove(attempt.Address); }
            _pending.Clear();
        }
    }

    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left > right ? left : right;
    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left == Timeout.InfiniteTimeSpan || right < left ? right : left;
    private sealed class Slot { internal bool Active; internal long? LastStart; }
    private sealed class Recovery(Func<bool> needsPreparation, Action request, CancellationToken token, long lastCheck)
    {
        internal Func<bool> NeedsPreparation { get; } = needsPreparation;
        internal Action Request { get; } = request;
        internal CancellationToken Token { get; } = token;
        internal long LastCheck = lastCheck;
    }
    private sealed class Attempt(string address, Func<CancellationToken, Task<IDevice>> connect,
        CancellationToken token, bool automatic, bool recovery, ExecutionContext? context, Func<Task>? prepare)
    {
        internal string Address { get; } = address;
        internal Func<CancellationToken, Task<IDevice>> Connect { get; } = connect;
        internal CancellationToken Token { get; } = token;
        internal bool Automatic { get; } = automatic;
        internal bool Recovery = recovery;
        internal CancellationTokenSource? RecoveryDeadline;
        internal bool Priority = !automatic;
        internal bool Started;
        internal ExecutionContext? Context { get; } = context;
        internal Func<Task>? Prepare { get; } = prepare;
        internal CancellationTokenRegistration Registration;
        internal TaskCompletionSource<IDevice> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class Scope(Action dispose) : IDisposable { public void Dispose() => dispose(); }
}
