namespace ArriOpto.BLE.Agp;

/// <summary>无法在限定时间内收到匹配 AGP indication 时抛出的异常。</summary>
public sealed class AgpTimeoutException : TimeoutException
{
    public AgpTimeoutException(AgpOpcode opcode, ushort transactionId, TimeSpan timeout)
        : base($"Timed out waiting {timeout} for AGP {opcode} transaction {transactionId}.")
    {
        Opcode = opcode;
        TransactionId = transactionId;
        Timeout = timeout;
    }

    public AgpOpcode Opcode { get; }
    public ushort TransactionId { get; }
    public TimeSpan Timeout { get; }
}

/// <summary>AGP 状态消息或响应无法解析时提供原始数据与失败原因。</summary>
public sealed class AgpInvalidPacketEventArgs : EventArgs
{
    public AgpInvalidPacketEventArgs(ReadOnlyMemory<byte> data, Exception exception)
    {
        Data = data;
        Exception = exception;
    }

    public ReadOnlyMemory<byte> Data { get; }
    public Exception Exception { get; }
}

/// <summary>
/// 类型化 AGP v1 客户端。请求强制串行，自动分配非零事务 ID，并仅用同时匹配
/// opcode 与 transaction ID 的 Control Point Response 完成当前请求。
/// </summary>
public sealed partial class AgpClient : IAsyncDisposable
{
    internal static readonly TimeSpan DefaultResponseTimeout = TimeSpan.FromSeconds(6);
    internal static readonly TimeSpan DefaultAuthenticationTimeout = TimeSpan.FromSeconds(60);
    private readonly IAgpGattConnection _connection;
    private readonly IAgpColorGattConnection? _colorConnection;
    private readonly IAgpBatteryGattConnection? _batteryConnection;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly object _pendingLock = new();
    private PendingRequest? _pending;
    private Task? _disposeTask;
    private ushort _lastTransactionId;
    private bool _disposed;

    public AgpClient(IAgpGattConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _colorConnection = connection as IAgpColorGattConnection;
        _batteryConnection = connection as IAgpBatteryGattConnection;
        _connection.ControlPointIndicated += OnControlPointIndicated;
        _connection.LightStateNotified += OnLightStateNotified;
        if (_colorConnection is not null)
        {
            _colorConnection.ColorStateNotified += OnColorStateNotified;
        }
        if (_batteryConnection is not null)
        {
            _batteryConnection.BatteryStateNotified += OnBatteryStateNotified;
        }
    }

    public string DeviceId => _connection.DeviceId;
    public bool HasColorStateCharacteristic =>
        _colorConnection?.HasColorStateCharacteristic == true;
    public bool HasBatteryStateCharacteristic =>
        _batteryConnection?.HasBatteryStateCharacteristic == true;

    public event EventHandler<AgpLightState>? LightStateChanged;
    public event EventHandler<AgpColorStatus>? ColorStateChanged;
    public event EventHandler<AgpBatteryState>? BatteryStateChanged;
    public event EventHandler<AgpMessage>? UnmatchedControlMessageReceived;
    public event EventHandler<AgpInvalidPacketEventArgs>? InvalidPacketReceived;

