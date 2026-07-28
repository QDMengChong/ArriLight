using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ArriLight;

public partial class DeviceControlPage : ContentPage, IQueryAttributable
{
    private const int LongPressMilliseconds = 650;

    private string _deviceName = "设备管理";
    private bool _isReady;
    private bool _isMasterPowerOn = true;
    private bool _longPressTriggered;
    private bool _suppressPresetClick;
    private CancellationTokenSource? _presetPressCancellation;
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
    }

    protected override void OnDisappearing()
    {
        if (Application.Current is not null)
            Application.Current.RequestedThemeChanged -= OnRequestedThemeChanged;

        CancelPresetPress();
        base.OnDisappearing();
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("name", out var value) && value is not null)
            DeviceName = Uri.UnescapeDataString(value.ToString()!);
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
            System.Diagnostics.Debug.WriteLine(exception);
        }

        ToastService.Show(sent ? "计时指令发送成功" : "计时指令发送失败");
    }

    private Task<bool> SendTimerCommandAsync(string timerMode, int seconds)
    {
        // TODO: 蓝牙服务接入后，在此发送计时开灯或计时关灯指令，并返回真实结果。
        System.Diagnostics.Debug.WriteLine(
            $"Timer command: {DeviceName}, Mode={timerMode}, Delay={seconds}s");
        return Task.FromResult(true);
    }

    private async void OnScheduleTapped(object? sender, TappedEventArgs e) =>
        await Shell.Current.GoToAsync(
            $"{nameof(SchedulePage)}?name={Uri.EscapeDataString(DeviceName)}");

    private void OnMasterPowerTapped(object? sender, TappedEventArgs e)
    {
        _isMasterPowerOn = !_isMasterPowerOn;
        UpdateMasterPowerVisual();

        if (!_isReady)
            return;

        // TODO: 在蓝牙服务接入后，发送设备总开关指令。
        System.Diagnostics.Debug.WriteLine(
            $"Master power command: {DeviceName} = {_isMasterPowerOn}");
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

        // TODO: 接入蓝牙后应合并高频变化，并在短暂防抖后发送颜色、色温和亮度。
        System.Diagnostics.Debug.WriteLine(
            $"Light settings: H={ColorPicker.Hue:F2}, T={ColorPicker.Temperature:F2}, B={BrightnessSlider.Value:F0}");
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
                ? $"{Brightness:0}% 亮度"
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
