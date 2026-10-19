using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;

namespace ArriOpto.BLE.Agp;

/// <summary>把 Plugin.BLE 设备适配成 <see cref="IAgpGattConnection"/>。</summary>
/// <remarks>
/// <see cref="OpenAsync"/> 返回前已经连接、发现必需及可选特征，并依次开启 Control Point
/// Indicate、Light State 及可选 Color/Battery State Notify。PW01 要求部分特征加密访问；配对 UI 和平台
/// 权限仍由宿主应用负责。本类不改变现有 Mesh 或 OTA 连接生命周期。
/// </remarks>
public sealed class PluginBleAgpConnection : IAgpColorGattConnection, IAgpBatteryGattConnection
{
    private readonly IAdapter _adapter;
    private readonly IDevice _device;
    private readonly ICharacteristic _capabilities;
    private readonly ICharacteristic _controlPoint;
    private readonly ICharacteristic _lightState;
    private readonly ICharacteristic? _colorState;
    private readonly ICharacteristic? _batteryState;
    private readonly Func<Func<Task>, Task> _dispatch;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private bool _controlUpdatesStarted;
    private bool _lightUpdatesStarted;
    private bool _colorUpdatesStarted;
    private bool _batteryUpdatesStarted;
    private bool _disconnected;
    private bool _disposed;

    private PluginBleAgpConnection(
        IAdapter adapter,
        IDevice device,
        ICharacteristic capabilities,
        ICharacteristic controlPoint,
        ICharacteristic lightState,
        ICharacteristic? colorState,
        ICharacteristic? batteryState,
        Func<Func<Task>, Task>? dispatcher)
    {
        _adapter = adapter;
        _device = device;
        _capabilities = capabilities;
        _controlPoint = controlPoint;
        _lightState = lightState;
        _colorState = colorState;
        _batteryState = batteryState;
        _dispatch = dispatcher ?? (operation => operation());
    }

    public event EventHandler<AgpPacketEventArgs>? ControlPointIndicated;
    public event EventHandler<AgpPacketEventArgs>? LightStateNotified;
    public event EventHandler<AgpPacketEventArgs>? ColorStateNotified;
    public event EventHandler<AgpPacketEventArgs>? BatteryStateNotified;

    public string DeviceId => _device.Id.ToString();
    public IDevice Device => _device;
    public bool HasColorStateCharacteristic => _colorState is not null;
    public bool HasBatteryStateCharacteristic => _batteryState is not null;

