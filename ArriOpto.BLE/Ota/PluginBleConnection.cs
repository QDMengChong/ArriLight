using System.Diagnostics;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;

namespace ArriOpto.BLE.Ota;

/// <summary>
/// 把 Plugin.BLE 的设备、服务和特征适配为协议层的
/// <see cref="IOtaGattConnection"/>。
/// </summary>
/// <remarks>
/// <see cref="OpenAsync"/> 返回前已经完成连接、模式识别、MTU 协商和通知订阅。
/// 实例拥有连接的清理责任，推荐始终通过 <c>await using</c> 使用。所有 GATT 写入
/// 都经过内部锁串行执行。
/// </remarks>
public sealed class PluginBleConnection : IOtaGattConnection
{
    private readonly IAdapter _adapter;
    private readonly IDevice _device;
    private readonly ICharacteristic _command;
    private readonly ICharacteristic? _data;
    private readonly ICharacteristic _notify;
    private readonly Func<Func<Task>, Task> _dispatch;
    private readonly OtaDiagnostics? _diagnostics;
    private readonly PluginBleOtaTransport? _transport;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private bool _notificationsStarted;
    private bool _disconnected;
    private bool _disposed;

    private PluginBleConnection(
        IAdapter adapter,
        IDevice device,
        ICharacteristic command,
        ICharacteristic? data,
        ICharacteristic notify,
        OtaGattMode mode,
        int mtu,
        Func<Func<Task>, Task>? dispatcher,
        OtaDiagnostics? diagnostics, PluginBleOtaTransport? transport)
    {
        _adapter = adapter;
        _device = device;
        _command = command;
        _data = data;
        _notify = notify;
        Mode = mode;
        Mtu = mtu;
        _dispatch = dispatcher ?? (operation => operation());
        _diagnostics = diagnostics;
        _transport = transport;
    }

    /// <inheritdoc />
    public event EventHandler<OtaNotificationEventArgs>? NotificationReceived;

    /// <inheritdoc />
    public string DeviceId => _device.Id.ToString();

    /// <inheritdoc />
    public OtaGattMode Mode { get; }

    /// <inheritdoc />
    public int Mtu { get; }

    /// <summary>底层 Plugin.BLE 设备，仅供需要读取广播/平台信息的上层使用。</summary>
    public IDevice Device => _device;

