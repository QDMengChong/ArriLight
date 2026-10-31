namespace ArriOpto.BLE.Agp;

/// <summary>由 AgpCommands 创建的不可变请求；负载在创建时校验并复制。</summary>
public abstract class AgpCommand
{
    private protected AgpCommand(AgpOpcode opcode, byte[] payload, AgpOpcode[] responses, TimeSpan? timeout,
        bool retryOnBusy = true)
    {
        // Validate the complete frame before any batch can start writing.
        _ = AgpCodec.EncodeRequest(opcode, 1, payload);
        if (timeout is { } value)
            AgpBatchOptions.ValidateTimeout(value, nameof(timeout));
        Opcode = opcode;
        Payload = payload.ToArray();
        ExpectedResponseOpcodes = responses.ToArray();
        Timeout = timeout;
        RetryOnBusy = retryOnBusy;
    }

    public AgpOpcode Opcode { get; }
    internal ReadOnlyMemory<byte> Payload { get; }
    internal IReadOnlyCollection<AgpOpcode> ExpectedResponseOpcodes { get; }
    internal TimeSpan? Timeout { get; }
    internal bool RetryOnBusy { get; }
    internal abstract (object Response, AgpResultCode Result) Decode(AgpMessage message);
}

/// <summary>保留响应类型的批量命令描述。</summary>
public sealed class AgpCommand<TResponse> : AgpCommand where TResponse : class
{
    private readonly Func<AgpMessage, TResponse> _decode;
    private readonly Func<TResponse, AgpResultCode> _result;

    internal AgpCommand(AgpOpcode opcode, byte[] payload, AgpOpcode[] responses,
        Func<AgpMessage, TResponse> decode, Func<TResponse, AgpResultCode> result, TimeSpan? timeout = null,
        bool retryOnBusy = true)
        : base(opcode, payload, responses, timeout, retryOnBusy)
    {
        _decode = decode;
        _result = result;
    }

    internal override (object Response, AgpResultCode Result) Decode(AgpMessage message)
    {
        var response = _decode(message);
        return (response, _result(response));
    }
}

/// <summary>类型化 AGP Control Point 命令工厂；不发送报文。</summary>
public static class AgpCommands
{
    /// <summary>创建恢复出厂请求；即使批次允许 Busy 重试，此命令也只发送一次。</summary>
    public static AgpCommand<AgpFactoryResetResponse> FactoryReset() =>
        new(AgpOpcode.FactoryReset, [], [AgpOpcode.FactoryReset],
            AgpModelCodec.DecodeFactoryResetResponse, response => response.Result, retryOnBusy: false);

    public static AgpCommand<AgpProvisionTokenResponse> GetProvisionToken(TimeSpan? timeout = null) =>
        Create(AgpOpcode.AuthGetToken, [], AgpModelCodec.DecodeProvisionTokenResponse,
            response => response.Result, timeout ?? AgpClient.DefaultAuthenticationTimeout);

    public static AgpCommand<AgpAuthenticationResponse> Authenticate(byte[] token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return Create(AgpOpcode.AuthToken, AgpModelCodec.EncodeAuthToken(token),
            AgpModelCodec.DecodeAuthenticationResponse, response => response.Result);
    }

    public static AgpCommand<AgpControlResponse> SetOnOff(bool isOn, IEnumerable<AgpTlv>? tlvs = null) =>
        SetOnOff(isOn ? AgpOnOff.On : AgpOnOff.Off, tlvs);

    /// <summary>创建关灯、开灯或设备端翻转命令，保留原有可选 TLV。</summary>
    public static AgpCommand<AgpControlResponse> SetOnOff(AgpOnOff onOff, IEnumerable<AgpTlv>? tlvs = null) =>
        Control(AgpOpcode.OnOffSet, AgpModelCodec.EncodeOnOff(onOff, tlvs));

    /// <summary>创建 ONOFF_SET=2 翻转命令；批次仅在明确 Busy 时自动重试。</summary>
    public static AgpCommand<AgpControlResponse> ToggleOnOff(IEnumerable<AgpTlv>? tlvs = null) =>
        SetOnOff(AgpOnOff.Toggle, tlvs);

    public static AgpCommand<AgpControlResponse> SetLightness(byte brightnessPercent, IEnumerable<AgpTlv>? tlvs = null) =>
        Control(AgpOpcode.LightnessSet, AgpModelCodec.EncodeLightness(brightnessPercent, tlvs));

    public static AgpCommand<AgpModeResponse> SetMode(AgpModeConfig config) =>
        Create(AgpOpcode.ModeSet, AgpModelCodec.EncodeModeSet(config),
            AgpModelCodec.DecodeModeResponse, response => response.Result);

    public static AgpCommand<AgpModeResponse> GetMode() =>
        Create(AgpOpcode.ModeGet, [], AgpModelCodec.DecodeModeResponse, response => response.Result);

