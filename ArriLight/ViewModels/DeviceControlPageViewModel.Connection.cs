using ArriOpto.BLE.Agp;
using CommunityToolkit.Mvvm.Input;

namespace ArriLight.ViewModels;

public partial class DeviceControlPageViewModel
{
    public async Task PrepareControlSessionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsDemo)
            return;
        await Task.Run(_discoveryService.StopAsync, cancellationToken).WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureConnectedAsync(showError: false, cancellationToken: cancellationToken, scanAlreadyStopped: true);
    }

    [RelayCommand]
    private Task ConnectAsync() => EnsureConnectedAsync(showError: true);
    [RelayCommand]
    private Task RefreshAsync() => CanRefreshStatus ?
        EnsureConnectedAsync(showError: true, refreshStatus: true) : Task.CompletedTask;

    private async Task InitializeControlSessionAsync(
        bool showError, bool refreshStatus, CancellationToken cancellationToken, bool scanAlreadyStopped)
    {
        using var timing = _diagnostics.BeginBleTiming(
            "UI.Control.Initialize", DeviceAddress,
            $"refreshStatus={refreshStatus}; useCachedLightState={!refreshStatus}");
        IsInitializingConnection = true;
        var delaySecondsVersion = _delaySecondsEditVersion;
        OnPropertyChanged(nameof(CanRefreshStatus));
        using var initializationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _connectionInitializationCancellation = initializationCancellation;
        try
        {
            // Manual connect/refresh also runs only on the already visible page.
            // Retry scan teardown if the deferred preparation failed there.
            if (!scanAlreadyStopped)
                await Task.Run(_discoveryService.StopAsync, initializationCancellation.Token);
            initializationCancellation.Token.ThrowIfCancellationRequested();
            var profile = await Task.Run(() => _agpLight.GetControlProfileAsync(
                DeviceAddress, initializationCancellation.Token,
                useCachedLightState: !refreshStatus,
                refreshLightState: refreshStatus), initializationCancellation.Token);
            timing.Mark("完整控制资料已取得");
            initializationCancellation.Token.ThrowIfCancellationRequested();
            if (profile.LightState is { } state)
                ApplyLightState(state);
            ApplyControlProfile(profile);
            // A legacy record may have failed before its capabilities were
            // obtained. A later successful manual retry must still fill the UI.
            CapabilityViewsRetryRequested?.Invoke();
            // MODE_GET runs after the profile, without delaying capability views
            // or the main controls. It owns cancellation for this appearance.
            _localModeLoadTask = LoadLocalModeAsync(initializationCancellation.Token, delaySecondsVersion);
            timing.Mark("控制资料已应用到页面，控制初始化完成");
            timing.SetResult("ready");
            if (refreshStatus)
                _interaction.ShowToast(AppResources.Device_StatusRefreshed);
        }
        catch (OperationCanceledException) when (initializationCancellation.IsCancellationRequested)
        {
            timing.SetResult("canceled");
            // Leaving the page cancels initialization without replacing a known status.
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            timing.SetResult($"failed:{exception.GetType().Name}");
            _diagnostics.Error(
                "BLE/Connection",
                $"设备控制页连接失败：{DeviceAddress}。",
                exception);
            if (showError)
            {
                await _interaction.AlertAsync(
                    AppResources.Ble_ConnectionFailed,
                    Localization.Format(nameof(AppResources.Agp_SessionFailed), exception.Message),
                    AppResources.Common_Ok);
            }

            try
            {
                await _connectionService.DisconnectAsync(DeviceAddress);
            }
            catch
            {
                // 原始 AGP 连接异常更有诊断价值，断开清理失败不覆盖它。
            }
        }
        finally
        {
            _connectionInitializationCancellation = null;
            IsInitializingConnection = false;
            OnPropertyChanged(nameof(CanRefreshStatus));
        }
    }

    public void ApplyLightState(AgpLightState state)
    {
        _pendingLightState = state; IsMasterPowerOn = state.IsOn;
        Brightness = Math.Clamp(state.IsOn && state.PresentBrightness > 0 ?
            (int)state.PresentBrightness : state.LastNonzeroBrightness, 1, 100);
        RenderRequested?.Invoke();
    }

    private void OnConnectionStateChanged(
        object? sender,
        BluetoothSessionSnapshot snapshot) =>
        _dispatcher.Post(() =>
            ApplyConnectionSnapshot(snapshot));

    private void ApplyConnectionSnapshot(BluetoothSessionSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.Address) &&
            !string.Equals(
                snapshot.Address,
                DeviceAddress,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ConnectionStatusText = snapshot.Message;
        ConnectionButtonText = snapshot.State switch
        {
            BluetoothSessionState.Scanning => AppResources.Ble_Scanning,
            BluetoothSessionState.Connecting => AppResources.Ble_Connecting,
            BluetoothSessionState.Connected => AppResources.Ble_Connected,
            BluetoothSessionState.Updating => AppResources.Firmware_Updating,
            BluetoothSessionState.Error => AppResources.Common_Retry,
            _ => AppResources.Ble_Connect
        };
        CanConnect = snapshot.State is
            BluetoothSessionState.Disconnected or BluetoothSessionState.Error;
        ConnectionStatusColor = snapshot.State switch
        {
            BluetoothSessionState.Connected => PresentationColor.FromArgb("#35C987"),
            BluetoothSessionState.Updating => PresentationColor.FromArgb("#3B70F2"),
            BluetoothSessionState.Error => PresentationColor.FromArgb("#E65B65"),
            BluetoothSessionState.Scanning or BluetoothSessionState.Connecting =>
                PresentationColor.FromArgb("#F0A44B"),
            _ => PresentationColor.FromArgb("#A9B2C3")
        };
        IsDeviceOnline = snapshot.IsConnected;
        UpdateEffectAvailability();
    }

    private void ApplyCachedCapabilities(AgpCapabilities capabilities)
    {
        HasKnownCapabilities = true;
        SupportedCapabilities = capabilities;
        ControlPageKind = new AgpDeviceControlProfile(
            capabilities, null, _ctlRange).PageKind;
        EffectsSupported = capabilities.HasFlag(AgpCapabilities.Effects);
        _activeColorModel = ControlPageKind == AgpControlPageKind.Ctl
            ? AgpColorModel.Ctl
            : AgpColorModel.Hsl;
    }

    private void ApplyControlProfile(AgpDeviceControlProfile profile)
    {
        HasKnownCapabilities = true;
        _pendingControlProfile = profile;
        SupportedCapabilities = profile.Capabilities;
        ControlPageKind = profile.PageKind;
        ApplyEffectProfile(profile);
        _ctlRange = profile.CtlRange;
        ConfigureControlPage();

        ApplyProfileValues();
        RenderRequested?.Invoke();
    }

    private void ApplyProfileValues(bool applyBrightness = true, bool applyColor = true)
    {
        if (_pendingControlProfile is not { } profile) return;

        if (profile.ColorState is null)
        {
            _activeColorModel = ControlPageKind == AgpControlPageKind.Ctl
                ? AgpColorModel.Ctl
                : AgpColorModel.Hsl;
        }

        _isApplyingControlState = true;
        try
        {
            switch (profile.ColorState)
            {
                case AgpHslState hsl:
                    _activeColorModel = hsl.ActiveModel == AgpColorModel.None
                        ? AgpColorModel.Hsl
                        : hsl.ActiveModel;
                    if (applyBrightness)
                        Brightness =
                            AgpColorModelCodec.Lightness16ToBrightnessPercent(
                                hsl.Present.Lightness);
                    if (applyColor)
                    {
                        Hue = hsl.Present.Hue / (double)ushort.MaxValue;
                        Saturation =
                            hsl.Present.Saturation / (double)ushort.MaxValue;
                    }
                    break;

                case AgpCtlState ctl:
                    _activeColorModel = ctl.ActiveModel == AgpColorModel.None
                        ? AgpColorModel.Ctl
                        : ctl.ActiveModel;
                    if (applyBrightness)
                        Brightness =
                            AgpColorModelCodec.Lightness16ToBrightnessPercent(
                                ctl.Present.Lightness);
                    if (applyColor)
                        Temperature =
                            KelvinToTemperatureRatio(ctl.Present.TemperatureKelvin);
                    break;

                case AgpLightness16State lightness:
                    if (applyBrightness)
                        Brightness =
                            AgpColorModelCodec.Lightness16ToBrightnessPercent(
                                lightness.Present);
                    break;
            }
        }
        finally
        {
            _isApplyingControlState = false;
        }
    }

    private void OnControlDeviceStatusChanged(object? sender, DeviceStatusChange change)
    {
        if (!string.Equals(change.Address, DeviceAddress, StringComparison.OrdinalIgnoreCase) ||
            !change.Status.IsOnline || change.Status.LightState is not { } state)
            return;

        _dispatcher.Post(() =>
        {
            ApplyLightState(state);
            if (change.Status.Profile is { } profile)
                ApplyEffectProfile(profile);
        });
    }
}
