using ArriOpto.BLE.Agp;
using CommunityToolkit.Mvvm.Input;

namespace ArriLight.ViewModels;

public partial class DeviceControlPageViewModel
{
    [RelayCommand]
    private async Task TimerSendAsync()
    {
        if (!IsDeviceOnline || !SupportedCapabilities.HasFlag(AgpCapabilities.RelativeTimer))
            return;
        if (!int.TryParse(TimerSecondsText, out var seconds) || seconds is < 1 or > 86400)
        {
            _interaction.ShowToast(AppResources.Timer_InvalidSeconds);
            return;
        }

        var timerMode = TimerModeIndex == 1 ? "TimedOn" : "TimedOff";
        var sent = false;
        try
        {
            sent = await SendTimerCommandAsync(timerMode, seconds);
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            _diagnostics.Error(
                "Light/Timer",
                "发送计时指令失败。",
                exception);
        }

        _interaction.ShowToast(sent ? AppResources.Timer_SendSucceeded : AppResources.Timer_SendFailed);
    }

    private async Task<bool> SendTimerCommandAsync(string timerMode, int seconds)
    {
        if (IsDemo || string.IsNullOrWhiteSpace(DeviceAddress))
            return false;

        // 延时开/关均使用 Relative Timer，分别发送 DELAY_ON_SET / DELAY_OFF_SET。
        await _agpLight.SetTimerAsync(
            DeviceAddress,
            TimeSpan.FromSeconds(seconds),
            turnOn: timerMode == "TimedOn");
        return true;
    }

    [RelayCommand]
    private async Task MasterPowerAsync()
    {
        using var timing = _diagnostics.BeginBleTiming(
            "UI.Control.Power", DeviceAddress,
            $"desiredOn={!IsMasterPowerOn}; initializing={IsInitializingConnection}; isOnline={IsDeviceOnline}; isDemo={IsDemo}");
        if (!IsDeviceOnline)
        {
            timing.SetResult("ignored-not-online");
            return;
        }
        var desiredOn = !IsMasterPowerOn;
        var delaySeconds = 0;
        if (desiredOn && !TryGetTurnOnDelaySeconds(out delaySeconds))
            return;
        var commandVersion = ++_powerCommandVersion;
        var previousPower = IsMasterPowerOn;
        var previousMode = EffectMode;
        IsMasterPowerOn = desiredOn;
        UpdateMasterPowerVisual();

        if (!_isReady)
        {
            timing.SetResult("ignored-ui-not-ready");
            return;
        }

        if (IsDemo || string.IsNullOrWhiteSpace(DeviceAddress))
        {
            timing.SetResult("skipped");
            return;
        }

        try
        {
            timing.Mark($"提交开关操作; desiredOn={IsMasterPowerOn}; initializing={IsInitializingConnection}");
            await _agpLight.SetControlPageOnOffAsync(DeviceAddress, desiredOn, delaySeconds);
            if (commandVersion == _powerCommandVersion &&
                _agpLight.GetCachedDeviceStatus(DeviceAddress)?.LightState is { } state)
            {
                EffectMode = state.Mode;
                UpdateEffectVisual();
            }
            timing.SetResult("success");
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            if (commandVersion == _powerCommandVersion)
            {
                RestoreConfirmedControlState(previousPower, previousMode);
            }
            timing.SetResult($"failed:{exception.GetType().Name}");
            _diagnostics.Error(
                "Light/Power",
                $"发送总开关指令失败：{DeviceName} = {IsMasterPowerOn}。",
                exception);
            _interaction.ShowToast(AppResources.Control_SendFailed);
        }
    }

    private void LoadTurnOnDelaySettings()
    {
        IsTurnOnDelayEnabled = _repository.GetTurnOnDelayEnabled(DeviceAddress);
        _pendingTurnOnDelaySeconds = _repository.GetTurnOnDelaySeconds(DeviceAddress);
        OnPropertyChanged(nameof(IsTurnOnDelayEnabled));
        TurnOnDelaySecondsText = _pendingTurnOnDelaySeconds.ToString();
    }

    private bool TryGetTurnOnDelaySeconds(out int seconds)
    {
        seconds = 0;
        if (!IsTurnOnDelayEnabled ||
            !SupportedCapabilities.HasFlag(AgpCapabilities.RelativeTimer))
            return true;

        var text = TurnOnDelaySecondsText;
        if (int.TryParse(text, out seconds) && seconds is >= 1 and <= 86400)
            return true;

        _interaction.ShowToast(AppResources.Timer_InvalidSeconds);
        return false;
    }

    private void RestoreConfirmedControlState(bool previousPower, AgpLightMode previousMode)
    {
        var status = _agpLight.GetCachedDeviceStatus(DeviceAddress);
        IsMasterPowerOn = status?.LightState?.IsOn ?? previousPower;
        EffectMode = status?.Profile?.EffectMode ?? status?.LightState?.Mode ?? previousMode;
        if (status?.Profile?.EffectPeriodMilliseconds is { } period)
            RememberEffectPeriod(EffectMode, period);
        UpdateMasterPowerVisual();
        UpdateEffectVisual();
    }
}
