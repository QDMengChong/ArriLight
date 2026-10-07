using ArriOpto.BLE.Agp;
using CommunityToolkit.Mvvm.Input;

namespace ArriLight.ViewModels;

public partial class DeviceControlPageViewModel
{
    [RelayCommand]
    private Task SelectEffectAsync(AgpLightMode mode) => SetEffectAsync(mode, GetEffectPeriod(mode));

    public virtual async Task SetEffectAsync(
        AgpLightMode mode, ushort periodMilliseconds)
    {
        using var timing = _diagnostics.BeginBleTiming(
            "UI.Control.Effect", DeviceAddress,
            $"mode={mode}; periodMs={periodMilliseconds}; initializing={IsInitializingConnection}; isDemo={IsDemo}");
        if (IsDemo || string.IsNullOrWhiteSpace(DeviceAddress))
        {
            timing.SetResult("skipped");
            return;
        }

        if (!TryGetTurnOnDelaySeconds(out var delaySeconds))
            return;
        var commandVersion = ++_powerCommandVersion;
        var previousPower = IsMasterPowerOn;
        var previousMode = EffectMode;
        try
        {
            var period = mode == AgpLightMode.Steady ? (ushort)0 : periodMilliseconds;
            var response = await _agpLight.SetControlPageEffectAsync(
                DeviceAddress, mode, period, delaySeconds);
            if (commandVersion != _powerCommandVersion)
                return;
            EffectMode = response.Mode;
            // The batch explicitly confirms ONOFF_SET for all effect modes.
            IsMasterPowerOn = true;
            UpdateMasterPowerVisual();
            RememberEffectPeriod(response.Mode, response.PeriodMilliseconds);
            UpdateEffectVisual();
            timing.SetResult("success");
        }
        catch (OperationCanceledException)
        {
            if (commandVersion == _powerCommandVersion)
            {
                RestoreConfirmedControlState(previousPower, previousMode);
            }
            timing.SetResult("cancelled");
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            if (commandVersion != _powerCommandVersion)
                return;
            RestoreConfirmedControlState(previousPower, previousMode);
            timing.SetResult($"failed:{exception.GetType().Name}");
            _diagnostics.Error("Light/Effect", $"发送特效指令失败：{DeviceName}。", exception);
            _interaction.ShowToast(AppResources.Effect_CommandFailed);
        }
    }

    private void ApplyEffectProfile(AgpDeviceControlProfile profile)
    {
        EffectsSupported = profile.Capabilities.HasFlag(AgpCapabilities.Effects);
        EffectMode = profile.EffectMode;
        if (profile.EffectPeriodMilliseconds is { } period)
            RememberEffectPeriod(profile.EffectMode, period);
        UpdateEffectAvailability();
        UpdateEffectVisual();
    }

    public void LoadEffectParameters(SavedDevice? device = null)
    {
        device ??= _repository.GetByAddress(DeviceAddress);
        _blinkEffectParameters = device?.BlinkEffectParameters is { } blink &&
            IsValidEffectPeriod(blink.PeriodMilliseconds) ? blink : new();
        _breatheEffectParameters = device?.BreatheEffectParameters is { } breathe &&
            IsValidEffectPeriod(breathe.PeriodMilliseconds) ? breathe : new();
    }

    public ushort GetEffectPeriod(AgpLightMode mode) => mode switch
    {
        AgpLightMode.Blink => _blinkEffectParameters.PeriodMilliseconds,
        AgpLightMode.Breathe => _breatheEffectParameters.PeriodMilliseconds,
        _ => 0
    };

    public void RememberEffectPeriod(AgpLightMode mode, ushort period)
    {
        if (!IsValidEffectPeriod(period))
            return;

        switch (mode)
        {
            case AgpLightMode.Blink:
                _blinkEffectParameters = _blinkEffectParameters with { PeriodMilliseconds = period };
                _repository.SetBlinkEffectParameters(DeviceAddress, _blinkEffectParameters);
                break;
            case AgpLightMode.Breathe:
                _breatheEffectParameters = _breatheEffectParameters with { PeriodMilliseconds = period };
                _repository.SetBreatheEffectParameters(DeviceAddress, _breatheEffectParameters);
                break;
        }
    }

    public static bool IsValidEffectPeriod(ushort period) =>
        period >= AgpModelCodec.MinimumEffectPeriodMilliseconds &&
        period <= AgpModelCodec.MaximumEffectPeriodMilliseconds;

    public Task ApplyEffectParametersAsync(AgpLightMode mode, ushort period)
    {
        if (EffectMode == mode)
            return SetEffectAsync(mode, period);

        // An inactive effect is configured locally; only a short press selects it.
        RememberEffectPeriod(mode, period);
        return Task.CompletedTask;
    }
}
