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
            $"operation=Toggle; displayedIsOn={IsMasterPowerOn}; initializing={IsInitializingConnection}; isOnline={IsDeviceOnline}; isDemo={IsDemo}");
        if (!_isReady || !CanTogglePower)
        {
            timing.SetResult("ignored-busy-or-not-ready");
            return;
        }
        var desiredOn = !IsMasterPowerOn;
        var delaySeconds = 0;
        if (!TryGetTurnOnDelaySeconds(out delaySeconds))
            return;
        var commandVersion = ++_powerCommandVersion;
        var previousPower = IsMasterPowerOn;
        var previousMode = EffectMode;
        var previousOperationId = _agpLight.GetRuntimeSnapshot(DeviceAddress)?.LastOperation?.Id;
        if (IsDemo || string.IsNullOrWhiteSpace(DeviceAddress))
        {
            IsMasterPowerOn = desiredOn;
            UpdateMasterPowerVisual();
            if (IsDemo) _repository.RecordOperation(DeviceAddress);
            timing.SetResult("skipped");
            return;
        }

        // Mesh 没有 Toggle 语义，且没有延时开关/Delay 能力：直接走绝对 Set。
        if (IsMeshDevice)
        {
            await SetMeshMasterPowerAsync(desiredOn, previousPower, commandVersion, timing);
            return;
        }

        try
        {
            IsPowerChanging = true;
            timing.Mark($"提交 Toggle 开关操作; initializing={IsInitializingConnection}");
            await _agpLight.ToggleControlPagePowerAsync(DeviceAddress, delaySeconds);
            NotifyUnknownOutcomeIfNeeded(previousOperationId);
            if (commandVersion == _powerCommandVersion &&
                _agpLight.GetCachedDeviceStatus(DeviceAddress)?.LightState is { } state)
            {
                IsMasterPowerOn = state.IsOn;
                UpdateMasterPowerVisual();
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
            if (!NotifyUnknownOutcomeIfNeeded(previousOperationId) && _isControlPageVisible && !_agpLight.IsApplicationSuspended)
                _interaction.ShowToast(AppResources.Control_SendFailed);
        }
        finally { IsPowerChanging = false; }
    }

    /// <summary>
    /// Mesh 设备的总开关：Mesh 只有绝对 Set，且不支持 AGP 的延时开关。
    /// 失败时回滚到按下之前的值。
    /// </summary>
    private async Task SetMeshMasterPowerAsync(
        bool desiredOn,
        bool previousPower,
        int commandVersion,
        BleTimingScope timing)
    {
        try
        {
            IsPowerChanging = true;
            var result = await _meshControl.SetOnOffAsync(DeviceAddress, desiredOn);
            if (commandVersion == _powerCommandVersion)
            {
                IsMasterPowerOn = result;
                UpdateMasterPowerVisual();
            }
            _repository.RecordOperation(DeviceAddress);
            timing.SetResult("success");
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            if (commandVersion == _powerCommandVersion)
            {
                IsMasterPowerOn = previousPower;
                UpdateMasterPowerVisual();
            }
            timing.SetResult($"failed:{exception.GetType().Name}");
            _diagnostics.Error(
                "Light/Power",
                $"Mesh 总开关指令失败：{DeviceAddress}。",
                exception);
            if (_isControlPageVisible && !_agpLight.IsApplicationSuspended)
                _interaction.ShowToast(AppResources.Control_SendFailed);
        }
        finally { IsPowerChanging = false; }
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
