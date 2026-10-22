using ArriOpto.BLE.Agp;
using CommunityToolkit.Mvvm.Input;

namespace ArriLight.ViewModels;

public partial class DeviceControlPageViewModel
{
    public async Task PrepareControlSessionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsGroup) { await RedirectEmptyGroupAsync(); return; }
        if (IsDemo)
            return;
        await Task.Run(_discoveryService.StopAsync, cancellationToken).WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureConnectedAsync(showError: false, cancellationToken: cancellationToken, scanAlreadyStopped: true);
    }

    /// <summary>
    /// 该设备是否为 SIG Mesh：由持久化协议字段判定，避免靠连接结果猜。
    /// </summary>
    private bool IsMeshDevice =>
        _repository.GetByAddress(DeviceAddress)?.EffectiveProtocol ==
        BluetoothDeviceProtocol.Mesh;

    /// <summary>
    /// Mesh 设备的控制页初始化：按节点模型读取开关、亮度、颜色与色温，之后所有命令
    /// 都走 <see cref="MeshDeviceControlService"/> 的统一状态与会话管理。
    /// </summary>
    private async Task InitializeMeshControlSessionAsync(
        BleTimingScope timing,
        bool refreshStatus,
        CancellationToken cancellationToken,
        bool scanAlreadyStopped)
    {
        var lightingVersion = _lightingEditVersion;
        try
        {
            using var initializationCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _connectionInitializationCancellation = initializationCancellation;
            if (!scanAlreadyStopped)
                await Task.Run(_discoveryService.StopAsync, initializationCancellation.Token);
            var sharedPreparation = _meshControl.IsRuntimeStarted;
            if (sharedPreparation)
                await _meshControl.EnsureRuntimeReadyAsync(DeviceAddress, initializationCancellation.Token, refreshStatus);
            var profile = sharedPreparation ? _meshControl.GetCachedControlProfile(DeviceAddress) :
                await _meshControl.GetControlProfileAsync(DeviceAddress, refreshStatus, initializationCancellation.Token);
            initializationCancellation.Token.ThrowIfCancellationRequested();
            if (profile is null)
                throw new InvalidOperationException(AppResources.Mesh_CapabilitiesUnavailable);
            ApplyMeshControlProfile(profile);
            if (profile.OnOffAddress is not null && !sharedPreparation)
            {
                var isOn = await _meshControl.GetOnOffStateAsync(DeviceAddress, initializationCancellation.Token);
                if (isOn is { } on) IsMasterPowerOn = on;
            }
            if (SupportsBrightness)
            {
                var state = sharedPreparation ? _meshControl.GetCachedState(DeviceAddress) :
                    await _meshControl.GetLightingStateAsync(DeviceAddress, initializationCancellation.Token);
                initializationCancellation.Token.ThrowIfCancellationRequested();
                if (_meshControl.GetCachedControlProfile(DeviceAddress) is { } updated)
                    ApplyMeshControlProfile(updated);
                if (state is not null)
                    ApplyMeshLightState(state, refreshStatus ? lightingVersion == _lightingEditVersion : null);
            }
            IsLocalModeSupported = false;
            if (_meshControl.GetRuntimeSnapshot(DeviceAddress) is { } runtime)
                ApplyMeshRuntimeSnapshot(runtime);
            UpdateMasterPowerVisual();
            timing.SetResult("mesh-ready");
            if (refreshStatus)
                _interaction.ShowToast(AppResources.Device_StatusRefreshed);
        }
        catch (OperationCanceledException)
        {
            timing.SetResult("canceled");
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            IsDeviceOnline = false;
            timing.SetResult($"failed:{exception.GetType().Name}");
            _diagnostics.Error(
                "BLE/Connection",
                $"Mesh 控制页初始化失败：{DeviceAddress}。",
                exception);
        }
        finally
        {
            _connectionInitializationCancellation = null;
            IsInitializingConnection = false;
            OnPropertyChanged(nameof(CanRefreshStatus));
        }
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
            $"refreshStatus={refreshStatus}; useCachedLightState={!refreshStatus}; isMesh={IsMeshDevice}");
        IsInitializingConnection = true;
        // Mesh 设备不能用 AGP 的 GetControlProfileAsync 初始化：那会真实连上设备、
        // 在找不到 AGP 服务时失败，并且失败路径不断开连接。固件每盏灯只允许一个
        // BLE 连接，泄漏后真正的 Mesh 控制再也连不上。走自己的短连接查询路径。
        if (IsMeshDevice)
        {
            await InitializeMeshControlSessionAsync(timing, refreshStatus, cancellationToken, scanAlreadyStopped);
            return;
        }
        var delaySecondsVersion = _delaySecondsEditVersion;
        var lightingVersion = _lightingEditVersion;
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
            AgpDeviceControlProfile profile;
            if (_agpLight.GetRuntimeSnapshot(DeviceAddress) is not null)
            {
                // A page owns its wait, while preparation and accepted commands
                // remain owned by the application's device runtime.
                await _agpLight.EnsureRuntimeReadyAsync(
                    DeviceAddress, initializationCancellation.Token, refreshStatus);
                initializationCancellation.Token.ThrowIfCancellationRequested();
                profile = await _agpLight.GetControlProfileAsync(
                    DeviceAddress, initializationCancellation.Token, useCachedLightState: true);
            }
            else
            {
                profile = await Task.Run(() => _agpLight.GetControlProfileAsync(
                    DeviceAddress, initializationCancellation.Token,
                    useCachedLightState: !refreshStatus,
                    refreshLightState: refreshStatus), initializationCancellation.Token);
            }
            timing.Mark("完整控制资料已取得");
            initializationCancellation.Token.ThrowIfCancellationRequested();
            var applyLighting = _agpLight.GetRuntimeSnapshot(DeviceAddress) is not null
                ? _appearanceLightingEditVersion == _lightingEditVersion : lightingVersion == _lightingEditVersion;
            if (profile.LightState is { } state)
                ApplyLightState(state, applyLighting);
            ApplyControlProfile(profile, applyLighting);
            // A legacy record may have failed before its capabilities were
            // obtained. A later successful manual retry must still fill the UI.
            CapabilityViewsRetryRequested?.Invoke();
            if (_agpLight.GetCachedLocalMode(DeviceAddress) is { } localMode)
            {
                if (_lastAppliedRuntimeLocalMode != localMode)
                    ApplyLocalModeConfiguration(localMode,
                        _agpLight.GetRuntimeSnapshot(DeviceAddress) is not null
                            ? _appearanceDelayEditVersion == _delaySecondsEditVersion : delaySecondsVersion == _delaySecondsEditVersion,
                        updateMode: !_localModeInputEdited);
                IsLocalModeSupported = true;
                // Local cancellation controls only callbacks for this appearance.
                // Completing unrelated initialization must not cancel a mode
                // command already submitted on this visible page.
                if (_localModeCancellation is null || _localModeCancellation.IsCancellationRequested)
                {
                    CancelLocalModeOperations();
                    _localModeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                }
                _localModeLoadTask = Task.CompletedTask;
            }
            else
            {
                _localModeLoadTask = LoadLocalModeAsync(initializationCancellation.Token, delaySecondsVersion);
            }
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
            if (_isControlPageVisible && _agpLight.GetRuntimeSnapshot(DeviceAddress) is { } runtime)
            {
                ApplyRuntimeSnapshot(runtime);
            }
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

            // A missing optional/required detail does not invalidate another
            // page's authenticated session. Runtime owns session recovery.
        }
        finally
        {
            _connectionInitializationCancellation = null;
            IsInitializingConnection = false;
            OnPropertyChanged(nameof(CanRefreshStatus));
        }
    }

    public void ApplyLightState(AgpLightState state, bool applyBrightness = true)
    {
        _pendingLightState = state; IsMasterPowerOn = state.IsOn;
        IsLocked = state.IsLocked;
        if (applyBrightness)
            Brightness = Math.Clamp(state.IsOn && state.PresentBrightness > 0 ?
                (int)state.PresentBrightness : state.LastNonzeroBrightness, 1, 100);
        RenderRequested?.Invoke();
    }

    private void OnConnectionStateChanged(
        object? sender,
        BluetoothSessionSnapshot snapshot) =>
        _dispatcher.Post(() =>
        {
            if (_isControlPageVisible) ApplyConnectionSnapshot(snapshot);
        });

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

        if (IsMeshDevice)
        {
            if (_meshControl.GetRuntimeSnapshot(DeviceAddress) is { } meshRuntime)
                ApplyMeshRuntimeSnapshot(meshRuntime);
            else
                IsDeviceOnline = snapshot.IsConnected;
            return;
        }

        if (_agpLight.GetRuntimeSnapshot(DeviceAddress) is { } runtime)
        {
            ApplyRuntimeSnapshot(runtime);
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

    private void OnControlRuntimeStateChanged(object? sender, DeviceRuntimeChange change)
    {
        if (!string.Equals(change.Address, DeviceAddress, StringComparison.OrdinalIgnoreCase)) return;
        _dispatcher.Post(() =>
        {
            if (!_isControlPageVisible) return;
            if (_agpLight.GetRuntimeSnapshot(DeviceAddress) is { } runtime)
                ApplyRuntimeSnapshot(runtime);
        });
    }

    private void OnMeshRuntimeStateChanged(object? sender, DeviceRuntimeChange change)
    {
        if (!IsMeshDevice || !string.Equals(change.Address, DeviceAddress, StringComparison.OrdinalIgnoreCase))
            return;
        _dispatcher.Post(() =>
        {
            if (!_isControlPageVisible) return;
            if (_meshControl.GetRuntimeSnapshot(DeviceAddress) is { } runtime)
                ApplyMeshRuntimeSnapshot(runtime);
        });
    }

    private void ApplyRuntimeSnapshot(DeviceRuntimeSnapshot snapshot)
    {
        if (snapshot.Protocol == BluetoothDeviceProtocol.Mesh)
        {
            ApplyMeshRuntimeSnapshot(snapshot);
            return;
        }

        var availabilityText = snapshot.Availability switch
        {
            DeviceRuntimeAvailability.Ready => AppResources.Device_Ready,
            DeviceRuntimeAvailability.Online => AppResources.Device_Online,
            _ => AppResources.Device_Offline
        };
        IsDeviceOnline = snapshot.Availability is DeviceRuntimeAvailability.Online or DeviceRuntimeAvailability.Ready;
        HasPendingUserOperation = snapshot.HasPendingUserOperation;
        var detail = GetRuntimeStatusDetail(snapshot);
        ConnectionStatusText = string.IsNullOrWhiteSpace(detail)
            ? availabilityText : $"{availabilityText} · {detail}";
        ConnectionButtonText = IsDeviceOnline ? availabilityText : snapshot.IsBusy
            ? AppResources.Ble_Connecting : AppResources.Common_Retry;
        CanConnect = !IsDeviceOnline && !snapshot.IsBusy && !_agpLight.IsApplicationSuspended;
        ConnectionStatusColor = PresentationColor.FromArgb(IsDeviceOnline ? "#35C987" : "#E65B65");
        ApplyAvailableRuntimeData(snapshot);
        UpdateEffectAvailability();
    }

    private void ApplyMeshRuntimeSnapshot(DeviceRuntimeSnapshot snapshot)
    {
        var availabilityText = snapshot.Availability switch
        {
            DeviceRuntimeAvailability.Ready => AppResources.Device_Ready,
            DeviceRuntimeAvailability.Online => AppResources.Device_Online,
            _ => AppResources.Device_Offline
        };
        IsDeviceOnline = snapshot.Availability is DeviceRuntimeAvailability.Online or DeviceRuntimeAvailability.Ready;
        HasPendingUserOperation = snapshot.HasPendingUserOperation;
        ConnectionStatusText = string.IsNullOrWhiteSpace(snapshot.StageText)
            ? availabilityText
            : $"{availabilityText} · {snapshot.StageText}";
        ConnectionButtonText = IsDeviceOnline ? availabilityText : snapshot.IsBusy
            ? AppResources.Ble_Connecting : AppResources.Common_Retry;
        CanConnect = !IsDeviceOnline && !snapshot.IsBusy && !_agpLight.IsApplicationSuspended;
        ConnectionStatusColor = PresentationColor.FromArgb(IsDeviceOnline ? "#35C987" : "#E65B65");
        if (_meshControl.GetCachedControlProfile(DeviceAddress) is { } profile)
            ApplyMeshControlProfile(profile);
        IsLocalModeSupported = false;
        if (_meshControl.GetCachedState(DeviceAddress) is { } state)
            ApplyMeshLightState(state);
        UpdateEffectAvailability();
    }

    private void ApplyMeshControlProfile(MeshControlProfile profile)
    {
        var changed = !HasKnownCapabilities || SupportedCapabilities != profile.Capabilities;
        HasKnownCapabilities = true;
        SupportedCapabilities = profile.Capabilities;
        ControlPageKind = new AgpDeviceControlProfile(profile.Capabilities, null, profile.CtlRange).PageKind;
        _ctlRange = profile.CtlRange;
        EffectsSupported = false;
        if (!SupportsColor) _activeColorModel = AgpColorModel.Ctl;
        else if (!SupportsTemperature) _activeColorModel = AgpColorModel.Hsl;
        ConfigureControlPage();
        if (changed) CapabilityViewsRetryRequested?.Invoke();
    }

    private void ApplyMeshLightState(MeshDeviceState state, bool? applyLighting = null)
    {
        if (!IsPowerChanging && state.IsOn is { } isOn) IsMasterPowerOn = isOn;
        if (applyLighting ?? _lightingEditVersion == _appearanceLightingEditVersion)
        {
            _hasPendingColorInput = false;
            if (state.LightnessPercent is > 0) Brightness = state.LightnessPercent.Value;
            if (state.Hue is { } hue) Hue = hue / (double)ushort.MaxValue;
            if (state.Saturation is { } saturation) Saturation = saturation / (double)ushort.MaxValue;
            if (state.TemperatureKelvin is { } temperature) Temperature = KelvinToTemperatureRatio(temperature);
            if (state.ActiveColorModel is { } model) _activeColorModel = model;
        }
        RenderRequested?.Invoke();
    }

    private void ApplyAvailableRuntimeData(DeviceRuntimeSnapshot snapshot)
    {
        if (!_isControlPageVisible) return;
        var status = _agpLight.GetCachedDeviceStatus(DeviceAddress);
        if (status?.LightState is { } knownState)
            IsLocked = knownState.IsLocked;
        if (!IsDeviceOnline)
        {
            IsLocalModeSupported = false;
            return;
        }
        bool Succeeded(DeviceRuntimeStep step) => snapshot.Steps.GetValueOrDefault(step)?.Status == DeviceRuntimeStepStatus.Succeeded;
        var applyLighting = _lightingEditVersion == _appearanceLightingEditVersion;
        if (status?.LightState is { } state && state != _pendingLightState)
            ApplyLightState(state, applyLighting);
        if (status?.Profile is { } profile)
        {
            var previous = _pendingControlProfile;
            HasKnownCapabilities = true;
            SupportedCapabilities = profile.Capabilities;
            ApplyBatteryProfile(profile);
            ControlPageKind = profile.PageKind;
            if (Succeeded(DeviceRuntimeStep.CtlRange)) _ctlRange = profile.CtlRange;
            // A partial profile may retain historical color values. Use only
            // color data that has succeeded for this connection.
            _pendingControlProfile = profile with
            {
                ColorState = Succeeded(DeviceRuntimeStep.ColorState) ? profile.ColorState : null,
                CtlRange = _ctlRange
            };
            if (Succeeded(DeviceRuntimeStep.Effects) &&
                (!_hasAppliedRuntimeEffect || _lastAppliedRuntimeEffectMode != profile.EffectMode ||
                    _lastAppliedRuntimeEffectPeriod != profile.EffectPeriodMilliseconds))
            {
                ApplyEffectProfile(profile);
                _hasAppliedRuntimeEffect = true;
                _lastAppliedRuntimeEffectMode = profile.EffectMode;
                _lastAppliedRuntimeEffectPeriod = profile.EffectPeriodMilliseconds;
            }
            if (_pendingControlProfile != previous)
            {
                ConfigureControlPage();
                if (Succeeded(DeviceRuntimeStep.ColorState)) ApplyProfileValues(applyLighting, applyLighting);
            }
        }
        if (Succeeded(DeviceRuntimeStep.LocalMode) && _agpLight.GetCachedLocalMode(DeviceAddress) is { } localMode)
        {
            if (_lastAppliedRuntimeLocalMode != localMode)
            {
                ApplyLocalModeConfiguration(localMode, _appearanceDelayEditVersion == _delaySecondsEditVersion,
                    updateMode: !_localModeInputEdited);
                _lastAppliedRuntimeLocalMode = localMode;
            }
            IsLocalModeSupported = true;
            _localModeCancellation ??= new CancellationTokenSource();
        }
        else IsLocalModeSupported = false;
        RenderRequested?.Invoke();
    }

    private static string GetRuntimeStatusDetail(DeviceRuntimeSnapshot snapshot)
    {
        var detail = snapshot.StageText ?? snapshot.Error ?? string.Empty;
        if (snapshot.LastOperation?.Stage != DeviceRuntimeOperationStage.Unknown) return detail;
        var unknown = AppResources.Control_StateUnavailable;
        return string.IsNullOrWhiteSpace(detail) || detail == unknown ? unknown : $"{detail} · {unknown}";
    }

    private bool NotifyUnknownOutcomeIfNeeded(Guid? previousOperationId)
    {
        if (_agpLight.GetRuntimeSnapshot(DeviceAddress)?.LastOperation is not { Stage: DeviceRuntimeOperationStage.Unknown } operation)
            return false;
        if (operation.Id == previousOperationId) return false;
        if (_isControlPageVisible && !_agpLight.IsApplicationSuspended && _lastUnknownToastOperation != operation.Id)
        {
            _lastUnknownToastOperation = operation.Id;
            _interaction.ShowToast(AppResources.Control_StateUnavailable);
        }
        return true;
    }

    private void ApplyCachedCapabilities(AgpCapabilities capabilities)
    {
        HasKnownCapabilities = true;
        SupportedCapabilities = capabilities;
        IsBatterySupported = !IsDemo && !IsMeshDevice && capabilities.HasFlag(AgpCapabilities.Battery);
        ControlPageKind = new AgpDeviceControlProfile(
            capabilities, null, _ctlRange).PageKind;
        EffectsSupported = capabilities.HasFlag(AgpCapabilities.Effects);
        _activeColorModel = ControlPageKind == AgpControlPageKind.Ctl
            ? AgpColorModel.Ctl
            : AgpColorModel.Hsl;
    }

    private void ApplyControlProfile(AgpDeviceControlProfile profile, bool applyLighting = true)
    {
        HasKnownCapabilities = true;
        ApplyBatteryProfile(profile);
        _pendingControlProfile = profile;
        SupportedCapabilities = profile.Capabilities;
        ControlPageKind = profile.PageKind;
        ApplyEffectProfile(profile);
        _ctlRange = profile.CtlRange;
        ConfigureControlPage();

        ApplyProfileValues(applyLighting, applyLighting);
        RenderRequested?.Invoke();
    }

    private void ApplyProfileValues(bool applyBrightness = true, bool applyColor = true)
    {
        if (_pendingControlProfile is not { } profile) return;

        if (applyColor && profile.ColorState is null)
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
                    if (applyColor) _activeColorModel = hsl.ActiveModel == AgpColorModel.None
                        ? AgpColorModel.Hsl : hsl.ActiveModel;
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
                    if (applyColor) _activeColorModel = ctl.ActiveModel == AgpColorModel.None
                        ? AgpColorModel.Ctl : ctl.ActiveModel;
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
        if (!string.Equals(change.Address, DeviceAddress, StringComparison.OrdinalIgnoreCase))
            return;

        _dispatcher.Post(() =>
        {
            if (!_isControlPageVisible) return;
            // Dispatcher callbacks use the newest cached values, so older queued
            // notifications cannot overwrite a completed operation's result.
            var status = _agpLight.GetCachedDeviceStatus(DeviceAddress);
            if (_agpLight.GetRuntimeSnapshot(DeviceAddress) is { } runtime) ApplyRuntimeSnapshot(runtime);
            else
            {
                if (status?.LightState is { } state) ApplyLightState(state);
                if (status?.Profile is { } profile)
                {
                    IsDeviceOnline = status.IsOnline;
                    ApplyEffectProfile(profile);
                    ApplyBatteryProfile(profile);
                }
            }
        });
    }
}