    /// <summary>
    /// 打开 OTA 连接，自动识别 SLB、Single Bank APP 或 Single Bank OTA 模式。
    /// </summary>
    /// <param name="adapter">Plugin.BLE adapter。</param>
    /// <param name="device">当前扫描或重新扫描得到的设备。</param>
    /// <param name="requestedMtu">OTA 模式希望协商的 ATT MTU。</param>
    /// <param name="dispatcher">可选的平台线程调度器。</param>
    /// <param name="cancellationToken">取消连接、发现、MTU 和订阅。</param>
    /// <param name="diagnostics">可选结构化诊断配置。</param>
    /// <param name="transport">可选平台写入及 MTU 单位适配。</param>
    /// <returns>通知已经启用、可以立即执行协议命令的连接。</returns>
    /// <exception cref="OtaException">没有服务、服务组合异常或必要特征不可用。</exception>
    public static async Task<PluginBleConnection> OpenAsync(
        IAdapter adapter,
        IDevice device,
        int requestedMtu = 517,
        Func<Func<Task>, Task>? dispatcher = null,
        CancellationToken cancellationToken = default,
        OtaDiagnostics? diagnostics = null,
        PluginBleOtaTransport? transport = null)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(device);
        if (requestedMtu is < 23 or > 517)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedMtu));
        }

        var openStopwatch = Stopwatch.StartNew();
        diagnostics?.Report(
            new OtaDiagnosticEvent(
                DateTimeOffset.UtcNow,
                OtaDiagnosticLevel.Information,
                "connection.open.start",
                "connection",
                "开始建立 OTA GATT 连接。")
            {
                DeviceId = device.Id.ToString(),
                Mtu = requestedMtu,
                Properties = new Dictionary<string, string>
                {
                    ["deviceName"] = device.Name ?? string.Empty,
                    ["wasConnected"] = (device.State == DeviceState.Connected).ToString()
                }
            });

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

            var slbService = await device
                .GetServiceAsync(OtaConstants.Slb.Service, cancellationToken)
                .ConfigureAwait(false);
            var singleBankService = await device
                .GetServiceAsync(OtaConstants.SingleBank.Service, cancellationToken)
                .ConfigureAwait(false);

            if (slbService is not null && singleBankService is not null)
            {
                throw new OtaException(
                    OtaState.ErrorAbnormalCharacteristic,
                    "Device exposes both SLB and Single Bank OTA services.");
            }

            PluginBleConnection connection;
            if (slbService is not null)
            {
                connection = await CreateSlbAsync(
                        adapter,
                        device,
                        slbService,
                        requestedMtu,
                        dispatcher,
                        diagnostics,
                        transport,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (singleBankService is not null)
            {
                connection = await CreateSingleBankAsync(
                        adapter,
                        device,
                        singleBankService,
                        requestedMtu,
                        dispatcher,
                        diagnostics,
                        transport,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                throw new OtaException(
                    OtaState.ErrorNoService,
                    "Device does not expose a supported PHY Plus OTA service.");
            }

            connection._notify.ValueUpdated += connection.OnValueUpdated;
            try
            {
                await connection._dispatch(
                        () => connection._notify.StartUpdatesAsync(cancellationToken))
                    .ConfigureAwait(false);
                connection._notificationsStarted = true;
                openStopwatch.Stop();
                diagnostics?.Report(
                    new OtaDiagnosticEvent(
                        DateTimeOffset.UtcNow,
                        OtaDiagnosticLevel.Information,
                        "connection.open.ready",
                        "connection",
                        "OTA GATT 连接、特征和通知已准备完成。")
                    {
                        DeviceId = connection.DeviceId,
                        Mode = connection.Mode,
                        Mtu = connection.Mtu,
                        Elapsed = openStopwatch.Elapsed,
                        Properties = new Dictionary<string, string>
                        {
                            ["commandCharacteristic"] = connection._command.Id.ToString(),
                            ["dataCharacteristic"] =
                                connection._data?.Id.ToString() ?? string.Empty,
                            ["notifyCharacteristic"] = connection._notify.Id.ToString()
                        }
                    });
                return connection;
            }
            catch
            {
                connection._notify.ValueUpdated -= connection.OnValueUpdated;
                try
                {
                    await connection
                        .DisconnectAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // 清理失败不应覆盖更有诊断价值的通知订阅异常。
                }

                throw;
            }
        }
        catch (Exception exception)
        {
            openStopwatch.Stop();
            diagnostics?.Report(
                new OtaDiagnosticEvent(
                    DateTimeOffset.UtcNow,
                    OtaDiagnosticLevel.Error,
                    "connection.open.failed",
                    "connection",
                    "建立或准备 OTA GATT 连接失败。")
                {
                    DeviceId = device.Id.ToString(),
                    Elapsed = openStopwatch.Elapsed,
                    ExceptionType = exception.GetType().FullName,
                    ExceptionMessage = exception.Message
                });
            if (!wasConnected && device.State == DeviceState.Connected)
            {
                try
                {
                    await adapter
                        .DisconnectDeviceAsync(device, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // 清理失败不应覆盖原始的发现或订阅异常。
                }
            }

            throw;
        }
    }

    /// <inheritdoc />
    public Task WriteCommandAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default) =>
        WriteAsync(_command, data, OtaPayloadKind.Command, cancellationToken);

    /// <inheritdoc />
    public Task WriteDataAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        if (_data is null)
        {
            throw new InvalidOperationException(
                "Current OTA mode does not expose a write-without-response data characteristic.");
        }

        return WriteAsync(_data, data, OtaPayloadKind.FirmwareData, cancellationToken);
    }

    /// <inheritdoc />
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_disconnected)
        {
            return;
        }

        _disconnected = true;
        var disconnectStopwatch = Stopwatch.StartNew();
        if (_notificationsStarted)
        {
            _notificationsStarted = false;
            try
            {
                await _dispatch(() => _notify.StopUpdatesAsync(cancellationToken))
                    .ConfigureAwait(false);
            }
            catch
            {
                // OTA 完成后外设通常先重启，停止通知失败属于可接受的清理竞态。
            }
        }

        _notify.ValueUpdated -= OnValueUpdated;
        if (_device.State == DeviceState.Connected)
        {
            await _adapter.DisconnectDeviceAsync(_device, cancellationToken).ConfigureAwait(false);
        }

        disconnectStopwatch.Stop();
        _diagnostics?.Report(
            new OtaDiagnosticEvent(
                DateTimeOffset.UtcNow,
                OtaDiagnosticLevel.Information,
                "connection.closed",
                "connection",
                "OTA GATT 连接已清理。")
            {
                DeviceId = DeviceId,
                Mode = Mode,
                Mtu = Mtu,
                Elapsed = disconnectStopwatch.Elapsed
            });
    }

    /// <summary>
    /// 以 best-effort 方式停止通知并断开，且不让清理错误覆盖 OTA 协议异常。
    /// </summary>
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
            // best-effort 清理不能替换更早发生的 OTA 协议异常。
        }
        finally
        {
            _operationLock.Dispose();
        }
    }

    private static async Task<PluginBleConnection> CreateSlbAsync(
        IAdapter adapter,
        IDevice device,
        IService service,
        int requestedMtu,
        Func<Func<Task>, Task>? dispatcher,
        OtaDiagnostics? diagnostics,
        PluginBleOtaTransport? transport,
        CancellationToken cancellationToken)
    {
        var command = await RequiredCharacteristicAsync(
                service,
                OtaConstants.Slb.Command,
                cancellationToken)
            .ConfigureAwait(false);
        var data = await RequiredCharacteristicAsync(
                service,
                OtaConstants.Slb.Data,
                cancellationToken)
            .ConfigureAwait(false);
        var notify = await RequiredCharacteristicAsync(
                service,
                OtaConstants.Slb.Notify,
                cancellationToken)
            .ConfigureAwait(false);
        var mtu = await device.RequestMtuAsync(requestedMtu, cancellationToken)
            .ConfigureAwait(false);

        ValidateCharacteristics(command, data, notify);
        command.WriteType = CharacteristicWriteType.WithResponse;
        data.WriteType = CharacteristicWriteType.WithoutResponse;
        return new PluginBleConnection(
            adapter,
            device,
            command,
            data,
            notify,
            OtaGattMode.Slb,
            Math.Max(23, transport?.NormalizeRequestedMtuResult?.Invoke(mtu) ?? mtu),
            dispatcher,
            diagnostics, transport);
    }

    private static async Task<PluginBleConnection> CreateSingleBankAsync(
        IAdapter adapter,
        IDevice device,
        IService service,
        int requestedMtu,
        Func<Func<Task>, Task>? dispatcher,
        OtaDiagnostics? diagnostics,
        PluginBleOtaTransport? transport,
        CancellationToken cancellationToken)
    {
        var command = await RequiredCharacteristicAsync(
                service,
                OtaConstants.SingleBank.Command,
                cancellationToken)
            .ConfigureAwait(false);
        var notify = await RequiredCharacteristicAsync(
                service,
                OtaConstants.SingleBank.Notify,
                cancellationToken)
            .ConfigureAwait(false);
        var data = await service
            .GetCharacteristicAsync(OtaConstants.SingleBank.Data, cancellationToken)
            .ConfigureAwait(false);

        // APP 与 OTA 共用 service/command/notify；FF04 Data 是否存在是可靠的模式
        // 判据，不能仅依赖广播名称。
        var mode = data is null
            ? OtaGattMode.SingleBankApp
            : OtaGattMode.SingleBankOta;
        var mtu = mode == OtaGattMode.SingleBankOta
            ? await device.RequestMtuAsync(requestedMtu, cancellationToken).ConfigureAwait(false)
            : 23;

        ValidateCharacteristics(command, data, notify);
        command.WriteType = CharacteristicWriteType.WithResponse;
        if (data is not null)
        {
            data.WriteType = CharacteristicWriteType.WithoutResponse;
        }

        return new PluginBleConnection(
            adapter,
            device,
            command,
            data,
            notify,
            mode,
            Math.Max(23, mode == OtaGattMode.SingleBankOta
                ? transport?.NormalizeRequestedMtuResult?.Invoke(mtu) ?? mtu : mtu),
            dispatcher,
            diagnostics, transport);
    }

    private static async Task<ICharacteristic> RequiredCharacteristicAsync(
        IService service,
        Guid id,
        CancellationToken cancellationToken)
    {
        var characteristic = await service.GetCharacteristicAsync(id, cancellationToken)
            .ConfigureAwait(false);
        return characteristic
            ?? throw new OtaException(
                OtaState.ErrorCharacteristic,
                $"Required OTA characteristic {id} was not found.");
    }

    private static void ValidateCharacteristics(
        ICharacteristic command,
        ICharacteristic? data,
        ICharacteristic notify)
    {
        if (!command.CanWrite)
        {
            throw new OtaException(
                OtaState.ErrorCharacteristic,
                $"OTA command characteristic {command.Id} is not writable.");
        }

        if (data is not null && !data.CanWrite)
        {
            throw new OtaException(
                OtaState.ErrorCharacteristic,
                $"OTA data characteristic {data.Id} is not writable.");
        }

        if (!notify.CanUpdate)
        {
            throw new OtaException(
                OtaState.ErrorCharacteristic,
                $"OTA notification characteristic {notify.Id} does not support notifications.");
        }
    }

    private async Task WriteAsync(
        ICharacteristic characteristic,
        ReadOnlyMemory<byte> data,
        OtaPayloadKind payloadKind,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // 移动平台 GATT 栈通常只允许一个在途操作。即使协议状态机目前串行调用，
        // 这里仍在传输边界强制保证该约束。
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        var capture = _diagnostics?.CapturePayload(data.Span, payloadKind);
        var result = 0;
        using var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        writeTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await _dispatch(
                    async () =>
                    {
                        var write = characteristic.WriteType == CharacteristicWriteType.WithoutResponse &&
                            _transport?.WriteWithoutResponseAsync is { } nativeWrite
                            ? nativeWrite(_device, characteristic, data, writeTimeout.Token)
                            : characteristic.WriteAsync(data.ToArray(), writeTimeout.Token);
                        result = await write.WaitAsync(writeTimeout.Token)
                            .ConfigureAwait(false);
                    })
                .ConfigureAwait(false);
            if (result != 0)
            {
                throw new IOException(
                    $"BLE write to {characteristic.Id} failed with GATT result {result}.");
            }

            stopwatch.Stop();
            _diagnostics?.Report(
                new OtaDiagnosticEvent(
                    DateTimeOffset.UtcNow,
                    OtaDiagnosticLevel.Trace,
                    "gatt.write",
                    "gatt",
                    "GATT 写入成功。")
                {
                    DeviceId = DeviceId,
                    Mode = Mode,
                    Direction = OtaDiagnosticDirection.Transmit,
                    CharacteristicId = characteristic.Id,
                    PayloadLength = data.Length,
                    PayloadHex = capture?.Hex,
                    PayloadTruncated = capture?.IsTruncated ?? false,
                    Mtu = Mtu,
                    Elapsed = stopwatch.Elapsed,
                    GattResult = result,
                    Properties = new Dictionary<string, string>
                    {
                        ["payloadKind"] = payloadKind.ToString(),
                        ["writeType"] = characteristic.WriteType.ToString()
                    }
                });
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            _diagnostics?.Report(
                new OtaDiagnosticEvent(
                    DateTimeOffset.UtcNow,
                    exception is OperationCanceledException
                        ? OtaDiagnosticLevel.Warning
                        : OtaDiagnosticLevel.Error,
                    "gatt.write.failed",
                    "gatt",
                    "GATT 写入失败。")
                {
                    DeviceId = DeviceId,
                    Mode = Mode,
                    Direction = OtaDiagnosticDirection.Transmit,
                    CharacteristicId = characteristic.Id,
                    PayloadLength = data.Length,
                    PayloadHex = capture?.Hex,
                    PayloadTruncated = capture?.IsTruncated ?? false,
                    Mtu = Mtu,
                    Elapsed = stopwatch.Elapsed,
                    GattResult = result,
                    ExceptionType = exception.GetType().FullName,
                    ExceptionMessage = exception.Message,
                    Properties = new Dictionary<string, string>
                    {
                        ["payloadKind"] = payloadKind.ToString(),
                        ["writeType"] = characteristic.WriteType.ToString()
                    }
                });
            if (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested && writeTimeout.IsCancellationRequested)
                throw new TimeoutException("OTA GATT write did not complete within 10 seconds.", exception);
            throw;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private void OnValueUpdated(object? sender, CharacteristicUpdatedEventArgs args)
    {
        var value = args.Characteristic.Value;
        if (value is { Length: > 0 })
        {
            var capture = _diagnostics?.CapturePayload(
                value,
                OtaPayloadKind.Notification);
            _diagnostics?.Report(
                new OtaDiagnosticEvent(
                    DateTimeOffset.UtcNow,
                    OtaDiagnosticLevel.Trace,
                    "gatt.notification",
                    "gatt",
                    "收到 OTA notification。")
                {
                    DeviceId = DeviceId,
                    Mode = Mode,
                    Direction = OtaDiagnosticDirection.Receive,
                    CharacteristicId = _notify.Id,
                    PayloadLength = value.Length,
                    PayloadHex = capture?.Hex,
                    PayloadTruncated = capture?.IsTruncated ?? false,
                    Mtu = Mtu
                });
            // Plugin.BLE 可能复用 Value 数组；事件离开 BLE 边界前必须复制。
            NotificationReceived?.Invoke(
                this,
                new OtaNotificationEventArgs(value.ToArray()));
        }
    }
}
