using ArriOpto.BLE.Agp;
using CommunityToolkit.Mvvm.Input;

namespace ArriLight.ViewModels;

public partial class DeviceControlPageViewModel
{
    private readonly DeviceGroupService? _groups;
    private string _groupId = string.Empty;
    private string? _groupReference;
    private bool _groupInitialized, _loadingGroup, _redirectingGroup, _groupHasMembers;
    private bool? _groupPowerState;
    public string GroupId => _groupId;
    public bool IsGroup => !string.IsNullOrWhiteSpace(GroupId);
    public bool? GroupPowerState => IsGroup ? _groupPowerState : IsMasterPowerOn;
    public string GroupOperationText { get; private set; } = string.Empty;

    private void ApplyGroupQuery(IDictionary<string, object> query)
    {
        var id = query.TryGetValue("groupId", out var value) ? Uri.UnescapeDataString(value?.ToString() ?? "") : string.Empty;
        if (_groupId == id) return;
        if (_groups is not null) _groups.Changed -= OnGroupChanged;
        _groupId = id;
        _groupInitialized = false; _groupReference = null;
        if (IsGroup) DeviceAddress = string.Empty;
        OnPropertyChanged(nameof(GroupId)); OnPropertyChanged(nameof(IsGroup));
        OnPropertyChanged(nameof(CanRefreshStatus)); OnPropertyChanged(nameof(SupportsPower));
    }

    private Task OnGroupAppearingAsync()
    {
        _isControlPageVisible = true;
        if (_groups is not null) { _groups.Changed -= OnGroupChanged; _groups.Changed += OnGroupChanged; }
        if (!_groupInitialized) ResetLocalModeState(); else CancelLocalModeOperations();
        ReloadGroup(initialize: !_groupInitialized);
        if (SupportsLocalMode)
        {
            IsLocalModeSupported = true;
            _localModeCancellation = new();
        }
        return Task.CompletedTask;
    }

    private void OnGroupChanged(object? sender, EventArgs args) => _dispatcher.Post(() =>
    {
        if (!_isControlPageVisible || !IsGroup) return;
        ReloadGroup(initialize: false);
        if (!_groupHasMembers && !AutomaticConnectionPending) _ = RedirectEmptyGroupAsync();
    });

