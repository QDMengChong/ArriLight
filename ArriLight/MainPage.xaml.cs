using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ArriLight;

public partial class MainPage : ContentPage
{
    public ObservableCollection<LightDeviceItem> Devices { get; } =
    [
        new("客厅主灯", "在线 · 可以控制", Color.FromArgb("#38C793"), true),
        new("书房氛围灯", "离线", Color.FromArgb("#A9B2C3"))
    ];

    public string DeviceCountText => $"{Devices.Count} 台设备";

    public MainPage()
    {
        InitializeComponent();
        BindingContext = this;
    }

    private async void OnScanQrCodeClicked(object? sender, EventArgs e)
    {
        await DisplayAlertAsync("扫描二维码", "扫码添加功能将在设备接入阶段启用。", "知道了");
    }

    private async void OnAddDeviceClicked(object? sender, EventArgs e)
    {
        await DisplayAlertAsync("添加设备", "设备搜索与蓝牙配对功能将在下一阶段接入。", "知道了");
    }

    private void OnDevicePowerClicked(object? sender, EventArgs e)
    {
        if (sender is not BindableObject { BindingContext: LightDeviceItem device })
            return;

        device.IsOn = !device.IsOn;
        // TODO: 在蓝牙服务接入后，从这里发送设备开关指令。
        System.Diagnostics.Debug.WriteLine($"Device power command: {device.Name} = {device.IsOn}");
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

    public LightDeviceItem(string name, string status, Color statusColor, bool isOn = false)
    {
        Name = name;
        Status = status;
        StatusColor = statusColor;
        _isOn = isOn;
    }

    public string Name { get; }
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

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
