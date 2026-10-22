using ArriOpto.BLE.Agp;
using CommunityToolkit.Mvvm.Input;

namespace ArriLight.ViewModels;

public partial class DeviceControlPageViewModel
{
    private void QueueLightSettingsCommand()
    {
        if (!_isReady || _isApplyingControlState || !IsDeviceOnline)
            return;

        if (!IsGroup && (IsDemo || string.IsNullOrWhiteSpace(DeviceAddress)))
            return;

        _lightSettingsCancellation?.Cancel();
        _lightSettingsCancellation?.Dispose();
        _lightSettingsCancellation = new CancellationTokenSource();
        _lightSettingsTask = SendLightSettingsAsync(_lightSettingsCancellation.Token);
    }

    private async Task StopPendingLightSettingsAsync()
    {
        var pending = _lightSettingsTask;
        _lightSettingsCancellation?.Cancel();
        if (pending is not null)
        {
            try
            {
                await pending;
            }
            catch (OperationCanceledException)
            {
                // A cancelled pending write is intentionally discarded.
            }
        }

        _lightSettingsTask = null;
        _lightSettingsCancellation?.Dispose();
        _lightSettingsCancellation = null;
    }

    private async Task SendLightSettingsAsync(
        CancellationToken cancellationToken,
        bool debounce = true)
    {
        using var timing = _diagnostics.BeginBleTiming(
            "UI.Control.LightSettings", DeviceAddress,
            $"debounce={debounce}; debounceMs={LightSettingsDebounce.TotalMilliseconds:0}; initializing={IsInitializingConnection}; pageKind={ControlPageKind}; colorModel={_activeColorModel}");
        if (debounce)
        {
            try
            {
                await Task.Delay(LightSettingsDebounce, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                timing.SetResult("debounce-canceled");
                return;
            }
            timing.Mark("灯光设置防抖等待结束");
        }
        cancellationToken.ThrowIfCancellationRequested();
        var lightingVersion = _lightingEditVersion;

        var brightnessPercent = (byte)Math.Clamp(
            Math.Round(Brightness),
            0,
            AgpModelCodec.MaximumBrightnessPercent);
        timing.Mark($"提交灯光设置; brightnessPercent={brightnessPercent}");
        try
        {
            if (IsGroup)
            {
                SubmitGroupCommand(new(DeviceGroupCommandKind.Lighting, Brightness: brightnessPercent,
                    ColorModel: SupportsTemperature && (!SupportsColor || _activeColorModel == AgpColorModel.Ctl)
                        ? AgpColorModel.Ctl : SupportsColor ? AgpColorModel.Hsl : AgpColorModel.None,
                    Hue: Hue, Saturation: Saturation, TemperatureKelvin: TemperatureRatioToKelvin(Temperature)), showToast: false);
            }
            else if (IsMeshDevice)
            {
                if (!_hasPendingColorInput && SupportsBrightness)
                    await _meshControl.SetLightnessAsync(DeviceAddress, brightnessPercent, cancellationToken);
                else if (SupportsColor && (!SupportsTemperature || _activeColorModel != AgpColorModel.Ctl))
                    await _meshControl.SetHslAsync(DeviceAddress, brightnessPercent, Hue, Saturation, cancellationToken);
                else if (SupportsTemperature)
                    await _meshControl.SetCtlAsync(DeviceAddress, brightnessPercent,
                        TemperatureRatioToKelvin(Temperature), cancellationToken);
                else if (SupportsBrightness)
                    await _meshControl.SetLightnessAsync(DeviceAddress, brightnessPercent, cancellationToken);
            }
            else if (ControlPageKind == AgpControlPageKind.Hsl ||
                (ControlPageKind == AgpControlPageKind.HslAndCtl &&
                 _activeColorModel != AgpColorModel.Ctl))
            {
                await _agpLight.SetHslAsync(
                    DeviceAddress,
                    brightnessPercent,
                    Hue,
                    Saturation,
                    CancellationToken.None);
            }
            else if (ControlPageKind == AgpControlPageKind.Ctl ||
                     ControlPageKind == AgpControlPageKind.HslAndCtl)
            {
                await _agpLight.SetCtlAsync(
                    DeviceAddress,
                    brightnessPercent,
                    TemperatureRatioToKelvin(Temperature),
                    cancellationToken: CancellationToken.None);
            }
            else
            {
                await _agpLight.SetLightnessAsync(
                    DeviceAddress,
                    brightnessPercent,
                    CancellationToken.None);
            }
            if (lightingVersion == _lightingEditVersion) _hasPendingColorInput = false;
            timing.SetResult("success");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            timing.SetResult("canceled");
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            timing.SetResult($"failed:{exception.GetType().Name}");
            _diagnostics.Error(
                "Light/Settings",
                $"发送灯光设置失败：{DeviceName}。",
                exception);
        }
    }

    private ushort TemperatureRatioToKelvin(double ratio)
    {
        var temperature = _ctlRange.TemperatureMinimumKelvin +
            Math.Clamp(ratio, 0, 1) *
            (_ctlRange.TemperatureMaximumKelvin -
             _ctlRange.TemperatureMinimumKelvin);
        return (ushort)Math.Round(temperature);
    }

    private double KelvinToTemperatureRatio(ushort temperatureKelvin)
    {
        var span = _ctlRange.TemperatureMaximumKelvin -
            _ctlRange.TemperatureMinimumKelvin;
        return span == 0
            ? 0
            : Math.Clamp(
                (temperatureKelvin - _ctlRange.TemperatureMinimumKelvin) /
                (double)span,
                0,
                1);
    }
}
