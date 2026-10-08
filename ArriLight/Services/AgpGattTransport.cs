#if DEBUG
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
#endif
using ArriOpto.BLE.Agp;
using Plugin.BLE.Abstractions;

namespace ArriLight;

/// <summary>
/// 在应用拥有的指定 BLE 会话上承载 AGP；每个设备地址拥有独立的通知和客户端通道。
/// </summary>
public sealed class AgpGattTransport : IAgpColorGattConnection
{
    private readonly BluetoothConnectionService _connectionService;
    private readonly string _address;
#if DEBUG
    private readonly ConcurrentDictionary<ushort, ResponseTimingProbe>
        _responseTimings = new();
#endif
    private readonly SemaphoreSlim _subscriptionLock = new(1, 1);
    private GattServiceInfo? _service;
    private bool _lightStateSubscribed;
    private bool _colorStateSubscribed;
    private bool _started;
    private bool _disposed;
    private long _connectionGeneration;

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
        using var timing = _connectionService.Diagnostics.BeginBleTiming(
            "Transport.Start", _address, $"subscribeState={subscribeState}");
        await _subscriptionLock.WaitAsync(cancellationToken);
        try
        {
            await StartCoreAsync(cancellationToken);
            if (subscribeState)
            {
                await EnsureLightStateSubscriptionCoreAsync(cancellationToken);
                await EnsureColorStateSubscriptionCoreAsync(cancellationToken);
            }
            timing.SetResult("started");
        }
        catch (Exception exception)
        {
            timing.SetResult(
                $"{(exception is OperationCanceledException ? "cancelled" : "failed")}:{exception.GetType().Name}");
            throw;
        }
        finally { _subscriptionLock.Release(); }
    }

    /// <summary>Upgrade the response-only transport without restarting its authenticated session.</summary>
    public async Task EnsureLightStateSubscriptionAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _subscriptionLock.WaitAsync(cancellationToken);
        try
        {
            await StartCoreAsync(cancellationToken);
            await EnsureLightStateSubscriptionCoreAsync(cancellationToken);
        }
        finally { _subscriptionLock.Release(); }
    }

    /// <summary>Subscribe to the optional color channel when present; failures leave Control Point usable.</summary>
    public async Task EnsureColorStateSubscriptionAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _subscriptionLock.WaitAsync(cancellationToken);
        try
        {
            await StartCoreAsync(cancellationToken);
            await EnsureColorStateSubscriptionCoreAsync(cancellationToken);
        }
        finally { _subscriptionLock.Release(); }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_started)
        {
            EnsureCurrentConnection();
            return;
        }

        var service = await _connectionService.GetGattServiceAsync(
            _address, AgpProfile.ServiceId, cancellationToken);
        var capabilities = RequireCharacteristic(service, AgpProfile.CapabilitiesCharacteristicId);
        var controlPoint = RequireCharacteristic(service, AgpProfile.ControlPointCharacteristicId);
        if (!capabilities.CanRead)
            throw new InvalidOperationException("AGP Capabilities 必须支持读取。");
        if (!controlPoint.CanWrite || !controlPoint.CanNotify)
            throw new InvalidOperationException("AGP Control Point 必须支持写入和 Indicate。");

        // Optional channels are discovered here, but their validation and CCCD
        // writes are deferred until the corresponding background step needs them.
        _service = service;
        HasColorStateCharacteristic = service.Characteristics.Any(
            item => item.Id == AgpProfile.ColorStateCharacteristicId);
        _connectionGeneration = _connectionService.GetConnectionGeneration(_address);
        EnsureCurrentConnection();
        _connectionService.MessageReceived += OnMessageReceived;
        try
        {
            await _connectionService.SubscribeAsync(_address, AgpProfile.ServiceId,
                AgpProfile.ControlPointCharacteristicId, cancellationToken, _connectionGeneration);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureCurrentConnection();
            _started = true;
        }
        catch
        {
            _connectionService.MessageReceived -= OnMessageReceived;
            throw;
        }
    }

    private async Task EnsureLightStateSubscriptionCoreAsync(CancellationToken cancellationToken)
    {
        EnsureCurrentConnection();
        cancellationToken.ThrowIfCancellationRequested();
        if (_lightStateSubscribed) return;
        var characteristic = RequireCharacteristic(_service!, AgpProfile.LightStateCharacteristicId);
        if (!characteristic.CanRead || !characteristic.CanNotify)
            throw new InvalidOperationException("AGP Light State 必须支持 Read 和 Notify。");
        await _connectionService.SubscribeAsync(_address, AgpProfile.ServiceId,
            AgpProfile.LightStateCharacteristicId, cancellationToken, _connectionGeneration);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureCurrentConnection();
        _lightStateSubscribed = true;
    }

    private async Task EnsureColorStateSubscriptionCoreAsync(CancellationToken cancellationToken)
    {
        EnsureCurrentConnection();
        cancellationToken.ThrowIfCancellationRequested();
        if (_colorStateSubscribed || !HasColorStateCharacteristic) return;
        var characteristic = RequireCharacteristic(_service!, AgpProfile.ColorStateCharacteristicId);
        if (!characteristic.CanRead || !characteristic.CanNotify)
            throw new InvalidOperationException("AGP Color State 存在时必须支持 Read 和 Notify。");
        await _connectionService.SubscribeAsync(_address, AgpProfile.ServiceId,
            AgpProfile.ColorStateCharacteristicId, cancellationToken, _connectionGeneration);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureCurrentConnection();
        _colorStateSubscribed = true;
    }

    private void EnsureCurrentConnection()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_connectionService.IsConnected(_address) ||
            _connectionService.GetConnectionGeneration(_address) != _connectionGeneration)
            throw new IOException("AGP 会话的设备连接已失效，需要重新建立通信和认证。");
    }
    public async Task<ReadOnlyMemory<byte>> ReadCapabilitiesAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureCurrentConnection();
        var result = await _connectionService.ReadAsync(
            _address,
            AgpProfile.ServiceId,
            AgpProfile.CapabilitiesCharacteristicId,
            cancellationToken, _connectionGeneration);
        EnsureCurrentConnection();
        return result;
    }

    public async Task<ReadOnlyMemory<byte>> ReadLightStateAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureCurrentConnection();
        var result = await _connectionService.ReadAsync(
            _address,
            AgpProfile.ServiceId,
            AgpProfile.LightStateCharacteristicId,
            cancellationToken, _connectionGeneration);
        EnsureCurrentConnection();
        return result;
    }

    public async Task<ReadOnlyMemory<byte>> ReadColorStateAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureCurrentConnection();
        if (!HasColorStateCharacteristic)
            throw new NotSupportedException("设备未提供 AGP Color State 特征。");

        var result = await _connectionService.ReadAsync(
            _address,
            AgpProfile.ServiceId,
            AgpProfile.ColorStateCharacteristicId,
            cancellationToken, _connectionGeneration);
        EnsureCurrentConnection();
        return result;
    }

    public async Task WriteControlPointAsync(
        ReadOnlyMemory<byte> packet,
        CancellationToken cancellationToken = default)
    {
        EnsureCurrentConnection();
#if DEBUG
        var responseKey = BeginResponseTiming(packet.Span);
        try
        {
#endif
            var result = await _connectionService.WriteAsync(
                _address,
                AgpProfile.ServiceId,
                AgpProfile.ControlPointCharacteristicId,
                packet.ToArray(),
                CharacteristicWriteType.WithResponse,
                cancellationToken, _connectionGeneration);
            if (result != 0)
                throw new IOException($"AGP Control Point 写入失败，GATT result={result}。");
            // A valid indication may arrive immediately before a disconnect
            // (notably AUTH_GET_TOKEN). Let AgpClient consume that response;
            // the next command still checks connection validity before writing.
#if DEBUG
        }
        catch
        {
            if (responseKey is { } key)
                _responseTimings.TryRemove(key, out _);
            throw;
        }
#endif
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default) =>
        _connectionService.DisconnectAsync(_address, cancellationToken);

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        _disposed = true;
        _connectionService.MessageReceived -= OnMessageReceived;
