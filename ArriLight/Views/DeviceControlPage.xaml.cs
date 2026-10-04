using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ArriOpto.BLE.Agp;
using ArriLight.Controls;
using CommunityToolkit.Maui.Views;

namespace ArriLight;

public partial class DeviceControlPage : ContentPage, IQueryAttributable
{
    private const int LongPressMilliseconds = 650;
    private static readonly TimeSpan LightSettingsDebounce = TimeSpan.FromMilliseconds(250);

    private readonly DeviceRepository _repository;
    private readonly BluetoothDiscoveryService _discoveryService;
    private readonly BluetoothConnectionService _connectionService;
    private readonly AgpLightControlService _agpLight;
    private readonly AppDiagnosticService _diagnostics;
    private AgpLightState? _pendingLightState;
    private int _pendingTimerSeconds = 60;
    private Task? _capabilityViewsTask;
    private bool _hasKnownCapabilities;
    private AgpDeviceControlProfile? _pendingControlProfile;
    // 能力相关区域使用 LazyView；内部视图只在能力确认后加载。
    private AgpControlPageKind _controlPageKind;
    private AgpCapabilities _supportedCapabilities = AgpCapabilities.Lightness;

    private string _deviceName = AppResources.Device_Management;
    private string _deviceAddress = string.Empty;
    private string _connectionStatusText = AppResources.Ble_NotYetConnected;
    private string _connectionButtonText = AppResources.Ble_Connect;
    private Color _connectionStatusColor = Color.FromArgb("#A9B2C3");
    private bool _canConnect = true;
    private bool _isReady;
    private bool _isDemo;
    private bool _isDeviceOnline;
    private bool _isMasterPowerOn = true;
    private bool _longPressTriggered;
    private bool _suppressPresetClick;
    private CancellationTokenSource? _presetPressCancellation;
    private CancellationTokenSource? _effectPressCancellation;
    private CancellationTokenSource? _lightSettingsCancellation;
    private Task? _lightSettingsTask;
    private ControlPreset? _pressedPreset;
    private AgpLightMode _effectMode = AgpLightMode.Steady;
    private bool _effectsSupported = true;
    private bool _effectLongPressTriggered;
    private bool _suppressEffectClick;
    private AgpColorModel _activeColorModel;
    private AgpCtlRange _ctlRange = new(
        AgpColorModelCodec.MinimumCtlTemperatureKelvin,
        AgpColorModelCodec.MaximumCtlTemperatureKelvin);
    private bool _isApplyingControlState;
    private bool _isInitializingConnection;
    private CancellationTokenSource? _connectionInitializationCancellation;
    private Task? _connectionInitializationTask;

    private LazyColorControlView ColorView =>
        (LazyColorControlView)ColorControlLazyView.Content!;
    private LazyBrightnessControlView BrightnessView =>
        (LazyBrightnessControlView)BrightnessControlLazyView.Content!;
    private LazyEffectControlView EffectsView =>
        (LazyEffectControlView)EffectControlLazyView.Content!;
    private LazySavePresetButtonView SavePresetView =>
        (LazySavePresetButtonView)SavePresetLazyView.Content!;
    private LazyPresetControlView PresetView =>
        (LazyPresetControlView)PresetControlLazyView.Content!;
    private LazyTimerControlView TimerView =>
        (LazyTimerControlView)TimerControlLazyView.Content!;
    private LazyScheduleControlView ScheduleView =>
        (LazyScheduleControlView)ScheduleControlLazyView.Content!;
    private LazyEffectSettingsOverlayView EffectSettingsView =>
        (LazyEffectSettingsOverlayView)EffectSettingsLazyView.Content!;

    private bool HasColorView => ColorControlLazyView.HasLazyViewLoaded;
    private bool HasTemperatureLegendView => TemperatureLegendLazyView.HasLazyViewLoaded;
    private bool HasBrightnessView => BrightnessControlLazyView.HasLazyViewLoaded;
    private bool HasEffectView => EffectControlLazyView.HasLazyViewLoaded;
    private bool HasSavePresetView => SavePresetLazyView.HasLazyViewLoaded;
    private bool HasPresetView => PresetControlLazyView.HasLazyViewLoaded;
    private bool HasTimerView => TimerControlLazyView.HasLazyViewLoaded;
    private bool HasScheduleView => ScheduleControlLazyView.HasLazyViewLoaded;
    private bool HasEffectSettingsView => EffectSettingsLazyView.HasLazyViewLoaded;

    private ColorTemperaturePicker ColorPicker => ColorView.ColorPicker;
    private Grid BrightnessControlRow => BrightnessView.BrightnessControlRow;
    private Slider BrightnessSlider => BrightnessView.BrightnessSlider;
    private Button SteadyEffectButton => EffectsView.SteadyEffectButton;
    private Button BlinkEffectButton => EffectsView.BlinkEffectButton;
    private Button BreatheEffectButton => EffectsView.BreatheEffectButton;
    private Button SavePresetButton => SavePresetView.SavePresetButton;
    private VerticalStackLayout CustomPresetSection => PresetView.CustomPresetSection;
    private Label PresetModeLabel => PresetView.PresetModeLabel;
    private Button FinishReorderButton => PresetView.FinishReorderButton;
    private Border RelativeTimerCard => TimerView.RelativeTimerCard;
    private Picker TimerModePicker => TimerView.TimerModePicker;
    private Entry DelaySecondsEntry => TimerView.DelaySecondsEntry;
    private Border ScheduleSettingsCard => ScheduleView.ScheduleSettingsCard;
    private LazyEffectSettingsOverlayView EffectSettingsOverlay => EffectSettingsView;
    private Label EffectSettingsTitle => EffectSettingsView.EffectSettingsTitle;
    private Label EffectSettingsPeriodLabel => EffectSettingsView.EffectSettingsPeriodLabel;
    private Slider EffectSettingsPeriodSlider => EffectSettingsView.EffectSettingsPeriodSlider;

    public string DeviceName
    {
        get => _deviceName;
        private set
        {
            if (_deviceName == value)
                return;

            _deviceName = value;
            OnPropertyChanged();
        }
    }
    public string DeviceAddress
    {
        get => _deviceAddress;
        private set
        {
            _deviceAddress = value;
            OnPropertyChanged(nameof(CanRefreshStatus));
        }
    }

    public bool CanRefreshStatus =>
        !_isDemo && !_automaticConnectionPending && !_isInitializingConnection &&
        !string.IsNullOrWhiteSpace(DeviceAddress);

    public string ConnectionStatusText
    {
        get => _connectionStatusText;
        private set
        {
            if (_connectionStatusText == value)
                return;
            _connectionStatusText = value;
            OnPropertyChanged();
        }
    }

    public string ConnectionButtonText
    {
        get => _connectionButtonText;
        private set
        {
            if (_connectionButtonText == value)
                return;
            _connectionButtonText = value;
            OnPropertyChanged();
        }
    }

    public Color ConnectionStatusColor
    {
        get => _connectionStatusColor;
        private set
        {
            if (_connectionStatusColor == value)
                return;
            _connectionStatusColor = value;
            OnPropertyChanged();
        }
    }

    public bool CanConnect
    {
        get => _canConnect;
        private set
        {
            if (_canConnect == value)
                return;
            _canConnect = value;
            OnPropertyChanged();
        }
    }