    /// <summary>创建空负载电池查询；调用方通过 Battery 能力位判断支持情况。</summary>
    public static AgpCommand<AgpBatteryResponse> GetBattery() =>
        Create(AgpOpcode.BatteryGet, [], AgpModelCodec.DecodeBatteryResponse, response => response.Result);

    public static AgpCommand<AgpTimeResponse> SetTime(uint utcSeconds) =>
        Create(AgpOpcode.TimeSet, AgpModelCodec.EncodeTimeSet(utcSeconds),
            AgpModelCodec.DecodeTimeResponse, response => response.Result);

    public static AgpCommand<AgpTimeResponse> SetTime(DateTimeOffset utcTime)
    {
        var seconds = utcTime.ToUniversalTime().ToUnixTimeSeconds();
        if (seconds is < 0 or > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(utcTime));
        return SetTime((uint)seconds);
    }

    public static AgpCommand<AgpTimeResponse> GetTime() =>
        Create(AgpOpcode.TimeGet, [], AgpModelCodec.DecodeTimeResponse, response => response.Result);

    public static AgpCommand<AgpDelayOffResponse> SetDelayOff(uint delaySeconds) =>
        Create(AgpOpcode.DelayOffSet, AgpModelCodec.EncodeDelayOffSet(delaySeconds),
            AgpModelCodec.DecodeDelayOffResponse, response => response.Result);

    public static AgpCommand<AgpDelayOffResponse> GetDelayOff() =>
        Create(AgpOpcode.DelayOffGet, [], AgpModelCodec.DecodeDelayOffResponse, response => response.Result);

    public static AgpCommand<AgpDelayOnResponse> SetDelayOn(uint delaySeconds) =>
        Create(AgpOpcode.DelayOnSet, AgpModelCodec.EncodeDelayOnSet(delaySeconds),
            AgpModelCodec.DecodeDelayOnResponse, response => response.Result);

    public static AgpCommand<AgpDelayOnResponse> GetDelayOn() =>
        Create(AgpOpcode.DelayOnGet, [], AgpModelCodec.DecodeDelayOnResponse, response => response.Result);

    public static AgpCommand<AgpScheduleResponse> SetSchedule(AgpSchedule schedule) =>
        Create(AgpOpcode.ScheduleSet, AgpModelCodec.EncodeSchedule(schedule),
            AgpModelCodec.DecodeScheduleResponse, response => response.Result);

    public static AgpCommand<AgpScheduleResponse> GetSchedule(byte slot) =>
        Create(AgpOpcode.ScheduleGet, AgpModelCodec.EncodeScheduleSlot(slot),
            AgpModelCodec.DecodeScheduleResponse, response => response.Result);

    public static AgpCommand<AgpScheduleListResponse> DeleteSchedule(byte slot) =>
        Create(AgpOpcode.ScheduleDelete, AgpModelCodec.EncodeScheduleSlot(slot),
            AgpModelCodec.DecodeScheduleListResponse, response => response.Result);

    public static AgpCommand<AgpScheduleListResponse> GetScheduleList() =>
        Create(AgpOpcode.ScheduleListGet, [], AgpModelCodec.DecodeScheduleListResponse, response => response.Result);

    public static AgpCommand<AgpEffectResponse> SetEffect(AgpLightMode mode, ushort periodMilliseconds) =>
        Create(AgpOpcode.EffectSet, AgpModelCodec.EncodeEffect(mode, periodMilliseconds),
            AgpModelCodec.DecodeEffectResponse, response => response.Result);

    public static AgpCommand<AgpEffectResponse> GetEffect() =>
        Create(AgpOpcode.EffectGet, [], AgpModelCodec.DecodeEffectResponse, response => response.Result);

    public static AgpCommand<AgpControlResponse> SetLightness16(ushort lightness, AgpColorTransition? transition = null) =>
        ColorControl(AgpOpcode.Lightness16Set, AgpColorModelCodec.EncodeLightness16Set(lightness, transition));

    public static AgpCommand<AgpColorQueryResponse<AgpLightness16State>> GetLightness16() =>
        ColorQuery(AgpOpcode.Lightness16Get, AgpOpcode.Lightness16StateStatus, AgpColorModelCodec.DecodeLightness16State);

    public static AgpCommand<AgpControlResponse> SetHsl(AgpHsl value, AgpColorTransition? transition = null) =>
        ColorControl(AgpOpcode.HslSet, AgpColorModelCodec.EncodeHslSet(value, transition));

    public static AgpCommand<AgpColorQueryResponse<AgpHslState>> GetHsl() =>
        ColorQuery(AgpOpcode.HslGet, AgpOpcode.HslStateStatus, AgpColorModelCodec.DecodeHslState);

