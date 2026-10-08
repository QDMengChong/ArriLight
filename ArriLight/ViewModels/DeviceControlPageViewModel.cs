using System.Collections.ObjectModel;
using ArriOpto.BLE.Agp;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ArriLight.ViewModels;

public partial class DeviceControlPageViewModel : ViewModelBase
{
    private static readonly TimeSpan LightSettingsDebounce = TimeSpan.FromMilliseconds(250);

    private readonly DeviceRepository _repository;
    private readonly BluetoothDiscoveryService _discoveryService;
    private readonly BluetoothConnectionService _connectionService;
    private readonly AgpLightControlService _agpLight;
    private readonly AppDiagnosticService _diagnostics;
    private AgpLightState? _pendingLightState;
    private int _pendingTimerSeconds = 60;
    [ObservableProperty]
    private bool _hasKnownCapabilities;
    private AgpDeviceControlProfile? _pendingControlProfile;
    // 能力相关区域使用 LazyView；内部视图只在能力确认后加载。
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SupportsBrightness)), NotifyPropertyChangedFor(nameof(SupportsColor)), NotifyPropertyChangedFor(nameof(SupportsTemperature))]
    private AgpControlPageKind _controlPageKind;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SupportsBrightness)), NotifyPropertyChangedFor(nameof(SupportsEffect)),
        NotifyPropertyChangedFor(nameof(SupportsTurnOnDelay)), NotifyPropertyChangedFor(nameof(SupportsLocalMode)),
        NotifyPropertyChangedFor(nameof(CanSetLocalMode)), NotifyPropertyChangedFor(nameof(CanEditDelaySeconds))]
    private AgpCapabilities _supportedCapabilities = AgpCapabilities.Lightness;

    [ObservableProperty]
    private string _deviceName = AppResources.Device_Management;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanRefreshStatus))]
    private string _deviceAddress = string.Empty;
    [ObservableProperty]
    private string _connectionStatusText = AppResources.Ble_NotYetConnected;
    [ObservableProperty]
    private string _connectionButtonText = AppResources.Ble_Connect;
    [ObservableProperty]
    private PresentationColor _connectionStatusColor = PresentationColor.FromArgb("#A9B2C3");
    [ObservableProperty]
    private bool _canConnect = true;
    private bool _isReady;
    private bool _isControlPageVisible;
    private int _lightingEditVersion;
    private int _appearanceLightingEditVersion;
    private int _appearanceDelayEditVersion;
    private bool _hasAppliedRuntimeEffect;
    private AgpLightMode _lastAppliedRuntimeEffectMode;
    private ushort? _lastAppliedRuntimeEffectPeriod;
    private Guid? _lastUnknownToastOperation;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanRefreshStatus)), NotifyPropertyChangedFor(nameof(SupportsLocalMode)),
        NotifyPropertyChangedFor(nameof(CanSetLocalMode)), NotifyPropertyChangedFor(nameof(CanEditDelaySeconds))]
    private bool _isDemo;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ControlOpacity)),
        NotifyPropertyChangedFor(nameof(CanSetLocalMode)), NotifyPropertyChangedFor(nameof(CanEditDelaySeconds))]
    private bool _isDeviceOnline;
    [ObservableProperty]
    private bool _isMasterPowerOn = true;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanTogglePower))]
    private bool _isPowerChanging;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanTogglePower))]
    private bool _hasPendingUserOperation;
    public bool CanTogglePower => !IsPowerChanging && !HasPendingUserOperation;
    private CancellationTokenSource? _lightSettingsCancellation;
    private Task? _lightSettingsTask;
    [ObservableProperty]
    private AgpLightMode _effectMode = AgpLightMode.Steady;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SupportsEffect))]
    private bool _effectsSupported = true;
    private AgpColorModel _activeColorModel;
    private AgpCtlRange _ctlRange = new(
        AgpColorModelCodec.MinimumCtlTemperatureKelvin,
        AgpColorModelCodec.MaximumCtlTemperatureKelvin);
    private bool _isApplyingControlState;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanRefreshStatus))]
    private bool _isInitializingConnection;
    private CancellationTokenSource? _connectionInitializationCancellation;
    private Task? _connectionInitializationTask;

    public bool CanRefreshStatus =>
        !IsDemo && !AutomaticConnectionPending && !IsInitializingConnection &&
        !string.IsNullOrWhiteSpace(DeviceAddress);

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

    private readonly IUiDispatcher _dispatcher;
    private readonly IAppNavigator _navigator;
    private readonly IUserInteraction _interaction;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanRefreshStatus))]
    private bool _automaticConnectionPending;
    private double _brightness = 75;
    [ObservableProperty] private double _hue = 0.08;
    [ObservableProperty] private double _saturation = 0.78;
    [ObservableProperty] private double _temperature = 0.32;
    [ObservableProperty] private string _timerSecondsText = "60";
    [ObservableProperty] private int _timerModeIndex;
    [ObservableProperty] private string _turnOnDelaySecondsText = "60";
    [ObservableProperty] private bool _isReorderMode;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanEditDelaySeconds))] private bool _isTurnOnDelayEnabled;
    private int _pendingTurnOnDelaySeconds = 60;
    private int _powerCommandVersion;
    private BlinkEffectParameters _blinkEffectParameters = new();
    private BreatheEffectParameters _breatheEffectParameters = new();
    public event Action? RenderRequested;
    public event Action? CapabilityViewsRetryRequested;
    public AgpLightState? PendingLightState => _pendingLightState;
    public int PendingTimerSeconds => _pendingTimerSeconds;
    public int PendingTurnOnDelaySeconds => _pendingTurnOnDelaySeconds;
    public PresentationColor SelectedColor => PresentationColor.FromHsla(Hue, Saturation, 0.52);

    // Match the native slider's range even before its LazyView is created.
    public double Brightness
    {
        get => _brightness;
        set => SetProperty(ref _brightness, Math.Clamp(value, 1, 100));
    }

    public DeviceControlPageViewModel(DeviceRepository repository, BluetoothDiscoveryService discoveryService,
        BluetoothConnectionService connectionService, AgpLightControlService agpLight,
        AppDiagnosticService diagnostics, IUiDispatcher dispatcher, IAppNavigator navigator, IUserInteraction interaction)
    {
        _repository = repository; _discoveryService = discoveryService; _connectionService = connectionService;
        _agpLight = agpLight; _diagnostics = diagnostics; _dispatcher = dispatcher;
        _navigator = navigator; _interaction = interaction; _isReady = true;
    }

    public void ConfigureInitialKind(AgpControlPageKind kind)
    {
        ControlPageKind = kind;
        SupportedCapabilities = kind switch
        {
            AgpControlPageKind.Hsl => AgpCapabilities.Hsl | AgpCapabilities.Lightness16,
            AgpControlPageKind.Ctl => AgpCapabilities.Ctl | AgpCapabilities.Lightness16,
            AgpControlPageKind.HslAndCtl => AgpCapabilities.Hsl | AgpCapabilities.Ctl | AgpCapabilities.Lightness16,
            _ => AgpCapabilities.Lightness
        };
        _activeColorModel = kind == AgpControlPageKind.Ctl ? AgpColorModel.Ctl : AgpColorModel.Hsl;
    }

    public void UpdateLightingInput(double hue, double saturation, double temperature, double brightness,
        AgpColorModel? colorModel = null)
    {
        _lightingEditVersion++;
        Hue = hue; Saturation = saturation; Temperature = temperature; Brightness = brightness;
        if (colorModel is { } model) _activeColorModel = model;
        QueueLightSettingsCommand();
    }

    partial void OnIsTurnOnDelayEnabledChanged(bool value)
    {
        if (_isReady && !string.IsNullOrWhiteSpace(DeviceAddress))
            _repository.SetTurnOnDelayEnabled(DeviceAddress, value);
    }
    partial void OnTurnOnDelaySecondsTextChanged(string value)
    {
        if (!_isApplyingLocalMode) _delaySecondsEditVersion++;
        SaveTurnOnDelaySeconds(value);
    }
    partial void OnTimerSecondsTextChanged(string value) => SaveTimerSeconds(value);
    private void SaveTimerSeconds(string value)
    {
        if (_isReady && !string.IsNullOrWhiteSpace(DeviceAddress) &&
            int.TryParse(value, out var seconds) && seconds is >= 1 and <= 86400 &&
            _repository.GetTimerSeconds(DeviceAddress) != seconds)
            _repository.SetTimerSeconds(DeviceAddress, seconds);
    }
    private void SaveTurnOnDelaySeconds(string value)
    {
        if (!_isReady || string.IsNullOrWhiteSpace(DeviceAddress) ||
            !int.TryParse(value, out var seconds) || seconds is < 1 or > 86400) return;
        _pendingTurnOnDelaySeconds = seconds;
        if (_repository.GetTurnOnDelaySeconds(DeviceAddress) != seconds)
            _repository.SetTurnOnDelaySeconds(DeviceAddress, seconds);
    }
    private void ConfigureControlPage()
    {
        foreach (var preset in Presets)
            preset.SetSupportedContent(SupportsBrightness, SupportsColor, SupportsTemperature, SupportsEffect);
        RenderRequested?.Invoke();
    }
    private void UpdateMasterPowerVisual() => RenderRequested?.Invoke();
    private void UpdateEffectVisual() => RenderRequested?.Invoke();
    private void UpdateEffectAvailability() => RenderRequested?.Invoke();

    public override Task OnAppearingAsync()
    {
        _isControlPageVisible = true;
        _appearanceLightingEditVersion = _lightingEditVersion;
        _hasAppliedRuntimeEffect = false;
        using var timing = _diagnostics.BeginBleTiming(
            "UI.Control.Appearing", DeviceAddress);

        _connectionService.StateChanged -= OnConnectionStateChanged;
        _connectionService.StateChanged += OnConnectionStateChanged;
        _agpLight.DeviceStatusChanged -= OnControlDeviceStatusChanged;
        _agpLight.DeviceStatusChanged += OnControlDeviceStatusChanged;
        _agpLight.RuntimeStateChanged -= OnControlRuntimeStateChanged;
        _agpLight.RuntimeStateChanged += OnControlRuntimeStateChanged;
        ResetLocalModeState();

        if (!string.IsNullOrWhiteSpace(DeviceAddress) &&
            _repository.GetByAddress(DeviceAddress) is { } device)
        {
            _pendingTimerSeconds = device.TimerSeconds is >= 1 and <= 86400
                ? device.TimerSeconds : 60;
            LoadTurnOnDelaySettings();
            _appearanceDelayEditVersion = _delaySecondsEditVersion;
            IsDemo = device.IsDemo;
            timing.Mark($"载入控制页; isDemo={IsDemo}");
            OnPropertyChanged(nameof(CanRefreshStatus));
            DeviceName = device.DisplayName;
            LoadEffectParameters(device);
            LoadCustomPresets(device.CustomPresets ?? []);

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
                HasKnownCapabilities = true;
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
                if (_agpLight.GetRuntimeSnapshot(DeviceAddress) is { } runtime)
                {
                    ApplyRuntimeSnapshot(runtime);
                    _agpLight.RequestRuntimePreparation(DeviceAddress);
                }
                else
                {
                    ConnectionStatusText = AppResources.Ble_PreparingDeviceConnection;
                    CanConnect = false;
                }
            }
            timing.SetResult(device.IsDemo ? "demo" : "page-prepared");
        }
        else
        {
            timing.SetResult("device-not-found");
        }

        TimerSecondsText = _pendingTimerSeconds.ToString();
        return Task.CompletedTask;
    }

    public override void OnDisappearing()
    {
        _isControlPageVisible = false;
        CancelLocalModeOperations();
        _connectionInitializationCancellation?.Cancel();

        _connectionService.StateChanged -= OnConnectionStateChanged;
        _agpLight.DeviceStatusChanged -= OnControlDeviceStatusChanged;
        _agpLight.RuntimeStateChanged -= OnControlRuntimeStateChanged;

        _lightSettingsCancellation?.Cancel();
        _lightSettingsCancellation?.Dispose();
        _lightSettingsCancellation = null;
        _lightSettingsTask = null;

    }

    public override void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("name", out var value) && value is not null)
            DeviceName = Uri.UnescapeDataString(value.ToString()!);

        if (query.TryGetValue("address", out var address) && address is not null)
            DeviceAddress = Uri.UnescapeDataString(address.ToString()!);
    }

    [RelayCommand]
    private async Task MoreAsync()
    {
        if (string.IsNullOrWhiteSpace(DeviceAddress))
            return;

        await _navigator.GoToAsync(
            $"DeviceMorePage?address={Uri.EscapeDataString(DeviceAddress)}");
    }

    public async Task EnsureConnectedAsync(
        bool showError, bool refreshStatus = false, CancellationToken cancellationToken = default,
        bool scanAlreadyStopped = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsInitializingConnection &&
            _connectionInitializationCancellation?.IsCancellationRequested == true &&
            _connectionInitializationTask is { } previousInitialization)
        {
            await previousInitialization;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(DeviceAddress) || IsInitializingConnection)
            return;

        var initialization = InitializeControlSessionAsync(
            showError, refreshStatus, cancellationToken, scanAlreadyStopped);
        _connectionInitializationTask = initialization;
        await initialization;
    }

    [RelayCommand]
    private async Task ScheduleAsync()
    {
        if (!IsDeviceOnline || !SupportedCapabilities.HasFlag(AgpCapabilities.RtcTime))
            return;

        await _navigator.GoToAsync(
            $"SchedulePage?name={Uri.EscapeDataString(DeviceName)}" +
            $"&address={Uri.EscapeDataString(DeviceAddress)}");
    }

    public bool SupportsBrightness => ControlPageKind != AgpControlPageKind.Lightness ||
        SupportedCapabilities.HasFlag(AgpCapabilities.Lightness) ||
        SupportedCapabilities.HasFlag(AgpCapabilities.Lightness16);

    public bool SupportsColor => ControlPageKind is
        AgpControlPageKind.Hsl or AgpControlPageKind.HslAndCtl;

    public bool SupportsTemperature => ControlPageKind is
        AgpControlPageKind.Ctl or AgpControlPageKind.HslAndCtl;

    public bool SupportsEffect => SupportedCapabilities.HasFlag(AgpCapabilities.Effects) &&
        EffectsSupported;

}
