using ArriOpto.BLE.Protocol;

namespace ArriOpto.BLE.Agp;

/// <summary>ONOFF_SET 的单字节操作值，之后可跟原有 TLV。</summary>
public enum AgpOnOff : byte
{
    Off = 0,
    On = 1,
    /// <summary>由设备翻转当前逻辑开关状态；渐变中翻转当前开关目标。</summary>
    Toggle = 2
}

public enum AgpLightMode : byte
{
    Steady = 0,
    Blink = 1,
    Breathe = 2
}

/// <summary>本地硬件输入的开关灯延时策略，独立于 Light State 的灯效模式。</summary>
public enum AgpLocalMode : byte
{
    DelayNone = 0,
    DelayOn = 1,
    DelayOff = 2,
    DelayBoth = 3,
    /// <summary>本地开灯立即执行，随后按 OffSeconds 自动关灯。</summary>
    OnAutoOff = 4,
    /// <summary>本地关灯立即执行，随后按 OnSeconds 自动开灯。</summary>
    OffAutoOn = 5
}

/// <summary>秒数描述延后执行的目标动作，含自动动作；范围 1..2,592,000，未使用方向为 0。</summary>
public sealed record AgpModeConfig(
    AgpLocalMode Mode,
    uint OnSeconds = 0,
    uint OffSeconds = 0);

/// <summary>MODE_SET/GET 成功返回当前配置，失败时 Config 为 null。</summary>
public sealed record AgpModeResponse(
    AgpResultCode Result,
    AgpModeConfig? Config);

/// <summary>BATTERY_GET 成功返回电池快照，失败时 State 为 null。</summary>
public sealed record AgpBatteryResponse(
    AgpResultCode Result,
    AgpBatteryState? State);

/// <summary>电池状态原始字段；未知电量不表示 0%，未知时间不表示 0 分钟。</summary>
public sealed record AgpBatteryState(
    byte Level,
    uint TimeToDischargeMinutes,
    uint TimeToChargeMinutes,
    byte Flags)
{
    public const byte UnknownLevel = 0xFF;
    public const uint UnknownTimeMinutes = 0xFFFFFF;
    public const byte UnknownFlags = 0xFF;

    /// <summary>0..100 的电量百分比；设备尚未获得电量时为 null。</summary>
    public byte? LevelPercent => Level == UnknownLevel ? null : Level;
    public uint? KnownTimeToDischargeMinutes =>
        TimeToDischargeMinutes == UnknownTimeMinutes ? null : TimeToDischargeMinutes;
    public uint? KnownTimeToChargeMinutes =>
        TimeToChargeMinutes == UnknownTimeMinutes ? null : TimeToChargeMinutes;
    public AgpBatteryFlags ParsedFlags => new(Flags);
}

/// <summary>四个 2-bit 电池状态字段，每项的 3 都表示未知。</summary>
public readonly record struct AgpBatteryFlags(byte Raw)
{
    /// <summary>0 不存在，1 可拆卸，2 不可拆卸，3 未知。</summary>
    public byte Presence => (byte)(Raw & 0x03);
    /// <summary>0 严重低电，1 低电，2 电量良好，3 未知。</summary>
    public byte Indicator => (byte)((Raw >> 2) & 0x03);
    /// <summary>0 不可充电，1 未充电，2 正在充电，3 未知。</summary>
    public byte Charging => (byte)((Raw >> 4) & 0x03);
    /// <summary>0 保留，1 无需维护，2 需要维护，3 未知。</summary>
    public byte Serviceability => (byte)((Raw >> 6) & 0x03);
}

public enum AgpScheduleType : byte
{
    Once = 0,
    Daily = 1,
    Weekly = 2,
    Monthly = 3
}

public enum AgpScheduleAction : byte
{
    Off = 0,
    On = 1
}

public sealed record AgpControlResponse(
    AgpResultCode Result,
    uint StateRevision);

public sealed record AgpProvisionTokenResponse(
    AgpResultCode Result,
    byte[]? Token);

public sealed record AgpAuthenticationResponse(
    AgpResultCode Result);

