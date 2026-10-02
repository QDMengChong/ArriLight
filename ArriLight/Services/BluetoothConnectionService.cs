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

internal enum BluetoothDisconnectReason
{
    Requested,
    HomeQueryLeaseExpired,
    ApplicationBackground,
    ApplicationClosing,
    DeviceRemoval,
    QueryFailure,
    Abnormal
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
    string Address,
    long ConnectionGeneration,
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
    private readonly TimeProvider _timeProvider;
    // Serialize native GATT calls for Android stability; ownership and lifetime
    // are per device, so holding this lock never switches another connection.
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly ConcurrentDictionary<string, ConnectionSession> _sessions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, ConnectionSession> _sessionsByDeviceId = new();
    private ConnectionSession? _lastActiveSession;
    private volatile bool _applicationSuspended;

    public BluetoothConnectionService(
        IAdapter adapter,
        BluetoothDiscoveryService discoveryService,
        AppDiagnosticService diagnostics,
        TimeProvider? timeProvider = null)
    {
        _adapter = adapter;
        _discoveryService = discoveryService;
        _diagnostics = diagnostics;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _adapter.DeviceDisconnected += OnDeviceDisconnected;
        _adapter.DeviceConnectionLost += OnDeviceConnectionLost;
        _adapter.DeviceConnectionError += OnDeviceConnectionError;
    }

    public event EventHandler<BluetoothSessionSnapshot>? StateChanged;
    public event EventHandler<BluetoothMessage>? MessageReceived;

    /// <summary>Last active device, retained for diagnostics; UI must use GetSnapshot(address).</summary>
    public BluetoothSessionSnapshot Snapshot =>
        _lastActiveSession?.Snapshot ??
        new(BluetoothSessionState.Disconnected, string.Empty, "未连接");

    public BluetoothSessionSnapshot GetSnapshot(string address) =>
        GetOrCreateSession(address).Snapshot;

    public IReadOnlyList<string> GetConnectedAddresses() =>
        _sessions.Values
            .Where(session => session.Device?.State == DeviceState.Connected)
            .Select(session => session.Address)
            .ToList();

    public bool IsConnected(string address) =>
        !string.IsNullOrWhiteSpace(address) &&
        _sessions.TryGetValue(address, out var session) &&
        session.Device?.State == DeviceState.Connected &&
        session.Snapshot.IsConnected;

    internal long GetConnectionGeneration(string address) =>
        GetOrCreateSession(address).Generation;

    internal bool IsLongLived(string address) =>
        GetOrCreateSession(address).LongLived;

    /// <summary>Whether any connection is still governed by an idle lease.</summary>
    public bool HasActiveConnectionLease
    {
        get
        {
            return _sessions.Values.Any(session => session.HasActiveLease);
        }
    }