    public static async Task<PluginBleAgpConnection> OpenAsync(
        IAdapter adapter,
        IDevice device,
        Func<Func<Task>, Task>? dispatcher = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(device);

        var wasConnected = device.State == DeviceState.Connected;
        try
        {
            if (adapter.IsScanning)
            {
                await adapter.StopScanningForDevicesAsync().ConfigureAwait(false);
            }

            if (!wasConnected)
            {
                await adapter.ConnectToDeviceAsync(device, default, cancellationToken)
                    .ConfigureAwait(false);
            }

            var service = await device
                .GetServiceAsync(AgpProfile.ServiceId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Device does not expose AGP service {AgpProfile.ServiceId}.");
            var capabilities = await RequiredCharacteristicAsync(
                    service,
                    AgpProfile.CapabilitiesCharacteristicId,
                    cancellationToken)
                .ConfigureAwait(false);
            var controlPoint = await RequiredCharacteristicAsync(
                    service,
                    AgpProfile.ControlPointCharacteristicId,
                    cancellationToken)
                .ConfigureAwait(false);
            var lightState = await RequiredCharacteristicAsync(
                    service,
                    AgpProfile.LightStateCharacteristicId,
                    cancellationToken)
                .ConfigureAwait(false);
            var colorState = await service
                .GetCharacteristicAsync(
                    AgpProfile.ColorStateCharacteristicId,
                    cancellationToken)
                .ConfigureAwait(false);

            var batteryState = await service
                .GetCharacteristicAsync(
                    AgpProfile.BatteryStateCharacteristicId,
                    cancellationToken)
                .ConfigureAwait(false);

            ValidateCharacteristics(capabilities, controlPoint, lightState, colorState, batteryState);
            controlPoint.WriteType = CharacteristicWriteType.WithResponse;
            var connection = new PluginBleAgpConnection(
                adapter,
                device,
                capabilities,
                controlPoint,
                lightState,
                colorState,
                batteryState,
                dispatcher);
            await connection.StartUpdatesAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            if (!wasConnected && device.State == DeviceState.Connected)
            {
                try
                {
                    await adapter.DisconnectDeviceAsync(device, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // 清理失败不覆盖服务发现、加密或 CCC 订阅异常。
                }
            }

            throw;
        }
    }

    public Task<ReadOnlyMemory<byte>> ReadCapabilitiesAsync(
        CancellationToken cancellationToken = default) =>
        ReadAsync(_capabilities, cancellationToken);

    public Task<ReadOnlyMemory<byte>> ReadLightStateAsync(
        CancellationToken cancellationToken = default) =>
        ReadAsync(_lightState, cancellationToken);

    public Task<ReadOnlyMemory<byte>> ReadColorStateAsync(
        CancellationToken cancellationToken = default)
    {
        if (_colorState is null)
        {
            throw new NotSupportedException(
                "This AGP device does not expose the optional Color State characteristic.");
        }

        return ReadAsync(_colorState, cancellationToken);
    }

    public Task<ReadOnlyMemory<byte>> ReadBatteryStateAsync(
        CancellationToken cancellationToken = default)
    {
        if (_batteryState is null)
        {
            throw new NotSupportedException(
                "This AGP device does not expose the optional Battery State characteristic.");
        }

        return ReadAsync(_batteryState, cancellationToken);
    }

    public async Task WriteControlPointAsync(
        ReadOnlyMemory<byte> packet,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (packet.Length is < AgpProfile.HeaderSize or > AgpProfile.MaximumPacketSize)
        {
            throw new ArgumentException("Control Point packet is not a valid AGP v1 size.", nameof(packet));
        }

        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = 0;
            await _dispatch(
                    async () =>
                    {
                        result = await _controlPoint
                            .WriteAsync(packet.ToArray(), cancellationToken)
                            .ConfigureAwait(false);
                    })
                .ConfigureAwait(false);
            if (result != 0)
            {
                throw new IOException($"AGP Control Point write failed with GATT result {result}.");
            }
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_disconnected)
        {
            return;
        }

        _disconnected = true;
        await StopUpdatesBestEffortAsync(cancellationToken).ConfigureAwait(false);
        if (_device.State == DeviceState.Connected)
        {
            await _adapter.DisconnectDeviceAsync(_device, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // best-effort 清理不应掩盖更早发生的协议异常。
        }
        finally
        {
            _operationLock.Dispose();
        }
    }

    private async Task StartUpdatesAsync(CancellationToken cancellationToken)
    {
        _controlPoint.ValueUpdated += OnControlPointUpdated;
        _lightState.ValueUpdated += OnLightStateUpdated;
        if (_colorState is not null)
        {
            _colorState.ValueUpdated += OnColorStateUpdated;
        }
        if (_batteryState is not null)
        {
            _batteryState.ValueUpdated += OnBatteryStateUpdated;
        }
        try
        {
            await _dispatch(() => _controlPoint.StartUpdatesAsync(cancellationToken))
                .ConfigureAwait(false);
            _controlUpdatesStarted = true;
            await _dispatch(() => _lightState.StartUpdatesAsync(cancellationToken))
                .ConfigureAwait(false);
            _lightUpdatesStarted = true;
            if (_colorState is not null)
            {
                await _dispatch(() => _colorState.StartUpdatesAsync(cancellationToken))
                    .ConfigureAwait(false);
                _colorUpdatesStarted = true;
            }
            if (_batteryState is not null)
            {
                // A failed CCC write may already have enabled updates; include it in cleanup.
                _batteryUpdatesStarted = true;
                await _dispatch(() => _batteryState.StartUpdatesAsync(cancellationToken))
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            await StopUpdatesBestEffortAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task StopUpdatesBestEffortAsync(CancellationToken cancellationToken)
    {
        if (_batteryUpdatesStarted && _batteryState is not null)
        {
            _batteryUpdatesStarted = false;
            try
            {
                await _dispatch(() => _batteryState.StopUpdatesAsync(cancellationToken))
                    .ConfigureAwait(false);
            }
            catch
            {
                // 设备可能已经断开或重启。
            }
        }

        if (_colorUpdatesStarted && _colorState is not null)
        {
            _colorUpdatesStarted = false;
            try
            {
                await _dispatch(() => _colorState.StopUpdatesAsync(cancellationToken))
                    .ConfigureAwait(false);
            }
            catch
            {
                // 设备可能已经断开或重启。
            }
        }

        if (_lightUpdatesStarted)
        {
            _lightUpdatesStarted = false;
            try
            {
                await _dispatch(() => _lightState.StopUpdatesAsync(cancellationToken))
                    .ConfigureAwait(false);
            }
            catch
            {
                // 设备可能已经断开或重启。
            }
        }

        if (_controlUpdatesStarted)
        {
            _controlUpdatesStarted = false;
            try
            {
                await _dispatch(() => _controlPoint.StopUpdatesAsync(cancellationToken))
                    .ConfigureAwait(false);
            }
            catch
            {
                // 设备可能已经断开或重启。
            }
        }

        _controlPoint.ValueUpdated -= OnControlPointUpdated;
        _lightState.ValueUpdated -= OnLightStateUpdated;
        if (_colorState is not null)
        {
            _colorState.ValueUpdated -= OnColorStateUpdated;
        }
        if (_batteryState is not null)
        {
            _batteryState.ValueUpdated -= OnBatteryStateUpdated;
        }
    }

    private async Task<ReadOnlyMemory<byte>> ReadAsync(
        ICharacteristic characteristic,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte[]? data = null;
            var result = 0;
            await _dispatch(
                    async () =>
                    {
                        var read = await characteristic.ReadAsync(cancellationToken)
                            .ConfigureAwait(false);
                        data = read.Item1;
                        result = read.Item2;
                    })
                .ConfigureAwait(false);
            if (result != 0)
            {
                throw new IOException(
                    $"AGP characteristic {characteristic.Id} read failed with GATT result {result}.");
            }

            return data?.ToArray() ?? [];
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private static async Task<ICharacteristic> RequiredCharacteristicAsync(
        IService service,
        Guid characteristicId,
        CancellationToken cancellationToken)
    {
        var characteristic = await service
            .GetCharacteristicAsync(characteristicId, cancellationToken)
            .ConfigureAwait(false);
        return characteristic
            ?? throw new InvalidOperationException(
                $"Required AGP characteristic {characteristicId} was not found.");
    }

    private static void ValidateCharacteristics(
        ICharacteristic capabilities,
        ICharacteristic controlPoint,
        ICharacteristic lightState,
        ICharacteristic? colorState,
        ICharacteristic? batteryState)
    {
        if (!capabilities.CanRead)
        {
            throw new InvalidOperationException("AGP Capabilities characteristic is not readable.");
        }

        if (!controlPoint.CanWrite || !controlPoint.CanUpdate)
        {
            throw new InvalidOperationException(
                "AGP Control Point must support write and indication updates.");
        }

        if (!lightState.CanRead || !lightState.CanUpdate)
        {
            throw new InvalidOperationException(
                "AGP Light State must support read and notification updates.");
        }

        if (colorState is not null && (!colorState.CanRead || !colorState.CanUpdate))
        {
            throw new InvalidOperationException(
                "AGP Color State must support read and notification updates when present.");
        }

        if (batteryState is not null &&
            (!batteryState.CanRead || (batteryState.Properties & CharacteristicPropertyType.Notify) == 0))
        {
            throw new InvalidOperationException(
                "AGP Battery State must support read and notification updates when present.");
        }
    }

    private void OnControlPointUpdated(object? sender, CharacteristicUpdatedEventArgs args)
    {
        var value = args.Characteristic.Value;
        if (value is { Length: > 0 })
        {
            ControlPointIndicated?.Invoke(this, new AgpPacketEventArgs(value.ToArray()));
        }
    }

    private void OnLightStateUpdated(object? sender, CharacteristicUpdatedEventArgs args)
    {
        var value = args.Characteristic.Value;
        if (value is { Length: > 0 })
        {
            LightStateNotified?.Invoke(this, new AgpPacketEventArgs(value.ToArray()));
        }
    }

    private void OnColorStateUpdated(object? sender, CharacteristicUpdatedEventArgs args)
    {
        var value = args.Characteristic.Value;
        if (value is { Length: > 0 })
        {
            ColorStateNotified?.Invoke(this, new AgpPacketEventArgs(value.ToArray()));
        }
    }

    private void OnBatteryStateUpdated(object? sender, CharacteristicUpdatedEventArgs args)
    {
        var value = args.Characteristic.Value;
        if (value is { Length: > 0 })
        {
            BatteryStateNotified?.Invoke(this, new AgpPacketEventArgs(value.ToArray()));
        }
    }
}
