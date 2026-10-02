using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using ArriOpto.BLE.Agp;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;

namespace ArriLight;

public enum BluetoothSessionState
{
    Disconnected,
    Scanning,
    Connecting,
    Connected,
    Updating,
    Error
}

public sealed record BluetoothSessionSnapshot(
    BluetoothSessionState State,
    string Address,
    string Message)
{
    public bool IsConnected =>
        State is BluetoothSessionState.Connected or BluetoothSessionState.Updating;
}

public sealed record BluetoothMessage(
    Guid ServiceId,
    Guid CharacteristicId,
    byte[] Data,
    DateTimeOffset ReceivedAt);

/// <summary>设备 GATT 特征信息（用于服务发现/诊断）。</summary>
public sealed record GattCharacteristicInfo(
    Guid Id,
    string? Name,
    CharacteristicPropertyType Properties)
{
    public bool CanRead => Properties.HasFlag(CharacteristicPropertyType.Read);

    public bool CanWrite =>
        Properties.HasFlag(CharacteristicPropertyType.Write) ||
        Properties.HasFlag(CharacteristicPropertyType.WriteWithoutResponse);

    public bool CanNotify =>
        Properties.HasFlag(CharacteristicPropertyType.Notify) ||
        Properties.HasFlag(CharacteristicPropertyType.Indicate);
}

/// <summary>设备 GATT 服务信息（用于服务发现/诊断）。</summary>
public sealed record GattServiceInfo(
    Guid Id,
    string? Name,
    bool IsPrimary,
    IReadOnlyList<GattCharacteristicInfo> Characteristics);

public sealed class BluetoothConnectionService
{
    private static readonly TimeSpan DefaultConnectionLease =
        TimeSpan.FromSeconds(10);
    private static readonly Guid DeviceInformationService =
        Guid.Parse("0000180A-0000-1000-8000-00805F9B34FB");

    private static readonly Guid FirmwareRevisionCharacteristic =
        Guid.Parse("00002A26-0000-1000-8000-00805F9B34FB");

    private readonly IAdapter _adapter;
    private readonly BluetoothDiscoveryService _discoveryService;
    private readonly AppDiagnosticService _diagnostics;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly ConcurrentDictionary<Guid, ICharacteristic>
        _notificationCharacteristics = new();
    private readonly object _connectionLeaseSync = new();

    private IDevice? _currentDevice;
    private string _currentAddress = string.Empty;
    private CancellationTokenSource? _connectionLeaseCancellation;
    private Task? _connectionLeaseTask;
    private string? _longLivedAddress;
    private volatile bool _applicationSuspended;
    private BluetoothSessionSnapshot _snapshot =
        new(BluetoothSessionState.Disconnected, string.Empty, "未连接");

    public BluetoothConnectionService(
        IAdapter adapter,
        BluetoothDiscoveryService discoveryService,
        AppDiagnosticService diagnostics)
    {
        _adapter = adapter;
        _discoveryService = discoveryService;
        _diagnostics = diagnostics;
        _adapter.DeviceDisconnected += OnDeviceDisconnected;
        _adapter.DeviceConnectionLost += OnDeviceConnectionLost;
        _adapter.DeviceConnectionError += OnDeviceConnectionError;
    }

    public event EventHandler<BluetoothSessionSnapshot>? StateChanged;
    public event EventHandler<BluetoothMessage>? MessageReceived;

    public BluetoothSessionSnapshot Snapshot => _snapshot;

    /// <summary>Whether the current connection is still governed by the idle lease.</summary>
    public bool HasActiveConnectionLease
    {
        get
        {
            lock (_connectionLeaseSync)
            {
                return _connectionLeaseCancellation is not null;
            }
        }
    }

    internal AppDiagnosticService Diagnostics => _diagnostics;

    /// <summary>
    /// Keeps a successful public-state connection briefly available for an
    /// immediately-following control operation. The lease owns only the live
    /// connection; a later operation still creates its own AGP transport and
    /// authentication state.
    /// </summary>
    public void RetainConnectionFor(
        string address,
        TimeSpan? duration = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        if (!Snapshot.IsConnected ||
            !string.Equals(Snapshot.Address, address, StringComparison.OrdinalIgnoreCase))
            return;

        CancellationTokenSource lease;
        lock (_connectionLeaseSync)
        {
            if (_applicationSuspended ||
                string.Equals(_longLivedAddress, address, StringComparison.OrdinalIgnoreCase))
                return;
            _connectionLeaseCancellation?.Cancel();
            lease = new CancellationTokenSource();
            _connectionLeaseCancellation = lease;
            _connectionLeaseTask = ExpireConnectionLeaseAsync(
                address, duration ?? DefaultConnectionLease, lease);
        }
    }

