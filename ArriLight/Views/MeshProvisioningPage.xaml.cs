namespace ArriLight;

public partial class MeshProvisioningPage : ContentPage, IQueryAttributable
{
    private readonly MeshLightControlService _meshLight;
    private readonly MeshNetworkManager _networkManager;
    private readonly DeviceRepository _repository;
    private readonly AppDiagnosticService _diagnostics;
    private CancellationTokenSource? _cancellation;
    private string _address = string.Empty;
    private string _deviceName = string.Empty;
    private bool _handled;

    public MeshProvisioningPage()
    {
        _meshLight = ServiceHelper.GetRequiredService<MeshLightControlService>();
        _networkManager = ServiceHelper.GetRequiredService<MeshNetworkManager>();
        _repository = ServiceHelper.GetRequiredService<DeviceRepository>();
        _diagnostics = ServiceHelper.GetRequiredService<AppDiagnosticService>();
        InitializeComponent();
        BindingContext = this;
    }

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

    private string _statusText = string.Empty;
    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText == value)
                return;
            _statusText = value;
            OnPropertyChanged();
        }
    }

    private string _hintText = string.Empty;
    public string HintText
    {
        get => _hintText;
        private set
        {
            if (_hintText == value)
                return;
            _hintText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowHint));
        }
    }

    public bool ShowHint => !string.IsNullOrWhiteSpace(HintText);

    private double _progress;
    public double Progress
    {
        get => _progress;
        private set
        {
            if (Math.Abs(_progress - value) < 0.001)
                return;
            _progress = value;
            OnPropertyChanged();
        }
    }

    private bool _isWorking = true;
    public bool IsWorking
    {
        get => _isWorking;
        private set
        {
            if (_isWorking == value)
                return;
            _isWorking = value;
            OnPropertyChanged();
        }
    }

    private bool _showCancel = true;
    public bool ShowCancel
    {
        get => _showCancel;
        private set
        {
            if (_showCancel == value)
                return;
            _showCancel = value;
            OnPropertyChanged();
        }
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("address", out var address) && address is not null)
            _address = Uri.UnescapeDataString(address.ToString()!);
        if (query.TryGetValue("name", out var name) && name is not null)
            DeviceName = Uri.UnescapeDataString(name.ToString()!);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_handled || string.IsNullOrWhiteSpace(_address))
            return;

        _handled = true;
        await RunProvisioningAsync();
    }

    protected override void OnDisappearing()
    {
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
        base.OnDisappearing();
    }

    private async void OnCancelClicked(object? sender, EventArgs e)
    {
        _cancellation?.Cancel();
        await Shell.Current.GoToAsync("..");
    }

    private async Task RunProvisioningAsync()
    {
        _cancellation = new CancellationTokenSource();
        try
        {
            SetStage(AppResources.Mesh_ProvisioningConnecting, 0.08);
            var state = await _meshLight.ProbeMeshStateAsync(
                _address,
                _cancellation.Token);
            if (state.Kind == MeshDeviceProbeKind.NotMesh)
            {
                await ShowFailureAsync(AppResources.Mesh_ProvisioningFailed,
                    AppResources.Mesh_ProvisioningNotMesh);
                return;
            }

            SetStage(
                state.Kind == MeshDeviceProbeKind.Provisioned
                    ? AppResources.Mesh_ProvisioningRestoring
                    : AppResources.Mesh_ProvisioningStarting,
                0.25,
                state.Kind == MeshDeviceProbeKind.Unprovisioned
                    ? AppResources.Mesh_ProvisioningHint
                    : string.Empty);
            await _meshLight.EnsureProvisionedAsync(
                _address,
                state,
                _cancellation.Token);

            SetStage(AppResources.Mesh_ProvisioningSaving, 0.92);
            var isReplacing = _repository.Contains(_address);
            if (!_repository.TryAddOrReplace(new SavedDevice(
                    DeviceName,
                    _address,
                    DateTimeOffset.Now,
                    FirmwareVersion: SavedDevice.UnknownFirmwareVersion,
                    Protocol: BluetoothDeviceProtocol.Mesh)))
            {
                throw new InvalidOperationException(AppResources.Common_SaveFailed);
            }

            _networkManager.SetNodeName(_address, DeviceName);
            SetStage(AppResources.Mesh_ProvisioningSucceeded, 1);
            if (isReplacing)
                ToastService.ShowForDuration(AppResources.Device_Replaced, TimeSpan.FromSeconds(1));
            await _meshLight.DisconnectAsync(_address, CancellationToken.None);
            await Task.Delay(350);
            await Shell.Current.GoToAsync("//DevicesPage");
        }
        catch (OperationCanceledException)
        {
            // 用户取消或页面离开，不把未完成配网写成成功。
        }
        catch (Exception exception)
        {
            _diagnostics.Error(
                "Mesh/Provision",
                $"设备 {_address} 配网失败。",
                exception);
            await ShowFailureAsync(
                AppResources.Mesh_ProvisioningFailed,
                exception.Message);
        }
        finally
        {
            try
            {
                await _meshLight.DisconnectAsync(_address, CancellationToken.None);
            }
            catch (Exception exception)
            {
                _diagnostics.Warning(
                    "Mesh/Provision",
                    $"配网收尾断开失败：device={_address}，{exception.Message}");
            }
        }
    }

    private async Task ShowFailureAsync(string title, string message)
    {
        IsWorking = false;
        ShowCancel = false;
        var retry = await DisplayAlertAsync(
            title,
            message,
            AppResources.Mesh_ProvisioningRetry,
            AppResources.Mesh_ProvisioningBack);
        if (retry)
        {
            _handled = false;
            IsWorking = true;
            ShowCancel = true;
            await RunProvisioningAsync();
        }
        else
        {
            await Shell.Current.GoToAsync("..");
        }
    }

    private void SetStage(string status, double progress, string hint = "")
    {
        StatusText = status;
        Progress = progress;
        HintText = hint;
    }
}
