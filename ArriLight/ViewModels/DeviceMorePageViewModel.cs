using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ArriLight.ViewModels;

public partial class DeviceMorePageViewModel : ViewModelBase
{
    private readonly DeviceRepository _repository;
    private readonly AgpLightControlService _agpLight;
    private readonly IUiDispatcher? _dispatcher;
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
    private bool _isUnbinding;

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
        IAppNavigator navigator, IUserInteraction interaction, IUiDispatcher? dispatcher = null)
    { _repository = repository; _agpLight = agpLight; _navigator = navigator; _interaction = interaction;
        _dispatcher = dispatcher; }

    public override void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("address", out var value) && value is not null)
            DeviceId = Uri.UnescapeDataString(value.ToString()!);
    }

    public override Task OnAppearingAsync()
    {
        _isVisible = true;
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

        if (_repository.Rename(DeviceId, name))
            DeviceName = name.Trim();
    }

    [RelayCommand]
    private async Task ManualAsync() =>
        await _navigator.GoToAsync("DeviceManualPage");

    [RelayCommand]
    private async Task FirmwareAsync() =>
        await _navigator.GoToAsync(
            $"FirmwareUpdatePage?address={Uri.EscapeDataString(DeviceId)}");

    [RelayCommand]
    private async Task ShareAsync() =>
        await _navigator.GoToAsync(
            $"DeviceSharePage?address={Uri.EscapeDataString(DeviceId)}");

    [RelayCommand]
    private void SetPinned(bool value)
    {
        if (_isLoading)
            return;

        if (_repository.SetPinned(DeviceId, value))
            IsPinned = value;
    }

    [RelayCommand]
    private async Task UnbindAsync()
    {
        if (_isUnbinding)
            return;

        _isUnbinding = true;
        try
        {
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
            _isUnbinding = false;
        }
    }
}
