using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;

namespace ArriLight;

public sealed class BluetoothDiscoveryService(
    IBluetoothLE bluetoothLE,
    IAdapter adapter)
{
    private readonly SemaphoreSlim _scanLock = new(1, 1);

    public event EventHandler<DiscoveredBluetoothDevice>? DeviceFound;

    public bool IsScanning => adapter.IsScanning;

    public async Task ScanAsync(CancellationToken cancellationToken = default)
    {
        if (!await EnsurePermissionAsync())
            throw new BluetoothDiscoveryException("没有蓝牙扫描权限，请在系统设置中允许后重试。");

        if (bluetoothLE.State != BluetoothState.On)
            throw new BluetoothDiscoveryException("蓝牙未开启，请先开启手机蓝牙。");

        await _scanLock.WaitAsync(cancellationToken);
        try
        {
            if (adapter.IsScanning)
                await adapter.StopScanningForDevicesAsync();

            adapter.ScanTimeout = 12_000;
            adapter.DeviceDiscovered += OnDeviceDiscovered;
            try
            {
                await adapter.StartScanningForDevicesAsync(
                    cancellationToken: cancellationToken);
            }
            finally
            {
                adapter.DeviceDiscovered -= OnDeviceDiscovered;
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

    private void OnDeviceDiscovered(
        object? sender,
        DeviceEventArgs eventArgs)
    {
        var device = eventArgs.Device;
        if (!MatchesConfiguredSyncWord(device))
            return;

        DeviceFound?.Invoke(
            this,
            new DiscoveredBluetoothDevice(
                device.Name?.Trim() ?? "未命名设备",
                GetDeviceAddress(device),
                device.Rssi));
    }

    private static bool MatchesConfiguredSyncWord(IDevice device)
    {
        // TODO: 获得灯具的同步字后，在这里检查广播名称、
        // ManufacturerData 或 Service UUID。当前仅显示有名称的 BLE 设备，
        // 避免把大量匿名广播加入候选列表。
        return !string.IsNullOrWhiteSpace(device.Name);
    }

    private static string GetDeviceAddress(IDevice device)
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

    private static async Task<bool> EnsurePermissionAsync()
    {
#if ANDROID
        PermissionStatus status;
        if (OperatingSystem.IsAndroidVersionAtLeast(31))
            status = await Permissions.RequestAsync<Permissions.Bluetooth>();
        else
            status = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();

        return status == PermissionStatus.Granted;
#else
        return true;
#endif
    }
}

public sealed record DiscoveredBluetoothDevice(
    string Name,
    string Address,
    int Rssi);

public sealed class BluetoothDiscoveryException(string message)
    : Exception(message);
