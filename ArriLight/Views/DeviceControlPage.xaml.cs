using System.Collections.ObjectModel;
using ArriOpto.BLE.Agp;
using ArriLight.Controls;
using CommunityToolkit.Maui.Views;

namespace ArriLight;

public partial class DeviceControlPage : ContentPage, IQueryAttributable
{
    private const int LongPressMilliseconds = 650;
    private readonly AppDiagnosticService _diagnostics;
    private readonly ToolbarItem _groupMembersToolbar;
    private Task? _capabilityViewsTask;
    private bool _capabilityViewsReloadRequested;
    private bool _isApplyingControlState;
    private bool _longPressTriggered;
    private bool _suppressPresetClick;
    private CancellationTokenSource? _presetPressCancellation;
    private CancellationTokenSource? _effectPressCancellation;
    private ControlPreset? _pressedPreset;
    private bool _effectLongPressTriggered;
    private bool _suppressEffectClick;
    public DeviceControlPageViewModel ViewModel { get; }
    private bool _isDemo => ViewModel.IsDemo;
    private bool _isGroup => ViewModel.IsGroup;
    private bool _isMasterPowerOn => ViewModel.IsMasterPowerOn;
    private bool _hasKnownCapabilities => ViewModel.HasKnownCapabilities;
    private bool _effectsSupported => ViewModel.EffectsSupported;
    private AgpLightMode _effectMode => ViewModel.EffectMode;
    private AgpControlPageKind _controlPageKind => ViewModel.ControlPageKind;
    private AgpCapabilities _supportedCapabilities => ViewModel.SupportedCapabilities;
    private AgpLightState? _pendingLightState => ViewModel.PendingLightState;
    private int _pendingTimerSeconds => ViewModel.PendingTimerSeconds;
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

    public string DeviceName => ViewModel.DeviceName;
    public string DeviceAddress => ViewModel.DeviceAddress;
    public bool IsDeviceOnline => ViewModel.IsDeviceOnline;
    public bool CanRefreshStatus => ViewModel.CanRefreshStatus;
    private string ConnectionStatusText { get => ViewModel.ConnectionStatusText; set => ViewModel.ConnectionStatusText = value; }
    private bool CanConnect { get => ViewModel.CanConnect; set => ViewModel.CanConnect = value; }
    public ObservableCollection<ControlPreset> Presets => ViewModel.Presets;

