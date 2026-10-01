using ArriOpto.BLE.Agp;
using Plugin.BLE.Abstractions;

namespace ArriLight;

/// <summary>
/// 在应用拥有的 BLE 会话上承载 AGP。连接切换仍由
/// <see cref="BluetoothConnectionService"/> 统一管理，本适配器只负责 AGP 特征。
/// </summary>
public sealed class AgpGattTransport : IAgpColorGattConnection
{
    private readonly BluetoothConnectionService _connectionService;
    private readonly string _address;
    private bool _started;
    private bool _disposed;

    public AgpGattTransport(
        BluetoothConnectionService connectionService,
        string address)
    {
        _connectionService = connectionService;
        _address = address;
    }

    public event EventHandler<AgpPacketEventArgs>? ControlPointIndicated;
    public event EventHandler<AgpPacketEventArgs>? LightStateNotified;
    public event EventHandler<AgpPacketEventArgs>? ColorStateNotified;

    public string DeviceId => _address;
    public bool HasColorStateCharacteristic { get; private set; }

    public async Task StartAsync(
        CancellationToken cancellationToken = default,
        bool subscribeState = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
            return;

        var services = await _connectionService.EnumerateGattAsync(
            _address,
            refreshCache: false,
            cancellationToken);
        var service = services.FirstOrDefault(item => item.Id == AgpProfile.ServiceId)
            ?? throw new InvalidOperationException(
                $"设备未提供 AGP 服务 {AgpProfile.ServiceId}。");

        var capabilities = RequireCharacteristic(
            service,
            AgpProfile.CapabilitiesCharacteristicId);
        var controlPoint = RequireCharacteristic(
            service,
            AgpProfile.ControlPointCharacteristicId);
        var lightState = RequireCharacteristic(
            service,
            AgpProfile.LightStateCharacteristicId);
        var colorState = service.Characteristics.FirstOrDefault(
            item => item.Id == AgpProfile.ColorStateCharacteristicId);
        if (!capabilities.CanRead || !lightState.CanRead)
        {
            throw new InvalidOperationException(
                "AGP Capabilities 和 Light State 必须支持读取。");
        }
        if (!controlPoint.CanWrite || !controlPoint.CanNotify)
        {
            throw new InvalidOperationException(
                "AGP Control Point 必须支持写入和 Indicate。");
        }
        if (!lightState.CanNotify)
        {
            throw new InvalidOperationException(
                "AGP Light State 必须支持 Notify。");
        }
        if (colorState is not null && (!colorState.CanRead || !colorState.CanNotify))
        {
            throw new InvalidOperationException(
                "AGP Color State 存在时必须支持 Read 和 Notify。");
        }

        HasColorStateCharacteristic = colorState is not null;

        _connectionService.MessageReceived += OnMessageReceived;
        try
        {
            // Control Point Indicate 用于响应及自动 ATT 接收确认；配对只需此项。
            await _connectionService.SubscribeAsync(
                _address,
                AgpProfile.ServiceId,
                AgpProfile.ControlPointCharacteristicId,
                cancellationToken);
            if (subscribeState)
            {
                await _connectionService.SubscribeAsync(
                    _address,
                    AgpProfile.ServiceId,
                    AgpProfile.LightStateCharacteristicId,
                    cancellationToken);
                if (HasColorStateCharacteristic)
                {
                    await _connectionService.SubscribeAsync(
                        _address,
                        AgpProfile.ServiceId,
                        AgpProfile.ColorStateCharacteristicId,
                        cancellationToken);
                }
            }
            _started = true;
        }
        catch
        {
            _connectionService.MessageReceived -= OnMessageReceived;
            throw;
        }
    }

    public async Task<ReadOnlyMemory<byte>> ReadCapabilitiesAsync(
        CancellationToken cancellationToken = default) =>
        await _connectionService.ReadAsync(
            _address,
            AgpProfile.ServiceId,
            AgpProfile.CapabilitiesCharacteristicId,
            cancellationToken);

    public async Task<ReadOnlyMemory<byte>> ReadLightStateAsync(
        CancellationToken cancellationToken = default) =>
        await _connectionService.ReadAsync(
            _address,
            AgpProfile.ServiceId,
            AgpProfile.LightStateCharacteristicId,
            cancellationToken);

    public async Task<ReadOnlyMemory<byte>> ReadColorStateAsync(
        CancellationToken cancellationToken = default)
    {
        if (!HasColorStateCharacteristic)
            throw new NotSupportedException("设备未提供 AGP Color State 特征。");

        return await _connectionService.ReadAsync(
            _address,
            AgpProfile.ServiceId,
            AgpProfile.ColorStateCharacteristicId,
            cancellationToken);
    }

    public async Task WriteControlPointAsync(
        ReadOnlyMemory<byte> packet,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var result = await _connectionService.WriteAsync(
            _address,
            AgpProfile.ServiceId,
            AgpProfile.ControlPointCharacteristicId,
            packet.ToArray(),
            CharacteristicWriteType.WithResponse,
            cancellationToken);
        if (result != 0)
            throw new IOException($"AGP Control Point 写入失败，GATT result={result}。");
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default) =>
        _connectionService.DisconnectAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        _disposed = true;
        _connectionService.MessageReceived -= OnMessageReceived;
        return ValueTask.CompletedTask;
    }

    private void OnMessageReceived(object? sender, BluetoothMessage message)
    {
        if (message.ServiceId != AgpProfile.ServiceId)
            return;

        var args = new AgpPacketEventArgs(message.Data);
        if (message.CharacteristicId == AgpProfile.ControlPointCharacteristicId)
            ControlPointIndicated?.Invoke(this, args);
        else if (message.CharacteristicId == AgpProfile.LightStateCharacteristicId)
            LightStateNotified?.Invoke(this, args);
        else if (message.CharacteristicId == AgpProfile.ColorStateCharacteristicId)
            ColorStateNotified?.Invoke(this, args);
    }

    private static GattCharacteristicInfo RequireCharacteristic(
        GattServiceInfo service,
        Guid characteristicId) =>
        service.Characteristics.FirstOrDefault(item => item.Id == characteristicId)
        ?? throw new InvalidOperationException(
            $"AGP 服务未提供特征 {characteristicId}。");
}
