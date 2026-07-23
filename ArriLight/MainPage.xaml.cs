using System.Collections.ObjectModel;

namespace ArriLight;

public partial class MainPage : ContentPage
{
    public ObservableCollection<LightDeviceItem> Devices { get; } =
    [
        new("客厅主灯", "在线 · 可以控制", Color.FromArgb("#38C793")),
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
}

public sealed record LightDeviceItem(string Name, string Status, Color StatusColor);
