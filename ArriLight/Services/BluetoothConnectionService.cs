using System.Collections.Concurrent;
using System.Text;
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

    private IDevice? _currentDevice;
    private string _currentAddress = string.Empty;
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

    public async Task<IDevice> ConnectAsync(
        string address,
        CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            return await ConnectCoreAsync(address, cancellationToken);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            await ClearNotificationsAsync(cancellationToken);
            if (_currentDevice is { State: DeviceState.Connected } device)
            {
                _diagnostics.Information(
                    "BLE/Connection",
                    $"主动断开设备 {_currentAddress}。");
                await _adapter.DisconnectDeviceAsync(device, cancellationToken);
            }

            _currentDevice = null;
            _currentAddress = string.Empty;
            UpdateState(BluetoothSessionState.Disconnected, string.Empty, "未连接");
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
        await _operationLock.WaitAsync(cancellationToken);
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
                $"service={serviceId}, characteristic={characteristicId}, writeType={writeType ?? characteristic.WriteType}, length={data.Length}, data={Convert.ToHexString(data)}");
            var result = await characteristic.WriteAsync(data, cancellationToken);
            _diagnostics.Debug("BLE/TX", $"写入完成，GATT result={result}。");
            return result;
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
    /// true 时先调用 <c>ClearServices</c> 强制重新发现。Mesh 节点在
    /// “未配网（Provisioning 服务）→ 已配网（Proxy 服务）”切换后，Android 的
    /// GATT 缓存可能过期，重新发现才能看到最新服务表。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<IReadOnlyList<GattServiceInfo>> EnumerateGattAsync(
        string address,
        bool refreshCache = true,
        CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            var device = await ConnectCoreAsync(address, cancellationToken);
            if (refreshCache)
                device.ClearServices();

            var services = await device.GetServicesAsync(cancellationToken);
            var result = new List<GattServiceInfo>(services.Count);
            foreach (var service in services)
            {
                var characteristics = await service.GetCharacteristicsAsync(cancellationToken);
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
            return result;
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
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            var characteristic = await GetCharacteristicCoreAsync(
                address,
                serviceId,
                characteristicId,
                cancellationToken);
            var result = await characteristic.ReadAsync(cancellationToken);
            var data = result.Item1 ?? [];
            _diagnostics.Trace(
                "BLE/RX",
                $"读取完成，service={serviceId}, characteristic={characteristicId}, result={result.Item2}, length={data.Length}, data={Convert.ToHexString(data)}");
            return data;
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
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            var characteristic = await GetCharacteristicCoreAsync(
                address,
                serviceId,
                characteristicId,
                cancellationToken);
            if (_notificationCharacteristics.ContainsKey(characteristicId))
                return;

            characteristic.ValueUpdated += OnCharacteristicValueUpdated;
            try
            {
                await characteristic.StartUpdatesAsync(cancellationToken);
                _notificationCharacteristics[characteristicId] = characteristic;
                _diagnostics.Information(
                    "BLE/Notify",
                    $"通知订阅成功：{characteristicId}。");
            }
            catch
            {
                characteristic.ValueUpdated -= OnCharacteristicValueUpdated;
                throw;
            }
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
        await _operationLock.WaitAsync(cancellationToken);
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
        if (_currentDevice is { State: DeviceState.Connected } current &&
            string.Equals(
                _currentAddress,
                address,
                StringComparison.OrdinalIgnoreCase))
        {
            _diagnostics.Debug(
                "BLE/Connection",
                $"复用已连接的设备 {address}。");
            return current;
        }

        if (_currentDevice is { State: DeviceState.Connected } other)
        {
            _diagnostics.Information(
                "BLE/Connection",
                $"当前连接属于 {_currentAddress}，切换到 {address}。");
            await ClearNotificationsAsync(cancellationToken);
            await _adapter.DisconnectDeviceAsync(other, cancellationToken);
        }

        UpdateState(BluetoothSessionState.Scanning, address, "正在查找设备");
        var device = await _discoveryService.FindDeviceAsync(
            address,
            cancellationToken: cancellationToken);

        UpdateState(BluetoothSessionState.Connecting, address, "正在连接设备");
        _diagnostics.Information(
            "BLE/Connection",
            $"开始连接 {address}，id={device.Id}，name={device.Name ?? "未命名"}，state={device.State}。");

        try
        {
            if (device.State != DeviceState.Connected)
                await _adapter.ConnectToDeviceAsync(device, default, cancellationToken);

            _currentDevice = device;
            _currentAddress = address;
            UpdateState(BluetoothSessionState.Connected, address, "设备已连接");
            _diagnostics.Information(
                "BLE/Connection",
                $"设备连接成功：{address}，id={device.Id}。");
            return device;
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

    private async Task<ICharacteristic> GetCharacteristicCoreAsync(
        string address,
        Guid serviceId,
        Guid characteristicId,
        CancellationToken cancellationToken)
    {
        var device = await ConnectCoreAsync(address, cancellationToken);
        var service = await device.GetServiceAsync(serviceId, cancellationToken)
            ?? throw new InvalidOperationException($"设备未提供服务 {serviceId}。");
        return await service.GetCharacteristicAsync(characteristicId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"服务 {serviceId} 未提供特征 {characteristicId}。");
    }

    private async Task ClearNotificationsAsync(CancellationToken cancellationToken)
    {
        foreach (var characteristic in _notificationCharacteristics.Values)
        {
            characteristic.ValueUpdated -= OnCharacteristicValueUpdated;
            try
            {
                await characteristic.StopUpdatesAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                _diagnostics.Warning(
                    "BLE/Notify",
                    $"停止通知 {characteristic.Id} 失败：{exception.Message}");
            }
        }

        _notificationCharacteristics.Clear();
    }

    private void OnCharacteristicValueUpdated(
        object? sender,
        CharacteristicUpdatedEventArgs eventArgs)
    {
        var characteristic = eventArgs.Characteristic;
        var data = characteristic.Value?.ToArray() ?? [];
        _diagnostics.Trace(
            "BLE/RX",
            $"通知：service={characteristic.Service.Id}, characteristic={characteristic.Id}, length={data.Length}, data={Convert.ToHexString(data)}");
        MessageReceived?.Invoke(
            this,
            new BluetoothMessage(
                characteristic.Service.Id,
                characteristic.Id,
                data,
                DateTimeOffset.Now));
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
