using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ArriLight;

public partial class MainPage : ContentPage
{
    private static readonly TimeSpan FirstStatusCheckTimeout =
        TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan RetryStatusCheckTimeout =
        TimeSpan.FromSeconds(1);

    private readonly DeviceRepository _repository;
    private readonly AgpLightControlService _agpLight;
    private readonly AppDiagnosticService _diagnostics;
    private bool _isRefreshing;
    private CancellationTokenSource? _statusRefreshCancellation;
    private Task? _statusRefreshTask;

    public ObservableCollection<LightDeviceItem> Devices { get; } = [];

    public string DeviceCountText => $"{Devices.Count} 台设备";

    public MainPage()
    {
        InitializeComponent();
        _repository = ServiceHelper.GetRequiredService<DeviceRepository>();
        _agpLight = ServiceHelper.GetRequiredService<AgpLightControlService>();
        _diagnostics = ServiceHelper.GetRequiredService<AppDiagnosticService>();
        _repository.DevicesChanged += OnDevicesChanged;
        BindingContext = this;
        ReloadDevices();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        ReloadDevices();
        await StartStatusRefreshAsync();
    }

    protected override void OnDisappearing()
    {
        _statusRefreshCancellation?.Cancel();
        base.OnDisappearing();
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
        await StopStatusRefreshAsync();

        if (device.IsDemo)
        {
            device.IsOn = !device.IsOn;
            return;
        }

        device.IsOn = !device.IsOn;
        try
        {
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
    }

    private async void OnDeviceManageClicked(object? sender, EventArgs e)
    {
        if (sender is not BindableObject { BindingContext: LightDeviceItem device })
            return;

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
        if (_statusRefreshTask is { IsCompleted: false } activeTask)
        {
            await activeTask;
            return;
        }

        var refreshTask = RefreshDeviceStatusesAsync();
        _statusRefreshTask = refreshTask;
        await refreshTask;
    }

    /// <summary>
    /// 主页状态轮询分两遍执行：第一遍每台设备只等待 300ms，
    /// 第二遍只重试第一遍超时的设备，等待时间放宽到 1 秒。
    /// 查询只读取公开 Light State，不执行 AGP Token 授权。
    /// </summary>
    private async Task RefreshDeviceStatusesAsync()
    {
        if (_isRefreshing)
            return;

        _isRefreshing = true;
        using var refreshCancellation = new CancellationTokenSource();
        _statusRefreshCancellation = refreshCancellation;
        var cancellationToken = refreshCancellation.Token;
        try
        {
            var devices = Devices.Where(item => !item.IsDemo).ToList();
            var timedOutDevices = new List<LightDeviceItem>();

            foreach (var device in devices)
            {
                var result = await QueryDeviceStatusAsync(
                    device,
                    FirstStatusCheckTimeout,
                    cancellationToken);
                if (result == StatusQueryResult.Cancelled)
                    return;
                if (result == StatusQueryResult.TimedOut)
                    timedOutDevices.Add(device);
            }

            if (timedOutDevices.Count == 0 || cancellationToken.IsCancellationRequested)
                return;

            foreach (var device in timedOutDevices)
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                if (await QueryDeviceStatusAsync(
                        device,
                        RetryStatusCheckTimeout,
                        cancellationToken) == StatusQueryResult.Cancelled)
                {
                    return;
                }
            }
        }
        finally
        {
            _statusRefreshCancellation = null;
            _isRefreshing = false;
        }
    }

    private async Task<StatusQueryResult> QueryDeviceStatusAsync(
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
            return StatusQueryResult.Online;
        }
        catch (OperationCanceledException) when (
            refreshCancellationToken.IsCancellationRequested)
        {
            return StatusQueryResult.Cancelled;
        }
        catch (OperationCanceledException)
        {
            MarkDeviceOffline(device);
            _diagnostics.Debug(
                "BLE/Status",
                $"设备 {device.Address} 状态查询超时，等待 {timeout.TotalMilliseconds:0} ms。");
            return StatusQueryResult.TimedOut;
        }
        catch (Exception exception)
        {
            MarkDeviceOffline(device);
            _diagnostics.Debug(
                "BLE/Status",
                $"设备 {device.Address} 状态查询失败：{exception.Message}");
            return StatusQueryResult.Failed;
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

    private enum StatusQueryResult
    {
        Online,
        TimedOut,
        Failed,
        Cancelled
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
