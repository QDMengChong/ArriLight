using System.Runtime.CompilerServices;
using ArriOpto.BLE.Ota;

namespace ArriLight;

public partial class FirmwareUpdatePage : ContentPage, IQueryAttributable
{
    private readonly DeviceRepository _repository;
    private readonly BluetoothConnectionService _connectionService;
    private readonly FirmwareUpdateService _firmwareUpdateService;
    private readonly AppDiagnosticService _diagnostics;

    private string _deviceAddress = string.Empty;
    private string _deviceName = "灯光设备";
    private string _currentVersion = "1.0.0";
    private string _hardwareVersion = string.Empty;
    private string _connectionText = "正在准备连接…";
    private string _statusText = "请选择在线检查或加载本地 OTA 固件";
    private string _progressText = string.Empty;
    private double _progressValue;
    private bool _isBusy;
    private bool _hasInitializedDeviceInfo;
    private bool _canUseCandidate;
    private FirmwareCandidate? _candidate;
    private CancellationTokenSource? _operationCancellation;

    public FirmwareUpdatePage()
    {
        InitializeComponent();
        _repository = ServiceHelper.GetRequiredService<DeviceRepository>();
        _connectionService =
            ServiceHelper.GetRequiredService<BluetoothConnectionService>();
        _firmwareUpdateService =
            ServiceHelper.GetRequiredService<FirmwareUpdateService>();
        _diagnostics = ServiceHelper.GetRequiredService<AppDiagnosticService>();
        BindingContext = this;
    }

    public string DeviceName
    {
        get => _deviceName;
        private set => SetProperty(ref _deviceName, value);
    }

    public string CurrentVersion
    {
        get => _currentVersion;
        private set => SetProperty(ref _currentVersion, value);
    }

