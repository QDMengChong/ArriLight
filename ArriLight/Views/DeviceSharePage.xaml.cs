namespace ArriLight;

public partial class DeviceSharePage : ContentPage, IQueryAttributable
{
    private readonly DeviceRepository _repository;
    private string _deviceName = AppResources.Device_DefaultName;
    private string _deviceAddress = string.Empty;
    private string _shareCode = string.Empty;
    private string _shareMessage = string.Empty;

    public string ShareCode
    {
        get => _shareCode;
        private set
        {
            _shareCode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasShareCode));
        }
    }

    public bool HasShareCode => !string.IsNullOrEmpty(ShareCode);

    public string ShareMessage
    {
        get => _shareMessage;
        private set
        {
            _shareMessage = value;
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

    public string DeviceAddress
    {
        get => _deviceAddress;
        private set
        {
            _deviceAddress = value;
            OnPropertyChanged();
        }
    }

    public DeviceSharePage()
    {
        InitializeComponent();
        _repository = ServiceHelper.GetRequiredService<DeviceRepository>();
        BindingContext = this;
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("address", out var address) && address is not null)
            DeviceAddress = Uri.UnescapeDataString(address.ToString()!);

        ReloadShareCode();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        ReloadShareCode();
    }

    private void ReloadShareCode()
    {
        ShareCode = string.Empty;
        if (_repository.GetByAddress(DeviceAddress) is not { } device)
        {
            ShareMessage = AppResources.Share_RecordMissing;
            return;
        }

        DeviceName = device.DisplayName;
        try
        {
            ShareCode = DeviceShareCode.Encode(device);
            ShareMessage = AppResources.Share_ContentsDescription;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            ShareMessage = exception.Message;
        }
    }
}