#if DEBUG
        _responseTimings.Clear();
#endif
        return ValueTask.CompletedTask;
    }

    private void OnMessageReceived(object? sender, BluetoothMessage message)
    {
        if (_disposed ||
            !_connectionService.IsConnected(_address) ||
            message.ConnectionGeneration != _connectionGeneration ||
            !string.Equals(message.Address, _address, StringComparison.OrdinalIgnoreCase))
            return;
        if (message.ServiceId != AgpProfile.ServiceId)
            return;

        var args = new AgpPacketEventArgs(message.Data);
        if (message.CharacteristicId == AgpProfile.ControlPointCharacteristicId)
        {
#if DEBUG
            MarkResponseTiming(message.Data);
#endif
            ControlPointIndicated?.Invoke(this, args);
        }
        else if (message.CharacteristicId == AgpProfile.LightStateCharacteristicId)
            LightStateNotified?.Invoke(this, args);
        else if (message.CharacteristicId == AgpProfile.ColorStateCharacteristicId)
            ColorStateNotified?.Invoke(this, args);
    }

#if DEBUG
    private ushort? BeginResponseTiming(ReadOnlySpan<byte> packet)
    {
        // Native BLE callbacks do not inherit AsyncLocal. Capture the business
        // stage here so the response can mark the same trace before AGP completion.
        // AgpClient serializes requests. Drop a previous probe left by a timeout.
        _responseTimings.Clear();
        var timing = BleTimingScope.Current;
        if (timing is null || packet.Length < AgpProfile.HeaderSize ||
            packet[2] != (byte)AgpMessageType.Request)
            return null;

        var requestOpcode = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(4, 2));
        var transactionId = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(6, 2));
        _responseTimings[transactionId] = new ResponseTimingProbe(
            requestOpcode, detail => timing.Mark(detail), Stopwatch.GetTimestamp());
        return transactionId;
    }

    private void MarkResponseTiming(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < AgpProfile.HeaderSize ||
            packet[2] != (byte)AgpMessageType.Response)
            return;

        var responseOpcode = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(4, 2));
        var transactionId = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(6, 2));
        if (_responseTimings.TryRemove(transactionId, out var probe))
        {
            probe.Mark(
                $"responseReceived requestOpcode=0x{probe.RequestOpcode:X4}({(AgpOpcode)probe.RequestOpcode}), responseOpcode=0x{responseOpcode:X4}({(AgpOpcode)responseOpcode}), tid={transactionId}, requestElapsed={Stopwatch.GetElapsedTime(probe.StartedAt).TotalMilliseconds:0.0}ms");
        }
    }

    private sealed record ResponseTimingProbe(ushort RequestOpcode, Action<string> Mark, long StartedAt);
#endif

    private static GattCharacteristicInfo RequireCharacteristic(
        GattServiceInfo service,
        Guid characteristicId) =>
        service.Characteristics.FirstOrDefault(item => item.Id == characteristicId)
        ?? throw new InvalidOperationException(
            $"AGP 服务未提供特征 {characteristicId}。");
}
