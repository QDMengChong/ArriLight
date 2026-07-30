using System.Collections.Concurrent;
using System.Text;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;

namespace ArriLight;

public sealed class BluetoothDiscoveryService(
    IBluetoothLE bluetoothLE,
    IAdapter adapter,
    AppDiagnosticService diagnostics)
{
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly ConcurrentDictionary<string, IDevice> _knownDevices =
        new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<DiscoveredBluetoothDevice>? DeviceFound;

    public bool IsScanning => adapter.IsScanning;

    public async Task ScanAsync(CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync();

        await _scanLock.WaitAsync(cancellationToken);
        try
        {
            if (adapter.IsScanning)
                await adapter.StopScanningForDevicesAsync();

            adapter.ScanTimeout = 12_000;
            adapter.ScanMode = ScanMode.LowLatency;
            adapter.ScanMatchMode = ScanMatchMode.AGRESSIVE;
            adapter.DeviceAdvertised += OnDeviceAdvertised;
            diagnostics.Information(
                "BLE/Scan",
                "开始低延迟搜索附近的全部 BLE 广播，超时 12 秒。");
            try
            {
                await adapter.StartScanningForDevicesAsync(
                    new ScanFilterOptions(),
                    null,
                    true,
                    cancellationToken);
                diagnostics.Information("BLE/Scan", "蓝牙设备搜索结束。");
            }
            finally
            {
                adapter.DeviceAdvertised -= OnDeviceAdvertised;
            }
        }
        finally
        {
            _scanLock.Release();
        }
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
        CancellationToken cancellationToken = default)
    {
        if (GetCachedDevice(address) is { } cached)
        {
            diagnostics.Debug(
                "BLE/Scan",
                $"使用扫描缓存中的设备：{address}，state={cached.State}。");
            return cached;
        }

        await EnsureReadyAsync();
        await _scanLock.WaitAsync(cancellationToken);
        try
        {
            if (adapter.IsScanning)
                await adapter.StopScanningForDevicesAsync();

            adapter.ScanMode = ScanMode.LowLatency;
            adapter.ScanMatchMode = ScanMatchMode.AGRESSIVE;
            adapter.ScanTimeout =
                (int)(timeout ?? TimeSpan.FromSeconds(15)).TotalMilliseconds;
            var completion = new TaskCompletionSource<IDevice>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            void OnDiscovered(object? sender, DeviceEventArgs eventArgs)
            {
                CacheDevice(eventArgs.Device);
                if (string.Equals(
                        GetDeviceAddress(eventArgs.Device),
                        address,
                        StringComparison.OrdinalIgnoreCase))
                {
                    completion.TrySetResult(eventArgs.Device);
                }
            }

            adapter.DeviceAdvertised += OnDiscovered;
            using var scanCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            scanCancellation.CancelAfter(timeout ?? TimeSpan.FromSeconds(15));

            diagnostics.Information(
                "BLE/Scan",
                $"正在重新扫描目标设备 {address}。");

            try
            {
                var scanTask = adapter.StartScanningForDevicesAsync(
                    new ScanFilterOptions(),
                    null,
                    true,
                    scanCancellation.Token);
                var completed = await Task.WhenAny(completion.Task, scanTask);
                if (completed == completion.Task)
                {
                    await adapter.StopScanningForDevicesAsync();
                    var device = await completion.Task;
                    diagnostics.Information(
                        "BLE/Scan",
                        $"已找到目标设备 {address}，name={device.Name ?? "未命名"}，RSSI={device.Rssi}。");
                    return device;
                }

                await scanTask;
                throw new BluetoothDiscoveryException(
                    $"在扫描时限内没有发现设备 {address}。");
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested)
            {
                throw new BluetoothDiscoveryException(
                    $"扫描设备 {address} 超时，请让手机靠近灯具后重试。");
            }
            finally
            {
                adapter.DeviceAdvertised -= OnDiscovered;
                if (adapter.IsScanning)
                    await adapter.StopScanningForDevicesAsync();
            }
        }
        finally
        {
            _scanLock.Release();
        }
    }

    public async Task StopAsync()
    {
        if (adapter.IsScanning)
            await adapter.StopScanningForDevicesAsync();
    }

    private void OnDeviceAdvertised(
        object? sender,
        DeviceEventArgs eventArgs)
    {
        var device = eventArgs.Device;
        var isFirstObservation = CacheDevice(device);
        var address = GetDeviceAddress(device);
        var name = GetDeviceDisplayName(device);

        if (isFirstObservation)
        {
            var recordTypes = string.Join(
                ",",
                device.AdvertisementRecords.Select(record => record.Type));
            diagnostics.Information(
                "BLE/Scan",
                $"发现 BLE 广播：name={name}，address={address}，RSSI={device.Rssi}，records=[{recordTypes}]。");
        }

        DeviceFound?.Invoke(
            this,
            new DiscoveredBluetoothDevice(
                name,
                address,
                device.Rssi));
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

        return "未命名 BLE 设备";
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

    private async Task EnsureReadyAsync()
    {
        await EnsurePermissionAsync();

        if (bluetoothLE.State != BluetoothState.On)
            throw new BluetoothDiscoveryException(
                "蓝牙未开启，请先开启手机蓝牙。");
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
                    "没有“附近设备”权限，请在系统设置中允许 ArriLight 查找附近设备。");
            }
        }

        var locationStatus =
            await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
        if (locationStatus != PermissionStatus.Granted)
        {
            throw new BluetoothDiscoveryException(
                "没有定位权限，请在系统设置中允许 ArriLight 扫描蓝牙设备。");
        }
#endif
    }
}

public sealed record DiscoveredBluetoothDevice(
    string Name,
    string Address,
    int Rssi);

public sealed class BluetoothDiscoveryException(string message)
    : Exception(message);