/// <summary>Light State 的业务状态位，独立于 AGP 帧头 flags。</summary>
[Flags]
public enum AgpLightStateFlags : byte
{
    None = 0,
    Locked = 1 << 0
}

public sealed record AgpLightState(
    uint Revision,
    bool IsOn,
    byte PresentBrightness,
    byte TargetBrightness,
    byte LastNonzeroBrightness,
    byte RemainingTime,
    AgpLightMode Mode,
    byte StateFlags)
{
    /// <summary>设备上报的锁定状态；旧固件该位为 0，不能据此判断是否支持锁定上报。</summary>
    public bool IsLocked => (StateFlags & (byte)AgpLightStateFlags.Locked) != 0;
}

public sealed record AgpTimeResponse(
    AgpResultCode Result,
    bool IsValid,
    uint UtcSeconds,
    ushort TimeRevision)
{
    public DateTimeOffset? UtcTime => IsValid
        ? DateTimeOffset.FromUnixTimeSeconds(UtcSeconds)
        : null;
}

public sealed record AgpDelayOffResponse(
    AgpResultCode Result,
    bool IsActive,
    uint RemainingSeconds);

public sealed record AgpDelayOnResponse(
    AgpResultCode Result,
    bool IsActive,
    uint RemainingSeconds);

/// <summary>
/// AGP 日程的公共线格式。Once 的 <see cref="TimeValue"/> 是 Unix UTC 秒；
/// 其他类型是 UTC 日内秒。Weekly 的 Selector 是周一 bit0 到周日 bit6，
/// Monthly 的 Selector 是日期 1..31。
/// </summary>
public sealed record AgpSchedule(
    byte Slot,
    AgpScheduleType Type,
    bool IsEnabled,
    AgpScheduleAction Action,
    byte BrightnessPercent,
    byte Selector,
    uint TimeValue)
{
    public static AgpSchedule Once(
        byte slot,
        bool isEnabled,
        AgpScheduleAction action,
        byte brightnessPercent,
        uint executeAtUtc) =>
        new(slot, AgpScheduleType.Once, isEnabled, action, brightnessPercent, 0, executeAtUtc);

    public static AgpSchedule Daily(
        byte slot,
        bool isEnabled,
        AgpScheduleAction action,
        byte brightnessPercent,
        uint secondsOfDayUtc) =>
        new(slot, AgpScheduleType.Daily, isEnabled, action, brightnessPercent, 0, secondsOfDayUtc);

    public static AgpSchedule Weekly(
        byte slot,
        bool isEnabled,
        AgpScheduleAction action,
        byte brightnessPercent,
        byte weekdayMask,
        uint secondsOfDayUtc) =>
        new(slot, AgpScheduleType.Weekly, isEnabled, action, brightnessPercent, weekdayMask, secondsOfDayUtc);

    public static AgpSchedule Monthly(
        byte slot,
        bool isEnabled,
        AgpScheduleAction action,
        byte brightnessPercent,
        byte dayOfMonth,
        uint secondsOfDayUtc) =>
        new(slot, AgpScheduleType.Monthly, isEnabled, action, brightnessPercent, dayOfMonth, secondsOfDayUtc);
}

public sealed record AgpScheduleResponse(
    AgpResultCode Result,
    AgpSchedule? Schedule);

public sealed record AgpScheduleListResponse(
    AgpResultCode Result,
    byte MaximumSlots,
    byte EnabledSlotMask);

public sealed record AgpEffectResponse(
    AgpResultCode Result,
    AgpLightMode Mode,
    ushort PeriodMilliseconds);

/// <summary>AGP 已登记模型、电池状态及 PW01 灯效扩展的负载编解码。</summary>
public static class AgpModelCodec
{
    public const int AuthenticationTokenLength = 4;
    public const byte MaximumBrightnessPercent = 100;
    public const uint MinimumUtcSeconds = 1_704_067_200;
    public const uint MaximumUtcSeconds = 4_102_444_799;
    public const uint MaximumDelaySeconds = 2_592_000;
    public const uint MaximumDelayOffSeconds = MaximumDelaySeconds;
    public const uint MaximumDelayOnSeconds = MaximumDelaySeconds;
    public const uint MaximumSecondsOfDay = 86_399;
    public const byte MaximumScheduleSlots = 8;
    public const ushort MinimumEffectPeriodMilliseconds = 200;
    public const ushort MaximumEffectPeriodMilliseconds = 10_000;

