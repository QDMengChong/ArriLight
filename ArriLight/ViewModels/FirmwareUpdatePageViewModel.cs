using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ArriOpto.BLE.Ota;

namespace ArriLight.ViewModels;

public partial class FirmwareUpdatePageViewModel : ViewModelBase
{
    private readonly DeviceRepository _repository;
    private readonly BluetoothConnectionService _connectionService;
    private readonly FirmwareUpdateService _firmwareUpdateService;
    private readonly AppDiagnosticService _diagnostics;

    private string _deviceAddress = string.Empty;
    [ObservableProperty]
    private string _deviceName = AppResources.Device_DefaultName;
    private string _currentVersion = "1.0.0";
    private string _hardwareVersion = string.Empty;
    [ObservableProperty]
    private string _connectionText = AppResources.Ble_PreparingConnection;
    [ObservableProperty]
    private string _statusText = AppResources.Firmware_SelectSource;
    [ObservableProperty]
    private string _progressText = string.Empty;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsProgressVisible))]
    private double _progressValue;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanOperate)), NotifyPropertyChangedFor(nameof(CanStartUpdate)), NotifyPropertyChangedFor(nameof(CanShowOnlineUpdateButton)), NotifyPropertyChangedFor(nameof(IsProgressVisible))]
    private bool _isOperationRunning;
    private bool _hasInitializedDeviceInfo;
    private bool _canUseCandidate;
    private bool _isUpdateCompleted;
    private FirmwareCandidate? _candidate;
    private CancellationTokenSource? _operationCancellation;

    private readonly IUiDispatcher _dispatcher;
    private readonly IUserInteraction _interaction;
    private readonly IFirmwareFilePicker _filePicker;
    public IAsyncRelayCommand OnlineCheckCommand { get; }

    public FirmwareUpdatePageViewModel(DeviceRepository repository, BluetoothConnectionService connectionService,
        FirmwareUpdateService firmwareUpdateService, AppDiagnosticService diagnostics,
        IUiDispatcher dispatcher, IUserInteraction interaction, IFirmwareFilePicker filePicker)
    { _repository = repository; _connectionService = connectionService; _firmwareUpdateService = firmwareUpdateService;
      _diagnostics = diagnostics; _dispatcher = dispatcher; _interaction = interaction; _filePicker = filePicker; OnlineCheckCommand = new AsyncRelayCommand(OnlineCheckAsync); }

    public string CurrentVersion
    {
        get => _currentVersion == SavedDevice.UnknownFirmwareVersion
            ? AppResources.Common_Unknown
            : _currentVersion;
        private set => SetProperty(ref _currentVersion, value);
    }

    public bool CanOperate => !IsOperationRunning;
    public bool IsFirmwareReady => _candidate is not null;
    public bool CanStartUpdate =>
        _candidate?.SourceKind == FirmwareSourceKind.Online &&
        _canUseCandidate &&
        !_isUpdateCompleted &&
        !IsOperationRunning;
    public bool CanShowOnlineUpdateButton =>
        _candidate?.SourceKind == FirmwareSourceKind.Online &&
        _canUseCandidate &&
        !_isUpdateCompleted &&
        !IsOperationRunning;
    public bool IsProgressVisible => IsOperationRunning || ProgressValue > 0;

    public string CandidateTitle =>
        _candidate?.SourceKind == FirmwareSourceKind.Online
            ? Localization.Format(nameof(AppResources.Firmware_AvailableVersion), _candidate.Version)
            : Localization.Format(nameof(AppResources.Firmware_LocalTitle), _candidate?.ProtocolText ?? "OTA");

    public string CandidateSourceText => _candidate is null
        ? string.Empty
        : $"{_candidate.SourceText} · {_candidate.ProtocolText}";
    public string CandidateFileName => _candidate?.FileName ?? string.Empty;
    public string ReleaseNotes => _candidate?.ReleaseNotes ?? string.Empty;

    public override void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("address", out var value) && value is not null)
            _deviceAddress = Uri.UnescapeDataString(value.ToString()!);
    }

    public override Task OnAppearingAsync()
    {
        _connectionService.StateChanged += OnConnectionStateChanged;
        if (!string.IsNullOrWhiteSpace(_deviceAddress))
            ApplyConnectionSnapshot(_connectionService.GetSnapshot(_deviceAddress));

        if (!_hasInitializedDeviceInfo)
        {
            _hasInitializedDeviceInfo = true;
            if (_repository.GetByAddress(_deviceAddress) is { } device)
            {
                DeviceName = device.DisplayName;
                CurrentVersion = device.FirmwareVersion;
                _hardwareVersion = device.HardwareRevision?.Trim() ?? string.Empty;
            }

            _ = RefreshConnectedVersionAsync();
        }
        return Task.CompletedTask;
    }

    public override void OnDisappearing()
    {
        _connectionService.StateChanged -= OnConnectionStateChanged;

    }

    private async Task RefreshConnectedVersionAsync()
    {
        if (_repository.GetByAddress(_deviceAddress) is not { } device)
            return;

        if (device.IsDemo)
        {
            ConnectionText = AppResources.Demo_NoConnection;
            return;
        }

        try
        {
            await _connectionService.ConnectAsync(_deviceAddress);
            var hardwareRevision =
                await _connectionService.TryReadHardwareRevisionAsync(
                    _deviceAddress);
            if (!string.IsNullOrWhiteSpace(hardwareRevision))
            {
                _hardwareVersion = hardwareRevision.Trim();
                _repository.SetHardwareRevision(
                    _deviceAddress,
                    _hardwareVersion);
            }

            var firmwareVersion =
                await _connectionService.TryReadFirmwareRevisionAsync(
                    _deviceAddress);
            if (!string.IsNullOrWhiteSpace(firmwareVersion))
            {
                CurrentVersion = firmwareVersion;
                _repository.SetFirmwareVersion(
                    _deviceAddress,
                    firmwareVersion);
            }
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            ConnectionText = Localization.Format(nameof(AppResources.Ble_NotConnectedDetail), exception.Message);
        }
    }

    private async Task OnlineCheckAsync()
    {
        if (string.IsNullOrWhiteSpace(_hardwareVersion))
        {
            StatusText = AppResources.Firmware_HardwareNotRead;
            return;
        }

        await RunBusyOperationAsync(
            AppResources.Firmware_CheckingOnline,
            async token =>
            {
                var result = await _firmwareUpdateService.CheckOnlineAsync(
                    _hardwareVersion,
                    _currentVersion,
                    token);
                if (result.Candidate is { } candidate)
                {
                    SetCandidate(candidate, result.IsUpdateAvailable);
                    StatusText = result.IsUpdateAvailable
                        ? Localization.Format(nameof(AppResources.Firmware_UpdateAvailable), candidate.Version)
                        : Localization.Format(nameof(AppResources.Firmware_AlreadyLatest), CurrentVersion);
                }
                else
                {
                    ClearCandidate();
                    StatusText =
                        Localization.Format(nameof(AppResources.Firmware_AlreadyLatest), CurrentVersion);
                }
            });
    }

    [RelayCommand]
    private async Task SelectLocalAsync()
    {
        var file = await _filePicker.PickAsync(AppResources.Firmware_SelectFile);
        if (file is null)
            return;

        ClearCandidate();
        var loaded = await RunBusyOperationAsync(
            AppResources.Firmware_ValidatingLocal,
            async token =>
            {
                var candidate =
                    await _firmwareUpdateService.LoadLocalAsync(file, token);
                SetCandidate(candidate, canUpgrade: true);
                StatusText = Localization.Format(nameof(AppResources.Firmware_LocalReady), candidate.ProtocolText);
            });

        if (!loaded || _candidate is not { SourceKind: FirmwareSourceKind.Local } candidate)
            return;

        var confirmed = await _interaction.ConfirmAsync(
            AppResources.Firmware_ConfirmUpdate,
            Localization.Format(nameof(AppResources.Firmware_UpdateConfirmation), DeviceName, candidate.ProtocolText, candidate.FileName, candidate.ProductId ?? AppResources.Common_NotProvided),
            AppResources.Firmware_UpdateNow,
            AppResources.Common_Cancel);
        if (!confirmed)
        {
            StatusText = AppResources.Firmware_LocalCancelled;
            _diagnostics.Information(
                "OTA/Local",
                $"用户取消了本地固件升级确认：{candidate.FileName}。");
            return;
        }

        await StartUpdateAsync(candidate);
    }

    [RelayCommand]
    private async Task StartSelectedUpdateAsync()
    {
        var candidate = _candidate;
        if (candidate is null)
            return;

        await StartUpdateAsync(candidate);
    }

    private async Task StartUpdateAsync(FirmwareCandidate candidate)
    {
        ProgressValue = 0;
        ProgressText = "0%";
        await RunBusyOperationAsync(
            candidate.SourceKind == FirmwareSourceKind.Online
                ? AppResources.Firmware_DownloadingBeforeUpdate
                : AppResources.Firmware_PreparingLocalUpdate,
            async token =>
            {
                var progress = new Progress<FirmwareUpdateProgress>(
                    update =>
                    {
                        StatusText = update.Message;
                        ProgressValue = update.Percentage / 100d;
                        ProgressText = $"{update.Percentage:0.0}%";
                    });
                await _firmwareUpdateService.UpgradeAsync(
                    _deviceAddress,
                    candidate,
                    progress,
                    token);

                if (candidate.SourceKind == FirmwareSourceKind.Online)
                {
                    CurrentVersion = candidate.Version;
                    _repository.SetFirmwareVersion(
                        _deviceAddress,
                        candidate.Version);
                    _isUpdateCompleted = true;
                    OnPropertyChanged(nameof(CanStartUpdate));
                    OnPropertyChanged(nameof(CanShowOnlineUpdateButton));
                }

                StatusText = AppResources.Firmware_UpdateSucceeded;
                ProgressValue = 1;
                ProgressText = "100%";
            });
    }

    [RelayCommand]
    private void Cancel()
    {
        _diagnostics.Warning("OTA/UI", "用户请求取消当前 OTA 操作。");
        _operationCancellation?.Cancel();
        StatusText = AppResources.Firmware_Cancelling;
    }

    private async Task<bool> RunBusyOperationAsync(
        string initialStatus,
        Func<CancellationToken, Task> operation)
    {
        if (IsOperationRunning)
            return false;

        IsOperationRunning = true;
        StatusText = initialStatus;
        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();
        try
        {
            await operation(_operationCancellation.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            StatusText = AppResources.Firmware_OperationCancelled;
            _diagnostics.Warning("OTA/UI", "OTA 操作已取消。");
            return false;
        }
        catch (OtaException exception)
        {
            StatusText = ToOtaErrorMessage(exception);
            _diagnostics.Error(
                "OTA/UI",
                $"OTA 协议失败，state={exception.State}。",
                exception);
            return false;
        }
        catch (InvalidDataException exception)
        {
            StatusText = Localization.Format(nameof(AppResources.Firmware_ValidationFailed), exception.Message);
            _diagnostics.Error("OTA/UI", "固件数据无效。", exception);
            return false;
        }
        catch (NotSupportedException exception)
        {
            StatusText = exception.Message;
            _diagnostics.Warning("OTA/UI", exception.Message);
            return false;
        }
        catch (HttpRequestException exception)
        {
            StatusText = Localization.Format(nameof(AppResources.Firmware_NetworkFailed), exception.Message);
            _diagnostics.Error("OTA/UI", "在线固件请求失败。", exception);
            return false;
        }
        catch (IOException exception)
        {
            StatusText = _candidate?.Protocol == OtaProtocol.SingleBank
                ? AppResources.Firmware_SwitchDisconnect
                : Localization.Format(nameof(AppResources.Firmware_BleTransferFailed), exception.Message);
            _diagnostics.Error("OTA/UI", "蓝牙 OTA 传输失败。", exception);
            return false;
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            StatusText = Localization.Format(nameof(AppResources.Common_OperationFailed), exception.Message);
            _diagnostics.Error("OTA/UI", "未分类的 OTA 操作失败。", exception);
            return false;
        }
        finally
        {
            IsOperationRunning = false;
        }
    }

    private void SetCandidate(
        FirmwareCandidate candidate,
        bool canUpgrade)
    {
        _candidate = candidate;
        _canUseCandidate = canUpgrade;
        _isUpdateCompleted = false;
        OnPropertyChanged(nameof(IsFirmwareReady));
        OnPropertyChanged(nameof(CanStartUpdate));
        OnPropertyChanged(nameof(CanShowOnlineUpdateButton));
        OnPropertyChanged(nameof(CandidateTitle));
        OnPropertyChanged(nameof(CandidateSourceText));
        OnPropertyChanged(nameof(CandidateFileName));
        OnPropertyChanged(nameof(ReleaseNotes));
    }

    private void ClearCandidate()
    {
        _candidate = null;
        _canUseCandidate = false;
        _isUpdateCompleted = false;
        OnPropertyChanged(nameof(IsFirmwareReady));
        OnPropertyChanged(nameof(CanStartUpdate));
        OnPropertyChanged(nameof(CanShowOnlineUpdateButton));
        OnPropertyChanged(nameof(CandidateTitle));
        OnPropertyChanged(nameof(CandidateSourceText));
        OnPropertyChanged(nameof(CandidateFileName));
        OnPropertyChanged(nameof(ReleaseNotes));
    }

    private void OnConnectionStateChanged(
        object? sender,
        BluetoothSessionSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.Address) &&
            !string.Equals(
                snapshot.Address,
                _deviceAddress,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _dispatcher.Post(() => ApplyConnectionSnapshot(snapshot));
    }

    private void ApplyConnectionSnapshot(BluetoothSessionSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.Address) &&
            !string.Equals(
                snapshot.Address,
                _deviceAddress,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ConnectionText = snapshot.Message;
    }

    private static string ToOtaErrorMessage(OtaException exception) =>
        exception.State switch
        {
            OtaState.ErrorNoService =>
                AppResources.Firmware_SlbServiceMissing,
            OtaState.ErrorFileMismatch =>
                AppResources.Firmware_DeviceMismatch,
            OtaState.ErrorMtuChange or OtaState.ErrorMtuMismatch =>
                AppResources.Firmware_MtuFailed,
            OtaState.ErrorCharacteristic =>
                AppResources.Firmware_SlbCharacteristicsInvalid,
            OtaState.ErrorCannotConnect =>
                AppResources.Firmware_OtaConnectionFailed,
            OtaState.ErrorFirmwareFeedback =>
                AppResources.Firmware_Rejected,
            _ => Localization.Format(nameof(AppResources.Firmware_OtaFailed), exception.State, exception.Message)
        };

}
