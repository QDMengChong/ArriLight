using ArriOpto.BLE.Agp;
using CommunityToolkit.Mvvm.Input;

namespace ArriLight.ViewModels;

public partial class DeviceControlPageViewModel
{
    private void QueueLightSettingsCommand()
    {
        if (!_isReady || _isApplyingControlState || !IsDeviceOnline)
            return;

        if (IsDemo || string.IsNullOrWhiteSpace(DeviceAddress))
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

        var brightnessPercent = (byte)Math.Clamp(
            Math.Round(Brightness),
            0,
            AgpModelCodec.MaximumBrightnessPercent);
        timing.Mark($"提交灯光设置; brightnessPercent={brightnessPercent}");
        try
        {
            if (IsMeshDevice)
            {
                // Mesh 侧经由 Light HSL Set 只改写 Lightness 分量。
                // Light Lightness Server 虽然被 HSL Server 内部注册，但固件
                // 没有给它绑定 AppKey，直接下发会被静默丢弃。
                await _meshControl.SetLightnessAsync(
                    DeviceAddress,
                    brightnessPercent,
                    cancellationToken);
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
            timing.SetResult("success");
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
