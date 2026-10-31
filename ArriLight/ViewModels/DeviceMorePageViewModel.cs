using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ArriOpto.BLE.Agp;

namespace ArriLight.ViewModels;

public partial class DeviceMorePageViewModel : ViewModelBase
{
    private readonly DeviceRepository _repository;
    private readonly AgpLightControlService _agpLight;
    private readonly IUiDispatcher? _dispatcher;
    private readonly DeviceGroupRepository? _groups;
    private string _groupId = string.Empty;
    public bool IsGroup => !string.IsNullOrWhiteSpace(_groupId);
    public bool ShowDeviceDetails => !IsGroup;
    public string PageTitle => IsGroup ? AppResources.Group_Information : AppResources.Device_Information;
    public string NameFieldText => IsGroup ? AppResources.Group_Name : AppResources.Device_Name;
    public string IdFieldText => IsGroup ? AppResources.Group_Id : AppResources.Device_Id;
    public string RemovalText => IsGroup ? AppResources.Group_Delete : AppResources.Device_Unbind;
    public bool ShowResetButton => !IsGroup && _repository.GetByAddress(DeviceId) is
        { IsDemo: false, EffectiveProtocol: BluetoothDeviceProtocol.Agp, Capabilities: { } capabilities } &&
        capabilities.HasFlag(AgpCapabilities.FactoryReset);
    public string ResetButtonText => IsResetting ? AppResources.Device_Resetting : AppResources.Device_Reset;
    public string PinDescriptionText => IsGroup ? AppResources.Group_PinDescription : AppResources.Device_PinDescription;
    private bool _isVisible;
    private CancellationTokenSource? _runtimeWait;
    internal Task RuntimePreparationTask { get; private set; } = Task.CompletedTask;
    [ObservableProperty]
    private string _deviceId = string.Empty;
    [ObservableProperty]
    private string _deviceName = string.Empty;
    [ObservableProperty]
    private string _addedAtText = string.Empty;
    private string _firmwareVersion = "1.0.0";
    [ObservableProperty]
    private bool _isPinned;
    private bool _isLoading;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResetCommand))]
    [NotifyCanExecuteChangedFor(nameof(UnbindCommand))]
    private bool _isDeviceActionBusy;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResetButtonText))]
    private bool _isResetting;

    public string FirmwareVersion
    {
        get => _firmwareVersion == SavedDevice.UnknownFirmwareVersion
            ? AppResources.Common_Unknown
            : _firmwareVersion;
        private set
        {
            _firmwareVersion = value;
            OnPropertyChanged();
        }
    }

    private readonly IAppNavigator _navigator;
    private readonly IUserInteraction _interaction;
    public DeviceMorePageViewModel(DeviceRepository repository, AgpLightControlService agpLight,
        IAppNavigator navigator, IUserInteraction interaction, IUiDispatcher? dispatcher = null, DeviceGroupRepository? groups = null)
    { _repository = repository; _agpLight = agpLight; _navigator = navigator; _interaction = interaction;
        _dispatcher = dispatcher; _groups = groups; }

    public override void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        _groupId = query.TryGetValue("groupId", out var group) ? Uri.UnescapeDataString(group?.ToString() ?? "") : string.Empty;
        OnPropertyChanged(nameof(IsGroup)); OnPropertyChanged(nameof(ShowDeviceDetails));
        OnPropertyChanged(nameof(PageTitle)); OnPropertyChanged(nameof(RemovalText));
        OnPropertyChanged(nameof(NameFieldText)); OnPropertyChanged(nameof(IdFieldText));
        OnPropertyChanged(nameof(PinDescriptionText));
        OnPropertyChanged(nameof(ShowResetButton));
        ResetCommand.NotifyCanExecuteChanged();
        if (IsGroup) { DeviceId = _groupId; return; }
        if (query.TryGetValue("address", out var value) && value is not null)
            DeviceId = Uri.UnescapeDataString(value.ToString()!);
    }

    public override Task OnAppearingAsync()
    {
        _isVisible = true;
        if (IsGroup)
        {
            if (_groups is not null) { _groups.GroupsChanged -= OnDeviceRecordsChanged; _groups.GroupsChanged += OnDeviceRecordsChanged; }
            ReloadDevice();
            return Task.CompletedTask;
        }
        _repository.DevicesChanged -= OnDeviceRecordsChanged;
        _repository.DevicesChanged += OnDeviceRecordsChanged;
        _agpLight.RuntimeStateChanged -= OnRuntimeStateChanged;
        _agpLight.RuntimeStateChanged += OnRuntimeStateChanged;
        ReloadDevice();
        if (_agpLight.GetRuntimeSnapshot(DeviceId) is not null)
        {
            CancelRuntimeWait();
            _runtimeWait = new CancellationTokenSource();
            RuntimePreparationTask = JoinRuntimePreparationAsync(_runtimeWait.Token);
        }
        return Task.CompletedTask;
    }

    public override void OnDisappearing()
    {
        _isVisible = false;
        if (_groups is not null) _groups.GroupsChanged -= OnDeviceRecordsChanged;
        CancelRuntimeWait();
        _repository.DevicesChanged -= OnDeviceRecordsChanged;
        _agpLight.RuntimeStateChanged -= OnRuntimeStateChanged;
    }

    private async Task JoinRuntimePreparationAsync(CancellationToken token)
    {
        try { await _agpLight.EnsureRuntimeReadyAsync(DeviceId, token); }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (_isVisible && !token.IsCancellationRequested && !_agpLight.IsApplicationSuspended)
                ErrorMessage = exception.Message;
        }
    }
    private void CancelRuntimeWait()
    {
        var previous = _runtimeWait;
        _runtimeWait = null;
        if (previous is not null)
            _ = previous.CancelAsync().ContinueWith(_ => previous.Dispose(), TaskScheduler.Default);
    }

    private void OnDeviceRecordsChanged(object? sender, EventArgs e) => DispatchReload();
    private void OnRuntimeStateChanged(object? sender, DeviceRuntimeChange change)
    {
        if (string.Equals(change.Address, DeviceId, StringComparison.OrdinalIgnoreCase)) DispatchReload();
    }
    private void DispatchReload()
    {
        void Apply() { if (_isVisible) ReloadDevice(); }
        if (_dispatcher is not null) _dispatcher.Post(Apply);
        else Apply();
    }

    private void ReloadDevice()
    {
        OnPropertyChanged(nameof(ShowResetButton));
        ResetCommand.NotifyCanExecuteChanged();
        if (IsGroup)
        {
            if (_groups?.Get(_groupId) is { } group)
            {
                _isLoading = true;
                try
                {
                    DeviceName = group.Name;
                    AddedAtText = group.AddedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
                    IsPinned = group.IsPinned;
                }
                finally { _isLoading = false; }
            }
            else ErrorMessage = AppResources.Group_Removed;
            return;
        }
        if (_repository.GetByAddress(DeviceId) is not { } device)
            return;

        _isLoading = true;
        DeviceName = device.DisplayName;
        AddedAtText = device.AddedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
        FirmwareVersion = device.FirmwareVersion;
        IsPinned = device.IsPinned;
        _isLoading = false;
    }

    [RelayCommand]
    private async Task RenameAsync()
    {
        var name = await _interaction.PromptAsync(
            AppResources.Device_Rename,
            AppResources.Device_NewNamePrompt,
            accept: AppResources.Common_Save,
            cancel: AppResources.Common_Cancel,
            initialValue: DeviceName,
            maxLength: 24);

        if (string.IsNullOrWhiteSpace(name))
            return;

        if (IsGroup ? _groups?.Rename(_groupId, name) == true : _repository.Rename(DeviceId, name))
            DeviceName = name.Trim();
    }

    [RelayCommand]
    private async Task ManualAsync()
    { if (!IsGroup) await _navigator.GoToAsync("DeviceManualPage"); }

    [RelayCommand]
    private async Task FirmwareAsync()
    { if (!IsGroup) await _navigator.GoToAsync($"FirmwareUpdatePage?address={Uri.EscapeDataString(DeviceId)}"); }

    [RelayCommand]
    private async Task ShareAsync()
    { if (!IsGroup) await _navigator.GoToAsync($"DeviceSharePage?address={Uri.EscapeDataString(DeviceId)}"); }

    [RelayCommand]
    private void SetPinned(bool value)
    {
        if (_isLoading)
            return;

        if (IsGroup ? _groups?.SetPinned(_groupId, value) == true : _repository.SetPinned(DeviceId, value))
            IsPinned = value;
    }

    private bool CanReset() => !IsDeviceActionBusy && ShowResetButton && _repository.GetTokenBytes(DeviceId) is not null;
    private bool CanRemove() => !IsDeviceActionBusy;

    [RelayCommand(CanExecute = nameof(CanReset))]
    private async Task ResetAsync()
    {
        if (!CanReset()) return;
        IsDeviceActionBusy = true;
        var address = DeviceId;
        try
        {
            var confirmed = await _interaction.ConfirmAsync(
                AppResources.Device_Reset,
                Localization.Format(nameof(AppResources.Device_ResetConfirmation), DeviceName),
                AppResources.Device_Reset, AppResources.Common_Cancel);
            if (!confirmed || !_isVisible || address != DeviceId) return;

            IsResetting = true;
            await _agpLight.FactoryResetAsync(address);
            if (_isVisible)
                await _interaction.AlertAsync(AppResources.Device_ResetAccepted,
                    AppResources.Device_ResetAcceptedMessage, AppResources.Common_Ok);
            else
                _interaction.ShowToastForDuration(AppResources.Device_ResetAcceptedMessage, TimeSpan.FromSeconds(3));
        }
        catch (AgpFactoryResetUnconfirmedException)
        {
            await ShowResetErrorAsync(AppResources.Device_ResetUnconfirmed, AppResources.Device_ResetUnconfirmedMessage);
        }
        catch (Exception exception)
        {
            var message = exception.Data["AgpResult"] switch
            {
                AgpResultCode.Busy => AppResources.Device_ResetBusy,
                AgpResultCode.NotAuthorized => AppResources.Device_ResetNotAuthorized,
                AgpResultCode.InternalError => AppResources.Device_ResetPreparationFailed,
                AgpResultCode.UnknownOpcode or AgpResultCode.UnsupportedFeature => AppResources.Device_ResetUnsupported,
                _ => exception.Message
            };
            await ShowResetErrorAsync(AppResources.Device_ResetFailed, message);
        }
        finally
        {
            IsResetting = false;
            IsDeviceActionBusy = false;
            if (_isVisible) ReloadDevice();
        }
    }

    private Task ShowResetErrorAsync(string title, string message)
    {
        if (_isVisible) return _interaction.AlertAsync(title, message, AppResources.Common_Ok);
        _interaction.ShowToastForDuration(message, TimeSpan.FromSeconds(3));
        return Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private async Task UnbindAsync()
    {
        if (IsDeviceActionBusy)
            return;

        IsDeviceActionBusy = true;
        try
        {
            if (IsGroup)
            {
                if (await _interaction.ConfirmAsync(AppResources.Group_Delete, AppResources.Group_DeleteConfirmation,
                    AppResources.Common_Delete, AppResources.Common_Cancel))
                {
                    _groups?.Delete(_groupId);
                    await _navigator.GoToAsync("//DevicesPage");
                }
                return;
            }
            var confirmed = await _interaction.ConfirmAsync(
                AppResources.Device_Unbind,
                Localization.Format(nameof(AppResources.Device_RemoveConfirmation), DeviceName),
                AppResources.Device_Unbind,
                AppResources.Common_Cancel);
            if (!confirmed)
                return;

            try
            {
                await _agpLight.RemoveDeviceAsync(DeviceId);
            }
            catch (Exception)
            {
                await _interaction.AlertAsync(
                    AppResources.Device_UnbindFailed,
                    AppResources.Device_UnbindRetained,
                    AppResources.Common_Ok);
                return;
            }

            await _navigator.GoToAsync("//DevicesPage");
        }
        finally
        {
            IsDeviceActionBusy = false;
        }
    }
}
