using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ArriLight;

public partial class DeviceControlPage : ContentPage, IQueryAttributable
{
    private const int LongPressMilliseconds = 650;
    private const double ColorSaturationThreshold = 0.05;
    private static readonly TimeSpan LightSettingsDebounce = TimeSpan.FromMilliseconds(250);

    private readonly DeviceRepository _repository;
    private readonly BluetoothConnectionService _connectionService;
    private readonly MeshLightControlService _meshLight;
    private readonly AppDiagnosticService _diagnostics;

    private string _deviceName = "设备管理";
    private string _deviceAddress = string.Empty;
    private string _connectionStatusText = "尚未连接";
    private string _connectionButtonText = "连接";
    private Color _connectionStatusColor = Color.FromArgb("#A9B2C3");
    private bool _canConnect = true;
    private bool _isReady;
    private bool _isDemo;
    private bool _isMasterPowerOn = true;
    private bool _longPressTriggered;
    private bool _suppressPresetClick;
    private CancellationTokenSource? _presetPressCancellation;
    private CancellationTokenSource? _lightSettingsCancellation;
    private ControlPreset? _pressedPreset;

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
        private set => _deviceAddress = value;
    }

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


    public ObservableCollection<ControlPreset> Presets { get; } =
    [
        ControlPreset.Create("观影", 0.08, 0.82, 0.30, 35),
        ControlPreset.Create("阅读", 0.12, 0.30, 0.62, 82),
        ControlPreset.Empty(),
        ControlPreset.Empty(),
        ControlPreset.Empty(),
        ControlPreset.Empty()
    ];

    public DeviceControlPage()
    {
        InitializeComponent();
        _repository = ServiceHelper.GetRequiredService<DeviceRepository>();
        _connectionService =
            ServiceHelper.GetRequiredService<BluetoothConnectionService>();
        _meshLight = ServiceHelper.GetRequiredService<MeshLightControlService>();
        _diagnostics = ServiceHelper.GetRequiredService<AppDiagnosticService>();
        BindingContext = this;
        if (TimerModePicker.SelectedIndex < 0)
            TimerModePicker.SelectedIndex = 0;
        UpdateMasterPowerVisual();
        _isReady = true;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (Application.Current is not null)
            Application.Current.RequestedThemeChanged += OnRequestedThemeChanged;
        _connectionService.StateChanged += OnConnectionStateChanged;

        if (!string.IsNullOrWhiteSpace(DeviceAddress) &&
            _repository.GetByAddress(DeviceAddress) is { } device)
        {
            _isDemo = device.IsDemo;
            DeviceName = device.Name;
            if (device.IsDemo)
            {
                ConnectionStatusText = "演示设备不执行真实蓝牙连接";
                ConnectionButtonText = "演示";
                CanConnect = false;
            }
            else
            {
                ApplyConnectionSnapshot(_connectionService.Snapshot);
                _ = EnsureConnectedAsync(showError: false);
            }
        }
    }

    protected override void OnDisappearing()
    {
        if (Application.Current is not null)
            Application.Current.RequestedThemeChanged -= OnRequestedThemeChanged;
        _connectionService.StateChanged -= OnConnectionStateChanged;

        CancelPresetPress();
        _lightSettingsCancellation?.Cancel();
        _lightSettingsCancellation?.Dispose();
        _lightSettingsCancellation = null;
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

    private async Task EnsureConnectedAsync(bool showError)
    {
        if (string.IsNullOrWhiteSpace(DeviceAddress) || !CanConnect)
            return;

        try
        {
            await _connectionService.ConnectAsync(DeviceAddress);
        }
        catch (Exception exception)
        {
            _diagnostics.Error(
                "BLE/Connection",
                $"设备控制页连接失败：{DeviceAddress}。",
                exception);
            if (showError)
            {
                await DisplayAlertAsync(
                    "连接失败",
                    exception.Message,
                    "知道了");
            }
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
            BluetoothSessionState.Scanning => "搜索中",
            BluetoothSessionState.Connecting => "连接中",
            BluetoothSessionState.Connected => "已连接",
            BluetoothSessionState.Updating => "升级中",
            BluetoothSessionState.Error => "重试",
            _ => "连接"
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
    }

    private void OnRequestedThemeChanged(object? sender, AppThemeChangedEventArgs e)
    {
        UpdateMasterPowerVisual();
    }

    private void OnColorPickerSelectionChanged(object? sender, EventArgs e) =>
        QueueLightSettingsCommand();

    private void OnBrightnessChanged(object? sender, ValueChangedEventArgs e) =>
        QueueLightSettingsCommand();

    private async void OnSavePresetClicked(object? sender, EventArgs e)
    {
        if (Presets.All(preset => preset.IsSaved))
        {
            await DisplayAlertAsync("配置已满", "最多可以保存 6 个自定义配置。", "知道了");
            return;
        }

        var name = await DisplayPromptAsync(
            "保存自定义配置",
            "为当前灯光设置命名",
            accept: "保存",
            cancel: "取消",
            placeholder: "例如：晚间阅读",
            maxLength: 12);

        if (string.IsNullOrWhiteSpace(name))
            return;

        var emptyIndex = Presets
            .Select((preset, index) => (preset, index))
            .First(item => !item.preset.IsSaved)
            .index;

        Presets[emptyIndex] = ControlPreset.Create(
            name.Trim(),
            ColorPicker.Hue,
            ColorPicker.Saturation,
            ColorPicker.Temperature,
            BrightnessSlider.Value);
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
        var action = await DisplayActionSheetAsync(
            preset.DisplayName,
            "取消",
            null,
            "删除",
            "调整位置");

        if (action == "删除")
        {
            var confirmed = await DisplayAlertAsync(
                "删除配置",
                $"确定删除“{preset.DisplayName}”吗？",
                "删除",
                "取消");

            var index = Presets.IndexOf(preset);
            if (confirmed && index >= 0)
                Presets[index] = ControlPreset.Empty();
        }
        else if (action == "调整位置")
        {
            SetReorderMode(true);
            await DisplayAlertAsync(
                "调整位置",
                "长按一个已保存配置并拖到目标位置。完成一次拖放后会自动退出排序模式。",
                "开始调整");
        }
    }

    private async Task ApplyPresetAsync(ControlPreset preset)
    {
        if (!preset.IsSaved)
        {
            await DisplayAlertAsync(
                "空配置位",
                "点击上方“保存配置”可保存当前灯光设置。",
                "知道了");
            return;
        }

        ColorPicker.Hue = preset.Hue;
        ColorPicker.Saturation = preset.Saturation;
        ColorPicker.Temperature = preset.Temperature;
        BrightnessSlider.Value = preset.Brightness;
        QueueLightSettingsCommand();
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
        SetReorderMode(false);
    }

    private void SetReorderMode(bool enabled)
    {
        foreach (var preset in Presets)
            preset.IsReorderMode = enabled;

        PresetModeLabel.Text = enabled ? "拖动调整位置" : "最多 6 个";
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


    private async void OnTimerSendClicked(object? sender, EventArgs e)
    {
        if (!int.TryParse(DelaySecondsEntry.Text, out var seconds) || seconds is < 1 or > 86400)
        {
            ToastService.Show("发送失败：请输入 1 至 86400 秒");
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

        ToastService.Show(sent ? "计时指令发送成功" : "计时指令发送失败");
    }

    private async Task<bool> SendTimerCommandAsync(string timerMode, int seconds)
    {
        if (_isDemo || string.IsNullOrWhiteSpace(DeviceAddress))
            return false;

        // Scheduler 表达本地时间点，服务内会把“N 秒后”换算成具体时间。
        await _meshLight.SendTimerAsync(
            DeviceAddress,
            TimeSpan.FromSeconds(seconds),
            turnOn: timerMode == "TimedOn");
        return true;
    }

    private async void OnScheduleTapped(object? sender, TappedEventArgs e) =>
        await Shell.Current.GoToAsync(
            $"{nameof(SchedulePage)}?name={Uri.EscapeDataString(DeviceName)}" +
            $"&address={Uri.EscapeDataString(DeviceAddress)}");

    private async void OnMasterPowerTapped(object? sender, TappedEventArgs e)
    {
        _isMasterPowerOn = !_isMasterPowerOn;
        UpdateMasterPowerVisual();

        if (!_isReady)
            return;

        if (_isDemo || string.IsNullOrWhiteSpace(DeviceAddress))
            return;

        try
        {
            await _meshLight.SendOnOffAsync(DeviceAddress, _isMasterPowerOn);
        }
        catch (Exception exception)
        {
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
            MasterPowerLabel.Text = "关闭";
            MasterPowerLabel.TextColor = Colors.White;
            return;
        }

        var isDark = Application.Current?.RequestedTheme == AppTheme.Dark;
        MasterPowerPill.Background = new SolidColorBrush(
            isDark ? Color.FromArgb("#303B4D") : Color.FromArgb("#E8ECF3"));
        MasterPowerIcon.Source = "power_off.svg";
        MasterPowerLabel.Text = "开启";
        MasterPowerLabel.TextColor = isDark
            ? Color.FromArgb("#D9E0EC")
            : Color.FromArgb("#566174");
    }

    private void QueueLightSettingsCommand()
    {
        if (!_isReady)
            return;

        if (_isDemo || string.IsNullOrWhiteSpace(DeviceAddress))
            return;

        _lightSettingsCancellation?.Cancel();
        _lightSettingsCancellation?.Dispose();
        _lightSettingsCancellation = new CancellationTokenSource();
        _ = SendLightSettingsAsync(_lightSettingsCancellation.Token);
    }

    private async Task SendLightSettingsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(LightSettingsDebounce, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var lightness = (ushort)Math.Round(
            BrightnessSlider.Value / 100.0 * ushort.MaxValue);
        try
        {
            if (ColorPicker.Saturation >= ColorSaturationThreshold)
            {
                // 有彩色：走 Light HSL Set。
                var hue = (ushort)Math.Round(ColorPicker.Hue * ushort.MaxValue);
                var saturation = (ushort)Math.Round(
                    ColorPicker.Saturation * ushort.MaxValue);
                await _meshLight.SendHslAsync(
                    DeviceAddress,
                    lightness,
                    hue,
                    saturation);
            }
            else
            {
                // 接近白色：走 Light CTL Set（亮度 + 色温）。
                await _meshLight.SendCtlAsync(
                    DeviceAddress,
                    lightness,
                    MapTemperature(ColorPicker.Temperature));
            }
        }
        catch (Exception exception)
        {
            _diagnostics.Error(
                "Light/Settings",
                $"发送灯光设置失败：{DeviceName}。",
                exception);
        }
    }

    /// <summary>把色温环的 0-1 归一化值映射到 SIG CTL Temperature（800-20000 K）。</summary>
    private static ushort MapTemperature(double normalized)
    {
        const ushort minimumKelvin = 800;
        const ushort maximumKelvin = 20000;
        return (ushort)Math.Round(
            minimumKelvin + (maximumKelvin - minimumKelvin) * Math.Clamp(normalized, 0, 1));
    }
}

public sealed class ControlPreset : INotifyPropertyChanged
{
    private bool _isReorderMode;

    private ControlPreset(
        string displayName,
        bool isSaved,
        double hue,
        double saturation,
        double temperature,
        double brightness)
    {
        DisplayName = displayName;
        IsSaved = isSaved;
        Hue = hue;
        Saturation = saturation;
        Temperature = temperature;
        Brightness = brightness;
    }

    public string DisplayName { get; }
    public bool IsSaved { get; }
    public double Hue { get; }
    public double Saturation { get; }
    public double Temperature { get; }
    public double Brightness { get; }

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
            ? "拖动调整"
            : IsSaved
                ? $"{Brightness:0}%/{Temperature * 100:0}%"
                : "点击查看";

    public Color PreviewColor =>
        IsSaved ? Color.FromHsla(Hue, Saturation, 0.52) : Color.FromArgb("#DDE3ED");

    public static ControlPreset Create(
        string name,
        double hue,
        double saturation,
        double temperature,
        double brightness) =>
        new(name, true, hue, saturation, temperature, brightness);

    public static ControlPreset Empty() => new("空配置", false, 0, 0, 0, 0);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}