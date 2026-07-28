using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ArriLight;

public partial class MainPage : ContentPage
{
    private readonly DeviceRepository _repository;

    private static readonly LightDeviceItem[] DefaultDevices =
    [
        new(
            "客厅主灯",
            "02:00:00:00:00:01",
            "在线",
            Color.FromArgb("#35C987"),
            isOn: true),
        new(
            "卧室氛围灯",
            "02:00:00:00:00:02",
            "离线",
            Color.FromArgb("#A9B2C3"))
    ];

    public ObservableCollection<LightDeviceItem> Devices { get; } = [];

    public string DeviceCountText => $"{Devices.Count} 台设备";

    public MainPage()
    {
        InitializeComponent();
        _repository = ServiceHelper.GetRequiredService<DeviceRepository>();
        _repository.DevicesChanged += OnDevicesChanged;
        BindingContext = this;
        ReloadDevices();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        ReloadDevices();
    }

    private async void OnScanQrCodeClicked(object? sender, EventArgs e) =>
        await Shell.Current.GoToAsync(nameof(QrCodeScanPage));

    private async void OnAddDeviceClicked(object? sender, EventArgs e) =>
        await Shell.Current.GoToAsync(nameof(BluetoothScanPage));

    private void OnDevicesChanged(object? sender, EventArgs e) =>
        MainThread.BeginInvokeOnMainThread(ReloadDevices);

    private void ReloadDevices()
    {
        var savedDevices = _repository.GetAll();
        Devices.Clear();

        foreach (var defaultDevice in DefaultDevices)
            Devices.Add(defaultDevice);

        foreach (var savedDevice in savedDevices)
        {
            if (DefaultDevices.Any(device =>
                    string.Equals(device.Address, savedDevice.Address,
                        StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            Devices.Add(new LightDeviceItem(
                savedDevice.Name,
                savedDevice.Address,
                "已保存 · 等待连接",
                Color.FromArgb("#A9B2C3")));
        }

        OnPropertyChanged(nameof(DeviceCountText));
    }

    private void OnDevicePowerClicked(object? sender, EventArgs e)
    {
        if (sender is not BindableObject { BindingContext: LightDeviceItem device })
            return;

        device.IsOn = !device.IsOn;
        // TODO: 接入灯具通信协议后，在这里发送设备开关指令。
        System.Diagnostics.Debug.WriteLine(
            $"Device power command: {device.Address} = {device.IsOn}");
    }

    private async void OnDeviceManageClicked(object? sender, EventArgs e)
    {
        if (sender is not BindableObject { BindingContext: LightDeviceItem device })
            return;

        await Shell.Current.GoToAsync(
            $"{nameof(DeviceControlPage)}?name={Uri.EscapeDataString(device.Name)}");
    }
}

public sealed class LightDeviceItem : INotifyPropertyChanged
{
    private bool _isOn;

    public LightDeviceItem(
        string name,
        string address,
        string status,
        Color statusColor,
        bool isOn = false)
    {
        Name = name;
        Address = address;
        Status = status;
        StatusColor = statusColor;
        _isOn = isOn;
    }

    public string Name { get; }
    public string Address { get; }
    public string Status { get; }
    public Color StatusColor { get; }

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (_isOn == value)
                return;

            _isOn = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PowerIcon));
            OnPropertyChanged(nameof(PowerBackgroundColor));
            OnPropertyChanged(nameof(PowerBorderColor));
        }
    }

    public string PowerIcon => IsOn ? "power_on.svg" : "power_off.svg";

    public Color PowerBackgroundColor =>
        IsOn ? Color.FromArgb("#FFF2C7") : Colors.Transparent;

    public Color PowerBorderColor =>
        IsOn ? Color.FromArgb("#FFD66B") : Color.FromArgb("#56657A");

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(
        [CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