    public DeviceControlPage() : this(AgpControlPageKind.Lightness) { }
    protected DeviceControlPage(AgpControlPageKind controlPageKind)
    {
        _diagnostics = ServiceHelper.GetRequiredService<AppDiagnosticService>();
        ViewModel = ServiceHelper.GetRequiredService<DeviceControlPageViewModel>();
        ViewModel.ConfigureInitialKind(controlPageKind);
        using var timing = _diagnostics.BeginBleTiming("UI.Control.CreateViews");
        InitializeComponent();
        BindingContext = ViewModel;
        _groupMembersToolbar = new ToolbarItem
        {
            Text = AppResources.Group_Manage, IconImageSource = "action_group.svg", Order = ToolbarItemOrder.Primary,
            Priority = 1, Command = ViewModel.ManageGroupMembersCommand
        };
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ViewModel.IsGroup)) SyncGroupToolbar();
        };
        SyncGroupToolbar();
        ViewModel.RenderRequested += RenderControlState;
        ViewModel.CapabilityViewsRetryRequested += () =>
        {
            _capabilityViewsReloadRequested = true;
            if ((_capabilityViewsTask is null || _capabilityViewsTask.IsCompleted) &&
                _controlPageAppearanceCancellation is { } appearance)
            {
                _capabilityViewsTask = null;
                _ = ObserveCapabilityViewsRetryAsync(appearance.Token);
            }
        };
        RenderControlState();
        timing.SetResult("created");
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        BeginControlPageAppearance();
        ViewModel.AutomaticConnectionPending = _automaticConnectionPending;
        if (Application.Current is not null)
            Application.Current.RequestedThemeChanged += OnRequestedThemeChanged;
        _capabilityViewsTask = null;
        _ = ViewModel.OnAppearingAsync();
        RenderControlState();
    }

    protected override void OnDisappearing()
    {
        EndControlPageAppearance();
        ViewModel.AutomaticConnectionPending = false;
        if (Application.Current is not null)
            Application.Current.RequestedThemeChanged -= OnRequestedThemeChanged;
        CancelPresetPress(); CancelEffectPress(); CloseEffectSettings();
        ViewModel.OnDisappearing();
        base.OnDisappearing();
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query) => ViewModel.ApplyQueryAttributes(query);

    private void SyncGroupToolbar()
    {
        MoreToolbarItem.Priority = ViewModel.IsGroup ? 2 : 1;
        if (ViewModel.IsGroup && !ToolbarItems.Contains(_groupMembersToolbar)) ToolbarItems.Insert(1, _groupMembersToolbar);
        else if (!ViewModel.IsGroup) ToolbarItems.Remove(_groupMembersToolbar);
    }

    private void NotifyAutomaticConnectionPending(bool pending) => ViewModel.AutomaticConnectionPending = pending;
    private Task PrepareControlSessionAsync(CancellationToken token) => ViewModel.PrepareControlSessionAsync(token);

    private void ApplyLightState(AgpLightState state) => RenderControlState();

    private void OnRequestedThemeChanged(object? sender, AppThemeChangedEventArgs e)
    {
        UpdateMasterPowerVisual();
        UpdateEffectVisual();
    }

    private void OnColorPickerSelectionChanged(object? sender, EventArgs e)
    {
        if (_isApplyingControlState) return;
        var model = ColorPicker.LastChangedComponent switch
        {
            ColorTemperatureSelection.Color => (AgpColorModel?)AgpColorModel.Hsl,
            ColorTemperatureSelection.Temperature => AgpColorModel.Ctl, _ => null
        };
        ViewModel.UpdateLightingInput(ColorPicker.Hue, ColorPicker.Saturation, ColorPicker.Temperature, ViewModel.Brightness, model);
    }

    private void OnBrightnessChanged(object? sender, ValueChangedEventArgs e)
    {
        if (!_isApplyingControlState)
            ViewModel.UpdateLightingInput(ViewModel.Hue, ViewModel.Saturation, ViewModel.Temperature, e.NewValue);
    }

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

        if (ViewModel.SelectEffectCommand.CanExecute(mode))
            await ViewModel.SelectEffectCommand.ExecuteAsync(mode);
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

    private Task SetEffectAsync(AgpLightMode mode, ushort period) => ViewModel.SetEffectAsync(mode, period);

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

    private static void ExecuteCommand(System.Windows.Input.ICommand command)
    {
        if (command.CanExecute(null)) command.Execute(null);
    }

    private void OnSavePresetClicked(object? sender, EventArgs e) => ExecuteCommand(ViewModel.SavePresetCommand);

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

    private Task ShowPresetContextMenuAsync(ControlPreset preset) => ViewModel.PresetContextCommand.CanExecute(preset)
        ? ViewModel.PresetContextCommand.ExecuteAsync(preset) : Task.CompletedTask;

    private Task ApplyPresetAsync(ControlPreset preset) => ViewModel.ApplyPresetCommand.CanExecute(preset)
        ? ViewModel.ApplyPresetCommand.ExecuteAsync(preset) : Task.CompletedTask;

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
        if (sender is BindableObject { BindingContext: ControlPreset target } &&
            e.Data.Properties.TryGetValue("preset", out var value) && value is ControlPreset source)
            ViewModel.ReorderPreset(source, target);
    }

    private void SetReorderMode(bool enabled) => ViewModel.SetReorderMode(enabled);

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

    private void OnDelaySecondsTextChanged(object? sender, TextChangedEventArgs e) => ViewModel.TimerSecondsText = e.NewTextValue;

    private void OnTimerSendClicked(object? sender, EventArgs e)
    {
        ViewModel.TimerModeIndex = TimerModePicker.SelectedIndex;
        ExecuteCommand(ViewModel.TimerSendCommand);
    }

    private void ApplyTimerCapabilities(AgpCapabilities capabilities)
    {
        TurnOnDelayControlLazyView.IsVisible = HasTurnOnDelayView &&
            (capabilities.HasFlag(AgpCapabilities.RelativeTimer) || ViewModel.SupportsLocalMode);
        TimerControlLazyView.IsVisible = ShowTimedControls && HasTimerView &&
            capabilities.HasFlag(AgpCapabilities.RelativeTimer);
        if (!HasTimerView)
            return;
        // The schedule card is itself capability-dependent and is loaded lazily.
        // A device that only exposes RelativeTimer has no Schedule LazyView yet;
        // touching its content here would dereference a null view during the
        // first capability load.
        if (HasScheduleView)
            ScheduleSettingsCard.IsVisible = capabilities.HasFlag(AgpCapabilities.RtcTime);
        RelativeTimerCard.IsVisible = ShowTimedControls && capabilities.HasFlag(AgpCapabilities.RelativeTimer);

        var wasTimedOn = TimerModePicker.SelectedIndex == 1;
        var supportsTimedOn = capabilities.HasFlag(AgpCapabilities.RelativeTimer);
        TimerModePicker.Items.Clear();
        TimerModePicker.Items.Add(AppResources.Timer_Off);
        if (supportsTimedOn)
            TimerModePicker.Items.Add(AppResources.Timer_On);
        TimerModePicker.SelectedIndex = supportsTimedOn && wasTimedOn ? 1 : 0;
    }

    private void OnScheduleTapped(object? sender, TappedEventArgs e) => ExecuteCommand(ViewModel.ScheduleCommand);

    private void OnMasterPowerTapped(object? sender, TappedEventArgs e) => ExecuteCommand(ViewModel.MasterPowerCommand);

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

    private bool SupportsBrightness => ViewModel.SupportsBrightness;

    private bool SupportsColor => ViewModel.SupportsColor;

    private bool SupportsTemperature => ViewModel.SupportsTemperature;

    private bool SupportsEffect => ViewModel.SupportsEffect;

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
        LightControlCard.IsVisible = hasPresetSupport;
        if (HasSavePresetView)
            SavePresetLazyView.IsVisible = hasPresetSupport;
        if (HasPresetView)
        {
            PresetControlLazyView.IsVisible = hasPresetSupport;
            CustomPresetSection.IsVisible = hasPresetSupport;
        }
        if (HasEffectView) EffectControlLazyView.IsVisible = SupportsEffect;
        if (TurnOnDelayControlLazyView.HasLazyViewLoaded)
            TurnOnDelayControlLazyView.IsVisible = ViewModel.SupportsTurnOnDelay || ViewModel.SupportsLocalMode;
        BrightnessEffectSeparator.IsVisible = supportsBrightness && SupportsEffect;

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
            do
            {
                _capabilityViewsReloadRequested = false;
                await LoadCapabilityViewsAsync(token);
            } while (_capabilityViewsReloadRequested);
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

        if (ViewModel.SupportsTurnOnDelay || ViewModel.SupportsLocalMode)
        {
            var view = await LoadLazyViewAsync(TurnOnDelayControlLazyView);
            view.SecondsFocused -= OnTurnOnDelaySecondsFocused;
            view.SecondsFocused += OnTurnOnDelaySecondsFocused;
            TurnOnDelayControlLazyView.IsVisible = true;
            timing.Mark("turn-on delay LazyView loaded");
            await YieldControlFrameAsync(token);
        }

        if (ShowTimedControls && _supportedCapabilities.HasFlag(AgpCapabilities.RelativeTimer))
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
            lazyView.BindingContext = ViewModel;
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

    private void ApplyPendingProfileToLoadedViews(bool applyBrightness = true, bool applyColor = true) => RenderControlState();
    private void RenderControlState()
    {
        UpdateMasterPowerVisual(); UpdateEffectVisual(); UpdateEffectAvailability();
        ConfigureControlPage(); ApplyTimerCapabilities(_supportedCapabilities);
        if (HasPresetView)
        {
            PresetModeLabel.Text = ViewModel.IsReorderMode ? AppResources.Preset_DragToReorder : AppResources.Preset_MaximumCount;
            FinishReorderButton.IsVisible = ViewModel.IsReorderMode;
        }
        if (!IsDeviceOnline) { CancelEffectPress(); CloseEffectSettings(); }
        _isApplyingControlState = true;
        try
        {
            if (HasBrightnessView) BrightnessSlider.Value = ViewModel.Brightness;
            if (HasColorView)
            { ColorPicker.Hue = ViewModel.Hue; ColorPicker.Saturation = ViewModel.Saturation; ColorPicker.Temperature = ViewModel.Temperature; }
        }
        finally { _isApplyingControlState = false; }
    }

}
