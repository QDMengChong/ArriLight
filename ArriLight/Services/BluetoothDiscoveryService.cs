using System.Collections.Concurrent;
using System.Text;
using ArriOpto.BLE.Agp;
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

    /// <summary>
    /// 启动或重置服务级后台扫描窗口。扫描任务不属于某个页面，
    /// 因此页面离开后仍可继续运行，直到窗口到期或被显式停止。
    /// </summary>
    public async Task StartOrResetBackgroundScanAsync(
        TimeSpan? duration = null)
    {
        await EnsureReadyAsync();

        var scanDuration = duration ?? DefaultBackgroundScanDuration;
        if (scanDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration));

        await EnsureScanTaskAsync(scanDuration, resetActiveWindow: true);
    }

    public async Task ScanAsync(
        CancellationToken cancellationToken = default,
        TimeSpan? duration = null)
    {
        var scanDuration = duration ?? DefaultBackgroundScanDuration;
        await StartOrResetBackgroundScanAsync(scanDuration);
        // 页面离开只结束该页面的等待，不影响服务级后台扫描。
        await Task.Delay(scanDuration, cancellationToken);
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
        using var timing = diagnostics.BeginBleTiming(
            "BLE.Scan.FindDevice", address,
            $"timeoutMs={(timeout ?? TimeSpan.FromSeconds(15)).TotalMilliseconds:0}; isScanning={adapter.IsScanning}");
        if (GetCachedDevice(address) is { } cached)
        {
            timing.Mark($"扫描缓存命中; state={cached.State}; rssi={cached.Rssi}");
            timing.SetResult("cache-hit");
            diagnostics.Debug(
                "BLE/Scan",
                $"使用扫描缓存中的设备：{address}，state={cached.State}。");
            return cached;
        }

        try
        {
            using var ready = diagnostics.BeginBleTiming("BLE.Scan.EnsureReady", address);
            await EnsureReadyAsync();
            ready.SetResult("success");
        }
        catch (Exception exception)
        {
            timing.SetResult($"ready-failed:{exception.GetType().Name}");
            throw;
        }
        timing.Mark("扫描缓存未命中，蓝牙/权限检查完成");
        var completion = new TaskCompletionSource<IDevice>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnDiscovered(object? sender, DiscoveredBluetoothDevice discovered)
        {
            if (!string.Equals(
                    discovered.Address,
                    address,
                    StringComparison.OrdinalIgnoreCase))
                return;

            if (GetCachedDevice(address) is { } device)
                completion.TrySetResult(device);
        }

        DeviceFound += OnDiscovered;
        try
        {
            // 已有后台扫描时只等待它的广播，不重启或停止扫描；
            // 没有扫描时才按需启动一个短扫描窗口。
            using (var ensureScan = diagnostics.BeginBleTiming(
                       "BLE.Scan.EnsureTask", address, $"isScanning={adapter.IsScanning}"))
            {
                await EnsureScanTaskAsync(
                    timeout ?? TimeSpan.FromSeconds(15),
                    resetActiveWindow: false);
                ensureScan.SetResult("success");
            }
            using var scanCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            scanCancellation.CancelAfter(timeout ?? TimeSpan.FromSeconds(15));

            using var waitBroadcast = diagnostics.BeginBleTiming(
                "BLE.Scan.WaitBroadcast", address);
            try
            {
                var device = await completion.Task.WaitAsync(scanCancellation.Token);
                waitBroadcast.SetResult("found");
                timing.SetResult("found");
                diagnostics.Information(
                    "BLE/Scan",
                    $"已找到目标设备 {address}，name={device.Name ?? "未命名"}，RSSI={device.Rssi}。");
                return device;
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested)
            {
                waitBroadcast.SetResult("timeout");
                timing.SetResult("timeout");
                throw new BluetoothDiscoveryException(
                    $"扫描设备 {address} 超时，请让手机靠近灯具后重试。");
            }
            catch (OperationCanceledException)
            {
                waitBroadcast.SetResult("canceled");
                timing.SetResult("canceled");
                throw;
            }
        }
        catch (Exception exception) when (exception is not BluetoothDiscoveryException &&
                                         exception is not OperationCanceledException)
        {
            timing.SetResult($"failed:{exception.GetType().Name}");
            throw;
        }
        finally
        {
            DeviceFound -= OnDiscovered;
        }
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

        if (adapter.IsScanning)
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

    private async Task EnsureScanTaskAsync(
        TimeSpan duration,
        bool resetActiveWindow)
    {
        while (true)
        {
            Task? canceledScanTask = null;
            await _scanLock.WaitAsync();
            try
            {
                if (_backgroundScanTask is { IsCompleted: false } activeTask)
                {
                    if (_backgroundScanCancellation?.IsCancellationRequested != true)
                    {
                        if (resetActiveWindow)
                        {
                            _backgroundScanDeadline =
                                DateTimeOffset.UtcNow + duration;
                        }

                        return;
                    }

                    canceledScanTask = activeTask;
                }
                else
                {
                    _backgroundScanDeadline = DateTimeOffset.UtcNow + duration;
                    _backgroundScanCancellation?.Dispose();
                    _backgroundScanCancellation = new CancellationTokenSource();
                    _backgroundScanTask = RunBackgroundScanAsync(
                        _backgroundScanCancellation.Token);
                    return;
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
            "开始低延迟搜索附近的全部 BLE 广播，后台扫描窗口 60 秒。");

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
                device.Rssi,
                AgpAdvertisement.IsPairingOpen(device.AdvertisementRecords)));
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
    int Rssi,
    bool IsPairingOpen);

public sealed class BluetoothDiscoveryException(string message)
    : Exception(message);
