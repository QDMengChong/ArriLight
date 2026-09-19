namespace ArriLight;

public partial class DeviceMorePage : ContentPage, IQueryAttributable
{
    private readonly DeviceRepository _repository;
    private readonly AgpLightControlService _agpLightControlService;
    private string _deviceId = string.Empty;
    private string _deviceName = string.Empty;
    private string _addedAtText = string.Empty;
    private string _firmwareVersion = "1.0.0";
    private bool _isPinned;
    private bool _isLoading;

    public string DeviceId
    {
        get => _deviceId;
        private set
        {
            _deviceId = value;
            OnPropertyChanged();
        }
    }

    public string DeviceName
    {
        get => _deviceName;
        private set
        {
            _deviceName = value;
            OnPropertyChanged();
        }
    }

    public string AddedAtText
    {
        get => _addedAtText;
        private set
        {
            _addedAtText = value;
            OnPropertyChanged();
        }
    }

    public string FirmwareVersion
    {
        get => _firmwareVersion;
        private set
        {
            _firmwareVersion = value;
            OnPropertyChanged();
        }
    }

    public bool IsPinned
    {
        get => _isPinned;
        private set
        {
            _isPinned = value;
            OnPropertyChanged();
        }
    }

    public DeviceMorePage()
    {
        InitializeComponent();
        _repository = ServiceHelper.GetRequiredService<DeviceRepository>();
        _agpLightControlService = ServiceHelper.GetRequiredService<AgpLightControlService>();
        BindingContext = this;
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("address", out var value) && value is not null)
            DeviceId = Uri.UnescapeDataString(value.ToString()!);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        ReloadDevice();
    }

    private void ReloadDevice()
    {
        if (_repository.GetByAddress(DeviceId) is not { } device)
            return;

        _isLoading = true;
        DeviceName = device.Name;
        AddedAtText = device.AddedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
        FirmwareVersion = device.FirmwareVersion;
        IsPinned = device.IsPinned;
        PinnedSwitch.IsToggled = device.IsPinned;
        _isLoading = false;
    }

    private async void OnRenameTapped(object? sender, TappedEventArgs e)
    {
        var name = await DisplayPromptAsync(
            "修改设备名称",
            "请输入新的设备名称",
            accept: "保存",
            cancel: "取消",
            initialValue: DeviceName,
            maxLength: 24);

        if (string.IsNullOrWhiteSpace(name))
            return;

        if (_repository.Rename(DeviceId, name))
            DeviceName = name.Trim();
    }

    private async void OnManualTapped(object? sender, TappedEventArgs e) =>
        await Shell.Current.GoToAsync(nameof(DeviceManualPage));

    private async void OnFirmwareTapped(object? sender, TappedEventArgs e) =>
        await Shell.Current.GoToAsync(
            $"{nameof(FirmwareUpdatePage)}?address={Uri.EscapeDataString(DeviceId)}");

    private async void OnShareTapped(object? sender, TappedEventArgs e) =>
        await Shell.Current.GoToAsync(
            $"{nameof(DeviceSharePage)}?address={Uri.EscapeDataString(DeviceId)}&name={Uri.EscapeDataString(DeviceName)}");

    private async void OnRebindTapped(object? sender, TappedEventArgs e)
    {
        var confirmed = await DisplayAlertAsync(
            "重新绑定设备",
            "继续后请在 60 秒内轻触灯身按键。设备会将本机重新保存为可控制设备。",
            "开始绑定",
            "取消");
        if (!confirmed)
            return;

        try
        {
            await _agpLightControlService.RebindAsync(DeviceId);
            await DisplayAlertAsync("绑定成功", "设备已完成认证，可以正常控制和升级。", "知道了");
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync(
                "绑定未完成",
                $"请确认设备在附近且已在提示时间内轻触按键。\n\n{exception.Message}",
                "知道了");
        }
    }

    private void OnPinnedToggled(object? sender, ToggledEventArgs e)
    {
        if (_isLoading)
            return;

        if (_repository.SetPinned(DeviceId, e.Value))
            IsPinned = e.Value;
    }

    private async void OnUnbindClicked(object? sender, EventArgs e)
    {
        var confirmed = await DisplayAlertAsync(
            "解除绑定",
            $"确定从设备列表删除“{DeviceName}”吗？",
            "解除绑定",
            "取消");

        if (!confirmed)
            return;

        _repository.Remove(DeviceId);
        await Shell.Current.GoToAsync("//DevicesPage");
    }
}
