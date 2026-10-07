using ArriOpto.BLE.Agp;

namespace ArriLight;

public partial class DeviceControlPage
{
    // Keep the existing timer entry points unavailable until the project needs them.
    private static readonly bool ShowTimedControls = false;
    private bool _isTurnOnDelayEnabled;
    private int _pendingTurnOnDelaySeconds = 60;
    private int _powerCommandVersion;

    private bool HasTurnOnDelayView => TurnOnDelayControlLazyView.HasLazyViewLoaded;
    private Entry TurnOnDelaySecondsEntry =>
        ((LazyTurnOnDelayControlView)TurnOnDelayControlLazyView.Content!).SecondsEntry;

    public bool IsTurnOnDelayEnabled
    {
        get => _isTurnOnDelayEnabled;
        set
        {
            if (_isTurnOnDelayEnabled == value)
                return;

            _isTurnOnDelayEnabled = value;
            OnPropertyChanged();
            if (_isReady && !string.IsNullOrWhiteSpace(DeviceAddress))
                _repository.SetTurnOnDelayEnabled(DeviceAddress, value);
        }
    }

    private void LoadTurnOnDelaySettings()
    {
        _isTurnOnDelayEnabled = _repository.GetTurnOnDelayEnabled(DeviceAddress);
        _pendingTurnOnDelaySeconds = _repository.GetTurnOnDelaySeconds(DeviceAddress);
        OnPropertyChanged(nameof(IsTurnOnDelayEnabled));
        if (HasTurnOnDelayView)
            TurnOnDelaySecondsEntry.Text = _pendingTurnOnDelaySeconds.ToString();
    }

    private void OnTurnOnDelaySecondsFocused(object? sender, FocusEventArgs e) =>
        Dispatcher.Dispatch(() =>
        {
            TurnOnDelaySecondsEntry.CursorPosition = 0;
            TurnOnDelaySecondsEntry.SelectionLength = TurnOnDelaySecondsEntry.Text?.Length ?? 0;
        });

    private void OnTurnOnDelaySecondsTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_isReady || string.IsNullOrWhiteSpace(DeviceAddress) ||
            !int.TryParse(e.NewTextValue, out var seconds) || seconds is < 1 or > 86400)
            return;

        _pendingTurnOnDelaySeconds = seconds;
        if (_repository.GetTurnOnDelaySeconds(DeviceAddress) != seconds)
            _repository.SetTurnOnDelaySeconds(DeviceAddress, seconds);
    }

    private bool TryGetTurnOnDelaySeconds(out int seconds)
    {
        seconds = 0;
        if (!IsTurnOnDelayEnabled ||
            !_supportedCapabilities.HasFlag(AgpCapabilities.RelativeTimer))
            return true;

        var text = HasTurnOnDelayView ? TurnOnDelaySecondsEntry.Text : _pendingTurnOnDelaySeconds.ToString();
        if (int.TryParse(text, out seconds) && seconds is >= 1 and <= 86400)
            return true;

        ToastService.Show(AppResources.Timer_InvalidSeconds);
        return false;
    }

    private void RestoreConfirmedControlState(bool previousPower, AgpLightMode previousMode)
    {
        var status = _agpLight.GetCachedDeviceStatus(DeviceAddress);
        _isMasterPowerOn = status?.LightState?.IsOn ?? previousPower;
        _effectMode = status?.Profile?.EffectMode ?? status?.LightState?.Mode ?? previousMode;
        if (status?.Profile?.EffectPeriodMilliseconds is { } period)
            RememberEffectPeriod(_effectMode, period);
        UpdateMasterPowerVisual();
        UpdateEffectVisual();
    }

    private void OnControlDeviceStatusChanged(object? sender, DeviceStatusChange change)
    {
        if (!string.Equals(change.Address, DeviceAddress, StringComparison.OrdinalIgnoreCase) ||
            !change.Status.IsOnline || change.Status.LightState is not { } state)
            return;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            ApplyLightState(state);
            if (change.Status.Profile is { } profile)
                ApplyEffectProfile(profile);
        });
    }
}
