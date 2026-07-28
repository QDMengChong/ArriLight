namespace ArriLight;

public partial class DeviceSharePage : ContentPage, IQueryAttributable
{
    private string _deviceName = "灯光设备";
    private string _deviceAddress = string.Empty;

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
        BindingContext = this;
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("name", out var name) && name is not null)
            DeviceName = Uri.UnescapeDataString(name.ToString()!);

        if (query.TryGetValue("address", out var address) && address is not null)
            DeviceAddress = Uri.UnescapeDataString(address.ToString()!);
    }
}