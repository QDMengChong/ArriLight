using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ArriLight.ViewModels;

public partial class MainPageViewModel : ViewModelBase
{
    private readonly IUiDispatcher _dispatcher;
    private readonly IAppNavigator _navigator;
    private readonly IUserInteraction _interaction;
    public event Action<string>? PreparingControl;
    public event Action? ControlNavigationCompleted;
    public event Action<Exception>? ControlNavigationFailed;
    public bool CanPreloadControl => _isVisible && !_controlNavigationStarted &&
        !_agpLight.IsApplicationSuspended && Devices.Count > 0;

    public MainPageViewModel(DeviceRepository repository, BluetoothDiscoveryService discoveryService,
        AgpLightControlService agpLight, BluetoothConnectionService connectionService,
        AppDiagnosticService diagnostics, IUiDispatcher dispatcher, IAppNavigator navigator,
        IUserInteraction interaction, MeshDeviceControlService meshControl)
    {
        _repository = repository;
        _discoveryService = discoveryService;
        _agpLight = agpLight;
        _connectionService = connectionService;
        _diagnostics = diagnostics;
        _meshControl = meshControl;
        _dispatcher = dispatcher;
        _navigator = navigator;
        _interaction = interaction;
        _selectedSortIndex = (int)_repository.GetSortOrder();
        Devices.CollectionChanged += (_, _) => OnPropertyChanged(nameof(DeviceCountText));
        ReloadDevices();
    }

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
    private readonly MeshDeviceControlService _meshControl;
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
    private CancellationTokenSource? _statusRefreshCancellation;
    private Task _statusCancellationCallbacks = Task.CompletedTask;
    private Task? _statusRefreshTask;
    private Guid? _lastUnknownToastOperation;

    public ObservableCollection<LightDeviceItem> Devices { get; } = [];

    public IReadOnlyList<string> SortOptions { get; } =
        [AppResources.Home_SortLastOperation, AppResources.Home_SortAddedAt, AppResources.Home_SortName];

    [ObservableProperty]
    private int _selectedSortIndex;

    partial void OnSelectedSortIndexChanged(int value)
    {
        if (value is < 0 or > 2)
            return;
        _repository.SetSortOrder((DeviceSortOrder)value);
        ReloadDevices();
    }

    public string DeviceCountText => Localization.Format(nameof(AppResources.Home_DeviceCount), Devices.Count);

    public override Task OnAppearingAsync()
    {
        _repository.DevicesChanged -= OnDevicesChanged;
        _repository.DevicesChanged += OnDevicesChanged;
        _isVisible = true;
        _controlNavigationStarted = false;
        _agpLight.DeviceStatusChanged -= OnDeviceStatusChanged;
        _agpLight.DeviceStatusChanged += OnDeviceStatusChanged;
        _agpLight.RuntimeStateChanged -= OnRuntimeStateChanged;
        _agpLight.RuntimeStateChanged += OnRuntimeStateChanged;
        _meshControl.RuntimeStateChanged -= OnMeshRuntimeStateChanged;
        _meshControl.RuntimeStateChanged += OnMeshRuntimeStateChanged;
        ReloadDevices();
        _statusQuerySuppressed = false;
        lock (_statusQueueSync)
        {
            _pendingStatusQueries.Clear();
            _statusQueryAttempted.Clear();
        }

        return Task.CompletedTask;
    }

    public override void OnDisappearing()
    {
        _isVisible = false;
        _repository.DevicesChanged -= OnDevicesChanged;
        _agpLight.DeviceStatusChanged -= OnDeviceStatusChanged;
        _agpLight.RuntimeStateChanged -= OnRuntimeStateChanged;
        _meshControl.RuntimeStateChanged -= OnMeshRuntimeStateChanged;
        _statusQuerySuppressed = true;
        RequestStopStatusRefresh();
    }

