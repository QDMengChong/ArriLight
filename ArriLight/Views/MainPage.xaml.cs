using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ArriLight;

public partial class MainPage : ContentPage
{
    private static readonly TimeSpan StatusQueryTimeout =
        // 扫描已经确认设备正在广播；给 Android BLE 完成连接、服务发现
        // 和公开状态读取完整的 5 秒窗口，避免 300ms 内被提前取消。
        TimeSpan.FromSeconds(5);

    private readonly DeviceRepository _repository;
    private readonly BluetoothDiscoveryService _discoveryService;
    private readonly AgpLightControlService _agpLight;
    private readonly AppDiagnosticService _diagnostics;
    private readonly object _statusQueueSync = new();
    private readonly Dictionary<string, (LightDeviceItem Device, int Rssi)>
        _pendingStatusQueries = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _statusQueryAttempted =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _statusQuerySuppressed;
    private CancellationTokenSource? _statusRefreshCancellation;
    private Task? _statusRefreshTask;

    public ObservableCollection<LightDeviceItem> Devices { get; } = [];

    public string DeviceCountText => $"{Devices.Count} 台设备";

    public MainPage()
    {
        InitializeComponent();
        _repository = ServiceHelper.GetRequiredService<DeviceRepository>();
        _discoveryService =
            ServiceHelper.GetRequiredService<BluetoothDiscoveryService>();
        _agpLight = ServiceHelper.GetRequiredService<AgpLightControlService>();
        _diagnostics = ServiceHelper.GetRequiredService<AppDiagnosticService>();
        _repository.DevicesChanged += OnDevicesChanged;
        BindingContext = this;
        ReloadDevices();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        ReloadDevices();
        _statusQuerySuppressed = false;
        lock (_statusQueueSync)
        {
            _pendingStatusQueries.Clear();
            _statusQueryAttempted.Clear();
        }

        _discoveryService.DeviceFound += OnDeviceFound;
        _ = StartHomeScanAsync();
    }

    protected override void OnDisappearing()
    {
        _discoveryService.DeviceFound -= OnDeviceFound;
        _statusQuerySuppressed = true;
        _statusRefreshCancellation?.Cancel();
        // 主页离开后不再需要扫描；添加页进入时会重新启动并重置 60 秒窗口。
        _ = _discoveryService.StopAsync();
        base.OnDisappearing();
    }

    private async Task StartHomeScanAsync()
    {
        try
        {
            // 主页每次进入都将服务级扫描窗口重置为 60 秒。
            await _discoveryService.StartOrResetBackgroundScanAsync(
                TimeSpan.FromSeconds(60));
        }
        catch (Exception exception)
        {
            _diagnostics.Warning(
                "BLE/Scan",
                $"主页启动后台扫描失败：{exception.Message}");
        }
    }

    private void OnDeviceFound(
        object? sender,
        DiscoveredBluetoothDevice discovered)
    {
        if (_statusQuerySuppressed)
            return;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_statusQuerySuppressed)
                return;

            var device = Devices.FirstOrDefault(item =>
                !item.IsDemo &&
                string.Equals(
                    item.Address,
                    discovered.Address,
                    StringComparison.OrdinalIgnoreCase));
            if (device is null)
                return;

            lock (_statusQueueSync)
            {
                if (_statusQueryAttempted.Contains(device.Address))
                    return;

                _pendingStatusQueries[device.Address] = (device, discovered.Rssi);
            }