    /// <summary>设备是否在线（已连接或升级中）。离线时灯控功能全部置灰。</summary>
    public bool IsDeviceOnline
    {
        get => _isDeviceOnline;
        private set
        {
            if (_isDeviceOnline == value)
                return;
            _isDeviceOnline = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ControlOpacity));
        }
    }

    public double ControlOpacity => IsDeviceOnline ? 1 : 0.45;


    public ObservableCollection<ControlPreset> Presets { get; } =
    [
        ControlPreset.Empty(),
        ControlPreset.Empty(),
        ControlPreset.Empty(),
        ControlPreset.Empty(),
        ControlPreset.Empty(),
        ControlPreset.Empty()
    ];

    public DeviceControlPage()
        : this(AgpControlPageKind.Lightness)
    {
    }

    protected DeviceControlPage(AgpControlPageKind controlPageKind)
    {
        _diagnostics = ServiceHelper.GetRequiredService<AppDiagnosticService>();
        using var timing = _diagnostics.BeginBleTiming("UI.Control.Construct", detail: $"kind={controlPageKind}");
        _controlPageKind = controlPageKind;
        _supportedCapabilities = controlPageKind switch
        {
            AgpControlPageKind.Hsl => AgpCapabilities.Hsl | AgpCapabilities.Lightness16,
            AgpControlPageKind.Ctl => AgpCapabilities.Ctl | AgpCapabilities.Lightness16,
            AgpControlPageKind.HslAndCtl =>
                AgpCapabilities.Hsl | AgpCapabilities.Ctl | AgpCapabilities.Lightness16,
            _ => AgpCapabilities.Lightness
        };
        _activeColorModel = controlPageKind == AgpControlPageKind.Ctl
            ? AgpColorModel.Ctl
            : AgpColorModel.Hsl;
        using (var views = _diagnostics.BeginBleTiming("UI.Control.CreateViews"))
        {
            InitializeComponent();
            views.SetResult("created");
        }
        _repository = ServiceHelper.GetRequiredService<DeviceRepository>();
        _discoveryService =
            ServiceHelper.GetRequiredService<BluetoothDiscoveryService>();
        _connectionService =
            ServiceHelper.GetRequiredService<BluetoothConnectionService>();
        _agpLight = ServiceHelper.GetRequiredService<AgpLightControlService>();
        BindingContext = this;
        UpdateMasterPowerVisual();
        UpdateEffectVisual();
        _isReady = true;
        timing.SetResult("prepared");
    }

    protected override void OnAppearing()
    {
#if DEBUG && ANDROID
        _navigationTiming?.Mark("Appearing.Enter");
#endif
        base.OnAppearing();
        BeginControlPageAppearance();
        using var timing = _diagnostics.BeginBleTiming(
            "UI.Control.Appearing", DeviceAddress);
        if (Application.Current is not null)
            Application.Current.RequestedThemeChanged += OnRequestedThemeChanged;
        _connectionService.StateChanged += OnConnectionStateChanged;

        if (!string.IsNullOrWhiteSpace(DeviceAddress) &&
            _repository.GetByAddress(DeviceAddress) is { } device)
        {
            _pendingTimerSeconds = device.TimerSeconds is >= 1 and <= 86400
                ? device.TimerSeconds : 60;
            _isDemo = device.IsDemo;
            timing.Mark($"载入控制页; isDemo={_isDemo}");
            OnPropertyChanged(nameof(CanRefreshStatus));
            DeviceName = device.DisplayName;
            LoadEffectParameters(device);
            LoadCustomPresets(device.CustomPresets ?? []);
            _capabilityViewsTask = null;
            if (device.Capabilities is { } cachedCapabilities)
            {
                ApplyCachedCapabilities(cachedCapabilities);
                // Only local state here. Native controls are created in batches
                // after the page's first draw, independently of BLE preparation.
                timing.Mark($"使用本地能力缓存=0x{(ulong)cachedCapabilities:X}");
            }
            // A preloaded page may have been constructed before a theme change.
            UpdateMasterPowerVisual();
            UpdateEffectVisual();
            if (device.IsDemo)
            {
                ConnectionStatusText = AppResources.Demo_NoConnection;
                ConnectionButtonText = AppResources.Demo_Label;
                CanConnect = false;
                _hasKnownCapabilities = true;
            }
            else
            {
                // Entering the control page takes over a successful home-query
                // lease and keeps the authenticated session alive for controls.
                _agpLight.PromoteConnectionToLongLived(DeviceAddress);
                timing.Mark("控制页接管持续连接");
                if (_agpLight.GetCachedDeviceStatus(DeviceAddress)?.LightState is { } cachedState)
                    ApplyLightState(cachedState);
                ApplyConnectionSnapshot(_connectionService.GetSnapshot(DeviceAddress));
                // OnNavigatedTo schedules scan teardown and AGP preparation
                // after the page transition. Appearing only applies local state.
                ConnectionStatusText = AppResources.Ble_PreparingDeviceConnection;
                CanConnect = false;
            }
            timing.SetResult(device.IsDemo ? "demo" : "page-prepared");
        }
        else
        {
            timing.SetResult("device-not-found");
        }
#if DEBUG && ANDROID
        _navigationTiming?.Mark("Appearing.Exit");
#endif
    }

    protected override void OnDisappearing()
    {
        EndControlPageAppearance();
        _connectionInitializationCancellation?.Cancel();
        if (Application.Current is not null)
            Application.Current.RequestedThemeChanged -= OnRequestedThemeChanged;
        _connectionService.StateChanged -= OnConnectionStateChanged;

        CancelPresetPress();
        CancelEffectPress();
        CloseEffectSettings();
        _lightSettingsCancellation?.Cancel();
        _lightSettingsCancellation?.Dispose();
        _lightSettingsCancellation = null;
        _lightSettingsTask = null;
        base.OnDisappearing();
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("name", out var value) && value is not null)
            DeviceName = Uri.UnescapeDataString(value.ToString()!);

        if (query.TryGetValue("address", out var address) && address is not null)
            DeviceAddress = Uri.UnescapeDataString(address.ToString()!);
    }

    private async void OnMoreClicked(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(DeviceAddress))
            return;

        await Shell.Current.GoToAsync(
            $"{nameof(DeviceMorePage)}?address={Uri.EscapeDataString(DeviceAddress)}");
    }

    private async void OnConnectClicked(object? sender, EventArgs e) =>
        await EnsureConnectedAsync(showError: true);

    private async void OnRefreshClicked(object? sender, EventArgs e)
    {
        if (CanRefreshStatus)
            await EnsureConnectedAsync(showError: true, refreshStatus: true);
    }

    private async Task EnsureConnectedAsync(
        bool showError, bool refreshStatus = false, CancellationToken cancellationToken = default,
        bool scanAlreadyStopped = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_isInitializingConnection &&
            _connectionInitializationCancellation?.IsCancellationRequested == true &&
            _connectionInitializationTask is { } previousInitialization)
        {
            await previousInitialization;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(DeviceAddress) || _isInitializingConnection)
            return;

        var initialization = InitializeControlSessionAsync(
            showError, refreshStatus, cancellationToken, scanAlreadyStopped);
        _connectionInitializationTask = initialization;
        await initialization;
    }

    private async Task InitializeControlSessionAsync(
        bool showError, bool refreshStatus, CancellationToken cancellationToken, bool scanAlreadyStopped)
    {
        using var timing = _diagnostics.BeginBleTiming(
            "UI.Control.Initialize", DeviceAddress,
            $"refreshStatus={refreshStatus}; useCachedLightState={!refreshStatus}");
        _isInitializingConnection = true;
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
            if (_capabilityViewsTask is null && _controlPageAppearanceCancellation is { } appearance)
                _ = ObserveCapabilityViewsRetryAsync(appearance.Token);
            timing.Mark("控制资料已应用到页面，控制初始化完成");
            timing.SetResult("ready");
            if (refreshStatus)
                ToastService.Show(AppResources.Device_StatusRefreshed);
        }
        catch (OperationCanceledException) when (initializationCancellation.IsCancellationRequested)
        {
            timing.SetResult("canceled");
            // Leaving the page cancels initialization without replacing a known status.
        }
        catch (Exception exception)
        {
            timing.SetResult($"failed:{exception.GetType().Name}");
            _diagnostics.Error(
                "BLE/Connection",
                $"设备控制页连接失败：{DeviceAddress}。",
                exception);
            if (showError)
            {
                await DisplayAlertAsync(
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
            _isInitializingConnection = false;
            OnPropertyChanged(nameof(CanRefreshStatus));
        }
    }

    private void ApplyLightState(AgpLightState state)
    {
        _pendingLightState = state;
        _isMasterPowerOn = state.IsOn;
        UpdateMasterPowerVisual();
        if (!HasBrightnessView)
            return;

        _isApplyingControlState = true;
        try
        {
            BrightnessSlider.Value = Math.Clamp(
                state.IsOn && state.PresentBrightness > 0
                    ? (int)state.PresentBrightness : state.LastNonzeroBrightness, 1, 100);
        }
        finally
        {
            _isApplyingControlState = false;
        }
    }

    private void OnConnectionStateChanged(
        object? sender,
        BluetoothSessionSnapshot snapshot) =>
        MainThread.BeginInvokeOnMainThread(() =>
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
            BluetoothSessionState.Connected => Color.FromArgb("#35C987"),
            BluetoothSessionState.Updating => Color.FromArgb("#3B70F2"),
            BluetoothSessionState.Error => Color.FromArgb("#E65B65"),
            BluetoothSessionState.Scanning or BluetoothSessionState.Connecting =>
                Color.FromArgb("#F0A44B"),
            _ => Color.FromArgb("#A9B2C3")
        };
        IsDeviceOnline = snapshot.IsConnected;
        UpdateEffectAvailability();
        if (!IsDeviceOnline)
        {
            CancelEffectPress();
            CloseEffectSettings();
        }
    }

    private void OnRequestedThemeChanged(object? sender, AppThemeChangedEventArgs e)
    {
        UpdateMasterPowerVisual();
        UpdateEffectVisual();
    }

    private void OnColorPickerSelectionChanged(object? sender, EventArgs e)
    {
        _activeColorModel = ColorPicker.LastChangedComponent switch
        {
            ColorTemperatureSelection.Color => AgpColorModel.Hsl,
            ColorTemperatureSelection.Temperature => AgpColorModel.Ctl,
            _ => _activeColorModel
        };
        QueueLightSettingsCommand();
    }

    private void OnBrightnessChanged(object? sender, ValueChangedEventArgs e) =>
        QueueLightSettingsCommand();

    private void OnEffectPressed(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: string value } ||
            !Enum.TryParse<AgpLightMode>(value switch
            {
                "0" => nameof(AgpLightMode.Steady),
                "1" => nameof(AgpLightMode.Blink),
                "2" => nameof(AgpLightMode.Breathe),
                _ => string.Empty
            }, out var mode))
        {
            return;
        }

        CancelEffectPress();
        _effectLongPressTriggered = false;
        _suppressEffectClick = false;
        _effectPressCancellation = new CancellationTokenSource();
        _ = DetectEffectLongPressAsync(mode, _effectPressCancellation.Token);
    }

    private void OnEffectReleased(object? sender, EventArgs e) => CancelEffectPress();

    private async void OnEffectClicked(object? sender, EventArgs e)
    {
        if (!IsDeviceOnline || !_effectsSupported ||
            sender is not Button { CommandParameter: string value } ||
            !TryParseEffectMode(value, out var mode))
        {
            return;
        }

        if (_effectLongPressTriggered || _suppressEffectClick)
        {
            _effectLongPressTriggered = false;
            _suppressEffectClick = false;
            return;
        }

        await SetEffectAsync(mode, GetEffectPeriod(mode));
    }

    private async Task DetectEffectLongPressAsync(
        AgpLightMode mode,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(LongPressMilliseconds, cancellationToken);
            if (cancellationToken.IsCancellationRequested || !IsDeviceOnline || !_effectsSupported)
                return;

            _effectLongPressTriggered = true;
            if (mode is AgpLightMode.Blink or AgpLightMode.Breathe)
            {
                _diagnostics.Debug(
                    "Light/EffectSettings",
                    $"特效长按已触发：device={DeviceAddress}, mode={mode}。");
                await ShowEffectSettingsAsync(mode);
            }
        }
        catch (OperationCanceledException)
        {
            // Short press cancels long-press detection.
        }
        catch (Exception exception)
        {
            _diagnostics.Error(
                "Light/EffectSettings",
                $"特效参数设置失败：device={DeviceAddress}, mode={mode}。",
                exception);
            ToastService.Show(AppResources.Effect_SettingsUnavailable);
        }
    }

    private static bool TryParseEffectMode(string value, out AgpLightMode mode)
    {
        if (value is not ("0" or "1" or "2"))
        {
            mode = AgpLightMode.Steady;
            return false;
        }

        mode = value switch
        {
            "1" => AgpLightMode.Blink,
            "2" => AgpLightMode.Breathe,
            _ => AgpLightMode.Steady
        };
        return true;
    }

    private async Task SetEffectAsync(AgpLightMode mode, ushort periodMilliseconds)
    {
        using var timing = _diagnostics.BeginBleTiming(
            "UI.Control.Effect", DeviceAddress,
            $"mode={mode}; periodMs={periodMilliseconds}; initializing={_isInitializingConnection}; isDemo={_isDemo}");
        if (_isDemo || string.IsNullOrWhiteSpace(DeviceAddress))
        {
            timing.SetResult("skipped");
            return;
        }

        try
        {
            var period = mode == AgpLightMode.Steady ? (ushort)0 : periodMilliseconds;
            var response = await _agpLight.SetEffectAsync(DeviceAddress, mode, period);
            _effectMode = response.Mode;
            if (response.Mode is AgpLightMode.Blink or AgpLightMode.Breathe)
            {
                // The device turns the light on before presenting these effects.
                // Reflect that implicit power change in the master control.
                _isMasterPowerOn = true;
                UpdateMasterPowerVisual();
            }
            RememberEffectPeriod(response.Mode, response.PeriodMilliseconds);
            UpdateEffectVisual();
            timing.SetResult("success");
        }
        catch (Exception exception)
        {
            timing.SetResult($"failed:{exception.GetType().Name}");
            _diagnostics.Error("Light/Effect", $"发送特效指令失败：{DeviceName}。", exception);
            ToastService.Show(AppResources.Effect_CommandFailed);
        }
    }

    private void ApplyEffectProfile(AgpDeviceControlProfile profile)
    {
        _effectsSupported = profile.Capabilities.HasFlag(AgpCapabilities.Effects);
        _effectMode = profile.EffectMode;
        if (profile.EffectPeriodMilliseconds is { } period)
            RememberEffectPeriod(profile.EffectMode, period);
        UpdateEffectAvailability();
        UpdateEffectVisual();
    }

    private void UpdateEffectAvailability()
    {
        if (!HasEffectView)
            return;
        var enabled = IsDeviceOnline && _effectsSupported;
        SteadyEffectButton.IsEnabled = enabled;
        BlinkEffectButton.IsEnabled = enabled;
        BreatheEffectButton.IsEnabled = enabled;
    }

    private void UpdateEffectVisual()
    {
        if (!HasEffectView)
            return;
        var isDark = Application.Current?.RequestedTheme == AppTheme.Dark;
        var selectedBackground = Color.FromArgb("#3157E8");
        var inactiveBackground = Color.FromArgb(isDark ? "#263652" : "#EDF3FF");
        var inactiveText = Color.FromArgb(isDark ? "#F3F6FC" : "#17233D");

        SteadyEffectButton.BackgroundColor = _effectMode == AgpLightMode.Steady
            ? selectedBackground : inactiveBackground;
        SteadyEffectButton.TextColor = _effectMode == AgpLightMode.Steady
            ? Colors.White : inactiveText;
        BlinkEffectButton.BackgroundColor = _effectMode == AgpLightMode.Blink
            ? selectedBackground : inactiveBackground;
        BlinkEffectButton.TextColor = _effectMode == AgpLightMode.Blink
            ? Colors.White : inactiveText;
        BreatheEffectButton.BackgroundColor = _effectMode == AgpLightMode.Breathe
            ? selectedBackground : inactiveBackground;
        BreatheEffectButton.TextColor = _effectMode == AgpLightMode.Breathe
            ? Colors.White : inactiveText;
    }

    private void CancelEffectPress()
    {
        _effectPressCancellation?.Cancel();
        _effectPressCancellation?.Dispose();
        _effectPressCancellation = null;
    }

    private void LoadCustomPresets(IReadOnlyList<SavedControlPreset> savedPresets)
    {
        for (var index = 0; index < Presets.Count; index++)
        {
            var saved = savedPresets.LastOrDefault(preset => preset.Slot == index);
            if (saved is not null && (!Presets[index].IsSaved || Presets[index].ToSaved(index) != saved))
                Presets[index] = ControlPreset.FromSaved(saved);
            else if (saved is null && Presets[index].IsSaved)
                Presets[index] = ControlPreset.Empty();
        }

        ConfigureControlPage();
    }

    private void PersistCustomPresets()
    {
        if (string.IsNullOrWhiteSpace(DeviceAddress))
            return;

        _repository.SetCustomPresets(
            DeviceAddress,
            Presets
                .Select((preset, slot) => preset.IsSaved
                    ? preset.ToSaved(slot)
                    : null)
                .OfType<SavedControlPreset>());
    }

    private async void OnSavePresetClicked(object? sender, EventArgs e)
    {
        if (!IsDeviceOnline ||
            (!SupportsBrightness && !SupportsColor && !SupportsTemperature && !SupportsEffect))
            return;
        if (Presets.All(preset => preset.IsSaved))
        {
            await DisplayAlertAsync(AppResources.Preset_Full, AppResources.Preset_MaximumReached, AppResources.Common_Ok);
            return;
        }

        var name = await DisplayPromptAsync(
            AppResources.Preset_SaveCustom,
            AppResources.Preset_NamePrompt,
            accept: AppResources.Common_Save,
            cancel: AppResources.Common_Cancel,
            placeholder: AppResources.Preset_NameExample,
            maxLength: 12);

        if (string.IsNullOrWhiteSpace(name))
            return;

        var emptyIndex = Presets
            .Select((preset, index) => (preset, index))
            .First(item => !item.preset.IsSaved)
            .index;

        Presets[emptyIndex] = ControlPreset.Create(
            name.Trim(),
            SupportsColor ? ColorPicker.Hue : null,
            SupportsColor ? ColorPicker.Saturation : null,
            SupportsTemperature ? ColorPicker.Temperature : null,
            SupportsBrightness ? BrightnessSlider.Value : null,
            SupportsColor ? ColorPicker.SelectedColor : null,
            SupportsEffect ? _effectMode : null,
            SupportsEffect && _effectMode != AgpLightMode.Steady
                ? GetEffectPeriod(_effectMode)
                : (ushort?)0);
        PersistCustomPresets();
    }

    private void OnPresetPressed(object? sender, EventArgs e)
    {
        if (sender is not BindableObject { BindingContext: ControlPreset preset })
            return;

        CancelPresetPress();
        _pressedPreset = preset;
        _longPressTriggered = false;
        _suppressPresetClick = false;
        _presetPressCancellation = new CancellationTokenSource();
        _ = DetectLongPressAsync(preset, _presetPressCancellation.Token);
    }

    private void OnPresetReleased(object? sender, EventArgs e) =>
        CancelPresetPress();

    private async void OnPresetClicked(object? sender, EventArgs e)
    {
        if (!IsDeviceOnline)
            return;
        if (_longPressTriggered || _suppressPresetClick)
        {
            _longPressTriggered = false;
            _suppressPresetClick = false;
            return;
        }

        if (sender is BindableObject { BindingContext: ControlPreset preset })
            await ApplyPresetAsync(preset);
    }

    private async Task DetectLongPressAsync(ControlPreset preset, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(LongPressMilliseconds, cancellationToken);
            if (cancellationToken.IsCancellationRequested || !preset.IsSaved)
                return;

            _longPressTriggered = true;
            await ShowPresetContextMenuAsync(preset);
        }
        catch (OperationCanceledException)
        {
            // A normal short tap cancels long-press detection.
        }
    }

    private async Task ShowPresetContextMenuAsync(ControlPreset preset)
    {
        var deleteAction = AppResources.Common_Delete;
        var reorderAction = AppResources.Preset_Reorder;
        var action = await DisplayActionSheetAsync(
            preset.DisplayName,
            AppResources.Common_Cancel,
            null,
            deleteAction,
            reorderAction);

        if (action == deleteAction)
        {
            var confirmed = await DisplayAlertAsync(
                AppResources.Preset_Delete,
                Localization.Format(nameof(AppResources.Preset_DeleteConfirmation), preset.DisplayName),
                AppResources.Common_Delete,
                AppResources.Common_Cancel);

            var index = Presets.IndexOf(preset);
            if (confirmed && index >= 0)
            {
                Presets[index] = ControlPreset.Empty();
                PersistCustomPresets();
            }
        }
        else if (action == reorderAction)
        {
            SetReorderMode(true);
            await DisplayAlertAsync(
                AppResources.Preset_Reorder,
                AppResources.Preset_ReorderInstructions,
                AppResources.Preset_StartReordering);
        }
    }

    private async Task ApplyPresetAsync(ControlPreset preset)
    {
        if (!preset.IsSaved)
        {
            await DisplayAlertAsync(
                AppResources.Preset_EmptySlot,
                AppResources.Preset_SaveInstructions,
                AppResources.Common_Ok);
            return;
        }

        _isApplyingControlState = true;
        try
        {
            if (SupportsColor && preset.HasColor)
            {
                ColorPicker.Hue = preset.Hue ?? ColorPicker.Hue;
                ColorPicker.Saturation = preset.Saturation ?? ColorPicker.Saturation;
            }

            if (SupportsTemperature && preset.HasTemperature)
                ColorPicker.Temperature = preset.Temperature!.Value;

            if (SupportsBrightness && preset.HasBrightness)
                BrightnessSlider.Value = preset.Brightness!.Value;
        }
        finally
        {
            _isApplyingControlState = false;
        }

        var hasLightSettings = (SupportsColor && preset.HasColor) ||
            (SupportsTemperature && preset.HasTemperature) ||
            (SupportsBrightness && preset.HasBrightness);
        if (hasLightSettings)
        {
            // Light-model writes can reset a running effect. Send them first,
            // then send the saved effect as the final command in this restore.
            await StopPendingLightSettingsAsync();
            await SendLightSettingsAsync(CancellationToken.None, debounce: false);
        }
        else if (SupportsEffect && preset.EffectMode is not null)
        {
            // A previous slider/color change must not arrive after this effect.
            await StopPendingLightSettingsAsync();
        }

        if (SupportsEffect && preset.EffectMode is { } effectMode)
        {
            var period = preset.EffectPeriodMilliseconds ??
                GetEffectPeriod(effectMode);
            await SetEffectAsync(effectMode, period);
        }
    }

    private void OnPresetDragStarting(object? sender, DragStartingEventArgs e)
    {
        _suppressPresetClick = true;
        CancelPresetPress();

        if (sender is not BindableObject { BindingContext: ControlPreset preset } ||
            !preset.IsSaved ||
            !preset.IsReorderMode)
        {
            e.Cancel = true;
            return;
        }

        e.Data.Properties["preset"] = preset;
    }

    private void OnPresetDrop(object? sender, DropEventArgs e)
    {
        if (sender is not BindableObject { BindingContext: ControlPreset target } ||
            !e.Data.Properties.TryGetValue("preset", out var value) ||
            value is not ControlPreset source)
        {
            return;
        }

        var sourceIndex = Presets.IndexOf(source);
        var targetIndex = Presets.IndexOf(target);
        if (sourceIndex < 0 || targetIndex < 0 || sourceIndex == targetIndex)
        {
            SetReorderMode(false);
            return;
        }

        Presets[sourceIndex] = target;
        Presets[targetIndex] = source;
        PersistCustomPresets();
        SetReorderMode(false);
    }

    private void SetReorderMode(bool enabled)
    {
        foreach (var preset in Presets)
            preset.IsReorderMode = enabled;

        PresetModeLabel.Text = enabled ? AppResources.Preset_DragToReorder : AppResources.Preset_MaximumCount;
        FinishReorderButton.IsVisible = enabled;
    }

    private void OnFinishReorderClicked(object? sender, EventArgs e) =>
        SetReorderMode(false);

    private void CancelPresetPress()
    {
        _presetPressCancellation?.Cancel();
        _presetPressCancellation?.Dispose();
        _presetPressCancellation = null;
        _pressedPreset = null;
    }

    private void OnDelaySecondsFocused(object? sender, FocusEventArgs e)
    {
        Dispatcher.Dispatch(() =>
        {
            DelaySecondsEntry.CursorPosition = 0;
            DelaySecondsEntry.SelectionLength = DelaySecondsEntry.Text?.Length ?? 0;
        });
    }

    private void OnDelaySecondsTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_isReady || string.IsNullOrWhiteSpace(DeviceAddress) ||
            !int.TryParse(e.NewTextValue, out var seconds) || seconds is < 1 or > 86400)
            return;

        if (_repository.GetTimerSeconds(DeviceAddress) != seconds)
            _repository.SetTimerSeconds(DeviceAddress, seconds);
    }


    private async void OnTimerSendClicked(object? sender, EventArgs e)
    {
        if (!IsDeviceOnline || !HasTimerView || !RelativeTimerCard.IsVisible)
            return;
        if (!int.TryParse(DelaySecondsEntry.Text, out var seconds) || seconds is < 1 or > 86400)
        {
            ToastService.Show(AppResources.Timer_InvalidSeconds);
            return;
        }

        var timerMode = TimerModePicker.SelectedIndex == 1 ? "TimedOn" : "TimedOff";
        var sent = false;
        try
        {
            sent = await SendTimerCommandAsync(timerMode, seconds);
        }
        catch (Exception exception)
        {
            _diagnostics.Error(
                "Light/Timer",
                "发送计时指令失败。",
                exception);
        }

        ToastService.Show(sent ? AppResources.Timer_SendSucceeded : AppResources.Timer_SendFailed);
    }

    private async Task<bool> SendTimerCommandAsync(string timerMode, int seconds)
    {
        if (_isDemo || string.IsNullOrWhiteSpace(DeviceAddress))
            return false;

        // 延时开/关均使用 Relative Timer，分别发送 DELAY_ON_SET / DELAY_OFF_SET。
        await _agpLight.SetTimerAsync(
            DeviceAddress,
            TimeSpan.FromSeconds(seconds),
            turnOn: timerMode == "TimedOn");
        return true;
    }

    private void ApplyTimerCapabilities(AgpCapabilities capabilities)
    {
        if (!HasTimerView)
            return;
        // The schedule card is itself capability-dependent and is loaded lazily.
        // A device that only exposes RelativeTimer has no Schedule LazyView yet;
        // touching its content here would dereference a null view during the
        // first capability load.
        if (HasScheduleView)
            ScheduleSettingsCard.IsVisible = capabilities.HasFlag(AgpCapabilities.RtcTime);
        RelativeTimerCard.IsVisible = capabilities.HasFlag(AgpCapabilities.RelativeTimer);

        var wasTimedOn = TimerModePicker.SelectedIndex == 1;
        var supportsTimedOn = capabilities.HasFlag(AgpCapabilities.RelativeTimer);
        TimerModePicker.Items.Clear();
        TimerModePicker.Items.Add(AppResources.Timer_Off);
        if (supportsTimedOn)
            TimerModePicker.Items.Add(AppResources.Timer_On);
        TimerModePicker.SelectedIndex = supportsTimedOn && wasTimedOn ? 1 : 0;
    }

    private async void OnScheduleTapped(object? sender, TappedEventArgs e)
    {
        if (!IsDeviceOnline || !HasScheduleView || !ScheduleSettingsCard.IsVisible)
            return;

        await Shell.Current.GoToAsync(
            $"{nameof(SchedulePage)}?name={Uri.EscapeDataString(DeviceName)}" +
            $"&address={Uri.EscapeDataString(DeviceAddress)}");
    }

    private async void OnMasterPowerTapped(object? sender, TappedEventArgs e)
    {
        using var timing = _diagnostics.BeginBleTiming(
            "UI.Control.Power", DeviceAddress,
            $"desiredOn={!_isMasterPowerOn}; initializing={_isInitializingConnection}; isOnline={IsDeviceOnline}; isDemo={_isDemo}");
        if (!IsDeviceOnline)
        {
            timing.SetResult("ignored-not-online");
            return;
        }
        _isMasterPowerOn = !_isMasterPowerOn;
        UpdateMasterPowerVisual();

        if (!_isReady)
        {
            timing.SetResult("ignored-ui-not-ready");
            return;
        }

        if (_isDemo || string.IsNullOrWhiteSpace(DeviceAddress))
        {
            timing.SetResult("skipped");
            return;
        }

        try
        {
            timing.Mark($"提交开关操作; desiredOn={_isMasterPowerOn}; initializing={_isInitializingConnection}");
            await _agpLight.SetOnOffAsync(DeviceAddress, _isMasterPowerOn);
            timing.SetResult("success");
        }
        catch (Exception exception)
        {
            timing.SetResult($"failed:{exception.GetType().Name}");
            _diagnostics.Error(
                "Light/Power",
                $"发送总开关指令失败：{DeviceName} = {_isMasterPowerOn}。",
                exception);
        }
    }

    private void UpdateMasterPowerVisual()
    {
        if (_isMasterPowerOn)
        {
            MasterPowerPill.Background = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new(Color.FromArgb("#35C9EE"), 0),
                    new(Color.FromArgb("#3945FF"), 1)
                },
                new Point(0, 0),
                new Point(1, 0));
            MasterPowerIcon.Source = "power_white.svg";
            MasterPowerLabel.Text = AppResources.Common_TurnOff;
            MasterPowerLabel.TextColor = Colors.White;
            return;
        }

        var isDark = Application.Current?.RequestedTheme == AppTheme.Dark;
        MasterPowerPill.Background = new SolidColorBrush(
            isDark ? Color.FromArgb("#303B4D") : Color.FromArgb("#E8ECF3"));
        MasterPowerIcon.Source = "power_off.svg";
        MasterPowerLabel.Text = AppResources.Common_TurnOn;
        MasterPowerLabel.TextColor = isDark
            ? Color.FromArgb("#D9E0EC")
            : Color.FromArgb("#566174");
    }

    private void QueueLightSettingsCommand()
    {
        if (!_isReady || _isApplyingControlState || !IsDeviceOnline)
            return;

        if (_isDemo || string.IsNullOrWhiteSpace(DeviceAddress))
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
            $"debounce={debounce}; debounceMs={LightSettingsDebounce.TotalMilliseconds:0}; initializing={_isInitializingConnection}; pageKind={_controlPageKind}; colorModel={_activeColorModel}");
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

        var brightnessPercent = (byte)Math.Clamp(
            Math.Round(BrightnessSlider.Value),
            0,
            AgpModelCodec.MaximumBrightnessPercent);
        timing.Mark($"提交灯光设置; brightnessPercent={brightnessPercent}");
        try
        {
            if (_controlPageKind == AgpControlPageKind.Hsl ||
                (_controlPageKind == AgpControlPageKind.HslAndCtl &&
                 _activeColorModel != AgpColorModel.Ctl))
            {
                await _agpLight.SetHslAsync(
                    DeviceAddress,
                    brightnessPercent,
                    ColorPicker.Hue,
                    ColorPicker.Saturation,
                    cancellationToken);
            }
            else if (_controlPageKind == AgpControlPageKind.Ctl ||
                     _controlPageKind == AgpControlPageKind.HslAndCtl)
            {
                await _agpLight.SetCtlAsync(
                    DeviceAddress,
                    brightnessPercent,
                    TemperatureRatioToKelvin(ColorPicker.Temperature),
                    cancellationToken: cancellationToken);
            }
            else
            {
                await _agpLight.SetLightnessAsync(
                    DeviceAddress,
                    brightnessPercent,
                    cancellationToken);
            }
            timing.SetResult("success");
        }
        catch (Exception exception)
        {
            timing.SetResult($"failed:{exception.GetType().Name}");
            _diagnostics.Error(
                "Light/Settings",
                $"发送灯光设置失败：{DeviceName}。",
                exception);
        }
    }

    private bool SupportsBrightness => _controlPageKind != AgpControlPageKind.Lightness ||
        _supportedCapabilities.HasFlag(AgpCapabilities.Lightness) ||
        _supportedCapabilities.HasFlag(AgpCapabilities.Lightness16);

    private bool SupportsColor => _controlPageKind is
        AgpControlPageKind.Hsl or AgpControlPageKind.HslAndCtl;

    private bool SupportsTemperature => _controlPageKind is
        AgpControlPageKind.Ctl or AgpControlPageKind.HslAndCtl;

    private bool SupportsEffect => _supportedCapabilities.HasFlag(AgpCapabilities.Effects) &&
        _effectsSupported;

    private void ConfigureControlPage()
    {
        var supportsHsl = _controlPageKind is
            AgpControlPageKind.Hsl or AgpControlPageKind.HslAndCtl;
        var supportsCtl = _controlPageKind is
            AgpControlPageKind.Ctl or AgpControlPageKind.HslAndCtl;
        var supportsBrightness = SupportsBrightness;

        if (HasColorView)
        {
            ColorControlLazyView.IsVisible = supportsHsl || supportsCtl;
            ColorPicker.IsColorControlEnabled = supportsHsl;
            ColorPicker.IsTemperatureControlEnabled = supportsCtl;
        }
        if (HasTemperatureLegendView)
            TemperatureLegendLazyView.IsVisible = supportsCtl;
        ColorBrightnessSeparator.IsVisible = supportsHsl || supportsCtl;
        if (HasBrightnessView)
        {
            BrightnessControlLazyView.IsVisible = supportsBrightness;
            BrightnessControlRow.IsVisible = supportsBrightness;
        }
        var hasPresetSupport = supportsBrightness || supportsHsl || supportsCtl || SupportsEffect;
        if (HasSavePresetView)
            SavePresetLazyView.IsVisible = hasPresetSupport;
        if (HasPresetView)
        {
            PresetControlLazyView.IsVisible = hasPresetSupport;
            CustomPresetSection.IsVisible = hasPresetSupport;
        }
        BrightnessEffectSeparator.IsVisible = supportsBrightness && SupportsEffect;
        foreach (var preset in Presets)
        {
            preset.SetSupportedContent(
                supportsBrightness,
                supportsHsl,
                supportsCtl,
                SupportsEffect);
        }
        LightControlDescription.Text = _controlPageKind switch
        {
            AgpControlPageKind.Hsl => AppResources.Control_HslDescription,
            AgpControlPageKind.Ctl => AppResources.Control_CtlDescription,
            AgpControlPageKind.HslAndCtl => AppResources.Control_CombinedDescription,
            _ => AppResources.Control_BrightnessDescription
        };
    }

    private Task LoadDeferredCapabilityViewsAsync(Task blePreparation, CancellationToken token) =>
        _capabilityViewsTask ??= LoadDeferredCapabilityViewsCoreAsync(blePreparation, token);

    private async Task LoadDeferredCapabilityViewsCoreAsync(Task blePreparation, CancellationToken token)
    {
        // Give the first frame/entrance transition another drawing opportunity.
        // Legacy records without cached capabilities wait only for the profile.
        var completed = false;
        try
        {
            await WaitForFirstControlFrameAsync(token);
            await YieldControlFrameAsync(token);
            if (!_hasKnownCapabilities) await blePreparation;
            token.ThrowIfCancellationRequested();
            if (!_hasKnownCapabilities) return;
            await LoadCapabilityViewsAsync(token);
            completed = true;
        }
        finally
        {
            if (!completed && _controlPageAppearanceCancellation?.Token == token)
                _capabilityViewsTask = null;
        }
    }

    private async Task ObserveCapabilityViewsRetryAsync(CancellationToken token)
    {
        try { await LoadDeferredCapabilityViewsAsync(Task.CompletedTask, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { _diagnostics.Error("UI/Control", "能力控件补充加载失败。", exception); }
    }

    private void ApplyCachedCapabilities(AgpCapabilities capabilities)
    {
        _hasKnownCapabilities = true;
        _supportedCapabilities = capabilities;
        _controlPageKind = new AgpDeviceControlProfile(
            capabilities, null, _ctlRange).PageKind;
        _effectsSupported = capabilities.HasFlag(AgpCapabilities.Effects);
        _activeColorModel = _controlPageKind == AgpControlPageKind.Ctl
            ? AgpColorModel.Ctl
            : AgpColorModel.Hsl;
    }

    private async Task LoadCapabilityViewsAsync(CancellationToken token)
    {
        using var timing = _diagnostics.BeginBleTiming(
            "UI.Control.LoadCapabilityViews",
            DeviceAddress,
            $"pageKind={_controlPageKind}; capabilities=0x{(ulong)_supportedCapabilities:X16}");
        var supportsHsl = _controlPageKind is
            AgpControlPageKind.Hsl or AgpControlPageKind.HslAndCtl;
        var supportsCtl = _controlPageKind is
            AgpControlPageKind.Ctl or AgpControlPageKind.HslAndCtl;
        var supportsBrightness = SupportsBrightness;
        var hasPresetSupport = supportsBrightness || supportsHsl || supportsCtl || SupportsEffect;

        if (supportsHsl || supportsCtl)
        {
            token.ThrowIfCancellationRequested();
            var view = await LoadLazyViewAsync(ColorControlLazyView);
            view.SelectionChanged -= OnColorPickerSelectionChanged;
            view.SelectionChanged += OnColorPickerSelectionChanged;
            ColorControlLazyView.IsVisible = true;
            ConfigureControlPage();
            ApplyPendingProfileToLoadedViews(applyBrightness: false);
            timing.Mark("HSL/CTL LazyView loaded");
            await YieldControlFrameAsync(token);
        }

        if (supportsCtl)
        {
            await LoadLazyViewAsync(TemperatureLegendLazyView);
            TemperatureLegendLazyView.IsVisible = true;
            timing.Mark("CTL legend LazyView loaded");
            await YieldControlFrameAsync(token);
        }

        if (supportsBrightness)
        {
            var view = await LoadLazyViewAsync(BrightnessControlLazyView);
            view.BrightnessChanged -= OnBrightnessChanged;
            view.BrightnessChanged += OnBrightnessChanged;
            BrightnessControlLazyView.IsVisible = true;
            if (_pendingLightState is { } state) ApplyLightState(state);
            ApplyPendingProfileToLoadedViews(applyColor: false);
            timing.Mark("brightness LazyView loaded");
            await YieldControlFrameAsync(token);
        }

        if (SupportsEffect)
        {
            var view = await LoadLazyViewAsync(EffectControlLazyView);
            view.EffectClicked -= OnEffectClicked;
            view.EffectPressed -= OnEffectPressed;
            view.EffectReleased -= OnEffectReleased;
            view.EffectClicked += OnEffectClicked;
            view.EffectPressed += OnEffectPressed;
            view.EffectReleased += OnEffectReleased;
            EffectControlLazyView.IsVisible = true;
            UpdateEffectAvailability();
            UpdateEffectVisual();
            timing.Mark("effects LazyView loaded");
            await YieldControlFrameAsync(token);
        }

        if (hasPresetSupport)
        {
            var saveView = await LoadLazyViewAsync(SavePresetLazyView);
            saveView.SaveClicked -= OnSavePresetClicked;
            saveView.SaveClicked += OnSavePresetClicked;
            SavePresetLazyView.IsVisible = true;
            timing.Mark("save preset LazyView loaded");
            await YieldControlFrameAsync(token);

            var presetView = await LoadLazyViewAsync(PresetControlLazyView);
            presetView.FinishReorderClicked -= OnFinishReorderClicked;
            presetView.PresetClicked -= OnPresetClicked;
            presetView.PresetPressed -= OnPresetPressed;
            presetView.PresetReleased -= OnPresetReleased;
            presetView.PresetDragStarting -= OnPresetDragStarting;
            presetView.PresetDrop -= OnPresetDrop;
            presetView.FinishReorderClicked += OnFinishReorderClicked;
            presetView.PresetClicked += OnPresetClicked;
            presetView.PresetPressed += OnPresetPressed;
            presetView.PresetReleased += OnPresetReleased;
            presetView.PresetDragStarting += OnPresetDragStarting;
            presetView.PresetDrop += OnPresetDrop;
            PresetControlLazyView.IsVisible = true;
            timing.Mark("preset LazyView loaded");
            await YieldControlFrameAsync(token);
        }

        if (_supportedCapabilities.HasFlag(AgpCapabilities.RelativeTimer))
        {
            var view = await LoadLazyViewAsync(TimerControlLazyView);
            view.DelayFocused -= OnDelaySecondsFocused;
            view.DelayTextChanged -= OnDelaySecondsTextChanged;
            view.SendClicked -= OnTimerSendClicked;
            view.DelayFocused += OnDelaySecondsFocused;
            view.DelayTextChanged += OnDelaySecondsTextChanged;
            view.SendClicked += OnTimerSendClicked;
            view.TimerModePicker.SelectedIndex = 0;
            view.DelaySecondsEntry.Text = _pendingTimerSeconds.ToString();
            TimerControlLazyView.IsVisible = true;
            timing.Mark("timer LazyView loaded");
            await YieldControlFrameAsync(token);
        }

        if (_supportedCapabilities.HasFlag(AgpCapabilities.RtcTime))
        {
            var view = await LoadLazyViewAsync(ScheduleControlLazyView);
            view.Tapped -= OnScheduleTapped;
            view.Tapped += OnScheduleTapped;
            ScheduleControlLazyView.IsVisible = true;
            timing.Mark("schedule LazyView loaded");
            await YieldControlFrameAsync(token);
        }

        ConfigureControlPage();
        ApplyTimerCapabilities(_supportedCapabilities);
        UpdateEffectAvailability();
        UpdateEffectVisual();
        timing.SetResult("capability views ready");
    }

    private async Task<T> LoadLazyViewAsync<T>(LazyView<T> lazyView)
        where T : View, new()
    {
        if (!lazyView.HasLazyViewLoaded)
        {
#if DEBUG && ANDROID
            var ownsTiming = ControlNavigationTiming.Current is null;
            var observer = ControlNavigationTiming.Current ??
                ControlNavigationTiming.StartLazyLoad(_diagnostics, DeviceAddress);
            var creationStarted = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
            lazyView.BindingContext = this;
            await lazyView.LoadViewAsync();
#if DEBUG && ANDROID
            observer.RecordCreation(typeof(T).Name, creationStarted);
            if (ownsTiming) _ = FinishLazyTimingAsync(observer);
#endif
        }

        return (T)lazyView.Content!;
    }

#if DEBUG && ANDROID
    private async Task FinishLazyTimingAsync(ControlNavigationTiming observer)
    {
        try
        {
            await YieldControlFrameAsync(_controlPageAppearanceCancellation?.Token ?? new CancellationToken(true));
        }
        catch (OperationCanceledException) { }
        finally { observer.Dispose(); }
    }
#endif

    private bool HasEffectSettingsOverlay => HasEffectSettingsView;

    private async Task EnsureEffectSettingsOverlayLoadedAsync(string title, ushort period)
    {
        if (!HasEffectSettingsView)
            await LoadLazyViewAsync(EffectSettingsLazyView);

        EffectSettingsTitle.Text = title;
        EffectSettingsPeriodSlider.Value = period;
        EffectSettingsPeriodLabel.Text = FormatEffectPeriod(period);
    }

    private void SetEffectSettingsOverlayVisible(bool visible) =>
        EffectSettingsLazyView.IsVisible = visible;

    private void ApplyControlProfile(AgpDeviceControlProfile profile)
    {
        _hasKnownCapabilities = true;
        _pendingControlProfile = profile;
        _supportedCapabilities = profile.Capabilities;
        _controlPageKind = profile.PageKind;
        ApplyEffectProfile(profile);
        _ctlRange = profile.CtlRange;
        ConfigureControlPage();
        ApplyTimerCapabilities(_supportedCapabilities);
        ApplyPendingProfileToLoadedViews();
    }

    private void ApplyPendingProfileToLoadedViews(bool applyBrightness = true, bool applyColor = true)
    {
        if (_pendingControlProfile is not { } profile) return;

        if (profile.ColorState is null)
        {
            _activeColorModel = _controlPageKind == AgpControlPageKind.Ctl
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
                    if (applyBrightness && HasBrightnessView)
                        BrightnessSlider.Value =
                            AgpColorModelCodec.Lightness16ToBrightnessPercent(
                                hsl.Present.Lightness);
                    if (applyColor && HasColorView)
                    {
                        ColorPicker.Hue = hsl.Present.Hue / (double)ushort.MaxValue;
                        ColorPicker.Saturation =
                            hsl.Present.Saturation / (double)ushort.MaxValue;
                    }
                    break;

                case AgpCtlState ctl:
                    _activeColorModel = ctl.ActiveModel == AgpColorModel.None
                        ? AgpColorModel.Ctl
                        : ctl.ActiveModel;
                    if (applyBrightness && HasBrightnessView)
                        BrightnessSlider.Value =
                            AgpColorModelCodec.Lightness16ToBrightnessPercent(
                                ctl.Present.Lightness);
                    if (applyColor && HasColorView)
                        ColorPicker.Temperature =
                            KelvinToTemperatureRatio(ctl.Present.TemperatureKelvin);
                    break;

                case AgpLightness16State lightness:
                    if (applyBrightness && HasBrightnessView)
                        BrightnessSlider.Value =
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

public sealed class ControlPreset : INotifyPropertyChanged
{
    private bool _isReorderMode;
    private bool _supportsBrightness = true;
    private bool _supportsColor = true;
    private bool _supportsTemperature = true;
    private bool _supportsEffect = true;

    private ControlPreset(
        string displayName,
        bool isSaved,
        double? hue,
        double? saturation,
        double? temperature,
        double? brightness,
        Color? lightColor,
        AgpLightMode? effectMode,
        ushort? effectPeriodMilliseconds)
    {
        DisplayName = displayName;
        IsSaved = isSaved;
        Hue = hue;
        Saturation = saturation;
        Temperature = temperature;
        Brightness = brightness;
        LightColor = lightColor;
        EffectMode = effectMode;
        EffectPeriodMilliseconds = effectPeriodMilliseconds;
    }

    public string DisplayName { get; }
    public bool IsSaved { get; }
    public double? Hue { get; }
    public double? Saturation { get; }
    public double? Temperature { get; }
    public double? Brightness { get; }
    public Color? LightColor { get; }
    public AgpLightMode? EffectMode { get; }
    public ushort? EffectPeriodMilliseconds { get; }

    public bool HasColor => _supportsColor && LightColor is not null;
    public bool HasTemperature => _supportsTemperature && Temperature is not null;
    public bool HasBrightness => _supportsBrightness && Brightness is not null;
    public bool HasEffect => _supportsEffect && EffectMode is
        AgpLightMode.Blink or AgpLightMode.Breathe;

    public bool IsReorderMode
    {
        get => _isReorderMode;
        set
        {
            if (_isReorderMode == value)
                return;

            _isReorderMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DetailText));
        }
    }

    public string DetailText =>
        IsReorderMode
            ? AppResources.Preset_Drag
            : !IsSaved
                ? AppResources.Preset_TapToView
                : HasEffect
                    ? EffectName(EffectMode!.Value)
                    : HasBrightness || HasTemperature
                        ? FormatLevels()
                        : ToRgbHex(PreviewColor);

    public Color PreviewColor =>
        !IsSaved
            ? Color.FromArgb("#DDE3ED")
            : HasColor
                ? LightColor!
                : CreateTemperatureColor(
                    HasTemperature ? Temperature : null,
                    HasBrightness ? Brightness : null);

    public static ControlPreset Create(
        string name,
        double hue,
        double saturation,
        double temperature,
        double brightness) =>
        new(
            name,
            true,
            hue,
            saturation,
            temperature,
            brightness,
            Color.FromHsla(hue, saturation, 0.52),
            null,
            null);

    public static ControlPreset Create(
        string name,
        double? hue,
        double? saturation,
        double? temperature,
        double? brightness,
        Color? lightColor,
        AgpLightMode? effectMode,
        ushort? effectPeriodMilliseconds) =>
        new(
            name,
            true,
            hue,
            saturation,
            temperature,
            brightness,
            lightColor,
            effectMode,
            effectPeriodMilliseconds);

    public static ControlPreset Empty() => new(
        AppResources.Preset_Empty, false, null, null, null, null, null, null, null);

    internal SavedControlPreset ToSaved(int slot) => new(
        slot,
        DisplayName,
        Hue,
        Saturation,
        Temperature,
        Brightness,
        HasColor ? ToRgbHex(LightColor!) : null,
        EffectMode,
        EffectPeriodMilliseconds);

    internal static ControlPreset FromSaved(SavedControlPreset saved)
    {
        Color? lightColor = null;
        if (!string.IsNullOrWhiteSpace(saved.LightColorHex))
        {
            try
            {
                lightColor = Color.FromArgb(saved.LightColorHex);
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException)
            {
                // Keep the remaining preset values when an older color value is invalid.
            }
        }

        return new(
            string.IsNullOrWhiteSpace(saved.DisplayName) ? AppResources.Preset_Unnamed : saved.DisplayName,
            true,
            saved.Hue,
            saved.Saturation,
            saved.Temperature,
            saved.Brightness,
            lightColor,
            saved.EffectMode,
            saved.EffectPeriodMilliseconds);
    }

    internal void SetSupportedContent(
        bool supportsBrightness,
        bool supportsColor,
        bool supportsTemperature,
        bool supportsEffect)
    {
        if (_supportsBrightness == supportsBrightness && _supportsColor == supportsColor &&
            _supportsTemperature == supportsTemperature && _supportsEffect == supportsEffect)
            return;
        _supportsBrightness = supportsBrightness;
        _supportsColor = supportsColor;
        _supportsTemperature = supportsTemperature;
        _supportsEffect = supportsEffect;
        OnPropertyChanged(nameof(DetailText));
        OnPropertyChanged(nameof(PreviewColor));
    }

    private string FormatLevels()
    {
        var levels = new List<string>(2);
        if (HasBrightness)
            levels.Add($"{Brightness!.Value:0}%");
        if (HasTemperature)
            levels.Add($"{Temperature!.Value * 100:0}%");
        return string.Join('/', levels);
    }

    private static string EffectName(AgpLightMode mode) => mode switch
    {
        AgpLightMode.Blink => AppResources.Effect_Blink,
        AgpLightMode.Breathe => AppResources.Effect_Breathe,
        _ => AppResources.Effect_Steady
    };

    private static Color CreateTemperatureColor(double? temperature, double? brightness)
    {
        // The picker exposes a normalized cool-to-warm range. Map it to the
        // common 6500 K..2700 K lighting range before applying the standard
        // Kelvin-to-RGB approximation.
        var kelvin = 6500 - Math.Clamp(temperature ?? 0.5, 0, 1) * (6500 - 2700);
        var temperatureHundreds = kelvin / 100;
        var red = temperatureHundreds <= 66
            ? 255
            : 329.698727446 * Math.Pow(temperatureHundreds - 60, -0.1332047592);
        var green = temperatureHundreds <= 66
            ? 99.4708025861 * Math.Log(Math.Max(temperatureHundreds, 1)) - 161.1195681661
            : 288.1221695283 * Math.Pow(temperatureHundreds - 60, -0.0755148492);
        var blue = temperatureHundreds >= 66
            ? 255
            : temperatureHundreds <= 19
                ? 0
                : 138.5177312231 * Math.Log(temperatureHundreds - 10) - 305.0447927307;
        var level = Math.Clamp(brightness ?? 50, 1, 100) / 100;
        var whiteMix = 0.15 + level * 0.75;
        var intensity = 0.65 + level * 0.35;

        return Color.FromRgb(
            (float)Math.Clamp((red / 255 * (1 - whiteMix) + whiteMix) * intensity, 0, 1),
            (float)Math.Clamp((green / 255 * (1 - whiteMix) + whiteMix) * intensity, 0, 1),
            (float)Math.Clamp((blue / 255 * (1 - whiteMix) + whiteMix) * intensity, 0, 1));
    }

    private static string ToRgbHex(Color color) =>
        $"#{ToByte(color.Red):X2}{ToByte(color.Green):X2}{ToByte(color.Blue):X2}";

    private static int ToByte(float value) =>
        (int)Math.Round(Math.Clamp(value, 0, 1) * 255);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
