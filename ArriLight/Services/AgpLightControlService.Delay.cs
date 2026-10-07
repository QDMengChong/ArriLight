using ArriOpto.BLE.Agp;

namespace ArriLight;

public sealed partial class AgpLightControlService
{
    private readonly TimeProvider _controlDelayTimeProvider;
    private readonly object _controlDelaySync = new();
    private readonly Dictionary<string, PendingControlDelay> _controlDelays =
        new(StringComparer.OrdinalIgnoreCase);

    public bool HasPendingControlTurnOn(string address)
    {
        lock (_controlDelaySync)
            return _controlDelays.TryGetValue(address, out var pending) &&
                !pending.Token.IsCancellationRequested &&
                _controlDelayTimeProvider.GetUtcNow() < pending.DueAt;
    }

    // The device owns ordinary delayed power-on, so it survives leaving the page
    // or disconnecting BLE. Only control-page calls opt into the saved delay.
    public async Task SetControlPageOnOffAsync(
        string address, bool isOn, int delaySeconds,
        CancellationToken cancellationToken = default)
    {
        ValidateControlDelay(delaySeconds);
        if (!isOn || delaySeconds == 0)
        {
            await SetOnOffAsync(address, isOn, cancellationToken);
            return;
        }

        var (pending, cancelTimer) = BeginControlDelay(address, delaySeconds, nativeTimer: true, cancellationToken);
        var token = pending.Token;
        try
        {
            await WithClientAsync(address, async handle =>
            {
                RequireCapability(handle.Capabilities, AgpCapabilities.RelativeTimer);
                if (cancelTimer)
                    await CancelDeviceTurnOnTimerAsync(handle, token);
                var response = await handle.Client.SetDelayOnAsync((uint)delaySeconds, cancellationToken: token);
                EnsureSuccess(response.Result);
            }, token);
            pending.DueAt = _controlDelayTimeProvider.GetUtcNow().AddSeconds(delaySeconds);
        }
        catch
        {
            FinishControlDelay(address, pending);
            throw;
        }
    }

    // EFFECT_SET immediately starts output and has no delay field. Wait outside
    // the BLE operation lock, then send the original effect command. The service
    // owns this wait so navigating away does not discard it; backgrounding does.
    public async Task<AgpEffectResponse> SetControlPageEffectAsync(
        string address, AgpLightMode mode, ushort periodMilliseconds, int delaySeconds,
        CancellationToken cancellationToken = default,
        Func<CancellationToken, Task>? beforeEffect = null)
    {
        ValidateControlDelay(delaySeconds);
        _ = AgpModelCodec.EncodeEffect(mode, periodMilliseconds);
        if (delaySeconds == 0)
        {
            if (beforeEffect is not null)
                await beforeEffect(cancellationToken);
            return await SetEffectAsync(address, mode, periodMilliseconds, cancellationToken);
        }

        var (pending, cancelTimer) = BeginControlDelay(address, delaySeconds, nativeTimer: false, cancellationToken);
        var token = pending.Token;
        try
        {
            await WithClientAsync(address, async handle =>
            {
                RequireCapability(handle.Capabilities, AgpCapabilities.Effects);
                RequireCapability(handle.Capabilities, AgpCapabilities.RelativeTimer);
                if (cancelTimer)
                    await CancelDeviceTurnOnTimerAsync(handle, token);
            }, token);
            pending.DueAt = _controlDelayTimeProvider.GetUtcNow().AddSeconds(delaySeconds);
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), _controlDelayTimeProvider, token);
            if (beforeEffect is not null)
                await beforeEffect(token);
            return await SetEffectCoreAsync(address, mode, periodMilliseconds, cancelTimer: false, token);
        }
        finally
        {
            FinishControlDelay(address, pending);
        }
    }

    private static void ValidateControlDelay(int seconds)
    {
        if (seconds is < 0 or > 86400)
            throw new ArgumentOutOfRangeException(nameof(seconds));
    }

    private (PendingControlDelay Pending, bool CancelTimer) BeginControlDelay(
        string address, int seconds, bool nativeTimer, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        // Native timers must survive application suspension. Effect waits must
        // stop when the app releases BLE, rather than unexpectedly lighting later.
        var cancellation = nativeTimer
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : LinkApplicationCancellation(cancellationToken);
        lock (_controlDelaySync)
        {
            var cancelTimer = CancelPendingControlDelay(address);
            var pending = new PendingControlDelay(cancellation, nativeTimer,
                _controlDelayTimeProvider.GetUtcNow().AddSeconds(seconds));
            _controlDelays[address] = pending;
            return (pending, cancelTimer);
        }
    }

    private bool CancelPendingControlDelay(string address)
    {
        lock (_controlDelaySync)
        {
            if (!_controlDelays.Remove(address, out var pending))
                return false;
            pending.Cancellation.Cancel();
            if (pending.NativeTimer)
                pending.Cancellation.Dispose();
            return pending.NativeTimer;
        }
    }

    private void FinishControlDelay(string address, PendingControlDelay pending)
    {
        lock (_controlDelaySync)
        {
            if (_controlDelays.GetValueOrDefault(address) == pending)
                _controlDelays.Remove(address);
            pending.Cancellation.Dispose();
        }
    }

    private void CancelAllPendingControlDelays()
    {
        lock (_controlDelaySync)
            foreach (var address in _controlDelays.Keys.ToArray())
                CancelPendingControlDelay(address);
    }

    private static async Task CancelDeviceTurnOnTimerAsync(
        AgpClientHandle handle, CancellationToken cancellationToken)
    {
        var response = await handle.Client.SetDelayOnAsync(0, cancellationToken: cancellationToken);
        EnsureSuccess(response.Result);
    }

    private sealed class PendingControlDelay(
        CancellationTokenSource cancellation, bool nativeTimer, DateTimeOffset dueAt)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public CancellationToken Token { get; } = cancellation.Token;
        public bool NativeTimer { get; } = nativeTimer;
        public DateTimeOffset DueAt { get; set; } = dueAt;
    }
}