    /// <summary>Cancel a pending idle lease before another BLE operation starts.</summary>
    public void CancelConnectionLease()
    {
        lock (_connectionLeaseSync)
        {
            _connectionLeaseCancellation?.Cancel();
            _connectionLeaseCancellation = null;
        }
    }

    /// <summary>
    /// Promote the current connection to a caller-owned long-lived session.
    /// This only cancels the idle lease; it never creates or switches a
    /// connection, so callers can invoke it before or after a control action.
    /// </summary>
    public void PromoteConnectionToLongLived(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        lock (_connectionLeaseSync)
        {
            if (_applicationSuspended)
                return;
            _longLivedAddress = address;
            if (string.Equals(Snapshot.Address, address, StringComparison.OrdinalIgnoreCase))
                CancelConnectionLease();
        }
        _diagnostics.Debug(
            "BLE/Connection",
            $"设备 {address} 的主页短时连接已升级为持续控制连接。");
    }

    /// <summary>Start a new public home-query lease ownership cycle.</summary>
    public void ResetConnectionLeaseOwnership(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        lock (_connectionLeaseSync)
        {
            if (string.Equals(_longLivedAddress, address, StringComparison.OrdinalIgnoreCase))
                _longLivedAddress = null;
        }
        CancelConnectionLease();
    }

    internal void SetApplicationSuspended(bool suspended)
    {
        _applicationSuspended = suspended;
        if (suspended)
            CancelConnectionLease();
    }

