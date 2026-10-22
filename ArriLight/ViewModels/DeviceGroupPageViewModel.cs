using System.Collections.ObjectModel;
using System.Globalization;
using ArriOpto.BLE.Agp;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ArriLight.ViewModels;

public sealed record GroupPresetItem(SavedControlPreset Preset)
{
    public string Name => Preset.DisplayName;
}

public partial class DeviceGroupPageViewModel : ViewModelBase
{
    private readonly DeviceGroupService _groups;
    private readonly IUiDispatcher _dispatcher;
    private readonly IAppNavigator _navigator;
    private readonly IUserInteraction _interaction;
    private bool _visible, _loading;
    private string? _referenceAddress;
    private AgpCapabilities? _capabilities;
    private CancellationTokenSource? _sliderCancellation;
    private SavedDeviceGroup? _group;
    private bool _initialized;

    public DeviceGroupPageViewModel(DeviceGroupService groups, IUiDispatcher dispatcher,
        IAppNavigator navigator, IUserInteraction interaction)
    { _groups = groups; _dispatcher = dispatcher; _navigator = navigator; _interaction = interaction; }

    public string GroupId { get; private set; } = string.Empty;
    [ObservableProperty] private string _groupName = string.Empty;
    [ObservableProperty] private string _referenceText = string.Empty;
    [ObservableProperty] private string _availabilityText = string.Empty;
    [ObservableProperty] private string _operationText = string.Empty;
    [ObservableProperty] private bool _canControl;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PowerText))] private bool? _isOn;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(BrightnessText))] private double _brightness = 100;
    [ObservableProperty] private double _hue;
    [ObservableProperty] private double _saturation;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(TemperatureText)), NotifyPropertyChangedFor(nameof(TemperatureRatio))] private double _temperatureKelvin = 4000;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(TemperatureRatio))] private double _temperatureMinimum = 800;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(TemperatureRatio))] private double _temperatureMaximum = 20000;
    [ObservableProperty] private int _colorModeIndex;
    [ObservableProperty] private int _effectIndex;
    [ObservableProperty] private string _effectPeriodText = "1000";
    [ObservableProperty] private bool _autoOffEnabled;
    [ObservableProperty] private string _autoOffSecondsText = "60";
    [ObservableProperty] private string _timerSecondsText = "60";
    [ObservableProperty] private int _timerActionIndex;
    [ObservableProperty] private int _localModeIndex;
    [ObservableProperty] private string _localOnSecondsText = "60";
    [ObservableProperty] private string _localOffSecondsText = "60";
    public ObservableCollection<GroupPresetItem> Presets { get; } = [];
    public IReadOnlyList<string> ColorModes { get; } = [AppResources.Group_Color, AppResources.Group_Temperature];
    public IReadOnlyList<string> Effects { get; } = [AppResources.Effect_Steady, AppResources.Effect_Blink, AppResources.Effect_Breathe];
    public IReadOnlyList<string> TimerActions { get; } = [AppResources.Timer_On, AppResources.Timer_Off];
    public IReadOnlyList<string> LocalModes { get; } = [AppResources.Group_ModeNone, AppResources.Group_ModeDelayOn,
        AppResources.Group_ModeDelayOff, AppResources.Group_ModeDelayBoth, AppResources.Group_ModeOnAutoOff, AppResources.Group_ModeOffAutoOn];
    public bool SupportsPower => _capabilities?.HasFlag(AgpCapabilities.OnOff) == true;
    public bool SupportsBrightness => (_capabilities.GetValueOrDefault() &
        (AgpCapabilities.Lightness | AgpCapabilities.Lightness16 | AgpCapabilities.Hsl | AgpCapabilities.Ctl)) != 0;
    public bool SupportsColor => _capabilities?.HasFlag(AgpCapabilities.Hsl) == true;
    public bool SupportsTemperature => _capabilities?.HasFlag(AgpCapabilities.Ctl) == true;
    public bool SupportsBothColors => SupportsColor && SupportsTemperature;
    public bool SupportsEffects => _capabilities?.HasFlag(AgpCapabilities.Effects | AgpCapabilities.OnOff) == true;
    public bool SupportsTimer => _capabilities?.HasFlag(AgpCapabilities.RelativeTimer) == true;
    public bool SupportsLocalMode => _capabilities?.HasFlag(AgpCapabilities.LocalMode) == true;
    public bool ShowColor => SupportsColor && (!SupportsTemperature || ColorModeIndex == 0);
    public bool ShowTemperature => SupportsTemperature && (!SupportsColor || ColorModeIndex == 1);
    public string PowerText => IsOn is { } value ? value ? AppResources.Common_TurnOn : AppResources.Common_TurnOff : AppResources.Common_Unknown;
    public string BrightnessText => $"{Brightness:0}%";
    public string TemperatureText => $"{TemperatureKelvin:0} K";
    public double TemperatureRatio
    {
        get => TemperatureMaximum <= TemperatureMinimum ? 0 : Math.Clamp((TemperatureKelvin - TemperatureMinimum) / (TemperatureMaximum - TemperatureMinimum), 0, 1);
        set => TemperatureKelvin = TemperatureMinimum + Math.Clamp(value, 0, 1) * (TemperatureMaximum - TemperatureMinimum);
    }

    public override void ApplyQueryAttributes(IDictionary<string, object> query)
    { if (query.TryGetValue("groupId", out var value)) GroupId = Uri.UnescapeDataString(value?.ToString() ?? ""); }

    public override Task OnAppearingAsync()
    {
        _visible = true;
        _groups.Changed -= OnChanged; _groups.Changed += OnChanged;
        Reload(initialize: !_initialized);
        _initialized = true;
        return Task.CompletedTask;
    }

    public override void OnDisappearing()
    {
        _visible = false; _groups.Changed -= OnChanged;
        CancelSlider();
    }

    private void OnChanged(object? sender, EventArgs args) => _dispatcher.Post(() => { if (_visible) Reload(false); });
    private void Reload(bool initialize)
    {
        _group = _groups.Repository.Get(GroupId);
        if (_group is null) { CanControl = false; ErrorMessage = AppResources.Group_Removed; return; }
        GroupName = _group.Name;
        var members = _groups.GetMembers(_group);
        var reference = members.FirstOrDefault();
        var referenceChanged = _referenceAddress != reference?.Device.Address;
        var capabilitiesChanged = _capabilities != reference?.Capabilities;
        _referenceAddress = reference?.Device.Address;
        _capabilities = reference?.Capabilities;
        if (initialize || referenceChanged)
        {
            CancelSlider();
            _loading = true;
            try
            {
                IsOn = reference?.IsOn;
                Brightness = reference?.Brightness ?? 100;
                Hue = reference?.Hue ?? 0; Saturation = reference?.Saturation ?? 0;
                TemperatureMinimum = reference?.CtlRange?.TemperatureMinimumKelvin ?? 800;
                TemperatureMaximum = Math.Max(TemperatureMinimum, reference?.CtlRange?.TemperatureMaximumKelvin ?? 20000);
                TemperatureKelvin = Math.Clamp(reference?.TemperatureKelvin ?? 4000, TemperatureMinimum, TemperatureMaximum);
                ColorModeIndex = reference?.ColorModel == AgpColorModel.Ctl || !SupportsColor ? 1 : 0;
                EffectIndex = (int)(reference?.Effect ?? AgpLightMode.Steady);
                EffectPeriodText = (reference?.EffectPeriodMilliseconds ?? _group.BlinkPeriodMilliseconds).ToString(CultureInfo.InvariantCulture);
                AutoOffEnabled = _group.AutoOffEnabled; AutoOffSecondsText = _group.AutoOffSeconds.ToString(CultureInfo.InvariantCulture);
                TimerSecondsText = _group.TimerSeconds.ToString(CultureInfo.InvariantCulture);
                LocalModeIndex = (int)(reference?.LocalMode?.Mode ?? AgpLocalMode.DelayNone);
                LocalOnSecondsText = Math.Max(1, reference?.LocalMode?.OnSeconds ?? 60).ToString(CultureInfo.InvariantCulture);
                LocalOffSecondsText = Math.Max(1, reference?.LocalMode?.OffSeconds ?? 60).ToString(CultureInfo.InvariantCulture);
                OperationText = string.Empty; ErrorMessage = string.Empty;
            }
            finally { _loading = false; }
        }
        ReferenceText = reference is null ? AppResources.Group_NoMembers : Localization.Format(nameof(AppResources.Group_ReferenceDisplay),
            reference.Device.DisplayName, reference.ConnectionGeneration is null ? AppResources.Group_LastKnown : AppResources.Group_Reference);
        var compatible = reference is null ? [] : members.Where(m => DeviceGroupCompatibility.GetIssue(reference, m) is null).ToArray();
        var connected = compatible.Count(m => m.ConnectionGeneration is not null && !m.IsLocked);
        AvailabilityText = Localization.Format(nameof(AppResources.Group_ConnectedCount), connected, members.Count);
        if (compatible.Length < members.Count) AvailabilityText += " · " + AppResources.Group_Incompatible;
        CanControl = connected > 0;
        if (initialize || referenceChanged || capabilitiesChanged)
        {
            foreach (var property in new[] { nameof(SupportsPower), nameof(SupportsBrightness), nameof(SupportsColor), nameof(SupportsTemperature),
                nameof(SupportsBothColors), nameof(SupportsEffects), nameof(SupportsTimer), nameof(SupportsLocalMode), nameof(ShowColor), nameof(ShowTemperature) })
                OnPropertyChanged(property);
        }
        var presets = (_group.CustomPresets ?? []).OrderBy(p => p.Slot).ToArray();
        if (!Presets.Select(p => p.Preset).SequenceEqual(presets))
        { Presets.Clear(); foreach (var preset in presets) Presets.Add(new(preset)); }
    }

    partial void OnBrightnessChanged(double value) => QueueLighting();
    partial void OnHueChanged(double value) => QueueLighting();
    partial void OnSaturationChanged(double value) => QueueLighting();
    partial void OnTemperatureKelvinChanged(double value) => QueueLighting();
    partial void OnColorModeIndexChanged(int value)
    { OnPropertyChanged(nameof(ShowColor)); OnPropertyChanged(nameof(ShowTemperature)); }
    partial void OnEffectIndexChanged(int value)
    {
        if (_loading || _group is null) return;
        EffectPeriodText = (value == 2 ? _group.BreathePeriodMilliseconds : _group.BlinkPeriodMilliseconds).ToString(CultureInfo.InvariantCulture);
    }

    private void CancelSlider() { _sliderCancellation?.Cancel(); _sliderCancellation?.Dispose(); _sliderCancellation = null; }
    private void QueueLighting()
    {
        if (_loading || !_visible || !CanControl) return;
        CancelSlider(); _sliderCancellation = new();
        _ = SendSliderAsync(_sliderCancellation.Token);
    }
    private async Task SendSliderAsync(CancellationToken token)
    {
        try { await Task.Delay(250, token); if (!token.IsCancellationRequested && _visible) Submit(LightingCommand()); }
        catch (OperationCanceledException) { }
    }
    private DeviceGroupCommand LightingCommand() => new(DeviceGroupCommandKind.Lighting,
        Brightness: (byte)Math.Clamp(Math.Round(Brightness), 1, 100),
        ColorModel: ShowTemperature ? AgpColorModel.Ctl : ShowColor ? AgpColorModel.Hsl : AgpColorModel.None,
        Hue: Hue, Saturation: Saturation, TemperatureKelvin: (ushort)Math.Round(TemperatureKelvin));

    private bool Submit(DeviceGroupCommand command)
    {
        try
        {
            var dispatch = _groups.Submit(GroupId, command);
            OperationText = Localization.Format(nameof(AppResources.Group_Dispatched), dispatch.TargetCount, dispatch.SkippedCount);
            ErrorMessage = string.Empty;
            return true;
        }
        catch (Exception exception) { ErrorMessage = exception.Message; return false; }
    }

    [RelayCommand] private void ApplyLighting() { CancelSlider(); Submit(LightingCommand()); }
    [RelayCommand] private void TurnOn() => SetPower(true);
    [RelayCommand] private void TurnOff() => SetPower(false);
    private void SetPower(bool isOn)
    {
        try
        {
            var seconds = isOn ? GetAutoOffSeconds() : 0;
            if (Submit(new(DeviceGroupCommandKind.Power, IsOn: isOn, Seconds: seconds))) { IsOn = isOn; SaveSettings(); }
        }
        catch (Exception exception) { ErrorMessage = exception.Message; }
    }
    [RelayCommand] private void ApplyEffect()
    {
        try
        {
            var effect = (AgpLightMode)EffectIndex;
            var period = effect == AgpLightMode.Steady ? (ushort)0 : ushort.Parse(EffectPeriodText, CultureInfo.InvariantCulture);
            var command = new DeviceGroupCommand(DeviceGroupCommandKind.Effect, Effect: effect, PeriodMilliseconds: period, Seconds: GetAutoOffSeconds());
            if (Submit(command)) { IsOn = true; SaveSettings(); }
        }
        catch (Exception) { ErrorMessage = AppResources.Group_InvalidParameters; }
    }
    [RelayCommand] private void SendTimer() => SetTimer(cancel: false);
    [RelayCommand] private void CancelTimer() => SetTimer(cancel: true);
    private void SetTimer(bool cancel)
    {
        try
        {
            var seconds = cancel ? 0 : ParseSeconds(TimerSecondsText);
            if (Submit(new(DeviceGroupCommandKind.Timer, IsOn: TimerActionIndex == 0, Seconds: seconds))) SaveSettings();
        }
        catch (Exception) { ErrorMessage = AppResources.Group_InvalidParameters; }
    }
    [RelayCommand] private void ApplyLocalMode()
    {
        try
        {
            var mode = (AgpLocalMode)LocalModeIndex;
            var on = mode is AgpLocalMode.DelayOn or AgpLocalMode.DelayBoth or AgpLocalMode.OffAutoOn ? (uint)ParseSeconds(LocalOnSecondsText) : 0;
            var off = mode is AgpLocalMode.DelayOff or AgpLocalMode.DelayBoth or AgpLocalMode.OnAutoOff ? (uint)ParseSeconds(LocalOffSecondsText) : 0;
            Submit(new(DeviceGroupCommandKind.LocalMode, LocalMode: new(mode, on, off)));
        }
        catch (Exception) { ErrorMessage = AppResources.Group_InvalidParameters; }
    }
    private int GetAutoOffSeconds() => SupportsTimer && AutoOffEnabled ? ParseSeconds(AutoOffSecondsText) : 0;
    private static int ParseSeconds(string text) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds is >= 1 and <= 86400
        ? seconds : throw new ArgumentException(AppResources.Group_InvalidParameters);
    private void SaveSettings()
    {
        var group = _groups.Repository.Get(GroupId);
        if (group is null) return;
        var blink = group.BlinkPeriodMilliseconds; var breathe = group.BreathePeriodMilliseconds;
        if (ushort.TryParse(EffectPeriodText, out var period) && period >= AgpModelCodec.MinimumEffectPeriodMilliseconds && period <= AgpModelCodec.MaximumEffectPeriodMilliseconds)
        { if (EffectIndex == 1) blink = period; if (EffectIndex == 2) breathe = period; }
        _groups.Repository.SaveSettings(GroupId, group with { AutoOffEnabled = AutoOffEnabled,
            AutoOffSeconds = int.TryParse(AutoOffSecondsText, out var off) && off is >= 1 and <= 86400 ? off : group.AutoOffSeconds,
            TimerSeconds = int.TryParse(TimerSecondsText, out var timer) && timer is >= 1 and <= 86400 ? timer : group.TimerSeconds,
            BlinkPeriodMilliseconds = blink, BreathePeriodMilliseconds = breathe });
    }

    [RelayCommand] private void SaveGroupSettings()
    {
        try
        {
            if (AutoOffEnabled) _ = ParseSeconds(AutoOffSecondsText);
            _ = ParseSeconds(TimerSecondsText);
            if (SupportsEffects && EffectIndex != 0) ValidateEffectPeriod();
            SaveSettings(); OperationText = AppResources.Group_SettingsSaved; ErrorMessage = string.Empty;
        }
        catch (Exception exception) { ErrorMessage = exception.Message; }
    }

    [RelayCommand] private async Task ManageAsync() => await _navigator.GoToAsync($"DeviceGroupMembersPage?groupId={Uri.EscapeDataString(GroupId)}&returnToControl=true");
    [RelayCommand] private async Task SavePresetAsync()
    {
        if (_groups.Repository.Get(GroupId) is not { } group) return;
        try { if (SupportsEffects && EffectIndex != 0) ValidateEffectPeriod(); }
        catch (Exception exception) { ErrorMessage = exception.Message; return; }
        var presets = (group.CustomPresets ?? []).ToList();
        var slot = Enumerable.Range(0, 6).FirstOrDefault(slot => presets.All(p => p.Slot != slot), -1);
        if (slot < 0) { ErrorMessage = AppResources.Group_PresetsFull; return; }
        var name = await _interaction.PromptAsync(AppResources.Group_SavePreset, AppResources.Group_NamePrompt,
            AppResources.Common_Save, AppResources.Common_Cancel, maxLength: 24);
        if (string.IsNullOrWhiteSpace(name)) return;
        var effect = (AgpLightMode)EffectIndex;
        if (!ushort.TryParse(EffectPeriodText, out var period)) period = 1000;
        presets.Add(new(slot, name.Trim(), Hue, Saturation,
            TemperatureRatio, Brightness, null,
            SupportsEffects ? effect : null, SupportsEffects ? period : null, LightingCommand().ColorModel));
        _groups.Repository.SaveSettings(GroupId, group with { CustomPresets = presets });
    }
    [RelayCommand] private void ApplyPreset(GroupPresetItem item)
    {
        CancelSlider();
        var p = item.Preset;
        _loading = true;
        try
        {
            Brightness = Math.Clamp(p.Brightness ?? 100, 1, 100); Hue = p.Hue ?? 0; Saturation = p.Saturation ?? 0;
            TemperatureKelvin = TemperatureMinimum + Math.Clamp(p.Temperature ?? 0.5, 0, 1) * (TemperatureMaximum - TemperatureMinimum);
            ColorModeIndex = p.ActiveColorModel == AgpColorModel.Ctl ? 1 : 0;
            EffectIndex = (int)(p.EffectMode ?? AgpLightMode.Steady);
            EffectPeriodText = (p.EffectPeriodMilliseconds ?? 1000).ToString(CultureInfo.InvariantCulture);
        }
        finally { _loading = false; }
        if (SupportsBrightness && !Submit(LightingCommand())) return;
        if (SupportsEffects && p.EffectMode is not null) ApplyEffect();
    }
    [RelayCommand] private void DeletePreset(GroupPresetItem item)
    {
        if (_groups.Repository.Get(GroupId) is { } group)
            _groups.Repository.SaveSettings(GroupId, group with { CustomPresets = (group.CustomPresets ?? []).Where(p => p.Slot != item.Preset.Slot).ToArray() });
    }

    private void ValidateEffectPeriod()
    {
        if (!ushort.TryParse(EffectPeriodText, NumberStyles.None, CultureInfo.InvariantCulture, out var period) ||
            period < AgpModelCodec.MinimumEffectPeriodMilliseconds || period > AgpModelCodec.MaximumEffectPeriodMilliseconds)
            throw new ArgumentException(AppResources.Group_InvalidParameters);
    }
}
