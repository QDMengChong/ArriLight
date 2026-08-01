using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ArriLight;

public partial class MainPage : ContentPage
{
    private static readonly TimeSpan DeviceCheckTimeout = TimeSpan.FromSeconds(8);

    private readonly DeviceRepository _repository;
    private readonly BluetoothConnectionService _connectionService;
    private readonly MeshLightControlService _meshLight;
    private readonly AppDiagnosticService _diagnostics;
    private bool _isRefreshing;

    public ObservableCollection<LightDeviceItem> Devices { get; } = [];

    public string DeviceCountText => $"{Devices.Count} 台设备";

    public MainPage()
    {
        InitializeComponent();
        _repository = ServiceHelper.GetRequiredService<DeviceRepository>();
        _connectionService = ServiceHelper.GetRequiredService<BluetoothConnectionService>();
        _meshLight = ServiceHelper.GetRequiredService<MeshLightControlService>();
        _diagnostics = ServiceHelper.GetRequiredService<AppDiagnosticService>();
        _repository.DevicesChanged += OnDevicesChanged;
        BindingContext = this;
        ReloadDevices();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        ReloadDevices();
        await RefreshDeviceStatusesAsync();
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

        foreach (var savedDevice in savedDevices
                     .OrderByDescending(device => device.IsPinned)
                     .ThenBy(device => device.AddedAt))
        {
            var isOnlineDemo = savedDevice.IsDemo &&
                savedDevice.Address.EndsWith("01", StringComparison.Ordinal);

            Devices.Add(new LightDeviceItem(
                savedDevice.Name,
                savedDevice.Address,
                isOnlineDemo ? "在线" : savedDevice.IsDemo
                    ? "离线"
                    : "已保存 · 等待连接",
                isOnlineDemo
                    ? Color.FromArgb("#35C987")
                    : Color.FromArgb("#A9B2C3"),
                isOn: isOnlineDemo,
                isDemo: savedDevice.IsDemo,
                isOnline: isOnlineDemo));
        }

        OnPropertyChanged(nameof(DeviceCountText));
    }

    private async void OnDevicePowerClicked(object? sender, EventArgs e)
    {
        if (sender is not BindableObject { BindingContext: LightDeviceItem device })
            return;

        if (device.IsDemo)
        {
            device.IsOn = !device.IsOn;
            return;
        }

        if (!device.IsOnline || device.IsChecking)
        {
            ToastService.Show("设备离线，无法控制");
            return;
        }

        device.IsOn = !device.IsOn;
        try
        {
            await _meshLight.SendOnOffAsync(device.Address, device.IsOn);
        }
        catch (Exception exception)
        {
            device.IsOn = !device.IsOn;
            _diagnostics.Error(
                "Light/Power",
                $"列表页发送开关指令失败：{device.Address}。",
                exception);
            ToastService.Show("发送失败，请重试");
        }
    }

    private async void OnDeviceManageClicked(object? sender, EventArgs e)
    {
        if (sender is not BindableObject { BindingContext: LightDeviceItem device })
            return;

        await Shell.Current.GoToAsync(
            $"{nameof(DeviceControlPage)}?name={Uri.EscapeDataString(device.Name)}&address={Uri.EscapeDataString(device.Address)}");
    }

    /// <summary>
    /// 逐个检测已保存设备的在线状态：按设备连接，失败或超时标记为离线。
    /// 连接成功标记“在线”，失败或超时标记“离线”。
    /// </summary>
    private async Task RefreshDeviceStatusesAsync()
    {
        if (_isRefreshing)
            return;

        _isRefreshing = true;
        try
        {
            foreach (var device in Devices.Where(item => !item.IsDemo).ToList())
            {
                device.IsChecking = true;
                device.Status = "连接中";
                try
                {
                    using var cancellation = new CancellationTokenSource(DeviceCheckTimeout);
                    await _connectionService.ConnectAsync(
                        device.Address,
                        cancellation.Token);
                    device.IsOnline = true;
                    device.Status = "在线";
                    device.StatusColor = Color.FromArgb("#35C987");

                    // 同步灯具真实开关状态（打开 App / 从控制页返回都会走到这里）
                    try
                    {
                        var state = await _meshLight.GetOnOffStateAsync(
                            device.Address,
                            cancellation.Token);
                        if (state is { } onOff)
                            device.IsOn = onOff;
                    }
                    catch (Exception exception)
                    {
                        _diagnostics.Debug(
                            "Light/State",
                            $"读取设备 {device.Address} 开关状态失败：{exception.Message}");
                    }
                }
                catch (Exception exception)
                {
                    device.IsOnline = false;
                    device.Status = "离线";
                    device.StatusColor = Color.FromArgb("#E65B65");
                    _diagnostics.Debug(
                        "BLE/Status",
                        $"设备 {device.Address} 检测为离线：{exception.Message}");
                }
                finally
                {
                    device.IsChecking = false;
                }
            }
        }
        finally
        {
            _isRefreshing = false;
        }
    }
}

public sealed class LightDeviceItem : INotifyPropertyChanged
{
    private bool _isOn;
    private string _status;
    private Color _statusColor;
    private bool _isOnline;
    private bool _isChecking;

    public LightDeviceItem(
        string name,
        string address,
        string status,
        Color statusColor,
        bool isOn = false,
        bool isDemo = false,
        bool isOnline = false)
    {
        Name = name;
        Address = address;
        IsDemo = isDemo;
        _status = status;
        _statusColor = statusColor;
        _isOnline = isOnline;
        _isOn = isOn;
    }

    public string Name { get; }
    public string Address { get; }
    public bool IsDemo { get; }

    public string Status
    {
        get => _status;
        set
        {
            if (_status == value)
                return;
            _status = value;
            OnPropertyChanged();
        }
    }

    public Color StatusColor
    {
        get => _statusColor;
        set
        {
            if (_statusColor == value)
                return;
            _statusColor = value;
            OnPropertyChanged();
        }
    }

    /// <summary>是否检测为在线（离线时列表页开关不可用）。</summary>
    public bool IsOnline
    {
        get => _isOnline;
        set
        {
            if (_isOnline == value)
                return;
            _isOnline = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsEnabled));
            OnPropertyChanged(nameof(PowerOpacity));
        }
    }

    /// <summary>正在检测连接状态。</summary>
    public bool IsChecking
    {
        get => _isChecking;
        set
        {
            if (_isChecking == value)
                return;
            _isChecking = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsEnabled));
            OnPropertyChanged(nameof(PowerOpacity));
        }
    }

    /// <summary>列表页开关是否可用（仅在线且不在检测中）。</summary>
    public bool IsEnabled => IsOnline && !IsChecking;

    public double PowerOpacity => IsEnabled ? 1 : 0.35;

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
