using System.Collections.Concurrent;
using System.Text;
using ArriOpto.BLE.Agp;
using ArriOpto.BLE.Mesh;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;

namespace ArriLight;

public sealed class BluetoothDiscoveryService(
    IBluetoothLE bluetoothLE,
    IAdapter adapter,
    AppDiagnosticService diagnostics)
{
    private static readonly TimeSpan DefaultBackgroundScanDuration =
        TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly ConcurrentDictionary<string, IDevice> _knownDevices =
        new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _backgroundScanCancellation;
    private Task? _backgroundScanTask;
    private DateTimeOffset _backgroundScanDeadline;

    public event EventHandler<DiscoveredBluetoothDevice>? DeviceFound;

    public bool IsScanning => adapter.IsScanning;

    /// <summary>仅添加设备页调用；页面取消令牌同时终止原生扫描。</summary>
    public async Task ScanAsync(
        CancellationToken cancellationToken = default,
        TimeSpan? duration = null)
    {
        var scanDuration = duration ?? DefaultBackgroundScanDuration;
        if (scanDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureReadyAsync();
        // Permission dialogs can finish after the user leaves the page.
        cancellationToken.ThrowIfCancellationRequested();
        var scanTask = await EnsureScanTaskAsync(scanDuration, cancellationToken);
        await scanTask;
        cancellationToken.ThrowIfCancellationRequested();
    }

    public IDevice? GetCachedDevice(string address) =>
        _knownDevices.TryGetValue(address, out var device)
            ? device
            : null;

    public void InvalidateCachedDevice(string address)
    {
        if (!string.IsNullOrWhiteSpace(address))
            _knownDevices.TryRemove(address, out _);
    }

    public async Task<IDevice> FindDeviceAsync(
        string address,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default,
        bool readinessChecked = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (GetCachedDevice(address) is { } cached) return cached;
        if (!readinessChecked) await EnsureReadyAsync();
        cancellationToken.ThrowIfCancellationRequested();
        using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectionCancellation.CancelAfter(timeout ?? TimeSpan.FromSeconds(15));
        // Reconnect by saved MAC/UUID; discovering nearby devices is owned only
        // by the add-device page, including after a cold start or QR import.
        var device = await adapter.ConnectToKnownDeviceAsync(
            BluetoothAddressParser.ToDeviceId(address), default, connectionCancellation.Token);
        // The caller must see a late success to release it before retrying.
        _knownDevices[address] = device;
        return device;
    }
    public async Task StopAsync()
    {
        using var timing = diagnostics.BeginBleTiming(
            "BLE.Scan.Stop", detail: $"isScanning={adapter.IsScanning}");
        Task? scanTask;
        using (var waitLock = diagnostics.BeginBleTiming("BLE.Scan.Stop.Lock"))
        {
            await _scanLock.WaitAsync();
            waitLock.SetResult("acquired");
        }
        try
        {
            _backgroundScanCancellation?.Cancel();
            scanTask = _backgroundScanTask;
            timing.Mark($"已请求取消后台扫描; hasTask={scanTask is not null}; taskCompleted={scanTask?.IsCompleted}");
        }
        finally
        {
            _scanLock.Release();
        }

        if (scanTask is not null)
        {
            using var waitBackground = diagnostics.BeginBleTiming("BLE.Scan.Stop.WaitBackground");
            try
            {
                await scanTask;
                waitBackground.SetResult("success");
            }
            catch (OperationCanceledException)
            {
                waitBackground.SetResult("canceled");
                // 停止扫描是正常流程。
            }
            catch (Exception exception)
            {
                waitBackground.SetResult($"failed:{exception.GetType().Name}");
                timing.SetResult($"background-failed:{exception.GetType().Name}");
                throw;
            }
        }

        if (adapter.IsScanning && ReferenceEquals(scanTask, _backgroundScanTask))
        {
            using var nativeStop = diagnostics.BeginBleTiming("BLE.Scan.Stop.Native");
            try
            {
                await adapter.StopScanningForDevicesAsync();
                nativeStop.SetResult("success");
            }
            catch (Exception exception)
            {
                nativeStop.SetResult($"failed:{exception.GetType().Name}");
                timing.SetResult($"native-failed:{exception.GetType().Name}");
                throw;
            }
        }
        else
        {
            timing.Mark("后台任务已收尾，原生扫描已停止，无需重复停止");
        }
        timing.SetResult("success");
    }

    private async Task<Task> EnsureScanTaskAsync(
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            Task? canceledScanTask = null;
            await _scanLock.WaitAsync(cancellationToken);
            try
            {
                if (_backgroundScanTask is { IsCompleted: false } activeTask)
                {
                    if (_backgroundScanCancellation?.IsCancellationRequested != true)
                    {
                        _backgroundScanCancellation?.Cancel();
                    }

                    canceledScanTask = activeTask;
                }
                else
                {
                    _backgroundScanDeadline = DateTimeOffset.UtcNow + duration;
                    _backgroundScanCancellation?.Dispose();
                    cancellationToken.ThrowIfCancellationRequested();
                    _backgroundScanCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    _backgroundScanTask = RunBackgroundScanAsync(
                        _backgroundScanCancellation.Token);
                    return _backgroundScanTask;
                }
            }
            finally
            {
                _scanLock.Release();
            }

            if (canceledScanTask is not null)
            {
                try
                {
                    await canceledScanTask;
                }
                catch (OperationCanceledException)
                {
                    // 等待上一轮扫描完成后再启动新窗口。
                }
            }
        }
    }

    private async Task RunBackgroundScanAsync(CancellationToken cancellationToken)
    {
        diagnostics.Information(
            "BLE/Scan",
            "添加设备页开始搜索附近 BLE 广播。");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var remaining = _backgroundScanDeadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    break;

                adapter.ScanTimeout = (int)Math.Clamp(
                    remaining.TotalMilliseconds,
                    1_000,
                    60_000);
                adapter.ScanMode = ScanMode.LowLatency;
                adapter.ScanMatchMode = ScanMatchMode.AGRESSIVE;
                adapter.DeviceAdvertised += OnDeviceAdvertised;
                try
                {
                    await adapter.StartScanningForDevicesAsync(
                        new ScanFilterOptions(),
                        null,
                        true,
                        cancellationToken);
                }
                finally
                {
                    adapter.DeviceAdvertised -= OnDeviceAdvertised;
                }

                if (cancellationToken.IsCancellationRequested)
                    break;

                // 某些 Android 版本可能提前结束一次扫描；在窗口尚未到期时
                // 立即续扫，避免页面看到扫描已经停止。
                await Task.Yield();
            }
        }
        catch (OperationCanceledException)
        {
            // StopAsync 或窗口结束触发的取消是正常流程。
        }
        catch (Exception exception)
        {
            diagnostics.Warning(
                "BLE/Scan",
                $"后台 BLE 扫描结束：{exception.Message}");
            throw;
        }
        finally
        {
            if (adapter.IsScanning)
            {
                using var nativeStop = diagnostics.BeginBleTiming("BLE.Scan.Background.StopNative");
                try
                {
                    await adapter.StopScanningForDevicesAsync();
                    nativeStop.SetResult("success");
                }
                catch (Exception exception)
                {
                    nativeStop.SetResult($"failed:{exception.GetType().Name}");
                    diagnostics.Warning(
                        "BLE/Scan",
                        $"停止后台 BLE 扫描失败：{exception.Message}");
                }
            }

            diagnostics.Information("BLE/Scan", "后台 BLE 设备搜索结束。");
        }
    }

    private void OnDeviceAdvertised(
        object? sender,
        DeviceEventArgs eventArgs)
    {
        var device = eventArgs.Device;
        var isFirstObservation = CacheDevice(device);
        var address = GetDeviceAddress(device);
        var name = GetDeviceDisplayName(device);
        var isPairingOpen = AgpAdvertisement.IsPairingOpen(device.AdvertisementRecords);
        var hasAgpStatus = AgpAdvertisement.TryGetBroadcastStatus(
            device.AdvertisementRecords,
            out _);
        var isMesh = MeshAdvertisement.TryGetKind(
            device.AdvertisementRecords,
            out var meshKind);
        var protocol = isMesh
            ? BluetoothDeviceProtocol.Mesh
            : hasAgpStatus
                ? BluetoothDeviceProtocol.Agp
                : BluetoothDeviceProtocol.Unknown;

        if (isFirstObservation)
        {
            var recordTypes = string.Join(
                ",",
                device.AdvertisementRecords.Select(record => record.Type));
            diagnostics.Information(
                "BLE/Scan",
                $"发现 BLE 广播：name={name}，address={address}，RSSI={device.Rssi}，protocol={protocol}，meshKind={meshKind}，records=[{recordTypes}]。");
        }

        DeviceFound?.Invoke(
            this,
            new DiscoveredBluetoothDevice(
                name,
                address,
                device.Rssi,
                isPairingOpen,
                protocol,
                meshKind));
    }

    private bool CacheDevice(IDevice device)
    {
        var address = GetDeviceAddress(device);
        var isFirstObservation = _knownDevices.TryAdd(address, device);
        if (!isFirstObservation)
            _knownDevices[address] = device;
        return isFirstObservation;
    }

    private static string GetDeviceDisplayName(IDevice device)
    {
        if (!string.IsNullOrWhiteSpace(device.Name))
            return device.Name.Trim();

        var nameRecord = device.AdvertisementRecords.FirstOrDefault(record =>
            record.Type is AdvertisementRecordType.CompleteLocalName or
                AdvertisementRecordType.ShortLocalName or
                AdvertisementRecordType.BroadcastName);
        if (nameRecord?.Data is { Length: > 0 } nameData)
        {
            var advertisedName = Encoding.UTF8
                .GetString(nameData)
                .Trim('\0', ' ');
            if (!string.IsNullOrWhiteSpace(advertisedName))
                return advertisedName;
        }

        return AppResources.Device_UnnamedBle;
    }

    public static string GetDeviceAddress(IDevice device)
    {
#if ANDROID
        if (device.NativeDevice is Android.Bluetooth.BluetoothDevice nativeDevice &&
            !string.IsNullOrWhiteSpace(nativeDevice.Address))
        {
            return nativeDevice.Address.ToUpperInvariant();
        }
#endif
        return device.Id.ToString("D").ToUpperInvariant();
    }

    internal async Task PrepareConnectionAsync(bool automatic)
    {
        if (!automatic) { await EnsureReadyAsync(); return; }
        // Automatic recovery never opens a permission dialog. A foreground/manual
        // action can request access, and the next recovery tick observes the change.
#if ANDROID
        if (OperatingSystem.IsAndroidVersionAtLeast(31) &&
            await Permissions.CheckStatusAsync<Permissions.Bluetooth>() != PermissionStatus.Granted)
            throw new BluetoothDiscoveryException(AppResources.Ble_NearbyPermissionRequired);
        if (await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>() != PermissionStatus.Granted)
            throw new BluetoothDiscoveryException(AppResources.Ble_LocationPermissionRequired);
#endif
        if (bluetoothLE.State != BluetoothState.On)
            throw new BluetoothDiscoveryException(AppResources.Ble_Disabled);
    }

    private async Task EnsureReadyAsync()
    {
        await EnsurePermissionAsync();

        if (bluetoothLE.State != BluetoothState.On)
            throw new BluetoothDiscoveryException(
                AppResources.Ble_Disabled);
    }

    private static async Task EnsurePermissionAsync()
    {
#if ANDROID
        if (OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            var bluetoothStatus =
                await Permissions.RequestAsync<Permissions.Bluetooth>();
            if (bluetoothStatus != PermissionStatus.Granted)
            {
                throw new BluetoothDiscoveryException(
                    AppResources.Ble_NearbyPermissionRequired);
            }
        }

        var locationStatus =
            await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
        if (locationStatus != PermissionStatus.Granted)
        {
            throw new BluetoothDiscoveryException(
                AppResources.Ble_LocationPermissionRequired);
        }
#endif
    }
}

public sealed record DiscoveredBluetoothDevice(
    string Name,
    string Address,
    int Rssi,
    bool IsPairingOpen,
    BluetoothDeviceProtocol Protocol = BluetoothDeviceProtocol.Unknown,
    MeshAdvertisementKind MeshKind = MeshAdvertisementKind.None)
{
    public bool IsMesh => Protocol == BluetoothDeviceProtocol.Mesh;

    public string ProtocolLabel => IsMesh
        ? AppResources.Mesh_Badge
        : AppResources.Gatt_Badge;

    public string IconSource => IsMesh ? "mesh.svg" : "device_light.svg";
}

public sealed class BluetoothDiscoveryException(string message)
    : Exception(message);