    public bool HasActiveConnectionLeaseFor(string address) =>
        !string.IsNullOrWhiteSpace(address) &&
        _sessions.TryGetValue(address, out var session) &&
        session.HasActiveLease;

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
        var session = GetOrCreateSession(address);
        lock (session.LeaseSync)
        {
            if (_applicationSuspended || session.LongLived || !session.Snapshot.IsConnected)
                return;
            session.CancelLease();
            var lease = new CancellationTokenSource();
            session.LeaseCancellation = lease;
            _ = ExpireConnectionLeaseAsync(session, duration ?? DefaultConnectionLease, lease);
        }
    }

    /// <summary>Cancel a pending idle lease before another BLE operation starts.</summary>
    public void CancelConnectionLease(string address)
    {
        if (_sessions.TryGetValue(address, out var session))
            session.CancelLease();
    }

    public void CancelConnectionLease()
    {
        foreach (var session in _sessions.Values)
            session.CancelLease();
    }

    /// <summary>
    /// Promote the current connection to a caller-owned long-lived session.
    /// This only cancels the idle lease; it never creates or switches a
    /// connection, so callers can invoke it before or after a control action.
    /// </summary>
    public void PromoteConnectionToLongLived(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        var session = GetOrCreateSession(address);
        if (_applicationSuspended)
            return;
        lock (session.LeaseSync)
        {
            session.LongLived = true;
            session.CancelLease();
        }
        _diagnostics.Debug(
            "BLE/Connection",
            $"设备 {address} 的主页短时连接已升级为持续控制连接。");
    }

    /// <summary>Start a new public home-query lease ownership cycle.</summary>
    public void ResetConnectionLeaseOwnership(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        var session = GetOrCreateSession(address);
        session.CancelLease();
    }

    /// <summary>Refresh every existing short lease except the device being operated.</summary>
    public void RefreshConnectionLeasesExcept(string address)
    {
        foreach (var session in _sessions.Values)
        {
            if (!string.Equals(session.Address, address, StringComparison.OrdinalIgnoreCase) &&
                session.HasActiveLease)
            {
                RetainConnectionFor(session.Address);
            }
        }
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
        CancelConnectionLease(address);
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

    public Task DisconnectAsync(CancellationToken cancellationToken = default) =>
        DisconnectAsync(BluetoothDisconnectReason.Requested, cancellationToken);

    public Task DisconnectAsync(
        string address,
        CancellationToken cancellationToken = default) =>
        DisconnectAsync(address, BluetoothDisconnectReason.Requested, cancellationToken);

    internal async Task DisconnectAsync(
        BluetoothDisconnectReason reason,
        CancellationToken cancellationToken = default)
    {
        CancelConnectionLease();
        var addresses = GetConnectedAddresses();
        using var timing = _diagnostics.BeginBleTiming(
            "Connection.Disconnect", string.Join(",", addresses));
        await WaitForOperationLockAsync("disconnect", string.Join(",", addresses), cancellationToken);
        try
        {
            List<Exception> failures = [];
            foreach (var address in GetConnectedAddresses())
            {
                var session = GetOrCreateSession(address);
                try { await DisconnectCoreAsync(session, cancellationToken, reason); }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    failures.Add(exception);
                }
            }
            if (failures.Count > 0)
                throw new AggregateException("部分设备连接释放失败。", failures);
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
                $"device={address}, service={serviceId}, characteristic={characteristicId}, writeType={writeType ?? characteristic.WriteType}, length={data.Length}{packetDetail}, data={FormatBlePayload(serviceId, characteristicId, data)}");
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
                $"读取完成，device={address}, service={serviceId}, characteristic={characteristicId}, result={result.Item2}, length={data.Length}, data={FormatBlePayload(serviceId, characteristicId, data)}");
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
            var session = GetOrCreateSession(address);
            if (session.NotificationCharacteristics.ContainsKey(characteristicId))
            {
                timing.Mark("subscription=reused; no CCCD write");
                timing.SetResult("reused");
                return;
            }

            var generation = session.Generation;
            EventHandler<CharacteristicUpdatedEventArgs> handler =
                (sender, args) => OnCharacteristicValueUpdated(session, generation, sender, args);
            session.NotificationCharacteristics[characteristicId] = characteristic;
            session.NotificationHandlers[characteristicId] = handler;
            characteristic.ValueUpdated += handler;
            try
            {
                await MeasureAsync(
                    "GATT.NativeStartUpdates", address,
                    $"characteristic={characteristicId}, properties={characteristic.Properties}; notification/indication CCCD subscription",
                    () => characteristic.StartUpdatesAsync(cancellationToken));
                if (!session.Snapshot.IsConnected || session.Generation != generation)
                    throw new IOException("设备在开启通知期间已断开，请重新连接。");
                _diagnostics.Information(
                    "BLE/Notify",
                    $"通知订阅成功：{characteristicId}。");
                timing.SetResult("subscribed");
            }
            catch
            {
                characteristic.ValueUpdated -= handler;
                session.NotificationCharacteristics.TryRemove(characteristicId, out _);
                session.NotificationHandlers.TryRemove(characteristicId, out _);
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
                GetOrCreateSession(address),
                BluetoothSessionState.Updating,
                address,
                "已连接，正在执行 OTA");
            await operation(device, cancellationToken);
        }
        finally
        {
            var session = GetOrCreateSession(address);
            if (session.Device?.State == DeviceState.Connected)
            {
                UpdateState(
                    session,
                    BluetoothSessionState.Connected,
                    address,
                    "设备已连接");
            }
            else
            {
                session.Device = null;
                UpdateState(session, BluetoothSessionState.Disconnected, address, "OTA 结束，设备已断开");
            }

            _operationLock.Release();
        }
    }

    private async Task<IDevice> ConnectCoreAsync(
        string address,
        CancellationToken cancellationToken)
    {
        var session = GetOrCreateSession(address);
        cancellationToken.ThrowIfCancellationRequested();
        if (_applicationSuspended)
            throw new OperationCanceledException("应用已进入后台，停止 BLE 建连。", new CancellationToken(true));
        if (session.Device is { State: DeviceState.Connected } current)
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
            // The platform may update Device.State before delivering its event.
            // Drop objects belonging to that closed connection before reconnecting.
            if (session.Device is { } stale)
                _sessionsByDeviceId.TryRemove(stale.Id, out _);
            session.Device = null;
            DiscardNotificationHandlers(session);
            UpdateState(session, BluetoothSessionState.Scanning, address, "正在查找设备");
            var device = await MeasureAsync(
                "Connection.FindDevice", address, null,
                () => _discoveryService.FindDeviceAsync(
                    address, cancellationToken: cancellationToken),
                value => $"found:state={value.State}");

            UpdateState(session, BluetoothSessionState.Connecting, address, "正在连接设备");
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

                session.Device = device;
                session.Generation++;
                _sessionsByDeviceId[device.Id] = session;
                UpdateState(session, BluetoothSessionState.Connected, address, "设备已连接");
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
                UpdateState(session, BluetoothSessionState.Error, address, $"连接失败：{exception.Message}");
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

    private async Task ClearNotificationsAsync(
        ConnectionSession session,
        CancellationToken cancellationToken)
    {
        using var timing = _diagnostics.BeginBleTiming(
            "Connection.ClearNotifications", session.Address,
            $"subscriptions={session.NotificationCharacteristics.Count}");
        var failures = 0;
        foreach (var pair in session.NotificationCharacteristics)
        {
            var characteristic = pair.Value;
            if (session.NotificationHandlers.TryGetValue(pair.Key, out var handler))
                characteristic.ValueUpdated -= handler;
            try
            {
                await MeasureAsync(
                    "GATT.NativeStopUpdates", session.Address, $"characteristic={characteristic.Id}",
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

        session.NotificationCharacteristics.Clear();
        session.NotificationHandlers.Clear();
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
        CancelConnectionLease(address);
        using var timing = _diagnostics.BeginBleTiming("Connection.DisconnectDevice", address);
        await WaitForOperationLockAsync("disconnectDevice", address, cancellationToken);
        try
        {
            if (GetOrCreateSession(address).Device is not null)
            {
                await DisconnectCoreAsync(
                    GetOrCreateSession(address),
                    cancellationToken,
                    BluetoothDisconnectReason.DeviceRemoval);
                timing.SetResult("disconnected");
            }
            else
            {
                timing.SetResult("target-not-connected");
                _discoveryService.InvalidateCachedDevice(address);
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

    internal async Task DisconnectAsync(
        string address,
        BluetoothDisconnectReason reason,
        CancellationToken cancellationToken = default)
    {
        CancelConnectionLease(address);
        using var timing = _diagnostics.BeginBleTiming("Connection.Disconnect", address);
        await WaitForOperationLockAsync("disconnect", address, cancellationToken);
        try
        {
            await DisconnectCoreAsync(
                GetOrCreateSession(address), cancellationToken, reason);
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

    private async Task DisconnectCoreAsync(
        ConnectionSession session,
        CancellationToken cancellationToken,
        BluetoothDisconnectReason reason)
    {
        var address = session.Address;
        var previousReason = session.ExpectedDisconnectReason;
        session.ExpectedDisconnectReason = reason;
        try
        {
            await ClearNotificationsAsync(session, cancellationToken);
            if (session.Device is { State: DeviceState.Connected } device)
            {
                _sessionsByDeviceId[device.Id] = session;
                _diagnostics.Information(
                    "BLE/Connection",
                    $"主动断开设备 {address}，reason={reason}。");
                await MeasureAsync(
                    "Connection.NativeDisconnect", address, $"reason={reason}",
                    () => _adapter.DisconnectDeviceAsync(device, cancellationToken));
            }

            var deviceId = session.Device?.Id;
            session.Device = null;
            if (deviceId is { } id)
                _sessionsByDeviceId.TryRemove(id, out _);
            lock (session.LeaseSync)
                session.LongLived = false;
            if (ShouldInvalidateCachedDevice(reason))
                _discoveryService.InvalidateCachedDevice(address);
            UpdateState(session, BluetoothSessionState.Disconnected, address, "未连接");
        }
        finally
        {
            session.ExpectedDisconnectReason = previousReason;
        }
    }

    private async Task ExpireConnectionLeaseAsync(
        ConnectionSession session,
        TimeSpan duration,
        CancellationTokenSource lease)
    {
        try
        {
            await Task.Delay(duration, _timeProvider, lease.Token);
            await WaitForOperationLockAsync("connectionLease", session.Address, CancellationToken.None);
            try
            {
                lock (session.LeaseSync)
                {
                    if (!ReferenceEquals(session.LeaseCancellation, lease) || session.LongLived)
                        return;
                    session.LeaseCancellation = null;
                }

                if (session.Device is not null && session.Snapshot.IsConnected)
                {
                    _diagnostics.Information(
                        "BLE/Connection",
                        $"主页查询连接租约到期，释放设备 {session.Address}。");
                    await DisconnectCoreAsync(
                        session,
                        CancellationToken.None,
                        BluetoothDisconnectReason.HomeQueryLeaseExpired);
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
                $"主页查询连接租约释放失败：device={session.Address}，{exception.Message}。");
        }
        finally
        {
            lock (session.LeaseSync)
            {
                if (ReferenceEquals(session.LeaseCancellation, lease))
                    session.LeaseCancellation = null;
                lease.Dispose();
            }
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
        ConnectionSession session,
        long generation,
        object? sender,
        CharacteristicUpdatedEventArgs eventArgs)
    {
        if (session.Generation != generation || !session.Snapshot.IsConnected)
            return;
        var address = session.Address;
        var characteristic = eventArgs.Characteristic;
        var data = characteristic.Value?.ToArray() ?? [];
        _diagnostics.Trace(
            "BLE/RX",
            $"通知：device={address}, service={characteristic.Service.Id}, characteristic={characteristic.Id}, length={data.Length}{FormatAgpHeader(characteristic.Service.Id, characteristic.Id, data)}, data={FormatBlePayload(characteristic.Service.Id, characteristic.Id, data)}");
        MessageReceived?.Invoke(
            this,
            new BluetoothMessage(
                address,
                generation,
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
        if (!_sessionsByDeviceId.TryGetValue(eventArgs.Device.Id, out var session))
            return;

        _diagnostics.Information(
            "BLE/Connection",
            $"设备已断开：{session.Address}。");
        HandleDisconnected(
            session,
            "设备已断开",
            session.ExpectedDisconnectReason ?? BluetoothDisconnectReason.Abnormal);
    }

    private void OnDeviceConnectionLost(
        object? sender,
        DeviceErrorEventArgs eventArgs)
    {
        if (!_sessionsByDeviceId.TryGetValue(eventArgs.Device.Id, out var session))
            return;

        _diagnostics.Warning(
            "BLE/Connection",
            $"设备连接丢失：{session.Address}，{eventArgs.ErrorMessage}");
        HandleDisconnected(
            session,
            "连接意外断开",
            session.ExpectedDisconnectReason ?? BluetoothDisconnectReason.Abnormal);
    }

    private void OnDeviceConnectionError(
        object? sender,
        DeviceErrorEventArgs eventArgs)
    {
        if (!_sessionsByDeviceId.TryGetValue(eventArgs.Device.Id, out var session))
            return;

        _diagnostics.Error(
            "BLE/Connection",
            $"设备连接错误：{session.Address}，{eventArgs.ErrorMessage}");
        UpdateState(
            session,
            BluetoothSessionState.Error,
            session.Address,
            eventArgs.ErrorMessage ?? "连接错误");
    }

    private void HandleDisconnected(
        ConnectionSession session,
        string message,
        BluetoothDisconnectReason reason)
    {
        var address = session.Address;
        var deviceId = session.Device?.Id;
        session.CancelLease();
        lock (session.LeaseSync)
            session.LongLived = false;
        DiscardNotificationHandlers(session);
        session.Device = null;
        if (deviceId is { } id)
            _sessionsByDeviceId.TryRemove(id, out _);
        if (session.ExpectedDisconnectReason is null && ShouldInvalidateCachedDevice(reason))
            _discoveryService.InvalidateCachedDevice(address);
        UpdateState(
            session,
            BluetoothSessionState.Disconnected,
            address,
            message);
    }

    private static void DiscardNotificationHandlers(ConnectionSession session)
    {
        foreach (var pair in session.NotificationCharacteristics)
        {
            if (session.NotificationHandlers.TryGetValue(pair.Key, out var handler))
                pair.Value.ValueUpdated -= handler;
        }
        session.NotificationCharacteristics.Clear();
        session.NotificationHandlers.Clear();
    }

    private static bool ShouldInvalidateCachedDevice(BluetoothDisconnectReason reason) =>
        reason is BluetoothDisconnectReason.DeviceRemoval or
            BluetoothDisconnectReason.QueryFailure or
            BluetoothDisconnectReason.Abnormal;

    private ConnectionSession GetOrCreateSession(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        return _sessions.GetOrAdd(address, value => new ConnectionSession(value));
    }

    private void UpdateState(
        ConnectionSession session,
        BluetoothSessionState state,
        string address,
        string message)
    {
        session.Snapshot = new BluetoothSessionSnapshot(state, address, message);
        if (state is not BluetoothSessionState.Disconnected)
            _lastActiveSession = session;
        StateChanged?.Invoke(this, session.Snapshot);
    }

    private sealed class ConnectionSession(string address)
    {
        public string Address { get; } = address;
        public object LeaseSync { get; } = new();
        public IDevice? Device { get; set; }
        public long Generation { get; set; }
        public ConcurrentDictionary<Guid, ICharacteristic> NotificationCharacteristics { get; } = new();
        public ConcurrentDictionary<Guid, EventHandler<CharacteristicUpdatedEventArgs>> NotificationHandlers { get; } = new();
        public CancellationTokenSource? LeaseCancellation { get; set; }
        public bool LongLived { get; set; }
        public BluetoothDisconnectReason? ExpectedDisconnectReason { get; set; }
        public BluetoothSessionSnapshot Snapshot { get; set; } =
            new(BluetoothSessionState.Disconnected, address, "未连接");

        public bool HasActiveLease
        {
            get { lock (LeaseSync) return LeaseCancellation is not null; }
        }

        public void CancelLease()
        {
            lock (LeaseSync)
            {
                var lease = LeaseCancellation;
                LeaseCancellation = null;
                lease?.Cancel();
            }
        }
    }
}
