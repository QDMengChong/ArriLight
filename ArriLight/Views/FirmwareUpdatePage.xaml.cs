namespace ArriLight;

public partial class FirmwareUpdatePage : ContentPage, IQueryAttributable
{
    private const string AvailableVersion = "1.1.0";
    private readonly DeviceRepository _repository;
    private string _deviceAddress = string.Empty;
    private string _currentVersion = "1.0.0";
    private string _statusText = "点击下方按钮检查固件更新";

    public string CurrentVersion
    {
        get => _currentVersion;
        private set
        {
            _currentVersion = value;
            OnPropertyChanged();
        }
    }

    public string LatestVersion => AvailableVersion;

    public string StatusText
    {
        get => _statusText;
        private set
        {
            _statusText = value;
            OnPropertyChanged();
        }
    }

    public FirmwareUpdatePage()
    {
        InitializeComponent();
        _repository = ServiceHelper.GetRequiredService<DeviceRepository>();
        BindingContext = this;
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("address", out var value) && value is not null)
            _deviceAddress = Uri.UnescapeDataString(value.ToString()!);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (_repository.GetByAddress(_deviceAddress) is { } device)
            CurrentVersion = device.FirmwareVersion;
    }

    private async void OnCheckClicked(object? sender, EventArgs e)
    {
        CheckButton.IsEnabled = false;
        StatusText = "正在检查新版本…";
        await Task.Delay(700);

        var hasUpdate = !string.Equals(
            CurrentVersion,
            AvailableVersion,
            StringComparison.OrdinalIgnoreCase);
        StatusText = hasUpdate
            ? $"发现新版本 {AvailableVersion}，建议保持灯具通电后升级。"
            : "当前已经是最新版本。";
        UpdateButton.IsVisible = hasUpdate;
        CheckButton.IsEnabled = true;
    }

    private async void OnUpdateClicked(object? sender, EventArgs e)
    {
        var confirmed = await DisplayAlertAsync(
            "固件 OTA 升级",
            "升级期间请保持灯具通电、蓝牙开启，并将手机靠近设备。是否继续？",
            "开始升级",
            "取消");
        if (!confirmed)
            return;

        CheckButton.IsEnabled = false;
        UpdateButton.IsEnabled = false;
        UpdateProgress.IsVisible = true;
        StatusText = "正在准备固件并建立 OTA 连接…";

        for (var step = 1; step <= 20; step++)
        {
            await Task.Delay(120);
            UpdateProgress.Progress = step / 20d;
            StatusText = $"正在传输固件… {step * 5}%";
        }

        // TODO: 接入真实 BLE OTA 服务后，用设备确认的新版本替代演示进度。
        _repository.SetFirmwareVersion(_deviceAddress, AvailableVersion);
        CurrentVersion = AvailableVersion;
        StatusText = "固件升级完成，设备已更新到最新版本。";
        UpdateButton.IsVisible = false;
        CheckButton.IsEnabled = true;
        UpdateButton.IsEnabled = true;
    }
}