    public static byte[] EncodeOnOff(bool isOn, IEnumerable<AgpTlv>? tlvs = null) =>
        EncodeOnOff(isOn ? AgpOnOff.On : AgpOnOff.Off, tlvs);

    /// <summary>编码关灯、开灯或翻转；拒绝未登记的操作值。</summary>
    public static byte[] EncodeOnOff(AgpOnOff onOff, IEnumerable<AgpTlv>? tlvs = null)
    {
        if (onOff is not (AgpOnOff.Off or AgpOnOff.On or AgpOnOff.Toggle))
        {
            throw new ArgumentOutOfRangeException(nameof(onOff));
        }

        return EncodeLightValue((byte)onOff, tlvs);
    }

    /// <summary>编码 MODE_SET 的 mode 及对应小端秒数，六种模式负载为 1/5/5/9/5/5 字节。</summary>
    public static byte[] EncodeModeSet(AgpModeConfig config)
    {
        ValidateModeConfig(config);
        var payload = new byte[ModePayloadLength(config.Mode)];
        payload[0] = (byte)config.Mode;
        var parameters = ModeParameters(config.Mode);
        if ((parameters & AgpLocalMode.DelayOn) != 0)
        {
            BinaryCodec.WriteUInt32LittleEndian(payload.AsSpan(1), config.OnSeconds);
        }

        if ((parameters & AgpLocalMode.DelayOff) != 0)
        {
            var offset = config.Mode == AgpLocalMode.DelayBoth ? 5 : 1;
            BinaryCodec.WriteUInt32LittleEndian(payload.AsSpan(offset), config.OffSeconds);
        }

        return payload;
    }

    /// <summary>MODE_SET/GET 成功为 result + 配置，失败仅含 result，不含 revision。</summary>
    public static AgpModeResponse DecodeModeResponse(AgpMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.MessageType != AgpMessageType.Response ||
            message.Opcode is not (AgpOpcode.ModeSet or AgpOpcode.ModeGet) ||
            message.TransactionId == 0)
        {
            throw InvalidPayload("The message is not a MODE_SET/GET response with a nonzero transaction ID.");
        }

        var payload = message.Payload.Span;
        if (payload.IsEmpty)
        {
            throw InvalidLength("Mode response must contain a result code.");
        }

        var result = DecodeResult(payload[0]);
        if (result != AgpResultCode.Success)
        {
            if (payload.Length != 1)
            {
                throw InvalidLength("A failed mode response must contain only its result code.");
            }

            return new AgpModeResponse(result, null);
        }

        if (payload.Length < 2)
        {
            throw InvalidLength("A successful mode response must contain its configuration.");
        }

        var mode = (AgpLocalMode)payload[1];
        if (!Enum.IsDefined(mode))
        {
            throw InvalidPayload("Mode response contains an unknown local mode.");
        }

        if (payload.Length != 1 + ModePayloadLength(mode))
        {
            throw InvalidLength("Mode response length does not match its local mode.");
        }

        var parameters = ModeParameters(mode);
        var onSeconds = (parameters & AgpLocalMode.DelayOn) != 0
            ? BinaryCodec.ReadUInt32LittleEndian(payload[2..]) : 0;
        var offSeconds = (parameters & AgpLocalMode.DelayOff) != 0
            ? BinaryCodec.ReadUInt32LittleEndian(payload[(mode == AgpLocalMode.DelayBoth ? 6 : 2)..]) : 0;
        var config = new AgpModeConfig(mode, onSeconds, offSeconds);
        try
        {
            ValidateModeConfig(config);
        }
        catch (ArgumentException exception)
        {
            throw InvalidPayload($"Mode response configuration is invalid: {exception.Message}");
        }