    private void OnDeviceFound(
        object? sender,
        DiscoveredBluetoothDevice discovered)
    {
        if (_statusQuerySuppressed)
            return;

        _dispatcher.Post(() =>
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

    [RelayCommand]
    private async Task ScanQrCodeAsync()
    {
        await StopStatusRefreshAsync();
        await _navigator.GoToAsync("QrCodeScanPage");
    }

    [RelayCommand]
    private async Task AddDeviceAsync()
    {
        await StopStatusRefreshAsync();
        await _navigator.GoToAsync("BluetoothScanPage");
    }

    private void OnDevicesChanged(object? sender, EventArgs e) =>
        _dispatcher.Post(ReloadDevices);

    private void OnDeviceStatusChanged(object? sender, DeviceStatusChange change) =>
        _dispatcher.Post(() =>
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
        if (device.IsDemo)
            return;

        if (device.IsMesh)
        {
            var meshRuntime = _meshControl.GetRuntimeSnapshot(device.Address);
            var meshState = _meshControl.GetCachedState(device.Address);
            if (meshRuntime is null)
            {
                device.IsOnline = false;
                device.Status = AppResources.Device_SavedWaiting;
                device.IsReady = false;
                device.IsRuntimeBusy = false;
                device.HasPendingUserOperation = false;
                if (!device.IsPowerChanging && meshState?.IsOn is { } initialState)
                    device.IsOn = initialState;
                return;
            }

            device.IsOnline = meshRuntime.Availability is DeviceRuntimeAvailability.Online or DeviceRuntimeAvailability.Ready;
            device.Status = meshRuntime.Availability switch
            {
                DeviceRuntimeAvailability.Ready => AppResources.Device_Ready,
                DeviceRuntimeAvailability.Online => AppResources.Device_Online,
                _ when meshRuntime.IsBusy => AppResources.Ble_Connecting,
                _ => AppResources.Device_Offline
            };
            device.IsReady = meshRuntime.Availability == DeviceRuntimeAvailability.Ready;
            device.IsRuntimeBusy = meshRuntime.IsBusy;
            device.HasPendingUserOperation = meshRuntime.HasPendingUserOperation;
            if (!device.IsPowerChanging && meshState?.IsOn is { } knownState)
                device.IsOn = knownState;
            return;
        }

        var runtime = _agpLight.GetRuntimeSnapshot(device.Address);
        var cachedStatus = _agpLight.GetCachedDeviceStatus(device.Address);
        if (cachedStatus?.LightState is { } knownLightState)
            device.IsLocked = knownLightState.IsLocked;
        var availability = runtime?.Availability ?? (cachedStatus?.IsOnline == true
            ? DeviceRuntimeAvailability.Online : DeviceRuntimeAvailability.Offline);
        device.IsOnline = availability is DeviceRuntimeAvailability.Online or DeviceRuntimeAvailability.Ready;
        device.Status = availability switch
        {
            DeviceRuntimeAvailability.Ready => AppResources.Device_Ready,
            DeviceRuntimeAvailability.Online => AppResources.Device_Online,
            _ => AppResources.Device_Offline
        };
        device.IsReady = availability == DeviceRuntimeAvailability.Ready;
        device.BatteryLevelPercent = device.IsOnline ? cachedStatus?.Profile?.BatteryState?.LevelPercent : null;
        device.IsRuntimeBusy = runtime?.IsBusy == true;
        device.HasPendingUserOperation = runtime?.HasPendingUserOperation == true;
        // Apply the latest confirmed state after the pending toggle finishes.
        if (!device.IsPowerChanging &&
            _agpLight.GetCachedDeviceStatus(device.Address)?.LightState is { } lightState)
            device.IsOn = lightState.IsOn;
    }

    private void OnRuntimeStateChanged(object? sender, DeviceRuntimeChange change) =>
        _dispatcher.Post(() =>
        {
            if (!_isVisible) return;
            var device = Devices.FirstOrDefault(item => !item.IsDemo &&
                string.Equals(item.Address, change.Address, StringComparison.OrdinalIgnoreCase));
            if (device is not null) ApplyCachedDeviceStatus(device);
        });

    private void OnMeshRuntimeStateChanged(object? sender, DeviceRuntimeChange change) =>
        _dispatcher.Post(() =>
        {
            if (!_isVisible) return;
            var device = Devices.FirstOrDefault(item => !item.IsDemo && item.IsMesh &&
                string.Equals(item.Address, change.Address, StringComparison.OrdinalIgnoreCase));
            if (device is not null) ApplyCachedDeviceStatus(device);
        });

    private void ReloadDevices()
    {
        var savedDevices = _repository.GetAll();
        var previousItems = Devices.ToDictionary(item => item.Address, StringComparer.OrdinalIgnoreCase);
        var pinned = savedDevices.OrderByDescending(device => device.IsPinned);
        var sorted = (DeviceSortOrder)SelectedSortIndex switch
        {
            DeviceSortOrder.AddedAt => pinned.ThenByDescending(device => device.AddedAt),
            DeviceSortOrder.Name => pinned.ThenBy(device => device.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            _ => pinned.ThenByDescending(device => device.LastOperatedAt ?? DateTimeOffset.MinValue)
                .ThenByDescending(device => device.AddedAt)
        };
        var items = new List<LightDeviceItem>();

        foreach (var savedDevice in sorted.ThenBy(device => device.Address, StringComparer.OrdinalIgnoreCase))
        {
            if (previousItems.TryGetValue(savedDevice.Address, out var existing) &&
                existing.Name == savedDevice.DisplayName &&
                existing.Protocol == savedDevice.EffectiveProtocol)
            {
                ApplyCachedDeviceStatus(existing);
                items.Add(existing);
                continue;
            }
            var isOnlineDemo = savedDevice.IsDemo &&
                savedDevice.Address.EndsWith("01", StringComparison.Ordinal);
            var cachedStatus = savedDevice.IsDemo || savedDevice.EffectiveProtocol == BluetoothDeviceProtocol.Mesh ? null :
                _agpLight.GetCachedDeviceStatus(savedDevice.Address);
            var meshState = savedDevice.EffectiveProtocol == BluetoothDeviceProtocol.Mesh
                ? _meshControl.GetCachedState(savedDevice.Address)
                : null;
            var meshRuntime = savedDevice.EffectiveProtocol == BluetoothDeviceProtocol.Mesh
                ? _meshControl.GetRuntimeSnapshot(savedDevice.Address)
                : null;
            var isOnline = savedDevice.EffectiveProtocol == BluetoothDeviceProtocol.Mesh
                ? meshRuntime?.Availability is DeviceRuntimeAvailability.Online or DeviceRuntimeAvailability.Ready
                : cachedStatus?.IsOnline ?? isOnlineDemo;

            var item = new LightDeviceItem(
                savedDevice.DisplayName,
                savedDevice.Address,
                isOnline ? AppResources.Device_Online : savedDevice.EffectiveProtocol == BluetoothDeviceProtocol.Mesh
                    ? AppResources.Device_SavedWaiting : savedDevice.IsDemo || cachedStatus is not null
                    ? AppResources.Device_Offline
                    : AppResources.Device_SavedWaiting,
                isOn: meshState?.IsOn ?? cachedStatus?.LightState?.IsOn ?? isOnlineDemo,
                isDemo: savedDevice.IsDemo,
                isOnline: isOnline,
                protocol: savedDevice.EffectiveProtocol);
            ApplyCachedDeviceStatus(item);
            items.Add(item);
        }

        // Reorder live rows without replacing their pending-operation guards.
        for (var index = Devices.Count - 1; index >= 0; index--)
            if (!items.Contains(Devices[index])) Devices.RemoveAt(index);
        for (var index = 0; index < items.Count; index++)
        {
            var current = Devices.IndexOf(items[index]);
            if (current < 0) Devices.Insert(index, items[index]);
            else if (current != index) Devices.Move(current, index);
        }
        OnPropertyChanged(nameof(DeviceCountText));
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task TogglePowerAsync(LightDeviceItem device)
    {
        if (!device.IsEnabled)
            return;

        var previousIsOn = device.IsOn;
        var previousOperationId = _agpLight.GetRuntimeSnapshot(device.Address)?.LastOperation?.Id;
        var desiredOn = !previousIsOn;
        using var timing = _diagnostics.BeginBleTiming(
            "UI.Home.Power", device.Address,
            $"operation={(device.IsMesh ? "MeshSet" : "Toggle")}; displayedIsOn={previousIsOn}; isDemo={device.IsDemo}; isChecking={device.IsChecking}");
        // Guard before awaiting cleanup so repeated clicks cannot send two toggles.
        device.IsPowerChanging = true;
        // 状态轮询不应抢占用户针对某台设备发起的授权控制操作。
        _statusQuerySuppressed = true;
        try
        {
            if (!device.IsDemo && !device.IsMesh)
                _agpLight.BeginHomeControlOperation(device.Address);
            await StopStatusRefreshAsync();
            timing.Mark("后台状态查询已收尾");

            if (device.IsDemo)
            {
                device.IsOn = desiredOn;
                _repository.RecordOperation(device.Address);
                timing.SetResult("demo");
                return;
            }

            timing.Mark("提交开关操作");
            if (device.IsMesh)
            {
                // Mesh 的 Generic OnOff Set 只有绝对目标值，没有 Toggle 语义：
                // 先查询设备状态取其反转，查询不到时默认发送开。
                device.IsOn = await _meshControl.ToggleOrTurnOnAsync(device.Address);
            }
            else
            {
                await _agpLight.ToggleOnOffAsync(device.Address);
                NotifyUnknownOutcomeIfNeeded(device.Address, previousOperationId);
            }
            _repository.RecordOperation(device.Address);
            timing.SetResult("success");
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            timing.SetResult($"failed:{exception.GetType().Name}");
            // Restore the latest reported state directly, avoiding a temporary
            // rollback to an older display value when preparation read new state.
            device.IsOn = device.IsMesh
                ? previousIsOn
                : _agpLight.GetCachedDeviceStatus(device.Address)?.LightState?.IsOn
                  ?? previousIsOn;
            _diagnostics.Error(
                "Light/Power",
                $"列表页发送开关指令失败：{device.Address}。",
                exception);
            var meshFailed = device.IsMesh ||
                !NotifyUnknownOutcomeIfNeeded(device.Address, previousOperationId);
            if (meshFailed && _isVisible && !_agpLight.IsApplicationSuspended)
                _interaction.ShowToast(AppResources.Control_SendFailed);
        }
        finally
        {
            device.IsPowerChanging = false;
            ApplyCachedDeviceStatus(device);
            _statusQuerySuppressed = false;
        }
    }

    private bool NotifyUnknownOutcomeIfNeeded(string address, Guid? previousOperationId)
    {
        if (_agpLight.GetRuntimeSnapshot(address)?.LastOperation is not { Stage: DeviceRuntimeOperationStage.Unknown } operation)
            return false;
        if (operation.Id == previousOperationId) return false;
        if (_isVisible && !_agpLight.IsApplicationSuspended && _lastUnknownToastOperation != operation.Id)
        {
            _lastUnknownToastOperation = operation.Id;
            _interaction.ShowToast(AppResources.Control_StateUnavailable);
        }
        return true;
    }

    [RelayCommand]
    private async Task OpenControlAsync(LightDeviceItem device)
    {
        if (!_isVisible || _controlNavigationInFlight || _controlNavigationStarted)
            return;
        // Hold the guard before any await. It stays closed while the home page
        // is hidden and is reset only when the user returns to the home page.
        _controlNavigationInFlight = true;
        _controlNavigationStarted = true;
#if DEBUG
        using var preparation = new DebugOperationTiming();
        preparation.Run("CancelPreload", () => PreparingControl?.Invoke(device.Address));
#else
        PreparingControl?.Invoke(device.Address);
#endif
        using var timing = _diagnostics.BeginBleTiming(
            "UI.Home.OpenControl", device.Address, $"isDemo={device.IsDemo}");
        if (!device.IsDemo && !device.IsMesh)
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
                ? "HslCtlDeviceControlPage"
                : "DeviceControlPage";

            using (var navigation = _diagnostics.BeginBleTiming(
                       "UI.Home.NavigateControl", device.Address, $"route={route}"))
            {
                await _navigator.GoToAsync(
                    $"{route}?name={Uri.EscapeDataString(device.Name)}" +
                    $"&address={Uri.EscapeDataString(device.Address)}", animate: false);
                navigation.SetResult("success");
                ControlNavigationCompleted?.Invoke();
            }
            timing.SetResult("success");
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            timing.SetResult($"failed:{exception.GetType().Name}");
            ControlNavigationFailed?.Invoke(exception);
            _controlNavigationStarted = false;
            if (_isVisible)
                _statusQuerySuppressed = false;
            _diagnostics.Error("UI/Navigation", $"打开设备控制页失败：{device.Address}。", exception);
            _interaction.ShowToast(AppResources.Home_OpenDeviceFailed);
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
            ErrorMessage = exception.Message;
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
        device.Status = AppResources.Ble_Connecting;
        using var timeoutCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(refreshCancellationToken);
        timeoutCancellation.CancelAfter(timeout);

        try
        {
            if (device.IsMesh)
            {
                await _meshControl.GetOnOffStateAsync(
                    device.Address,
                    timeoutCancellation.Token);
            }
            else
            {
                await _agpLight.GetOnOffStateWithoutAuthorizationAsync(
                    device.Address,
                    timeoutCancellation.Token);
            }
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
            ErrorMessage = exception.Message;
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
        if (device.IsMesh)
            _meshControl.RecordDeviceOffline(device.Address);
        else
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
            ErrorMessage = exception.Message;
            timing.SetResult($"failed:{exception.GetType().Name}");
            throw;
        }
    }

}