    private void ReloadGroup(bool initialize)
    {
        var group = _groups?.Repository.Get(GroupId);
        if (group is null)
        {
            _groupHasMembers = false; IsDeviceOnline = false; CanConnect = false;
            HasKnownCapabilities = false; SupportedCapabilities = AgpCapabilities.None;
            ErrorMessage = AppResources.Group_Removed;
            return;
        }
        DeviceName = group.Name;
        var members = _groups!.GetMembers(group);
        var reference = members.FirstOrDefault();
        _groupHasMembers = reference is not null;
        var referenceChanged = _groupReference != reference?.Device.Address;
        _groupReference = reference?.Device.Address;
        // Management-only and read-only device features never become group operations.
        var capabilities = (reference?.Capabilities ?? AgpCapabilities.None) &
            (AgpCapabilities.OnOff | AgpCapabilities.Lightness | AgpCapabilities.Lightness16 |
             AgpCapabilities.Hsl | AgpCapabilities.Ctl | AgpCapabilities.Effects |
             AgpCapabilities.RelativeTimer | AgpCapabilities.LocalMode);
        var capabilitiesChanged = capabilities != SupportedCapabilities || HasKnownCapabilities != (reference?.Capabilities is not null);
        IsDemo = false; IsLocked = false; IsBatterySupported = false; BatteryLevelPercent = null;
        HasKnownCapabilities = reference?.Capabilities is not null;
        SupportedCapabilities = capabilities;
        ControlPageKind = new AgpDeviceControlProfile(capabilities, null, new(800, 20000)).PageKind;
        EffectsSupported = capabilities.HasFlag(AgpCapabilities.Effects | AgpCapabilities.OnOff);
        if (initialize || referenceChanged)
        {
            _loadingGroup = true; _isReady = false; _isApplyingControlState = true;
            try
            {
                _pendingLightState = null; _pendingControlProfile = null;
                _groupPowerState = reference?.IsOn;
                IsMasterPowerOn = reference?.IsOn ?? false;
                Brightness = reference?.Brightness ?? 100;
                Hue = reference?.Hue ?? 0; Saturation = reference?.Saturation ?? 0;
                _ctlRange = reference?.CtlRange ?? new(800, 20000);
                Temperature = KelvinToTemperatureRatio(reference?.TemperatureKelvin ?? 4000);
                _activeColorModel = reference?.ColorModel is { } model && model != AgpColorModel.None ? model :
                    SupportsColor ? AgpColorModel.Hsl : SupportsTemperature ? AgpColorModel.Ctl : AgpColorModel.None;
                _hasPendingColorInput = false;
                EffectMode = reference?.Effect ?? AgpLightMode.Steady;
                _blinkEffectParameters = new(group.BlinkPeriodMilliseconds);
                _breatheEffectParameters = new(group.BreathePeriodMilliseconds);
                if (reference?.EffectPeriodMilliseconds is { } period && IsValidEffectPeriod(period))
                {
                    if (EffectMode == AgpLightMode.Blink) _blinkEffectParameters = new(period);
                    if (EffectMode == AgpLightMode.Breathe) _breatheEffectParameters = new(period);
                }
                IsTurnOnDelayEnabled = group.AutoOffEnabled;
                _pendingTurnOnDelaySeconds = group.AutoOffSeconds;
                TurnOnDelaySecondsText = group.AutoOffSeconds.ToString();
                _pendingTimerSeconds = group.TimerSeconds; TimerSecondsText = group.TimerSeconds.ToString();
                if (reference?.LocalMode is { } localMode) ApplyLocalModeConfiguration(localMode, updateSeconds: true);
                LoadCustomPresets(group.CustomPresets ?? []);
                GroupOperationText = string.Empty; ErrorMessage = string.Empty;
                _groupInitialized = true;
            }
            finally { _loadingGroup = false; _isReady = true; _isApplyingControlState = false; }
            OnPropertyChanged(nameof(GroupPowerState)); OnPropertyChanged(nameof(GroupOperationText));
        }
        var available = reference is null ? 0 : members.Count(member => member.ConnectionGeneration is not null &&
            !member.IsLocked && DeviceGroupCompatibility.GetIssue(reference, member) is null);
        IsDeviceOnline = available > 0; CanConnect = false; IsPowerChanging = false; HasPendingUserOperation = false;
        ConnectionStatusText = Localization.Format(nameof(AppResources.Group_ConnectedCount), available, members.Count);
        ConnectionStatusColor = PresentationColor.FromArgb(IsDeviceOnline ? "#35C987" : "#A9B2C3");
        OnPropertyChanged(nameof(CanTogglePower));
        ConfigureControlPage();
        if (capabilitiesChanged || referenceChanged) CapabilityViewsRetryRequested?.Invoke();
    }

    private async Task RedirectEmptyGroupAsync()
    {
        if (_groupHasMembers || _redirectingGroup || !_isControlPageVisible || _groups?.Repository.Get(GroupId) is null) return;
        _redirectingGroup = true;
        try { await _navigator.GoToAsync($"../DeviceGroupMembersPage?groupId={Uri.EscapeDataString(GroupId)}", animate: false); }
        catch (Exception exception) { ErrorMessage = exception.Message; _interaction.ShowToast(exception.Message); }
        finally { _redirectingGroup = false; }
    }

    [RelayCommand]
    private async Task ManageGroupMembersAsync()
    {
        if (IsGroup) await _navigator.GoToAsync($"DeviceGroupMembersPage?groupId={Uri.EscapeDataString(GroupId)}&returnToControl=true");
    }

    private bool SubmitGroupCommand(DeviceGroupCommand command, bool showToast = true)
    {
        try
        {
            var dispatch = (_groups ?? throw new InvalidOperationException(AppResources.Group_Removed)).Submit(GroupId, command);
            GroupOperationText = Localization.Format(nameof(AppResources.Group_Dispatched), dispatch.TargetCount, dispatch.SkippedCount);
            OnPropertyChanged(nameof(GroupOperationText)); ErrorMessage = string.Empty;
            if (showToast) _interaction.ShowToast(GroupOperationText);
            return true;
        }
        catch (Exception exception) { ErrorMessage = exception.Message; _interaction.ShowToast(exception.Message); return false; }
    }

    private void SetGroupPowerState(bool isOn)
    {
        _groupPowerState = isOn; IsMasterPowerOn = isOn;
        OnPropertyChanged(nameof(GroupPowerState)); UpdateMasterPowerVisual();
    }

    private void SaveGroupSettings(Func<SavedDeviceGroup, SavedDeviceGroup> update)
    {
        if (!_isReady || _loadingGroup || _groups?.Repository.Get(GroupId) is not { } group) return;
        _groups.Repository.SaveSettings(GroupId, update(group));
    }
}