    public static AgpCommand<AgpColorQueryResponse<AgpHslTarget>> GetHslTarget() =>
        ColorQuery(AgpOpcode.HslTargetGet, AgpOpcode.HslTargetStatus, AgpColorModelCodec.DecodeHslTarget);

    public static AgpCommand<AgpControlResponse> SetHslDefault(AgpHsl value) =>
        ColorControl(AgpOpcode.HslDefaultSet, AgpColorModelCodec.EncodeHslDefault(value));

    public static AgpCommand<AgpColorQueryResponse<AgpHslDefault>> GetHslDefault() =>
        ColorQuery(AgpOpcode.HslDefaultGet, AgpOpcode.HslDefaultStatus, AgpColorModelCodec.DecodeHslDefault);

    public static AgpCommand<AgpControlResponse> SetHslRange(AgpHslRange range) =>
        ColorControl(AgpOpcode.HslRangeSet, AgpColorModelCodec.EncodeHslRange(range));

    public static AgpCommand<AgpColorQueryResponse<AgpHslRangeStatus>> GetHslRange() =>
        ColorQuery(AgpOpcode.HslRangeGet, AgpOpcode.HslRangeStatus, AgpColorModelCodec.DecodeHslRange);

    public static AgpCommand<AgpControlResponse> SetCtl(AgpCtl value, AgpColorTransition? transition = null) =>
        ColorControl(AgpOpcode.CtlSet, AgpColorModelCodec.EncodeCtlSet(value, transition));

    public static AgpCommand<AgpColorQueryResponse<AgpCtlState>> GetCtl() =>
        ColorQuery(AgpOpcode.CtlGet, AgpOpcode.CtlStateStatus, AgpColorModelCodec.DecodeCtlState);

    public static AgpCommand<AgpColorQueryResponse<AgpCtlTarget>> GetCtlTarget() =>
        ColorQuery(AgpOpcode.CtlTargetGet, AgpOpcode.CtlTargetStatus, AgpColorModelCodec.DecodeCtlTarget);

    public static AgpCommand<AgpControlResponse> SetCtlDefault(AgpCtl value) =>
        ColorControl(AgpOpcode.CtlDefaultSet, AgpColorModelCodec.EncodeCtlDefault(value));

    public static AgpCommand<AgpColorQueryResponse<AgpCtlDefault>> GetCtlDefault() =>
        ColorQuery(AgpOpcode.CtlDefaultGet, AgpOpcode.CtlDefaultStatus, AgpColorModelCodec.DecodeCtlDefault);

    public static AgpCommand<AgpControlResponse> SetCtlRange(AgpCtlRange range) =>
        ColorControl(AgpOpcode.CtlRangeSet, AgpColorModelCodec.EncodeCtlRange(range));

    public static AgpCommand<AgpColorQueryResponse<AgpCtlRangeStatus>> GetCtlRange() =>
        ColorQuery(AgpOpcode.CtlRangeGet, AgpOpcode.CtlRangeStatus, AgpColorModelCodec.DecodeCtlRange);

    private static AgpCommand<T> Create<T>(AgpOpcode opcode, byte[] payload,
        Func<AgpMessage, T> decode, Func<T, AgpResultCode> result, TimeSpan? timeout = null) where T : class =>
        new(opcode, payload, [opcode], decode, result, timeout);

    private static AgpCommand<AgpControlResponse> Control(AgpOpcode opcode, byte[] payload) =>
        Create(opcode, payload, AgpModelCodec.DecodeControlResponse, response => response.Result);

    private static AgpCommand<AgpControlResponse> ColorControl(AgpOpcode opcode, byte[] payload) =>
        Create(opcode, payload, AgpModelCodec.DecodeStandardControlResponse, response => response.Result);

    private static AgpCommand<AgpColorQueryResponse<T>> ColorQuery<T>(AgpOpcode opcode,
        AgpOpcode statusOpcode, Func<AgpMessage, T> decode) where T : AgpColorStatus =>
        new(opcode, [], [opcode, statusOpcode],
            message => DecodeColorQuery(message, opcode, statusOpcode, decode), response => response.Result);

    internal static AgpColorQueryResponse<T> DecodeColorQuery<T>(AgpMessage message,
        AgpOpcode opcode, AgpOpcode statusOpcode, Func<AgpMessage, T> decode) where T : AgpColorStatus
    {
        if (message.Opcode == opcode)
        {
            var control = AgpModelCodec.DecodeStandardControlResponse(message);
            if (control.Result == AgpResultCode.Success)
                throw new AgpProtocolException(AgpResultCode.InvalidValue,
                    $"Successful {opcode} must return {statusOpcode} with its value.");
            return new(control.Result, null, control.StateRevision);
        }

        return new(AgpResultCode.Success, decode(message), null);
    }
}
