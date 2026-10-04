namespace ArriLight;

// Each activity owns its confirmation state. Use elapsed time so clock changes
// cannot accidentally turn an old back press into an exit confirmation.
internal sealed class BackExitGate(TimeProvider? timeProvider = null)
{
    internal static readonly TimeSpan ConfirmationWindow = TimeSpan.FromSeconds(2);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private long? _firstBackTimestamp;

    internal bool ShouldExit()
    {
        var now = _timeProvider.GetTimestamp();
        if (_firstBackTimestamp is { } first &&
            _timeProvider.GetElapsedTime(first, now) < ConfirmationWindow)
        {
            Reset();
            return true;
        }

        _firstBackTimestamp = now;
        return false;
    }

    internal void Reset() => _firstBackTimestamp = null;
}