            _ = StartStatusRefreshAsync();
        });
    }

    private async void OnScanQrCodeClicked(object? sender, EventArgs e)
    {
        await StopStatusRefreshAsync();
        await Shell.Current.GoToAsync(nameof(QrCodeScanPage));
    }

    private async void OnAddDeviceClicked(object? sender, EventArgs e)
    {
        await StopStatusRefreshAsync();
        await Shell.Current.GoToAsync(nameof(BluetoothScanPage));
    }

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

        // 状态轮询不应抢占用户针对某台设备发起的授权控制操作。
        _statusQuerySuppressed = true;
        await StopStatusRefreshAsync();

        try
        {
            if (device.IsDemo)
            {
                device.IsOn = !device.IsOn;
                return;
            }

            device.IsOn = !device.IsOn;
            await _agpLight.SetOnOffAsync(device.Address, device.IsOn);
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
        finally
        {
            _statusQuerySuppressed = false;
        }
    }

    private async void OnDeviceManageClicked(object? sender, EventArgs e)
    {
        if (sender is not BindableObject { BindingContext: LightDeviceItem device })
            return;

        _statusQuerySuppressed = true;
        await StopStatusRefreshAsync();

        // 不要在主页等待 BLE 连接、服务发现和能力读取后再导航。
        // 这些操作可能需要数秒，控制页本身已经会在进入后异步完成连接，
        // 并在能力读取完成后切换到对应的 HSL/CTL 控件。
        var route = device.IsDemo
            ? nameof(HslCtlDeviceControlPage)
            : nameof(DeviceControlPage);

        await Shell.Current.GoToAsync(
            $"{route}?name={Uri.EscapeDataString(device.Name)}" +
            $"&address={Uri.EscapeDataString(device.Address)}");
    }

    private async Task StartStatusRefreshAsync()
    {
        if (_statusQuerySuppressed)
            return;

        if (_statusRefreshTask is { IsCompleted: false })
            return;

        var refreshTask = RefreshDeviceStatusesAsync();
        _statusRefreshTask = refreshTask;
        await refreshTask;
    }

    /// <summary>
    /// 主页只查询扫描中发现的已保存设备，并按 RSSI 从强到弱处理。
    /// 查询只读取公开 Light State，不执行 AGP Token 授权。
    /// </summary>
    private async Task RefreshDeviceStatusesAsync()
    {
        if (_statusQuerySuppressed)
            return;

        using var refreshCancellation = new CancellationTokenSource();
        _statusRefreshCancellation = refreshCancellation;
        var cancellationToken = refreshCancellation.Token;
        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                   !_statusQuerySuppressed)
            {
                // 给同一轮扫描中的多个广播留出一个很短的收集窗口，
                // 使查询顺序更稳定地按 RSSI 从强到弱排列。
                await Task.Delay(200, cancellationToken);
                var candidates = TakePendingStatusQueries();
                if (candidates.Count == 0)
                    return;

                // 设备已经通过广播筛选，连接并读取状态只执行一次；
                // 失败后不在本次主页扫描窗口内再次连接重试。
                foreach (var candidate in candidates)
                {
                    if (_statusQuerySuppressed)
                        return;

                    await QueryDeviceStatusAsync(
                        candidate.Device,
                        StatusQueryTimeout,
                        cancellationToken);
                }
            }
        }
        finally
        {
            _statusRefreshCancellation = null;
        }
    }

    private List<(LightDeviceItem Device, int Rssi)> TakePendingStatusQueries()
    {
        lock (_statusQueueSync)
        {
            var candidates = _pendingStatusQueries.Values
                .OrderByDescending(item => item.Rssi)
                .ToList();
            _pendingStatusQueries.Clear();

            foreach (var candidate in candidates)
                _statusQueryAttempted.Add(candidate.Device.Address);

            return candidates;
        }
    }

    private async Task QueryDeviceStatusAsync(
        LightDeviceItem device,
        TimeSpan timeout,
        CancellationToken refreshCancellationToken)
    {
        device.IsChecking = true;
        device.Status = "连接中";
        using var timeoutCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(refreshCancellationToken);
        timeoutCancellation.CancelAfter(timeout);

        try
        {
            var state = await _agpLight.GetOnOffStateWithoutAuthorizationAsync(
                device.Address,
                timeoutCancellation.Token);
            device.IsOnline = true;
            device.Status = "在线";
            device.StatusColor = Color.FromArgb("#35C987");
            device.IsOn = state;
            return;
        }
        catch (OperationCanceledException) when (
            refreshCancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (OperationCanceledException)
        {
            MarkDeviceOffline(device);
            _diagnostics.Debug(
                "BLE/Status",
                $"设备 {device.Address} 状态查询超时，等待 {timeout.TotalMilliseconds:0} ms。");
            return;
        }
        catch (Exception exception)
        {
            MarkDeviceOffline(device);
            _diagnostics.Debug(
                "BLE/Status",
                $"设备 {device.Address} 状态查询失败：{exception.Message}");
            return;
        }
        finally
        {
            device.IsChecking = false;
        }
    }

    private static void MarkDeviceOffline(LightDeviceItem device)
    {
        device.IsOnline = false;
        device.Status = "离线";
        device.StatusColor = Color.FromArgb("#E65B65");
    }

    private async Task StopStatusRefreshAsync()
    {
        _statusRefreshCancellation?.Cancel();
        lock (_statusQueueSync)
            _pendingStatusQueries.Clear();
        if (_statusRefreshTask is not { } refreshTask)
            return;

        try
        {
            await refreshTask;
        }
        catch (OperationCanceledException)
        {
            // 用户操作取消轮询是正常流程。
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

    /// <summary>
    /// 查询中仍允许用户点击，以便取消轮询并立即执行该设备的授权控制操作。
    /// </summary>
    public bool IsEnabled => IsOnline || IsChecking;

    public double PowerOpacity => IsOnline || IsChecking ? 1 : 0.35;

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
