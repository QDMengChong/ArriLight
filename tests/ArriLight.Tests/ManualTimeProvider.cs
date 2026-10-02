namespace ArriLight.Tests;

// Advance lease time without making the suite wait ten real seconds per device.
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _sync = new();
    private readonly List<ManualTimer> _timers = [];
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() { lock (_sync) return _ticks; }
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());

    public override ITimer CreateTimer(TimerCallback callback, object? state,
        TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_sync)
        {
            _timers.Add(timer);
            timer.Change(dueTime, period);
        }
        return timer;
    }

    public void Advance(TimeSpan amount)
    {
        List<ManualTimer> due;
        lock (_sync)
        {
            _ticks += amount.Ticks;
            due = _timers.Where(timer => timer.DueAt <= _ticks).ToList();
            foreach (var timer in due)
                timer.DueAt = timer.Period > TimeSpan.Zero
                    ? _ticks + timer.Period.Ticks : long.MaxValue;
        }
        foreach (var timer in due)
            timer.Fire();
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private bool _disposed;
        public long DueAt { get; set; } = long.MaxValue;
        public TimeSpan Period { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._sync)
            {
                if (_disposed) return false;
                Period = period;
                DueAt = dueTime == Timeout.InfiniteTimeSpan
                    ? long.MaxValue : owner._ticks + dueTime.Ticks;
                return true;
            }
        }

        public void Fire() { if (!_disposed) callback(state); }
        public void Dispose()
        {
            lock (owner._sync)
            {
                _disposed = true;
                owner._timers.Remove(this);
            }
        }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