        return new AgpModeResponse(result, config);
    }

    private static int ModePayloadLength(AgpLocalMode mode) => mode switch
    {
        AgpLocalMode.DelayNone => 1,
        AgpLocalMode.DelayBoth => 9,
        _ => 5
    };

    // Mode IDs 4/5 are not direction bitmasks; their parameters describe the target action.
    private static AgpLocalMode ModeParameters(AgpLocalMode mode) => mode switch
    {
        AgpLocalMode.OnAutoOff => AgpLocalMode.DelayOff,
        AgpLocalMode.OffAutoOn => AgpLocalMode.DelayOn,
        _ => mode
    };

    private static void ValidateModeConfig(AgpModeConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!Enum.IsDefined(config.Mode) ||
            config.OnSeconds > MaximumDelaySeconds || config.OffSeconds > MaximumDelaySeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(config), "Local mode or duration is out of range.");
        }

        var parameters = ModeParameters(config.Mode);
        if (((parameters & AgpLocalMode.DelayOn) != 0) != (config.OnSeconds != 0) ||
            ((parameters & AgpLocalMode.DelayOff) != 0) != (config.OffSeconds != 0))
        {
            throw new ArgumentException("Enabled directions require positive seconds; unused directions require zero.", nameof(config));
        }
    }

    public static byte[] EncodeAuthToken(ReadOnlySpan<byte> token)
    {
        if (token.Length != AuthenticationTokenLength)
        {
            throw new ArgumentException(
                $"AGP authentication tokens must be exactly {AuthenticationTokenLength} bytes.",
                nameof(token));
        }

        return token.ToArray();
    }

    public static AgpProvisionTokenResponse DecodeProvisionTokenResponse(AgpMessage message)
    {
        if (message.MessageType != AgpMessageType.Response ||
            message.Opcode != AgpOpcode.AuthGetToken)
        {
            throw InvalidPayload("The message is not an AUTH_GET_TOKEN response.");
        }

        var payload = message.Payload.Span;
        if (payload.Length is not (1 or 5))
        {
            throw InvalidPayload("AUTH_GET_TOKEN response must contain result or result + 4-byte token.");
        }

        var result = DecodeResult(payload[0]);
        if (result == AgpResultCode.Success)
        {
            if (payload.Length != 5)
            {
                throw InvalidPayload("A successful AUTH_GET_TOKEN response must contain a 4-byte token.");
            }

            return new AgpProvisionTokenResponse(result, payload[1..].ToArray());
        }

        if (payload.Length != 1)
        {
            throw InvalidPayload("A failed AUTH_GET_TOKEN response must contain only the result code.");
        }

        return new AgpProvisionTokenResponse(result, null);
    }

    public static AgpAuthenticationResponse DecodeAuthenticationResponse(AgpMessage message)
    {
        if (message.MessageType != AgpMessageType.Response ||
            message.Opcode != AgpOpcode.AuthToken ||
            message.Payload.Length != 1)
        {
            throw InvalidPayload("The message is not an AUTH_TOKEN response.");
        }

        return new AgpAuthenticationResponse(DecodeResult(message.Payload.Span[0]));
    }

    public static byte[] EncodeLightness(
        byte brightnessPercent,
        IEnumerable<AgpTlv>? tlvs = null)
    {
        if (brightnessPercent > MaximumBrightnessPercent)
        {
            throw new ArgumentOutOfRangeException(nameof(brightnessPercent));
        }

        return EncodeLightValue(brightnessPercent, tlvs);
    }

    public static byte[] EncodeTimeSet(uint utcSeconds)
    {
        if (utcSeconds is < MinimumUtcSeconds or > MaximumUtcSeconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(utcSeconds),
                "AGP time must be between 2024-01-01 and 2099-12-31 UTC.");
        }

        var payload = new byte[4];
        BinaryCodec.WriteUInt32LittleEndian(payload, utcSeconds);
        return payload;
    }

    public static byte[] EncodeDelayOffSet(uint delaySeconds) => EncodeDelaySet(delaySeconds);

    public static byte[] EncodeDelayOnSet(uint delaySeconds) => EncodeDelaySet(delaySeconds);

    private static byte[] EncodeDelaySet(uint delaySeconds)
    {
        if (delaySeconds > MaximumDelaySeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(delaySeconds));
        }

        var payload = new byte[4];
        BinaryCodec.WriteUInt32LittleEndian(payload, delaySeconds);
        return payload;
    }

    public static byte[] EncodeScheduleSlot(byte slot)
    {
        ValidateSlot(slot);
        return [slot];
    }

    public static byte[] EncodeSchedule(AgpSchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ValidateSchedule(schedule);

        var hasSelector = schedule.Type is AgpScheduleType.Weekly or AgpScheduleType.Monthly;
        var payload = new byte[hasSelector ? 10 : 9];
        payload[0] = schedule.Slot;
        payload[1] = (byte)schedule.Type;
        payload[2] = schedule.IsEnabled ? (byte)1 : (byte)0;
        payload[3] = (byte)schedule.Action;
        payload[4] = schedule.BrightnessPercent;
        if (hasSelector)
        {
            payload[5] = schedule.Selector;
            BinaryCodec.WriteUInt32LittleEndian(payload.AsSpan(6), schedule.TimeValue);
        }
        else
        {
            BinaryCodec.WriteUInt32LittleEndian(payload.AsSpan(5), schedule.TimeValue);
        }

        return payload;
    }

    public static byte[] EncodeEffect(AgpLightMode mode, ushort periodMilliseconds)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        if (mode == AgpLightMode.Steady)
        {
            if (periodMilliseconds != 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(periodMilliseconds),
                    "Steady mode requires a zero period.");
            }
        }
        else if (periodMilliseconds is < MinimumEffectPeriodMilliseconds or > MaximumEffectPeriodMilliseconds)
        {
            throw new ArgumentOutOfRangeException(nameof(periodMilliseconds));
        }

        var payload = new byte[3];
        payload[0] = (byte)mode;
        BinaryCodec.WriteUInt16LittleEndian(payload.AsSpan(1), periodMilliseconds);
        return payload;
    }

    public static AgpCapabilities DecodeCapabilities(AgpMessage message)
    {
        RequireMessage(message, AgpMessageType.Status, AgpOpcode.CapabilitiesStatus, 8);
        if (message.TransactionId != 0)
        {
            throw InvalidPayload("Capabilities Status must use transaction ID zero.");
        }

        return (AgpCapabilities)BinaryCodec.ReadUInt64LittleEndian(message.Payload.Span);
    }

    public static AgpLightState DecodeLightState(AgpMessage message)
    {
        RequireMessage(message, AgpMessageType.Status, AgpOpcode.LightStateStatus, 11);
        if (message.TransactionId != 0)
        {
            throw InvalidPayload("Light State Status must use transaction ID zero.");
        }

        var payload = message.Payload.Span;
        if (payload[4] > 1 || payload[5] > 100 || payload[6] > 100 ||
            payload[7] is < 1 or > 100 || payload[9] > (byte)AgpLightMode.Breathe)
        {
            throw InvalidPayload("Light State contains an out-of-range field.");
        }

        return new AgpLightState(
            BinaryCodec.ReadUInt32LittleEndian(payload),
            payload[4] != 0,
            payload[5],
            payload[6],
            payload[7],
            payload[8],
            (AgpLightMode)payload[9],
            payload[10]);
    }

    /// <summary>解码 BATTERY_GET：成功为 result + 8 字节状态，失败仅有 result。</summary>
    public static AgpBatteryResponse DecodeBatteryResponse(AgpMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.MessageType != AgpMessageType.Response ||
            message.Opcode != AgpOpcode.BatteryGet || message.TransactionId == 0)
        {
            throw InvalidPayload("The message is not a BATTERY_GET response with a nonzero transaction ID.");
        }

        var payload = message.Payload.Span;
        if (payload.IsEmpty)
        {
            throw InvalidLength("Battery response must contain a result code.");
        }

        var result = DecodeResult(payload[0]);
        if (result != AgpResultCode.Success)
        {
            if (payload.Length != 1)
            {
                throw InvalidLength("A failed battery response must contain only its result code.");
            }

            return new AgpBatteryResponse(result, null);
        }

        if (payload.Length != 9)
        {
            throw InvalidLength("A successful battery response must contain result + 8-byte battery state.");
        }

        return new AgpBatteryResponse(result, DecodeBatteryFields(payload[1..]));
    }

    /// <summary>解码 Battery State Read/Notify 的 BATTERY_STATUS；无 result 前缀，TID 必须为 0。</summary>
    public static AgpBatteryState DecodeBatteryStatus(AgpMessage message)
    {
        RequireMessage(message, AgpMessageType.Status, AgpOpcode.BatteryStatus, 8);
        if (message.TransactionId != 0)
        {
            throw InvalidPayload("Battery Status must use transaction ID zero.");
        }

        return DecodeBatteryFields(message.Payload.Span);
    }

    private static AgpBatteryState DecodeBatteryFields(ReadOnlySpan<byte> payload)
    {
        if ((payload[0] > 100 && payload[0] != AgpBatteryState.UnknownLevel) ||
            (payload[7] & 0xC0) == 0)
        {
            throw InvalidPayload("Battery state contains an invalid level or reserved serviceability value.");
        }

        var timeToDischarge = (uint)(payload[1] | payload[2] << 8 | payload[3] << 16);
        var timeToCharge = (uint)(payload[4] | payload[5] << 8 | payload[6] << 16);
        return new AgpBatteryState(payload[0], timeToDischarge, timeToCharge, payload[7]);
    }

    public static AgpControlResponse DecodeControlResponse(AgpMessage message)
    {
        if (message.Opcode is not (AgpOpcode.OnOffSet or AgpOpcode.LightnessSet))
        {
            throw InvalidPayload("The response is not a Light Model control response.");
        }

        return DecodeStandardControlResponse(message);
    }

    /// <summary>
    /// 解码通用 result + state_revision Control Response。颜色 Get 失败时也使用此格式，
    /// 但 opcode 保持原请求 opcode。
    /// </summary>
    public static AgpControlResponse DecodeStandardControlResponse(AgpMessage message)
    {
        RequireResponse(message, 5);
        var payload = message.Payload.Span;
        return new AgpControlResponse(
            DecodeResult(payload[0]),
            BinaryCodec.ReadUInt32LittleEndian(payload[1..]));
    }

    public static AgpTimeResponse DecodeTimeResponse(AgpMessage message)
    {
        RequireResponse(message, 8);
        if (message.Opcode is not (AgpOpcode.TimeSet or AgpOpcode.TimeGet))
        {
            throw InvalidPayload("The response is not a Time Model response.");
        }

        var payload = message.Payload.Span;
        if (payload[1] > 1)
        {
            throw InvalidPayload("Time response valid flag must be 0 or 1.");
        }

        return new AgpTimeResponse(
            DecodeResult(payload[0]),
            payload[1] != 0,
            BinaryCodec.ReadUInt32LittleEndian(payload[2..]),
            BinaryCodec.ReadUInt16LittleEndian(payload[6..]));
    }

    public static AgpDelayOffResponse DecodeDelayOffResponse(AgpMessage message)
    {
        RequireResponse(message, 6);
        if (message.Opcode is not (AgpOpcode.DelayOffSet or AgpOpcode.DelayOffGet))
        {
            throw InvalidPayload("The response is not a Relative Timer response.");
        }

        var payload = message.Payload.Span;
        if (payload[1] > 1)
        {
            throw InvalidPayload("Delay response active flag must be 0 or 1.");
        }

        return new AgpDelayOffResponse(
            DecodeResult(payload[0]),
            payload[1] != 0,
            BinaryCodec.ReadUInt32LittleEndian(payload[2..]));
    }

    public static AgpDelayOnResponse DecodeDelayOnResponse(AgpMessage message)
    {
        RequireResponse(message, 6);
        if (message.Opcode is not (AgpOpcode.DelayOnSet or AgpOpcode.DelayOnGet))
        {
            throw InvalidPayload("The response is not a Delay On response.");
        }

        var payload = message.Payload.Span;
        if (payload[1] > 1)
        {
            throw InvalidPayload("Delay response active flag must be 0 or 1.");
        }

        return new AgpDelayOnResponse(
            DecodeResult(payload[0]),
            payload[1] != 0,
            BinaryCodec.ReadUInt32LittleEndian(payload[2..]));
    }

    public static AgpScheduleResponse DecodeScheduleResponse(AgpMessage message)
    {
        if (message.MessageType != AgpMessageType.Response ||
            message.Opcode is not (AgpOpcode.ScheduleSet or AgpOpcode.ScheduleGet))
        {
            throw InvalidPayload("The message is not a Schedule Set/Get response.");
        }

        var payload = message.Payload.Span;
        if (payload.Length < 1)
        {
            throw InvalidLength("Schedule response has no result code.");
        }

        var result = DecodeResult(payload[0]);
        if (result != AgpResultCode.Success)
        {
            if (payload.Length != 1)
            {
                throw InvalidLength("Failed Schedule response must contain only its result code.");
            }

            return new AgpScheduleResponse(result, null);
        }

        return new AgpScheduleResponse(result, DecodeSchedulePayload(payload[1..]));
    }

    public static AgpScheduleListResponse DecodeScheduleListResponse(AgpMessage message)
    {
        RequireResponse(message, 3);
        if (message.Opcode is not (AgpOpcode.ScheduleListGet or AgpOpcode.ScheduleDelete))
        {
            throw InvalidPayload("The response is not a Schedule List/Delete response.");
        }

        var payload = message.Payload.Span;
        return new AgpScheduleListResponse(
            DecodeResult(payload[0]),
            payload[1],
            payload[2]);
    }

    public static AgpEffectResponse DecodeEffectResponse(AgpMessage message)
    {
        RequireResponse(message, 5);
        if (message.Opcode is not (AgpOpcode.EffectSet or AgpOpcode.EffectGet))
        {
            throw InvalidPayload("The response is not a PW01 Effect response.");
        }

        var payload = message.Payload.Span;
        if (payload[1] > (byte)AgpLightMode.Breathe || payload[4] != 0)
        {
            throw InvalidPayload("Effect response contains an invalid mode or reserved field.");
        }

        return new AgpEffectResponse(
            DecodeResult(payload[0]),
            (AgpLightMode)payload[1],
            BinaryCodec.ReadUInt16LittleEndian(payload[2..]));
    }

    private static byte[] EncodeLightValue(byte value, IEnumerable<AgpTlv>? tlvs)
    {
        var tail = tlvs is null ? [] : AgpTlvCodec.Encode(tlvs);
        if (tail.Length + 1 > AgpProfile.MaximumPayloadSize)
        {
            throw new ArgumentException("Light command and TLVs exceed the AGP payload limit.", nameof(tlvs));
        }

        var payload = new byte[tail.Length + 1];
        payload[0] = value;
        tail.CopyTo(payload, 1);
        return payload;
    }

    private static AgpSchedule DecodeSchedulePayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2 || payload[1] > (byte)AgpScheduleType.Monthly)
        {
            throw InvalidPayload("Schedule type is missing or invalid.");
        }

        var type = (AgpScheduleType)payload[1];
        var expectedLength = type is AgpScheduleType.Weekly or AgpScheduleType.Monthly ? 10 : 9;
        if (payload.Length != expectedLength)
        {
            throw InvalidLength("Schedule payload length does not match its type.");
        }

        if (payload[2] > 1 || payload[3] > (byte)AgpScheduleAction.On)
        {
            throw InvalidPayload("Schedule flags or action are invalid.");
        }

        var hasSelector = expectedLength == 10;
        var schedule = new AgpSchedule(
            payload[0],
            type,
            payload[2] != 0,
            (AgpScheduleAction)payload[3],
            payload[4],
            hasSelector ? payload[5] : (byte)0,
            BinaryCodec.ReadUInt32LittleEndian(payload[(hasSelector ? 6 : 5)..]));

        // 未写入槽位由设备返回 disabled Once 全零哨兵；它可被读取，但不得原样 Set。
        if (schedule is
            {
                Type: AgpScheduleType.Once,
                IsEnabled: false,
                Action: AgpScheduleAction.Off,
                BrightnessPercent: 0,
                Selector: 0,
                TimeValue: 0
            })
        {
            if (schedule.Slot >= MaximumScheduleSlots)
            {
                throw InvalidPayload("Schedule slot is out of range.");
            }

            return schedule;
        }

        try
        {
            ValidateSchedule(schedule);
        }
        catch (ArgumentException exception)
        {
            throw InvalidPayload($"Schedule response is invalid: {exception.Message}");
        }

        return schedule;
    }

    private static void ValidateSchedule(AgpSchedule schedule)
    {
        ValidateSlot(schedule.Slot);
        if (!Enum.IsDefined(schedule.Type))
        {
            throw new ArgumentOutOfRangeException(nameof(schedule), "Schedule type is invalid.");
        }

        if (!Enum.IsDefined(schedule.Action) ||
            (schedule.Action == AgpScheduleAction.Off && schedule.BrightnessPercent != 0) ||
            (schedule.Action == AgpScheduleAction.On && schedule.BrightnessPercent is < 1 or > 100))
        {
            throw new ArgumentException("Schedule action and brightness are inconsistent.", nameof(schedule));
        }

        switch (schedule.Type)
        {
            case AgpScheduleType.Once:
                if (schedule.Selector != 0 ||
                    schedule.TimeValue is < MinimumUtcSeconds or > MaximumUtcSeconds)
                {
                    throw new ArgumentException("Once schedule UTC or selector is invalid.", nameof(schedule));
                }
                break;
            case AgpScheduleType.Daily:
                if (schedule.Selector != 0 || schedule.TimeValue > MaximumSecondsOfDay)
                {
                    throw new ArgumentException("Daily schedule time or selector is invalid.", nameof(schedule));
                }
                break;
            case AgpScheduleType.Weekly:
                if (schedule.Selector == 0 || (schedule.Selector & 0x80) != 0 ||
                    schedule.TimeValue > MaximumSecondsOfDay)
                {
                    throw new ArgumentException("Weekly schedule mask or time is invalid.", nameof(schedule));
                }
                break;
            case AgpScheduleType.Monthly:
                if (schedule.Selector is < 1 or > 31 || schedule.TimeValue > MaximumSecondsOfDay)
                {
                    throw new ArgumentException("Monthly schedule day or time is invalid.", nameof(schedule));
                }
                break;
        }
    }

    private static void ValidateSlot(byte slot)
    {
        if (slot >= MaximumScheduleSlots)
        {
            throw new ArgumentOutOfRangeException(nameof(slot));
        }
    }

    private static void RequireResponse(AgpMessage message, int payloadLength)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.MessageType != AgpMessageType.Response)
        {
            throw InvalidPayload("AGP model response must use the Response message type.");
        }

        if (message.Payload.Length != payloadLength)
        {
            throw InvalidLength($"AGP response payload must be {payloadLength} bytes.");
        }
    }

    private static void RequireMessage(
        AgpMessage message,
        AgpMessageType messageType,
        AgpOpcode opcode,
        int payloadLength)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.MessageType != messageType || message.Opcode != opcode)
        {
            throw InvalidPayload($"Expected {messageType} {opcode}.");
        }

        if (message.Payload.Length != payloadLength)
        {
            throw InvalidLength($"{opcode} payload must be {payloadLength} bytes.");
        }
    }

    private static AgpResultCode DecodeResult(byte value)
    {
        var result = (AgpResultCode)value;
        if (!Enum.IsDefined(result))
        {
            throw InvalidPayload("AGP response contains an unknown result code.");
        }

        return result;
    }

    private static AgpProtocolException InvalidLength(string message) =>
        new(AgpResultCode.InvalidLength, message);

    private static AgpProtocolException InvalidPayload(string message) =>
        new(AgpResultCode.InvalidValue, message);
}