    public async Task<AgpCapabilities> ReadCapabilitiesAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var packet = await _connection.ReadCapabilitiesAsync(cancellationToken)
            .ConfigureAwait(false);
        return AgpModelCodec.DecodeCapabilities(AgpCodec.Decode(packet.Span));
    }

    public async Task<AgpLightState> ReadLightStateAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var packet = await _connection.ReadLightStateAsync(cancellationToken)
            .ConfigureAwait(false);
        return AgpModelCodec.DecodeLightState(AgpCodec.Decode(packet.Span));
    }

    /// <summary>读取可选 Battery State 特征；不发送 Control Point 请求或触发状态事件。</summary>
    public async Task<AgpBatteryState> ReadBatteryStateAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_batteryConnection?.HasBatteryStateCharacteristic != true)
        {
            throw new NotSupportedException(
                "This AGP connection does not expose the optional Battery State characteristic.");
        }

        var packet = await _batteryConnection.ReadBatteryStateAsync(cancellationToken)
            .ConfigureAwait(false);
        return AgpModelCodec.DecodeBatteryStatus(AgpCodec.Decode(packet.Span));
    }

    /// <summary>
    /// Requests the provisioned device token. The device may keep this request
    /// pending while its product-level physical binding window is open.
    /// </summary>
    public async Task<AgpProvisionTokenResponse> GetProvisionTokenAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var message = await SendRequestAsync(
                AgpOpcode.AuthGetToken,
                ReadOnlyMemory<byte>.Empty,
                timeout ?? DefaultAuthenticationTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        return AgpModelCodec.DecodeProvisionTokenResponse(message);
    }

    /// <summary>Authenticates the current AGP connection with a 4-byte token.</summary>
    public async Task<AgpAuthenticationResponse> AuthenticateAsync(
        byte[] token,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(token);
        var message = await SendRequestAsync(
                AgpOpcode.AuthToken,
                AgpModelCodec.EncodeAuthToken(token),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return AgpModelCodec.DecodeAuthenticationResponse(message);
    }

    public async Task<AgpColorStatus> ReadColorStateAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_colorConnection?.HasColorStateCharacteristic != true)
        {
            throw new NotSupportedException(
                "This AGP connection does not expose the optional Color State characteristic.");
        }

        var packet = await _colorConnection.ReadColorStateAsync(cancellationToken)
            .ConfigureAwait(false);
        return AgpColorModelCodec.DecodeStatus(AgpCodec.Decode(packet.Span));
    }

    public Task<AgpControlResponse> SetOnOffAsync(
        bool isOn,
        IEnumerable<AgpTlv>? tlvs = null,
        CancellationToken cancellationToken = default) =>
        SetOnOffAsync(isOn ? AgpOnOff.On : AgpOnOff.Off, tlvs, cancellationToken);

    /// <summary>发送关灯、开灯或设备端翻转；成功响应表示请求已接受。</summary>
    /// <remarks>每次调用分配新 TID，不自动重试。翻转结果未知时应先读取状态再决定下一操作。</remarks>
    public async Task<AgpControlResponse> SetOnOffAsync(
        AgpOnOff onOff,
        IEnumerable<AgpTlv>? tlvs = null,
        CancellationToken cancellationToken = default) =>
        AgpModelCodec.DecodeControlResponse(
            await SendRequestAsync(
                    AgpOpcode.OnOffSet,
                    AgpModelCodec.EncodeOnOff(onOff, tlvs),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false));

    /// <summary>使用 ONOFF_SET=2，由设备翻转逻辑状态或渐变目标。</summary>
    /// <remarks>旧固件可能返回 InvalidValue；超时或断连后不自动重发。</remarks>
    public Task<AgpControlResponse> ToggleOnOffAsync(
        IEnumerable<AgpTlv>? tlvs = null,
        CancellationToken cancellationToken = default) =>
        SetOnOffAsync(AgpOnOff.Toggle, tlvs, cancellationToken);

    /// <summary>保存本地硬件延时模式；设备要求 Token 认证，成功返回当前配置。</summary>
    public async Task<AgpModeResponse> SetModeAsync(
        AgpModeConfig config,
        CancellationToken cancellationToken = default) =>
        AgpModelCodec.DecodeModeResponse(
            await SendRequestAsync(
                    AgpOpcode.ModeSet,
                    AgpModelCodec.EncodeModeSet(config),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false));

    /// <summary>查询本地硬件延时模式配置，无需 Token；不返回剩余倒计时。</summary>
    public async Task<AgpModeResponse> GetModeAsync(
        CancellationToken cancellationToken = default) =>
        AgpModelCodec.DecodeModeResponse(
            await SendRequestAsync(
                    AgpOpcode.ModeGet,
                    ReadOnlyMemory<byte>.Empty,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false));

    /// <summary>查询电池状态；PW01 无需 Token，调用方先检查 Battery 能力位。</summary>
    public async Task<AgpBatteryResponse> GetBatteryAsync(
        CancellationToken cancellationToken = default) =>
        AgpModelCodec.DecodeBatteryResponse(
            await SendRequestAsync(
                    AgpOpcode.BatteryGet,
                    ReadOnlyMemory<byte>.Empty,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false));

    public async Task<AgpControlResponse> SetLightnessAsync(
        byte brightnessPercent,
        IEnumerable<AgpTlv>? tlvs = null,
        CancellationToken cancellationToken = default) =>
        AgpModelCodec.DecodeControlResponse(
            await SendRequestAsync(
                    AgpOpcode.LightnessSet,
                    AgpModelCodec.EncodeLightness(brightnessPercent, tlvs),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false));

    public async Task<AgpTimeResponse> SetTimeAsync(
        uint utcSeconds,
        CancellationToken cancellationToken = default) =>
        AgpModelCodec.DecodeTimeResponse(
            await SendRequestAsync(
                    AgpOpcode.TimeSet,
                    AgpModelCodec.EncodeTimeSet(utcSeconds),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false));

    public Task<AgpTimeResponse> SetTimeAsync(
        DateTimeOffset utcTime,
        CancellationToken cancellationToken = default)
    {
        var seconds = utcTime.ToUniversalTime().ToUnixTimeSeconds();
        if (seconds is < 0 or > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(utcTime));
        }

        return SetTimeAsync((uint)seconds, cancellationToken);
    }

    public async Task<AgpTimeResponse> GetTimeAsync(
        CancellationToken cancellationToken = default) =>
        AgpModelCodec.DecodeTimeResponse(
            await SendRequestAsync(
                    AgpOpcode.TimeGet,
                    ReadOnlyMemory<byte>.Empty,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false));

    public async Task<AgpDelayOffResponse> SetDelayOffAsync(
        uint delaySeconds,
        CancellationToken cancellationToken = default) =>
        AgpModelCodec.DecodeDelayOffResponse(
            await SendRequestAsync(
                    AgpOpcode.DelayOffSet,
                    AgpModelCodec.EncodeDelayOffSet(delaySeconds),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false));

    public async Task<AgpDelayOffResponse> GetDelayOffAsync(
        CancellationToken cancellationToken = default) =>
        AgpModelCodec.DecodeDelayOffResponse(
            await SendRequestAsync(
                    AgpOpcode.DelayOffGet,
                    ReadOnlyMemory<byte>.Empty,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false));

    /// <summary>设置延时开灯；秒数为 0 时取消当前相对计时。</summary>
    public async Task<AgpDelayOnResponse> SetDelayOnAsync(
        uint delaySeconds,
        CancellationToken cancellationToken = default) =>
        AgpModelCodec.DecodeDelayOnResponse(
            await SendRequestAsync(
                    AgpOpcode.DelayOnSet,
                    AgpModelCodec.EncodeDelayOnSet(delaySeconds),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false));

    /// <summary>查询延时开灯是否启用及剩余秒数。</summary>
    public async Task<AgpDelayOnResponse> GetDelayOnAsync(
        CancellationToken cancellationToken = default) =>
        AgpModelCodec.DecodeDelayOnResponse(
            await SendRequestAsync(
                    AgpOpcode.DelayOnGet,
                    ReadOnlyMemory<byte>.Empty,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false));

    public async Task<AgpScheduleResponse> SetScheduleAsync(
        AgpSchedule schedule,
        CancellationToken cancellationToken = default) =>
        AgpModelCodec.DecodeScheduleResponse(
            await SendRequestAsync(
                    AgpOpcode.ScheduleSet,
                    AgpModelCodec.EncodeSchedule(schedule),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false));

    public async Task<AgpScheduleListResponse> DeleteScheduleAsync(
        byte slot,
        CancellationToken cancellationToken = default) =>
        AgpModelCodec.DecodeScheduleListResponse(
            await SendRequestAsync(
                    AgpOpcode.ScheduleDelete,
                    AgpModelCodec.EncodeScheduleSlot(slot),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false));

    public async Task<AgpScheduleResponse> GetScheduleAsync(
        byte slot,
        CancellationToken cancellationToken = default) =>
        AgpModelCodec.DecodeScheduleResponse(
            await SendRequestAsync(
                    AgpOpcode.ScheduleGet,
                    AgpModelCodec.EncodeScheduleSlot(slot),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false));

    public async Task<AgpScheduleListResponse> GetScheduleListAsync(
        CancellationToken cancellationToken = default) =>
        AgpModelCodec.DecodeScheduleListResponse(
            await SendRequestAsync(
                    AgpOpcode.ScheduleListGet,
                    ReadOnlyMemory<byte>.Empty,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false));

    public async Task<AgpControlResponse> SetLightness16Async(
        ushort lightness,
        AgpColorTransition? transition = null,
        CancellationToken cancellationToken = default) =>
        AgpModelCodec.DecodeStandardControlResponse(
            await SendRequestAsync(
                    AgpOpcode.Lightness16Set,
                    AgpColorModelCodec.EncodeLightness16Set(lightness, transition),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false));

    public Task<AgpColorQueryResponse<AgpLightness16State>> GetLightness16Async(
        CancellationToken cancellationToken = default) =>
        SendColorQueryAsync(
            AgpOpcode.Lightness16Get,
            AgpOpcode.Lightness16StateStatus,
            AgpColorModelCodec.DecodeLightness16State,
            cancellationToken);

    public async Task<AgpControlResponse> SetHslAsync(
        AgpHsl value,
        AgpColorTransition? transition = null,
        CancellationToken cancellationToken = default) =>
        AgpModelCodec.DecodeStandardControlResponse(
            await SendRequestAsync(
                    AgpOpcode.HslSet,
                    AgpColorModelCodec.EncodeHslSet(value, transition),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false));

    public Task<AgpColorQueryResponse<AgpHslState>> GetHslAsync(
        CancellationToken cancellationToken = default) =>
        SendColorQueryAsync(
            AgpOpcode.HslGet,
            AgpOpcode.HslStateStatus,
            AgpColorModelCodec.DecodeHslState,
            cancellationToken);

    public Task<AgpColorQueryResponse<AgpHslTarget>> GetHslTargetAsync(
        CancellationToken cancellationToken = default) =>
        SendColorQueryAsync(
            AgpOpcode.HslTargetGet,
            AgpOpcode.HslTargetStatus,
            AgpColorModelCodec.DecodeHslTarget,
            cancellationToken);

    public async Task<AgpControlResponse> SetHslDefaultAsync(
        AgpHsl value,
        CancellationToken cancellationToken = default) =>
        AgpModelCodec.DecodeStandardControlResponse(
            await SendRequestAsync(
                    AgpOpcode.HslDefaultSet,
                    AgpColorModelCodec.EncodeHslDefault(value),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false));

    public Task<AgpColorQueryResponse<AgpHslDefault>> GetHslDefaultAsync(
        CancellationToken cancellationToken = default) =>
        SendColorQueryAsync(
            AgpOpcode.HslDefaultGet,
            AgpOpcode.HslDefaultStatus,
            AgpColorModelCodec.DecodeHslDefault,
            cancellationToken);

    public async Task<AgpControlResponse> SetHslRangeAsync(
        AgpHslRange range,
        CancellationToken cancellationToken = default) =>
        AgpModelCodec.DecodeStandardControlResponse(
            await SendRequestAsync(
                    AgpOpcode.HslRangeSet,
                    AgpColorModelCodec.EncodeHslRange(range),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false));

    public Task<AgpColorQueryResponse<AgpHslRangeStatus>> GetHslRangeAsync(
        CancellationToken cancellationToken = default) =>
        SendColorQueryAsync(
            AgpOpcode.HslRangeGet,
            AgpOpcode.HslRangeStatus,
            AgpColorModelCodec.DecodeHslRange,
            cancellationToken);

    public async Task<AgpControlResponse> SetCtlAsync(
        AgpCtl value,
        AgpColorTransition? transition = null,
        CancellationToken cancellationToken = default) =>
        AgpModelCodec.DecodeStandardControlResponse(
            await SendRequestAsync(
                    AgpOpcode.CtlSet,
                    AgpColorModelCodec.EncodeCtlSet(value, transition),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false));

    public Task<AgpColorQueryResponse<AgpCtlState>> GetCtlAsync(
        CancellationToken cancellationToken = default) =>
        SendColorQueryAsync(
            AgpOpcode.CtlGet,
            AgpOpcode.CtlStateStatus,
            AgpColorModelCodec.DecodeCtlState,
            cancellationToken);

    public Task<AgpColorQueryResponse<AgpCtlTarget>> GetCtlTargetAsync(
        CancellationToken cancellationToken = default) =>
        SendColorQueryAsync(
            AgpOpcode.CtlTargetGet,
            AgpOpcode.CtlTargetStatus,
            AgpColorModelCodec.DecodeCtlTarget,
            cancellationToken);

    public async Task<AgpControlResponse> SetCtlDefaultAsync(
        AgpCtl value,
        CancellationToken cancellationToken = default) =>
        AgpModelCodec.DecodeStandardControlResponse(
            await SendRequestAsync(
                    AgpOpcode.CtlDefaultSet,
                    AgpColorModelCodec.EncodeCtlDefault(value),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false));

    public Task<AgpColorQueryResponse<AgpCtlDefault>> GetCtlDefaultAsync(
        CancellationToken cancellationToken = default) =>
        SendColorQueryAsync(
            AgpOpcode.CtlDefaultGet,
            AgpOpcode.CtlDefaultStatus,
            AgpColorModelCodec.DecodeCtlDefault,
            cancellationToken);

    public async Task<AgpControlResponse> SetCtlRangeAsync(
        AgpCtlRange range,
        CancellationToken cancellationToken = default) =>
        AgpModelCodec.DecodeStandardControlResponse(
            await SendRequestAsync(
                    AgpOpcode.CtlRangeSet,
                    AgpColorModelCodec.EncodeCtlRange(range),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false));

    public Task<AgpColorQueryResponse<AgpCtlRangeStatus>> GetCtlRangeAsync(
        CancellationToken cancellationToken = default) =>
        SendColorQueryAsync(
            AgpOpcode.CtlRangeGet,
            AgpOpcode.CtlRangeStatus,
            AgpColorModelCodec.DecodeCtlRange,
            cancellationToken);

    /// <summary>设置 AGP/PW01 动态灯效。</summary>
    public async Task<AgpEffectResponse> SetEffectAsync(
        AgpLightMode mode,
        ushort periodMilliseconds,
        CancellationToken cancellationToken = default) =>
        AgpModelCodec.DecodeEffectResponse(
            await SendRequestAsync(
                    AgpOpcode.EffectSet,
                    AgpModelCodec.EncodeEffect(mode, periodMilliseconds),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false));

    /// <summary>查询 AGP/PW01 动态灯效。</summary>
    public async Task<AgpEffectResponse> GetEffectAsync(
        CancellationToken cancellationToken = default) =>
        AgpModelCodec.DecodeEffectResponse(
            await SendRequestAsync(
                    AgpOpcode.EffectGet,
                    ReadOnlyMemory<byte>.Empty,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false));

    /// <summary>
    /// 发送一个扩展 Request 并等待匹配 Response。调用方仍受 v1 的 11 字节负载限制。
    /// </summary>
    public Task<AgpMessage> SendRequestAsync(
        AgpOpcode opcode,
        ReadOnlyMemory<byte> payload,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        SendRequestCoreAsync(
            opcode,
            payload,
            [opcode],
            timeout,
            cancellationToken);

    private async Task<AgpMessage> SendRequestCoreAsync(
        AgpOpcode opcode,
        ReadOnlyMemory<byte> payload,
        IReadOnlyCollection<AgpOpcode> expectedResponseOpcodes,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var responseTimeout = timeout ?? DefaultResponseTimeout;
        if (responseTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var gateHeld = false;
        try
        {
            using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCancellation.Token);
            await _requestLock.WaitAsync(execution.Token).ConfigureAwait(false);
            gateHeld = true;
            ThrowIfDisposed();
            return await SendRequestOnceAsync(opcode, payload, expectedResponseOpcodes,
                NextTransactionId(), responseTimeout, execution.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_disposed)
        {
            throw new ObjectDisposedException(nameof(AgpClient));
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(exception.Message, exception, cancellationToken);
        }
        finally
        {
            if (gateHeld)
                _requestLock.Release();
        }
    }

    // The caller owns _requestLock for either one request or an entire batch.
    private async Task<AgpMessage> SendRequestOnceAsync(
        AgpOpcode opcode,
        ReadOnlyMemory<byte> payload,
        IReadOnlyCollection<AgpOpcode> expectedResponseOpcodes,
        ushort transactionId,
        TimeSpan responseTimeout,
        CancellationToken cancellationToken,
        bool rejectInvalidResponse = false)
    {
        var packet = AgpCodec.EncodeRequest(opcode, transactionId, payload.Span);
        var pending = new PendingRequest(opcode, transactionId, expectedResponseOpcodes, rejectInvalidResponse);
        try
        {
            lock (_pendingLock)
            {
                _pending = pending;
            }

            await _connection.WriteControlPointAsync(packet, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                return await pending.Completion.Task
                    .WaitAsync(responseTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new AgpTimeoutException(opcode, transactionId, responseTimeout);
            }
        }
        finally
        {
            lock (_pendingLock)
            {
                if (ReferenceEquals(_pending, pending))
                {
                    _pending = null;
                }
            }
        }
    }

    private async Task<AgpColorQueryResponse<T>> SendColorQueryAsync<T>(
        AgpOpcode requestOpcode,
        AgpOpcode successStatusOpcode,
        Func<AgpMessage, T> decodeSuccess,
        CancellationToken cancellationToken)
        where T : AgpColorStatus
    {
        var message = await SendRequestCoreAsync(
                requestOpcode,
                ReadOnlyMemory<byte>.Empty,
                [requestOpcode, successStatusOpcode],
                timeout: null,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return AgpCommands.DecodeColorQuery(message, requestOpcode, successStatusOpcode, decodeSuccess);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }

        await _connection.DisconnectAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        lock (_pendingLock)
        {
            if (!_disposed)
            {
                _disposed = true;
                _disposeTask = DisposeCoreAsync();
            }

            return _disposeTask is null ? ValueTask.CompletedTask : new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        _lifetimeCancellation.Cancel();
        _connection.ControlPointIndicated -= OnControlPointIndicated;
        _connection.LightStateNotified -= OnLightStateNotified;
        if (_colorConnection is not null)
        {
            _colorConnection.ColorStateNotified -= OnColorStateNotified;
        }
        if (_batteryConnection is not null)
        {
            _batteryConnection.BatteryStateNotified -= OnBatteryStateNotified;
        }
        lock (_pendingLock)
        {
            _pending?.Completion.TrySetException(
                new ObjectDisposedException(nameof(AgpClient)));
            _pending = null;
        }

        try
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            // Let cancelled owners leave their finally blocks before destroying the shared gate.
            await _requestLock.WaitAsync().ConfigureAwait(false);
            _requestLock.Dispose();
            _lifetimeCancellation.Dispose();
        }
    }

    private void OnControlPointIndicated(object? sender, AgpPacketEventArgs args)
    {
        AgpMessage message;
        try
        {
            message = AgpCodec.Decode(args.Data.Span);
        }
        catch (Exception exception)
        {
            PendingRequest? malformedPending;
            lock (_pendingLock)
            {
                malformedPending = _pending;
            }

            // Attribute a malformed frame only when its header identifies the current batch request.
            // Unrelated/short frames keep their existing diagnostic-event behavior.
            var packet = args.Data.Span;
            if (malformedPending is { RejectInvalidResponse: true } && packet.Length >= AgpProfile.HeaderSize &&
                packet[2] == (byte)AgpMessageType.Response &&
                System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(packet[6..]) == malformedPending.TransactionId &&
                malformedPending.ExpectedResponseOpcodes.Contains((AgpOpcode)
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(packet[4..])))
            {
                malformedPending.Completion.TrySetException(exception);
            }

            InvalidPacketReceived?.Invoke(
                this,
                new AgpInvalidPacketEventArgs(args.Data, exception));
            return;
        }

        PendingRequest? pending;
        lock (_pendingLock)
        {
            pending = _pending;
        }

        if (message.MessageType == AgpMessageType.Response &&
            pending is not null &&
            pending.ExpectedResponseOpcodes.Contains(message.Opcode) &&
            message.TransactionId == pending.TransactionId)
        {
            pending.Completion.TrySetResult(message);
            return;
        }

        UnmatchedControlMessageReceived?.Invoke(this, message);
    }

    private void OnLightStateNotified(object? sender, AgpPacketEventArgs args)
    {
        try
        {
            var state = AgpModelCodec.DecodeLightState(AgpCodec.Decode(args.Data.Span));
            LightStateChanged?.Invoke(this, state);
        }
        catch (Exception exception)
        {
            InvalidPacketReceived?.Invoke(
                this,
                new AgpInvalidPacketEventArgs(args.Data, exception));
        }
    }

    private void OnColorStateNotified(object? sender, AgpPacketEventArgs args)
    {
        try
        {
            var state = AgpColorModelCodec.DecodeStatus(AgpCodec.Decode(args.Data.Span));
            ColorStateChanged?.Invoke(this, state);
        }
        catch (Exception exception)
        {
            InvalidPacketReceived?.Invoke(
                this,
                new AgpInvalidPacketEventArgs(args.Data, exception));
        }
    }

    private void OnBatteryStateNotified(object? sender, AgpPacketEventArgs args)
    {
        try
        {
            var state = AgpModelCodec.DecodeBatteryStatus(AgpCodec.Decode(args.Data.Span));
            BatteryStateChanged?.Invoke(this, state);
        }
        catch (Exception exception)
        {
            InvalidPacketReceived?.Invoke(
                this,
                new AgpInvalidPacketEventArgs(args.Data, exception));
        }
    }

    private ushort NextTransactionId()
    {
        _lastTransactionId++;
        if (_lastTransactionId == 0)
        {
            _lastTransactionId = 1;
        }

        return _lastTransactionId;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class PendingRequest(
        AgpOpcode opcode,
        ushort transactionId,
        IEnumerable<AgpOpcode> expectedResponseOpcodes,
        bool rejectInvalidResponse = false)
    {
        public AgpOpcode Opcode { get; } = opcode;
        public ushort TransactionId { get; } = transactionId;
        public HashSet<AgpOpcode> ExpectedResponseOpcodes { get; } =
            [.. expectedResponseOpcodes];
        public bool RejectInvalidResponse { get; } = rejectInvalidResponse;
        public TaskCompletionSource<AgpMessage> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