    public async Task<IDevice> ConnectAsync(
        string address,
        CancellationToken cancellationToken = default)
    {
        CancelConnectionLease();
        using var timing = _diagnostics.BeginBleTiming("Connection.Connect", address);
        await WaitForOperationLockAsync("connect", address, cancellationToken);
        try
        {
            var device = await ConnectCoreAsync(address, cancellationToken);
            timing.SetResult("connected");
            return device;
        }
        catch (Exception exception)
        {
            timing.SetResult(TimingFailure(exception));
            throw;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        CancelConnectionLease();
        using var timing = _diagnostics.BeginBleTiming("Connection.Disconnect", _currentAddress);
        await WaitForOperationLockAsync("disconnect", _currentAddress, cancellationToken);
        try
        {
            await DisconnectCoreAsync(cancellationToken);
            timing.SetResult("disconnected");
        }
        catch (Exception exception)
        {
            timing.SetResult(TimingFailure(exception));
            throw;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<int> WriteAsync(
        string address,
        Guid serviceId,
        Guid characteristicId,
        byte[] data,
        CharacteristicWriteType? writeType = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        var packetDetail = FormatAgpHeader(serviceId, characteristicId, data);
        using var timing = _diagnostics.BeginBleTiming(
            "Connection.Write", address,
            $"service={serviceId}, characteristic={characteristicId}, length={data.Length}{packetDetail}");
        await WaitForOperationLockAsync("write", address, cancellationToken);
        try
        {
            var characteristic = await GetCharacteristicCoreAsync(
                address,
                serviceId,
                characteristicId,
                cancellationToken);
            if (writeType is not null)
                characteristic.WriteType = writeType.Value;
            _diagnostics.Trace(
                "BLE/TX",
                $"service={serviceId}, characteristic={characteristicId}, writeType={writeType ?? characteristic.WriteType}, length={data.Length}{packetDetail}, data={FormatBlePayload(serviceId, characteristicId, data)}");
            var result = await MeasureAsync(
                "GATT.NativeWrite", address,
                $"characteristic={characteristicId}, writeType={writeType ?? characteristic.WriteType}, length={data.Length}{packetDetail}",
                () => characteristic.WriteAsync(data, cancellationToken),
                value => value == 0 ? "ok:gatt=0" : $"failed:gatt={value}");
            _diagnostics.Debug("BLE/TX", $"写入完成，GATT result={result}。");
            timing.SetResult(result == 0 ? "ok:gatt=0" : $"failed:gatt={result}");
            return result;
        }
        catch (Exception exception)
        {
            timing.SetResult(TimingFailure(exception));
            throw;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>
    /// 枚举设备的全部 GATT 服务与特征，并写入诊断日志。
    /// </summary>
    /// <param name="address">设备地址。</param>
    /// <param name="refreshCache">
    /// true 时先调用 <c>ClearServices</c> 强制重新发现，避免 Android GATT
    /// 缓存与设备当前固件暴露的服务表不一致。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<IReadOnlyList<GattServiceInfo>> EnumerateGattAsync(
        string address,
        bool refreshCache = true,
        CancellationToken cancellationToken = default)
    {
        using var timing = _diagnostics.BeginBleTiming(
            "GATT.Enumerate", address, $"refreshCache={refreshCache}");
        await WaitForOperationLockAsync("enumerate", address, cancellationToken);
        try
        {
            var device = await ConnectCoreAsync(address, cancellationToken);
            if (refreshCache)
                device.ClearServices();

            var services = await MeasureAsync(
                "GATT.GetServices", address,
                $"refreshCache={refreshCache}; API may use current connection cache",
                () => device.GetServicesAsync(cancellationToken),
                value => $"ok:services={value.Count}");
            var result = new List<GattServiceInfo>(services.Count);
            foreach (var service in services)
            {
                var characteristics = await MeasureAsync(
                    "GATT.GetCharacteristics", address,
                    $"service={service.Id}; API may use current connection cache",
                    () => service.GetCharacteristicsAsync(cancellationToken),
                    value => $"ok:characteristics={value.Count}");
                result.Add(new GattServiceInfo(
                    service.Id,
                    service.Name,
                    service.IsPrimary,
                    characteristics
                        .Select(c => new GattCharacteristicInfo(
                            c.Id,
                            c.Name,
                            c.Properties))
                        .ToList()));
            }

            _diagnostics.Information(
                "BLE/GATT",
                $"设备 {address} 服务表：" +
                string.Join(
                    " | ",
                    result.Select(s =>
                        $"{s.Id} [{string.Join(", ", s.Characteristics.Select(c => $"{c.Id}:{c.Properties}"))}]")));
            timing.SetResult($"ok:services={result.Count}");
            return result;
        }
        catch (Exception exception)
        {
            timing.SetResult(TimingFailure(exception));
            throw;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>协商 ATT MTU。</summary>
    public async Task<int> RequestMtuAsync(
        string address,
        int mtu,
        CancellationToken cancellationToken = default)
    {
        await WaitForOperationLockAsync("requestMtu", address, cancellationToken);
        try
        {
            var device = await ConnectCoreAsync(address, cancellationToken);
            var negotiated = await device.RequestMtuAsync(mtu, cancellationToken);
            _diagnostics.Debug(
                "BLE/MTU",
                $"请求 MTU {mtu}，协商结果 {negotiated}。");
            return negotiated;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<byte[]> ReadAsync(
        string address,
        Guid serviceId,
        Guid characteristicId,
        CancellationToken cancellationToken = default)
    {
        using var timing = _diagnostics.BeginBleTiming(
            "Connection.Read", address, $"service={serviceId}, characteristic={characteristicId}");
        await WaitForOperationLockAsync("read", address, cancellationToken);
        try
        {
            var characteristic = await GetCharacteristicCoreAsync(
                address,
                serviceId,
                characteristicId,
                cancellationToken);
            var result = await MeasureAsync(
                "GATT.NativeRead", address, $"characteristic={characteristicId}",
                () => characteristic.ReadAsync(cancellationToken),
                value => $"{(value.Item2 == 0 ? "ok" : "failed")}:gatt={value.Item2},length={value.Item1?.Length ?? 0}");
            var data = result.Item1 ?? [];
            _diagnostics.Trace(
                "BLE/RX",
                $"读取完成，service={serviceId}, characteristic={characteristicId}, result={result.Item2}, length={data.Length}, data={FormatBlePayload(serviceId, characteristicId, data)}");
            if (serviceId == AgpProfile.ServiceId &&
                characteristicId == AgpProfile.CapabilitiesCharacteristicId)
            {
                LogCapabilitiesRead(address, characteristic.Id, data);
            }
            timing.SetResult(result.Item2 == 0 ? "ok:gatt=0" : $"failed:gatt={result.Item2}");
            return data;
        }
        catch (Exception exception)
        {
            timing.SetResult(TimingFailure(exception));
            throw;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task SubscribeAsync(
        string address,
        Guid serviceId,
        Guid characteristicId,
        CancellationToken cancellationToken = default)
    {
        using var timing = _diagnostics.BeginBleTiming(
            "Connection.Subscribe", address, $"service={serviceId}, characteristic={characteristicId}");
        await WaitForOperationLockAsync("subscribe", address, cancellationToken);
        try
        {
            var characteristic = await GetCharacteristicCoreAsync(
                address,
                serviceId,
                characteristicId,
                cancellationToken);
            if (_notificationCharacteristics.ContainsKey(characteristicId))
            {
                timing.Mark("subscription=reused; no CCCD write");
                timing.SetResult("reused");
                return;
            }

            characteristic.ValueUpdated += OnCharacteristicValueUpdated;
            try
            {
                await MeasureAsync(
                    "GATT.NativeStartUpdates", address,
                    $"characteristic={characteristicId}, properties={characteristic.Properties}; notification/indication CCCD subscription",
                    () => characteristic.StartUpdatesAsync(cancellationToken));
                _notificationCharacteristics[characteristicId] = characteristic;
                _diagnostics.Information(
                    "BLE/Notify",
                    $"通知订阅成功：{characteristicId}。");
                timing.SetResult("subscribed");
            }
            catch
            {
                characteristic.ValueUpdated -= OnCharacteristicValueUpdated;
                throw;
            }
        }
        catch (Exception exception)
        {
            timing.SetResult(TimingFailure(exception));
            throw;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<string?> TryReadFirmwareRevisionAsync(
        string address,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var data = await ReadAsync(
                address,
                DeviceInformationService,
                FirmwareRevisionCharacteristic,
                cancellationToken);
            var value = Encoding.UTF8.GetString(data).Trim('\0', ' ', '\r', '\n');
            if (string.IsNullOrWhiteSpace(value))
                return null;

            _diagnostics.Information(
                "BLE/Version",
                $"设备通过标准 Firmware Revision 特征返回版本：{value}。");
            return value;
        }
        catch (Exception exception)
        {
            _diagnostics.Warning(
                "BLE/Version",
                $"设备未提供可读的标准 Firmware Revision 特征，将使用本地记录版本。原因：{exception.Message}");
            return null;
        }
    }

    public async Task RunExclusiveOtaAsync(
        string address,
        Func<IDevice, CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await WaitForOperationLockAsync("ota", address, cancellationToken);
        try
        {
            var device = await ConnectCoreAsync(address, cancellationToken);
            UpdateState(
                BluetoothSessionState.Updating,
                address,
                "已连接，正在执行 OTA");
            await operation(device, cancellationToken);
        }
        finally
        {
            if (_currentDevice?.State == DeviceState.Connected)
            {
                UpdateState(
                    BluetoothSessionState.Connected,
                    _currentAddress,
                    "设备已连接");
            }
            else
            {
                _currentDevice = null;
                UpdateState(
                    BluetoothSessionState.Disconnected,
                    address,
                    "OTA 结束，设备已断开");
            }

            _operationLock.Release();
        }
    }

    private async Task<IDevice> ConnectCoreAsync(
        string address,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_applicationSuspended)
            throw new OperationCanceledException("应用已进入后台，停止 BLE 建连。", new CancellationToken(true));
        if (_currentDevice is { State: DeviceState.Connected } current &&
            string.Equals(
                _currentAddress,
                address,
                StringComparison.OrdinalIgnoreCase))
        {
            BleTimingScope.Current?.Mark("connection=reused; nativeConnect=skipped");
            _diagnostics.Debug(
                "BLE/Connection",
                $"复用已连接的设备 {address}。");
            return current;
        }

        using var timing = _diagnostics.BeginBleTiming("Connection.ConnectCore", address);
        try
        {
            if (_currentDevice is { State: DeviceState.Connected } other)
            {
                _diagnostics.Information(
                    "BLE/Connection",
                    $"当前连接属于 {_currentAddress}，切换到 {address}。");
                await ClearNotificationsAsync(cancellationToken);
                await MeasureAsync(
                    "Connection.NativeDisconnect", _currentAddress, $"reason=switch; target={address}",
                    () => _adapter.DisconnectDeviceAsync(other, cancellationToken));
            }

            UpdateState(BluetoothSessionState.Scanning, address, "正在查找设备");
            var device = await MeasureAsync(
                "Connection.FindDevice", address, null,
                () => _discoveryService.FindDeviceAsync(
                    address, cancellationToken: cancellationToken),
                value => $"found:state={value.State}");

            UpdateState(BluetoothSessionState.Connecting, address, "正在连接设备");
            _diagnostics.Information(
                "BLE/Connection",
                $"开始连接 {address}，id={device.Id}，name={device.Name ?? "未命名"}，state={device.State}。");

            try
            {
                if (device.State != DeviceState.Connected)
                {
                    await MeasureAsync(
                        "Connection.NativeConnect", address, $"id={device.Id}, state={device.State}",
                        () => _adapter.ConnectToDeviceAsync(device, default, cancellationToken));
                }
                else
                {
                    timing.Mark("nativeConnect=skipped; discovered device already connected");
                }

                _currentDevice = device;
                _currentAddress = address;
                UpdateState(BluetoothSessionState.Connected, address, "设备已连接");
                _diagnostics.Information(
                    "BLE/Connection",
                    $"设备连接成功：{address}，id={device.Id}。");
                timing.SetResult("connected");
                return device;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 配对的单次连接等待和用户取消都属于正常取消，由调用方决定是否重试。
                throw;
            }
            catch (Exception exception)
            {
                _diagnostics.Error(
                    "BLE/Connection",
                    $"连接设备 {address} 失败。",
                    exception);
                UpdateState(
                    BluetoothSessionState.Error,
                    address,
                    $"连接失败：{exception.Message}");
                throw;
            }
        }
        catch (Exception exception)
        {
            timing.SetResult(TimingFailure(exception));
            throw;
        }
    }

    private async Task<ICharacteristic> GetCharacteristicCoreAsync(
        string address,
        Guid serviceId,
        Guid characteristicId,
        CancellationToken cancellationToken)
    {
        using var timing = _diagnostics.BeginBleTiming(
            "GATT.Lookup", address,
            $"service={serviceId}, characteristic={characteristicId}; API may use current connection cache");
        try
        {
            var device = await ConnectCoreAsync(address, cancellationToken);
            var service = await MeasureAsync(
                "GATT.GetService", address,
                $"service={serviceId}; API may use current connection cache",
                () => device.GetServiceAsync(serviceId, cancellationToken),
                value => value is null ? "missing" : "found")
                ?? throw new InvalidOperationException($"设备未提供服务 {serviceId}。");
            var characteristic = await MeasureAsync(
                "GATT.GetCharacteristic", address,
                $"service={serviceId}, characteristic={characteristicId}; API may use current connection cache",
                () => service.GetCharacteristicAsync(characteristicId, cancellationToken),
                value => value is null ? "missing" : "found")
                ?? throw new InvalidOperationException(
                    $"服务 {serviceId} 未提供特征 {characteristicId}。");
            timing.SetResult("found");
            return characteristic;
        }
        catch (Exception exception)
        {
            timing.SetResult(TimingFailure(exception));
            throw;
        }
    }

    private async Task ClearNotificationsAsync(CancellationToken cancellationToken)
    {
        using var timing = _diagnostics.BeginBleTiming(
            "Connection.ClearNotifications", _currentAddress,
            $"subscriptions={_notificationCharacteristics.Count}");
        var failures = 0;
        foreach (var characteristic in _notificationCharacteristics.Values)
        {
            characteristic.ValueUpdated -= OnCharacteristicValueUpdated;
            try
            {
                await MeasureAsync(
                    "GATT.NativeStopUpdates", _currentAddress, $"characteristic={characteristic.Id}",
                    () => characteristic.StopUpdatesAsync(cancellationToken));
            }
            catch (Exception exception)
            {
                failures++;
                _diagnostics.Warning(
                    "BLE/Notify",
                    $"停止通知 {characteristic.Id} 失败：{exception.Message}");
            }
        }

        _notificationCharacteristics.Clear();
        timing.SetResult(failures == 0 ? "cleared" : $"cleared:stopFailures={failures}");
    }

    private Task WaitForOperationLockAsync(
        string operation,
        string address,
        CancellationToken cancellationToken) =>
        MeasureAsync(
            "Connection.LockWait", address, $"operation={operation}",
            () => _operationLock.WaitAsync(cancellationToken));

    private async Task<T> MeasureAsync<T>(
        string stage,
        string address,
        string? detail,
        Func<Task<T>> operation,
        Func<T, string>? resultDetail = null)
    {
        using var timing = _diagnostics.BeginBleTiming(stage, address, detail);
        try
        {
            var result = await operation();
            timing.SetResult(resultDetail?.Invoke(result) ?? "ok");
            return result;
        }
        catch (Exception exception)
        {
            timing.SetResult(TimingFailure(exception));
            throw;
        }
    }

    /// <summary>Release only the requested device; address matching is protected by the GATT lock.</summary>
    public async Task DisconnectDeviceAsync(
        string address, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        CancelConnectionLease();
        using var timing = _diagnostics.BeginBleTiming("Connection.DisconnectDevice", address);
        await WaitForOperationLockAsync("disconnectDevice", address, cancellationToken);
        try
        {
            if (string.Equals(_currentAddress, address, StringComparison.OrdinalIgnoreCase))
            {
                await DisconnectCoreAsync(cancellationToken);
                timing.SetResult("disconnected");
            }
            else
            {
                timing.SetResult("target-not-connected");
            }

            _discoveryService.InvalidateCachedDevice(address);
        }
        catch (Exception exception)
        {
            timing.SetResult(TimingFailure(exception));
            throw;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task DisconnectCoreAsync(CancellationToken cancellationToken)
    {
        await ClearNotificationsAsync(cancellationToken);
        if (_currentDevice is { State: DeviceState.Connected } device)
        {
            _diagnostics.Information("BLE/Connection", $"主动断开设备 {_currentAddress}。");
            await MeasureAsync(
                "Connection.NativeDisconnect", _currentAddress, "reason=requested",
                () => _adapter.DisconnectDeviceAsync(device, cancellationToken));
        }

        _currentDevice = null;
        _currentAddress = string.Empty;
        lock (_connectionLeaseSync)
            _longLivedAddress = null;
        UpdateState(BluetoothSessionState.Disconnected, string.Empty, "未连接");
    }

    private async Task ExpireConnectionLeaseAsync(
        string address,
        TimeSpan duration,
        CancellationTokenSource lease)
    {
        try
        {
            await Task.Delay(duration, lease.Token);
            await WaitForOperationLockAsync("connectionLease", address, CancellationToken.None);
            try
            {
                lock (_connectionLeaseSync)
                {
                    if (!ReferenceEquals(_connectionLeaseCancellation, lease))
                        return;
                    _connectionLeaseCancellation = null;
                }

                if (_currentDevice is not null &&
                    string.Equals(_currentAddress, address, StringComparison.OrdinalIgnoreCase) &&
                    Snapshot.IsConnected)
                {
                    _diagnostics.Information(
                        "BLE/Connection",
                        $"主页查询连接租约到期，释放设备 {address}。");
                    await DisconnectCoreAsync(CancellationToken.None);
                }
            }
            finally
            {
                _operationLock.Release();
            }
        }
        catch (OperationCanceledException) when (lease.IsCancellationRequested)
        {
            // A user operation took over the retained connection.
        }
        catch (Exception exception)
        {
            _diagnostics.Warning(
                "BLE/Connection",
                $"主页查询连接租约释放失败：device={address}，{exception.Message}。");
        }
        finally
        {
            lease.Dispose();
        }
    }

    private async Task MeasureAsync(
        string stage,
        string address,
        string? detail,
        Func<Task> operation)
    {
        using var timing = _diagnostics.BeginBleTiming(stage, address, detail);
        try
        {
            await operation();
            timing.SetResult("ok");
        }
        catch (Exception exception)
        {
            timing.SetResult(TimingFailure(exception));
            throw;
        }
    }

    private static string TimingFailure(Exception exception) =>
        $"{(exception is OperationCanceledException ? "cancelled" : "failed")}:{exception.GetType().Name}";

    private void OnCharacteristicValueUpdated(
        object? sender,
        CharacteristicUpdatedEventArgs eventArgs)
    {
        var characteristic = eventArgs.Characteristic;
        var data = characteristic.Value?.ToArray() ?? [];
        _diagnostics.Trace(
            "BLE/RX",
            $"通知：service={characteristic.Service.Id}, characteristic={characteristic.Id}, length={data.Length}{FormatAgpHeader(characteristic.Service.Id, characteristic.Id, data)}, data={FormatBlePayload(characteristic.Service.Id, characteristic.Id, data)}");
        MessageReceived?.Invoke(
            this,
            new BluetoothMessage(
                characteristic.Service.Id,
                characteristic.Id,
                data,
                DateTimeOffset.Now));
    }

    private void LogCapabilitiesRead(
        string address,
        Guid characteristicId,
        byte[] data)
    {
        _diagnostics.Information(
            "AGP/Capabilities",
            $"读取能力原始数据：device={address}, characteristic={characteristicId}, length={data.Length}, raw={BitConverter.ToString(data).Replace('-', ' ')}");
        try
        {
            var capabilities = AgpModelCodec.DecodeCapabilities(AgpCodec.Decode(data));
            _diagnostics.Information(
                "AGP/Capabilities",
                $"解析能力：device={address}, characteristic={characteristicId}, capabilities=0x{(ulong)capabilities:X16}, flags={capabilities}, effectsSupported={capabilities.HasFlag(AgpCapabilities.Effects)}");
        }
        catch (AgpProtocolException exception)
        {
            // 诊断解析不改变 ReadAsync 返回的数据，由 AGP 客户端处理协议错误。
            _diagnostics.Warning(
                "AGP/Capabilities",
                $"能力解析失败：device={address}, characteristic={characteristicId}, error={exception.Message}");
        }
    }

    private static string FormatBlePayload(
        Guid serviceId,
        Guid characteristicId,
        ReadOnlySpan<byte> data)
    {
        if (serviceId == AgpProfile.ServiceId &&
            characteristicId == AgpProfile.ControlPointCharacteristicId &&
            data.Length >= AgpProfile.HeaderSize)
        {
            var opcode = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(4, 2));
            if (opcode is (ushort)AgpOpcode.AuthGetToken or (ushort)AgpOpcode.AuthToken)
                return $"<AGP auth payload redacted; length={data.Length}>";
        }

        return Convert.ToHexString(data);
    }

    private static string FormatAgpHeader(
        Guid serviceId,
        Guid characteristicId,
        ReadOnlySpan<byte> data)
    {
#if DEBUG
        if (serviceId != AgpProfile.ServiceId ||
            characteristicId != AgpProfile.ControlPointCharacteristicId ||
            data.Length < AgpProfile.HeaderSize)
            return string.Empty;

        var opcode = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(4, 2));
        var transactionId = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(6, 2));
        return $", opcode=0x{opcode:X4}({(AgpOpcode)opcode}), tid={transactionId}";
#else
        return string.Empty;
#endif
    }

    private void OnDeviceDisconnected(object? sender, DeviceEventArgs eventArgs)
    {
        if (!IsCurrentDevice(eventArgs.Device))
            return;

        _diagnostics.Information(
            "BLE/Connection",
            $"设备已断开：{_currentAddress}。");
        HandleDisconnected("设备已断开");
    }

    private void OnDeviceConnectionLost(
        object? sender,
        DeviceErrorEventArgs eventArgs)
    {
        if (!IsCurrentDevice(eventArgs.Device))
            return;

        _diagnostics.Warning(
            "BLE/Connection",
            $"设备连接丢失：{_currentAddress}，{eventArgs.ErrorMessage}");
        HandleDisconnected("连接意外断开");
    }

    private void OnDeviceConnectionError(
        object? sender,
        DeviceErrorEventArgs eventArgs)
    {
        if (!IsCurrentDevice(eventArgs.Device))
            return;

        _diagnostics.Error(
            "BLE/Connection",
            $"设备连接错误：{_currentAddress}，{eventArgs.ErrorMessage}");
        UpdateState(
            BluetoothSessionState.Error,
            _currentAddress,
            eventArgs.ErrorMessage ?? "连接错误");
    }

    private bool IsCurrentDevice(IDevice device) =>
        _currentDevice is not null && _currentDevice.Id == device.Id;

    private void HandleDisconnected(string message)
    {
        CancelConnectionLease();
        lock (_connectionLeaseSync)
            _longLivedAddress = null;
        foreach (var characteristic in _notificationCharacteristics.Values)
            characteristic.ValueUpdated -= OnCharacteristicValueUpdated;
        _notificationCharacteristics.Clear();
        _currentDevice = null;
        _discoveryService.InvalidateCachedDevice(_currentAddress);
        UpdateState(
            BluetoothSessionState.Disconnected,
            _currentAddress,
            message);
    }

    private void UpdateState(
        BluetoothSessionState state,
        string address,
        string message)
    {
        _snapshot = new BluetoothSessionSnapshot(state, address, message);
        StateChanged?.Invoke(this, _snapshot);
    }
}
