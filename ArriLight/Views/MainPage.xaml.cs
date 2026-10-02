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

    // Let an immediately tapped device take the connection before the passive
    // home refresh starts a public-state GATT query.
    private static readonly TimeSpan StatusQueryCollectionDelay =
        TimeSpan.FromMilliseconds(1500);

    private readonly DeviceRepository _repository;
    private readonly BluetoothDiscoveryService _discoveryService;
    private readonly AgpLightControlService _agpLight;
    private readonly BluetoothConnectionService _connectionService;
    private readonly AppDiagnosticService _diagnostics;
    private readonly object _statusQueueSync = new();
    private readonly Dictionary<string, (LightDeviceItem Device, int Rssi)>
        _pendingStatusQueries = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _statusQueryAttempted =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _statusQuerySuppressed;
    private bool _isVisible;
    private bool _controlNavigationInFlight;
    private bool _controlNavigationStarted;
    private CancellationTokenSource? _controlPagePreloadCancellation;
    private CancellationTokenSource? _statusRefreshCancellation;
    private Task _statusCancellationCallbacks = Task.CompletedTask;
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
        _connectionService = ServiceHelper.GetRequiredService<BluetoothConnectionService>();
        _diagnostics = ServiceHelper.GetRequiredService<AppDiagnosticService>();
        _repository.DevicesChanged += OnDevicesChanged;
        BindingContext = this;
        ReloadDevices();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _isVisible = true;
        _controlNavigationStarted = false;
        _agpLight.DeviceStatusChanged -= OnDeviceStatusChanged;
        _agpLight.DeviceStatusChanged += OnDeviceStatusChanged;
        ReloadDevices();
        _statusQuerySuppressed = false;
        lock (_statusQueueSync)
        {
            _pendingStatusQueries.Clear();
            _statusQueryAttempted.Clear();
        }

        _discoveryService.DeviceFound += OnDeviceFound;
        _ = StartHomeScanAsync();
        CancelControlPagePreload();
        _controlPagePreloadCancellation = new CancellationTokenSource();
        _ = PreloadControlPageAsync(_controlPagePreloadCancellation.Token);
    }

    protected override void OnDisappearing()
    {
        _isVisible = false;
        CancelControlPagePreload();
        _agpLight.DeviceStatusChanged -= OnDeviceStatusChanged;
        _discoveryService.DeviceFound -= OnDeviceFound;
        _statusQuerySuppressed = true;
        RequestStopStatusRefresh();
        // Control-page navigation owns scan teardown after the page transition.
        // Other destinations still release the home scan here.
        if (!_controlNavigationStarted)
            _ = StopHomeScanAsync();
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

    private async Task StopHomeScanAsync()
    {
        try { await _discoveryService.StopAsync(); }
        catch (Exception exception)
        {
            _diagnostics.Warning("BLE/Scan", $"主页停止扫描失败：{exception.Message}");
        }
    }

    private void CancelControlPagePreload()
    {
        _ = CancellationDispatch.Request(_controlPagePreloadCancellation, _diagnostics,
            "Home.Preload", dispose: true);
        _controlPagePreloadCancellation = null;
    }

    private async Task PreloadControlPageAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Let the home page render first. Do not warm every device or keep
            // pages that have already been used for a different device.
            await Task.Delay(250, cancellationToken);
            Dispatcher.Dispatch(() =>
            {
                if (cancellationToken.IsCancellationRequested || !_isVisible ||
                    _controlNavigationStarted || _agpLight.IsApplicationSuspended || Devices.Count == 0)
                    return;
                try { DeviceControlPageRoutes.Prepare(isDemo: Devices.All(device => device.IsDemo)); }
                catch (Exception exception)
                {
                    _diagnostics.Warning("UI/Navigation", $"控制页预加载失败，点击时重新创建：{exception.Message}");
                }
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
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

    private void OnDeviceStatusChanged(object? sender, DeviceStatusChange change) =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (!_isVisible)
                return;
            var device = Devices.FirstOrDefault(item =>
                !item.IsDemo && string.Equals(item.Address, change.Address,
                    StringComparison.OrdinalIgnoreCase));
            if (device is not null)
                ApplyCachedDeviceStatus(device);
        });

    private void ApplyCachedDeviceStatus(LightDeviceItem device)
    {
        // Read the latest snapshot: a queued offline update must not overwrite
        // a newer successful foreground reconnect.
        if (_agpLight.GetCachedDeviceStatus(device.Address) is not { } status)
            return;
        device.IsOnline = status.IsOnline;
        device.Status = status.IsOnline ? "在线" : "离线";
        device.StatusColor = Color.FromArgb(status.IsOnline ? "#35C987" : "#E65B65");
        if (status.LightState is { } lightState)
            device.IsOn = lightState.IsOn;
    }

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
            var cachedStatus = savedDevice.IsDemo ? null :
                _agpLight.GetCachedDeviceStatus(savedDevice.Address);
            var isOnline = cachedStatus?.IsOnline ?? isOnlineDemo;

            Devices.Add(new LightDeviceItem(
                savedDevice.Name,
                savedDevice.Address,
                isOnline ? "在线" : savedDevice.IsDemo || cachedStatus is not null
                    ? "离线"
                    : "已保存 · 等待连接",
                isOnline
                    ? Color.FromArgb("#35C987")
                    : Color.FromArgb(cachedStatus is null ? "#A9B2C3" : "#E65B65"),
                isOn: cachedStatus?.LightState?.IsOn ?? isOnlineDemo,
                isDemo: savedDevice.IsDemo,
                isOnline: isOnline));
        }

        OnPropertyChanged(nameof(DeviceCountText));
    }

    private async void OnDevicePowerClicked(object? sender, EventArgs e)
    {
        if (sender is not BindableObject { BindingContext: LightDeviceItem device })
            return;

        await ToggleDevicePowerAsync(device);
    }

    private async Task ToggleDevicePowerAsync(LightDeviceItem device)
    {
        using var timing = _diagnostics.BeginBleTiming(
            "UI.Home.Power", device.Address,
            $"desiredOn={!device.IsOn}; isDemo={device.IsDemo}; isChecking={device.IsChecking}");
        if (!device.IsDemo)
            _agpLight.BeginHomeControlOperation(device.Address);
        // 状态轮询不应抢占用户针对某台设备发起的授权控制操作。
        _statusQuerySuppressed = true;
        try
        {
            await StopStatusRefreshAsync();
        }
        catch (Exception exception)
        {
            timing.SetResult($"cleanup-failed:{exception.GetType().Name}");
            throw;
        }
        timing.Mark($"后台状态查询已收尾; desiredOn={!device.IsOn}");

        var previousIsOn = device.IsOn;
        try
        {
            if (device.IsDemo)
            {
                device.IsOn = !device.IsOn;
                timing.SetResult("demo");
                return;
            }

            device.IsOn = !device.IsOn;
            timing.Mark($"提交开关操作; desiredOn={device.IsOn}");
            await _agpLight.SetOnOffAsync(device.Address, device.IsOn);
            timing.SetResult("success");
        }
        catch (Exception exception)
        {
            timing.SetResult($"failed:{exception.GetType().Name}");
            device.IsOn = previousIsOn;
            ApplyCachedDeviceStatus(device);
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

    private async void OnDeviceCardTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not BindableObject { BindingContext: LightDeviceItem device })
            return;

        await OpenDeviceControlAsync(device);
    }

    private async Task OpenDeviceControlAsync(LightDeviceItem device)
    {
        if (!_isVisible || _controlNavigationInFlight || _controlNavigationStarted)
            return;
        // Hold the guard before any await. It stays closed while the home page
        // is hidden and is reset only when the user returns to the home page.
        _controlNavigationInFlight = true;
        _controlNavigationStarted = true;
#if DEBUG && ANDROID
        ControlNavigationTiming.Start(_diagnostics, device.Address);
#endif
#if DEBUG
        using var preparation = new DebugOperationTiming();
        preparation.Run("CancelPreload", CancelControlPagePreload);
#else
        CancelControlPagePreload();
#endif
        using var timing = _diagnostics.BeginBleTiming(
            "UI.Home.OpenControl", device.Address, $"isDemo={device.IsDemo}");
        if (!device.IsDemo)
        {
#if DEBUG
            preparation.Run("LeasePromotion", () => _agpLight.PromoteConnectionToLongLived(device.Address));
#else
            _agpLight.PromoteConnectionToLongLived(device.Address);
#endif
        }
        _statusQuerySuppressed = true;
        try
        {
            RequestStopStatusRefresh();
            _ = ObserveStatusRefreshCompletionAsync(_statusRefreshTask);
            timing.Mark("已请求取消主页查询，立即导航；BLE 收尾在页面显示后等待");
#if DEBUG
            preparation.Emit(_diagnostics, device.Address);
#endif

            // 控制页异步连接并认证；已有查询结果直接复用，不重复读取 Light State。
            var route = device.IsDemo
                ? nameof(HslCtlDeviceControlPage)
                : nameof(DeviceControlPage);

            using (var navigation = _diagnostics.BeginBleTiming(
                       "UI.Home.NavigateControl", device.Address, $"route={route}"))
            {
                await Shell.Current.GoToAsync(
                    $"{route}?name={Uri.EscapeDataString(device.Name)}" +
                    $"&address={Uri.EscapeDataString(device.Address)}", animate: false);
                navigation.SetResult("success");
#if DEBUG && ANDROID
                ControlNavigationTiming.Current?.Mark("GoToAsync.Returned");
#endif
            }
            timing.SetResult("success");
        }
        catch (Exception exception)
        {
            timing.SetResult($"failed:{exception.GetType().Name}");
#if DEBUG && ANDROID
            ControlNavigationTiming.Current?.Mark("NavigationFailed", exception.GetType().Name);
            ControlNavigationTiming.Current?.Dispose();
#endif
            _controlNavigationStarted = false;
            if (_isVisible)
                _statusQuerySuppressed = false;
            _diagnostics.Error("UI/Navigation", $"打开设备控制页失败：{device.Address}。", exception);
            ToastService.Show("打开设备失败，请重试");
        }
        finally
        {
            _controlNavigationInFlight = false;
        }
    }

    private async Task ObserveStatusRefreshCompletionAsync(Task? refreshTask)
    {
        if (refreshTask is null)
            return;
        try { await refreshTask; }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            _diagnostics.Warning("BLE/Status", $"导航后的主页查询收尾失败：{exception.Message}");
        }
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
        _statusCancellationCallbacks = Task.CompletedTask;
        var cancellationToken = refreshCancellation.Token;
        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                   !_statusQuerySuppressed)
            {
                // 给同一轮扫描中的多个广播留出收集窗口，也给用户在设备
                // 刚出现时点击开关/进入控制页的操作留出优先机会。
                await Task.Delay(StatusQueryCollectionDelay, cancellationToken);
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
            // Keep the source alive until asynchronously dispatched callbacks
            // finish. Disposal must not race their linked-token registrations.
            await _statusCancellationCallbacks;
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
            await _agpLight.GetOnOffStateWithoutAuthorizationAsync(
                device.Address,
                timeoutCancellation.Token);
            ApplyCachedDeviceStatus(device);
            return;
        }
        catch (OperationCanceledException) when (
            refreshCancellationToken.IsCancellationRequested ||
            _agpLight.IsApplicationSuspended)
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
            ApplyCachedDeviceStatus(device);
        }
    }

    private void MarkDeviceOffline(LightDeviceItem device)
    {
        // A home query can time out while queued behind foreground restoration.
        // It must not disable a connection that has already been restored.
        if (_connectionService.IsConnected(device.Address))
        {
            ApplyCachedDeviceStatus(device);
            return;
        }
        _agpLight.RecordDeviceOffline(device.Address);
        ApplyCachedDeviceStatus(device);
    }

    private void RequestStopStatusRefresh()
    {
#if DEBUG
        var preparation = DebugOperationTiming.Current;
        if (preparation is not null)
        {
            preparation.Run("CancelRequest", RequestCancellation);
            preparation.Run("QueueLockWait", () => Monitor.Enter(_statusQueueSync));
            try { preparation.Run("QueueClear", _pendingStatusQueries.Clear); }
            finally { Monitor.Exit(_statusQueueSync); }
            return;
        }
#endif
        RequestCancellation();
        lock (_statusQueueSync)
            _pendingStatusQueries.Clear();

        void RequestCancellation()
        {
            if (_statusRefreshCancellation is { IsCancellationRequested: false } source)
                _statusCancellationCallbacks = CancellationDispatch.Request(source, _diagnostics, "Home.StatusRefresh");
        }
    }

    private async Task StopStatusRefreshAsync()
    {
        using var timing = _diagnostics.BeginBleTiming(
            "UI.Home.StopStatusRefresh", detail:
            $"hasCancellation={_statusRefreshCancellation is not null}; hasTask={_statusRefreshTask is not null}; taskCompleted={_statusRefreshTask?.IsCompleted}");
        RequestStopStatusRefresh();
        timing.Mark("已请求取消并清空待查询队列");
        if (_statusRefreshTask is not { } refreshTask)
        {
            timing.SetResult("no-task");
            return;
        }

        try
        {
            await refreshTask;
            timing.SetResult("success");
        }
        catch (OperationCanceledException)
        {
            timing.SetResult("canceled");
            // 用户操作取消轮询是正常流程。
        }
        catch (Exception exception)
        {
            timing.SetResult($"failed:{exception.GetType().Name}");
            throw;
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