    public string ConnectionText
    {
        get => _connectionText;
        private set => SetProperty(ref _connectionText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    public double ProgressValue
    {
        get => _progressValue;
        private set => SetProperty(ref _progressValue, value);
    }

    public bool IsOperationRunning
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value))
                return;
            OnPropertyChanged(nameof(CanOperate));
            OnPropertyChanged(nameof(CanStartUpdate));
            OnPropertyChanged(nameof(IsProgressVisible));
        }
    }

    public bool CanOperate => !IsOperationRunning;
    public bool IsFirmwareReady => _candidate is not null;
    public bool CanStartUpdate =>
        _candidate?.SourceKind == FirmwareSourceKind.Online &&
        _canUseCandidate &&
        !IsOperationRunning;
    public bool IsOnlineCandidate =>
        _candidate?.SourceKind == FirmwareSourceKind.Online;
    public bool IsProgressVisible => IsOperationRunning || ProgressValue > 0;

    public string CandidateTitle =>
        _candidate?.SourceKind == FirmwareSourceKind.Online
            ? $"可用版本 {_candidate.Version}"
            : $"本地 {_candidate?.ProtocolText ?? "OTA"} 固件";

    public string CandidateSourceText => _candidate is null
        ? string.Empty
        : $"{_candidate.SourceText} · {_candidate.ProtocolText}";
    public string CandidateFileName => _candidate?.FileName ?? string.Empty;
    public string ReleaseNotes => _candidate?.ReleaseNotes ?? string.Empty;

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("address", out var value) && value is not null)
            _deviceAddress = Uri.UnescapeDataString(value.ToString()!);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _connectionService.StateChanged += OnConnectionStateChanged;
        if (!string.IsNullOrWhiteSpace(_deviceAddress))
            ApplyConnectionSnapshot(_connectionService.GetSnapshot(_deviceAddress));

        if (!_hasInitializedDeviceInfo)
        {
            _hasInitializedDeviceInfo = true;
            if (_repository.GetByAddress(_deviceAddress) is { } device)
            {
                DeviceName = device.Name;
                CurrentVersion = device.FirmwareVersion;
                _hardwareVersion = device.HardwareRevision?.Trim() ?? string.Empty;
            }

            _ = RefreshConnectedVersionAsync();
        }
    }

    protected override void OnDisappearing()
    {
        _connectionService.StateChanged -= OnConnectionStateChanged;
        base.OnDisappearing();
    }

    private async Task RefreshConnectedVersionAsync()
    {
        if (_repository.GetByAddress(_deviceAddress) is not { } device)
            return;

        if (device.IsDemo)
        {
            ConnectionText = "演示设备不执行真实蓝牙连接";
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
            ConnectionText = $"暂未连接：{exception.Message}";
        }
    }

    private async void OnOnlineCheckClicked(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_hardwareVersion))
        {
            StatusText = "未读取到设备硬件版本，无法查询匹配的在线固件。";
            return;
        }

        await RunBusyOperationAsync(
            "正在检查在线固件…",
            async token =>
            {
                var result = await _firmwareUpdateService.CheckOnlineAsync(
                    _hardwareVersion,
                    CurrentVersion,
                    token);
                if (result.Candidate is { } candidate)
                {
                    SetCandidate(candidate, result.IsUpdateAvailable);
                    StatusText = result.IsUpdateAvailable
                        ? $"发现新版本 {candidate.Version}，可以一键下载并升级。"
                        : $"当前版本 {CurrentVersion} 已是最新版本。";
                }
                else
                {
                    ClearCandidate();
                    StatusText =
                        $"当前版本 {CurrentVersion} 已是最新版本。";
                }
            });
    }

    private async void OnSelectLocalClicked(object? sender, EventArgs e)
    {
        var file = await FilePicker.Default.PickAsync(
            new PickOptions
            {
                PickerTitle = "选择 OTA 固件（.bin、.hex、.hex4、.hex16、.hexe16、.res）"
            });
        if (file is null)
            return;

        ClearCandidate();
        var loaded = await RunBusyOperationAsync(
            "正在校验本地固件…",
            async token =>
            {
                var candidate =
                    await _firmwareUpdateService.LoadLocalAsync(file, token);
                SetCandidate(candidate, canUpgrade: true);
                StatusText = $"本地 {candidate.ProtocolText} 固件已通过格式校验，等待确认升级。";
            });

        if (!loaded || _candidate is not { SourceKind: FirmwareSourceKind.Local } candidate)
            return;

        var confirmed = await DisplayAlertAsync(
            "确认固件升级",
            $"即将升级设备“{DeviceName}”。\n\n协议：{candidate.ProtocolText}\n文件：{candidate.FileName}\nProduct ID：{candidate.ProductId ?? "未提供"}\n\n升级期间请保持设备供电、手机蓝牙开启，并不要离开此页面。",
            "立即升级",
            "取消");
        if (!confirmed)
        {
            StatusText = "已取消本地固件升级。";
            _diagnostics.Information(
                "OTA/Local",
                $"用户取消了本地固件升级确认：{candidate.FileName}。");
            return;
        }

        await StartUpdateAsync(candidate);
    }

    private async void OnStartUpdateClicked(object? sender, EventArgs e)
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
                ? "正在下载固件，完成后将自动开始升级…"
                : "正在准备本地固件升级…",
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
                }

                StatusText = candidate.Protocol == OtaProtocol.SingleBank
                    ? "升级成功，请删除本设备后重新再次连接。"
                    : "固件升级完成，设备正在重启。";
                ProgressValue = 1;
                ProgressText = "100%";
            });
    }

    private void OnCancelClicked(object? sender, EventArgs e)
    {
        _diagnostics.Warning("OTA/UI", "用户请求取消当前 OTA 操作。");
        _operationCancellation?.Cancel();
        StatusText = "正在取消，请等待当前 BLE 操作结束…";
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
            StatusText = "操作已取消。若固件传输已经开始，请重新扫描设备并确认其状态。";
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
            StatusText = $"固件校验失败：{exception.Message}";
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
            StatusText = $"网络请求失败：{exception.Message}";
            _diagnostics.Error("OTA/UI", "在线固件请求失败。", exception);
            return false;
        }
        catch (IOException exception)
        {
            StatusText = _candidate?.Protocol == OtaProtocol.SingleBank
                ? "Single Bank 切换 OTA 模式时蓝牙连接断开（GATT 133）。当前 SDK 未将此切换期断开继续处理为重新扫描 bootloader。"
                : $"蓝牙传输失败：{exception.Message}";
            _diagnostics.Error("OTA/UI", "蓝牙 OTA 传输失败。", exception);
            return false;
        }
        catch (Exception exception)
        {
            StatusText = $"操作失败：{exception.Message}";
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
        OnPropertyChanged(nameof(IsFirmwareReady));
        OnPropertyChanged(nameof(CanStartUpdate));
        OnPropertyChanged(nameof(IsOnlineCandidate));
        OnPropertyChanged(nameof(CandidateTitle));
        OnPropertyChanged(nameof(CandidateSourceText));
        OnPropertyChanged(nameof(CandidateFileName));
        OnPropertyChanged(nameof(ReleaseNotes));
    }

    private void ClearCandidate()
    {
        _candidate = null;
        _canUseCandidate = false;
        OnPropertyChanged(nameof(IsFirmwareReady));
        OnPropertyChanged(nameof(CanStartUpdate));
        OnPropertyChanged(nameof(IsOnlineCandidate));
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

        MainThread.BeginInvokeOnMainThread(() => ApplyConnectionSnapshot(snapshot));
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
                "设备没有提供 SLB OTA 服务，请确认固件和设备模式。",
            OtaState.ErrorFileMismatch =>
                "固件与设备 Product ID、Boot 版本或 OTA 模式不匹配。",
            OtaState.ErrorMtuChange or OtaState.ErrorMtuMismatch =>
                "BLE MTU 协商失败，请重新连接并靠近设备后重试。",
            OtaState.ErrorCharacteristic =>
                "设备的 SLB OTA 特征不完整或属性不正确。",
            OtaState.ErrorCannotConnect =>
                "无法连接 OTA 设备，请确认设备供电和蓝牙距离。",
            OtaState.ErrorFirmwareFeedback =>
                "设备拒绝固件、offset 异常或最终校验失败。",
            _ => $"OTA 升级失败（{exception.State}）：{exception.Message}"
        };

    private bool SetProperty<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
