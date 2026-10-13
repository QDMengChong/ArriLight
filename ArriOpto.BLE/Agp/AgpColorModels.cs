using ArriOpto.BLE.Protocol;

namespace ArriOpto.BLE.Agp;

public enum AgpColorModel : byte
{
    None = 0,
    Hsl = 1,
    Ctl = 2
}

/// <summary>AGP HSL 元组；三个通道均使用完整的 16-bit 协议范围。</summary>
public sealed record AgpHsl(
    ushort Lightness,
    ushort Hue,
    ushort Saturation)
{
    public double HueDegrees => Hue * 360.0 / ushort.MaxValue;
    public double SaturationRatio => Saturation / (double)ushort.MaxValue;
}

/// <summary>AGP CTL 元组；Delta UV 是约 -1..+1 语义范围的有符号 16-bit 值。</summary>
public sealed record AgpCtl(
    ushort Lightness,
    ushort TemperatureKelvin,
    short DeltaUv)
{
    public double DeltaUvRatio => DeltaUv / 32768.0;
}

public sealed record AgpHslRange(
    ushort HueMinimum,
    ushort HueMaximum,
    ushort SaturationMinimum,
    ushort SaturationMaximum);

public sealed record AgpCtlRange(
    ushort TemperatureMinimumKelvin,
    ushort TemperatureMaximumKelvin);

/// <summary>
/// 颜色 Set 使用的组合过渡字段。Transition Time 沿用 Mesh 风格编码，
/// Execution Delay 每步 5 ms。
/// </summary>
public sealed record AgpColorTransition(
    byte TransitionTime,
    byte ExecutionDelay)
{
    public TimeSpan ExecutionDelayDuration =>
        TimeSpan.FromMilliseconds(ExecutionDelay * 5.0);
}

public abstract record AgpColorStatus(AgpOpcode Opcode);

public sealed record AgpLightness16State(
    uint Revision,
    ushort Present,
    ushort Target,
    ushort LastNonzero,
    byte RemainingTime)
    : AgpColorStatus(AgpOpcode.Lightness16StateStatus);

public sealed record AgpHslState(
    uint Revision,
    AgpColorModel ActiveModel,
    AgpHsl Present)
    : AgpColorStatus(AgpOpcode.HslStateStatus);

public sealed record AgpHslTarget(
    uint Revision,
    AgpHsl Target,
    byte RemainingTime)
    : AgpColorStatus(AgpOpcode.HslTargetStatus);

public sealed record AgpHslDefault(AgpHsl Value)
    : AgpColorStatus(AgpOpcode.HslDefaultStatus);

public sealed record AgpHslRangeStatus(AgpHslRange Value)
    : AgpColorStatus(AgpOpcode.HslRangeStatus);

public sealed record AgpCtlState(
    uint Revision,
    AgpColorModel ActiveModel,
    AgpCtl Present)
    : AgpColorStatus(AgpOpcode.CtlStateStatus);

public sealed record AgpCtlTarget(
    uint Revision,
    AgpCtl Target,
    byte RemainingTime)
    : AgpColorStatus(AgpOpcode.CtlTargetStatus);

public sealed record AgpCtlDefault(AgpCtl Value)
    : AgpColorStatus(AgpOpcode.CtlDefaultStatus);

public sealed record AgpCtlRangeStatus(AgpCtlRange Value)
    : AgpColorStatus(AgpOpcode.CtlRangeStatus);

/// <summary>
/// Color Get 的双形态响应：成功时 <see cref="Value"/> 是专用状态，失败时
/// Result/StateRevision 来自原请求 opcode 的标准 Control Response。
/// </summary>
public sealed record AgpColorQueryResponse<T>(
    AgpResultCode Result,
    T? Value,
    uint? StateRevision)
    where T : AgpColorStatus
{
    public bool IsSuccess => Result == AgpResultCode.Success;
}

/// <summary>AGP Lightness16、HSL 与 CTL 模型编解码。</summary>
public static class AgpColorModelCodec
{
    public const ushort MinimumCtlTemperatureKelvin = 800;
    public const ushort MaximumCtlTemperatureKelvin = 20_000;
    public const ushort UnknownCtlDefaultTemperature = ushort.MaxValue;
    public const byte UnknownTransitionSteps = 0x3F;

    public static ushort BrightnessPercentToLightness16(byte brightnessPercent)
    {
        if (brightnessPercent > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(brightnessPercent));
        }

        return (ushort)(((uint)brightnessPercent * ushort.MaxValue + 50U) / 100U);
    }

    public static byte Lightness16ToBrightnessPercent(ushort lightness) =>
        (byte)(((uint)lightness * 100U + 32_767U) / ushort.MaxValue);

    public static byte[] EncodeLightness16Set(
        ushort lightness,
        AgpColorTransition? transition = null)
    {
        var payload = new byte[2];
        BinaryCodec.WriteUInt16LittleEndian(payload, lightness);
        return AppendTransition(payload, transition);
    }

    public static byte[] EncodeHslSet(
        AgpHsl value,
        AgpColorTransition? transition = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        return AppendTransition(EncodeHslTuple(value), transition);
    }

    public static byte[] EncodeHslDefault(AgpHsl value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return EncodeHslTuple(value);
    }

    public static byte[] EncodeHslRange(AgpHslRange range)
    {
        ArgumentNullException.ThrowIfNull(range);
        ValidateHslRange(range);
        var payload = new byte[8];
        BinaryCodec.WriteUInt16LittleEndian(payload, range.HueMinimum);
        BinaryCodec.WriteUInt16LittleEndian(payload.AsSpan(2), range.HueMaximum);
        BinaryCodec.WriteUInt16LittleEndian(payload.AsSpan(4), range.SaturationMinimum);
        BinaryCodec.WriteUInt16LittleEndian(payload.AsSpan(6), range.SaturationMaximum);
        return payload;
    }

    public static byte[] EncodeCtlSet(
        AgpCtl value,
        AgpColorTransition? transition = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        ValidateCtl(value, allowUnknownTemperature: false);
        return AppendTransition(EncodeCtlTuple(value), transition);
    }

    public static byte[] EncodeCtlDefault(AgpCtl value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ValidateCtl(value, allowUnknownTemperature: true);
        return EncodeCtlTuple(value);
    }

    public static byte[] EncodeCtlRange(AgpCtlRange range)
    {
        ArgumentNullException.ThrowIfNull(range);
        ValidateCtlRange(range);
        var payload = new byte[4];
        BinaryCodec.WriteUInt16LittleEndian(payload, range.TemperatureMinimumKelvin);
        BinaryCodec.WriteUInt16LittleEndian(payload.AsSpan(2), range.TemperatureMaximumKelvin);
        return payload;
    }

    public static AgpColorStatus DecodeStatus(AgpMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ValidateStatusEnvelope(message);
        return message.Opcode switch
        {
            AgpOpcode.Lightness16StateStatus => DecodeLightness16State(message),
            AgpOpcode.HslStateStatus => DecodeHslState(message),
            AgpOpcode.HslTargetStatus => DecodeHslTarget(message),
            AgpOpcode.HslDefaultStatus => DecodeHslDefault(message),
            AgpOpcode.HslRangeStatus => DecodeHslRange(message),
            AgpOpcode.CtlStateStatus => DecodeCtlState(message),
            AgpOpcode.CtlTargetStatus => DecodeCtlTarget(message),
            AgpOpcode.CtlDefaultStatus => DecodeCtlDefault(message),
            AgpOpcode.CtlRangeStatus => DecodeCtlRange(message),
            _ => throw InvalidValue("Message opcode is not an AGP Color Status opcode.")
        };
    }

    public static AgpLightness16State DecodeLightness16State(AgpMessage message)
    {
        RequireStatus(message, AgpOpcode.Lightness16StateStatus, 11);
        var payload = message.Payload.Span;
        return new AgpLightness16State(
            BinaryCodec.ReadUInt32LittleEndian(payload),
            BinaryCodec.ReadUInt16LittleEndian(payload[4..]),
            BinaryCodec.ReadUInt16LittleEndian(payload[6..]),
            BinaryCodec.ReadUInt16LittleEndian(payload[8..]),
            payload[10]);
    }

    public static AgpHslState DecodeHslState(AgpMessage message)
    {
        RequireStatus(message, AgpOpcode.HslStateStatus, 11);
        var payload = message.Payload.Span;
        return new AgpHslState(
            BinaryCodec.ReadUInt32LittleEndian(payload),
            DecodeActiveModel(payload[4]),
            DecodeHslTuple(payload[5..]));
    }

    public static AgpHslTarget DecodeHslTarget(AgpMessage message)
    {
        RequireStatus(message, AgpOpcode.HslTargetStatus, 11);
        var payload = message.Payload.Span;
        return new AgpHslTarget(
            BinaryCodec.ReadUInt32LittleEndian(payload),
            DecodeHslTuple(payload[4..]),
            payload[10]);
    }

    public static AgpHslDefault DecodeHslDefault(AgpMessage message)
    {
        RequireStatus(message, AgpOpcode.HslDefaultStatus, 6);
        return new AgpHslDefault(DecodeHslTuple(message.Payload.Span));
    }

    public static AgpHslRangeStatus DecodeHslRange(AgpMessage message)
    {
        RequireStatus(message, AgpOpcode.HslRangeStatus, 8);
        var payload = message.Payload.Span;
        var range = new AgpHslRange(
            BinaryCodec.ReadUInt16LittleEndian(payload),
            BinaryCodec.ReadUInt16LittleEndian(payload[2..]),
            BinaryCodec.ReadUInt16LittleEndian(payload[4..]),
            BinaryCodec.ReadUInt16LittleEndian(payload[6..]));
        try
        {
            ValidateHslRange(range);
        }
        catch (ArgumentException exception)
        {
            throw InvalidValue($"HSL Range Status is invalid: {exception.Message}");
        }

        return new AgpHslRangeStatus(range);
    }

    public static AgpCtlState DecodeCtlState(AgpMessage message)
    {
        RequireStatus(message, AgpOpcode.CtlStateStatus, 11);
        var payload = message.Payload.Span;
        return new AgpCtlState(
            BinaryCodec.ReadUInt32LittleEndian(payload),
            DecodeActiveModel(payload[4]),
            DecodeCtlTuple(payload[5..], allowUnknownTemperature: false));
    }

    public static AgpCtlTarget DecodeCtlTarget(AgpMessage message)
    {
        RequireStatus(message, AgpOpcode.CtlTargetStatus, 11);
        var payload = message.Payload.Span;
        return new AgpCtlTarget(
            BinaryCodec.ReadUInt32LittleEndian(payload),
            DecodeCtlTuple(payload[4..], allowUnknownTemperature: false),
            payload[10]);
    }

    public static AgpCtlDefault DecodeCtlDefault(AgpMessage message)
    {
        RequireStatus(message, AgpOpcode.CtlDefaultStatus, 6);
        return new AgpCtlDefault(
            DecodeCtlTuple(message.Payload.Span, allowUnknownTemperature: true));
    }

    public static AgpCtlRangeStatus DecodeCtlRange(AgpMessage message)
    {
        RequireStatus(message, AgpOpcode.CtlRangeStatus, 4);
        var payload = message.Payload.Span;
        var range = new AgpCtlRange(
            BinaryCodec.ReadUInt16LittleEndian(payload),
            BinaryCodec.ReadUInt16LittleEndian(payload[2..]));
        try
        {
            ValidateCtlRange(range);
        }
        catch (ArgumentException exception)
        {
            throw InvalidValue($"CTL Range Status is invalid: {exception.Message}");
        }

        return new AgpCtlRangeStatus(range);
    }

    private static byte[] EncodeHslTuple(AgpHsl value)
    {
        var payload = new byte[6];
        BinaryCodec.WriteUInt16LittleEndian(payload, value.Lightness);
        BinaryCodec.WriteUInt16LittleEndian(payload.AsSpan(2), value.Hue);
        BinaryCodec.WriteUInt16LittleEndian(payload.AsSpan(4), value.Saturation);
        return payload;
    }

    private static AgpHsl DecodeHslTuple(ReadOnlySpan<byte> payload) =>
        new(
            BinaryCodec.ReadUInt16LittleEndian(payload),
            BinaryCodec.ReadUInt16LittleEndian(payload[2..]),
            BinaryCodec.ReadUInt16LittleEndian(payload[4..]));

    private static byte[] EncodeCtlTuple(AgpCtl value)
    {
        var payload = new byte[6];
        BinaryCodec.WriteUInt16LittleEndian(payload, value.Lightness);
        BinaryCodec.WriteUInt16LittleEndian(payload.AsSpan(2), value.TemperatureKelvin);
        BinaryCodec.WriteUInt16LittleEndian(payload.AsSpan(4), unchecked((ushort)value.DeltaUv));
        return payload;
    }

    private static AgpCtl DecodeCtlTuple(
        ReadOnlySpan<byte> payload,
        bool allowUnknownTemperature)
    {
        var value = new AgpCtl(
            BinaryCodec.ReadUInt16LittleEndian(payload),
            BinaryCodec.ReadUInt16LittleEndian(payload[2..]),
            unchecked((short)BinaryCodec.ReadUInt16LittleEndian(payload[4..])));
        try
        {
            ValidateCtl(value, allowUnknownTemperature);
        }
        catch (ArgumentException exception)
        {
            throw InvalidValue($"CTL Status is invalid: {exception.Message}");
        }

        return value;
    }

    private static byte[] AppendTransition(
        byte[] fixedPayload,
        AgpColorTransition? transition)
    {
        if (transition is null)
        {
            return fixedPayload;
        }

        if ((transition.TransitionTime & 0x3F) == UnknownTransitionSteps)
        {
            throw new ArgumentOutOfRangeException(
                nameof(transition),
                "Color Set cannot use unknown Transition Time steps (0x3F).");
        }

        var tlv = AgpTlvCodec.Encode(
            [
                new AgpTlv(
                    AgpTlvCodec.ColorTransitionType,
                    new byte[] { transition.TransitionTime, transition.ExecutionDelay })
            ]);
        var payload = new byte[fixedPayload.Length + tlv.Length];
        fixedPayload.CopyTo(payload, 0);
        tlv.CopyTo(payload, fixedPayload.Length);
        if (payload.Length > AgpProfile.MaximumPayloadSize)
        {
            throw new ArgumentException("Color payload exceeds the AGP v1 payload limit.");
        }

        return payload;
    }

    private static void ValidateHslRange(AgpHslRange range)
    {
        if (range.SaturationMinimum > range.SaturationMaximum)
        {
            throw new ArgumentException(
                "HSL saturation minimum cannot exceed maximum.",
                nameof(range));
        }
    }

    private static void ValidateCtl(AgpCtl value, bool allowUnknownTemperature)
    {
        var isKnownRange = value.TemperatureKelvin is
            >= MinimumCtlTemperatureKelvin and <= MaximumCtlTemperatureKelvin;
        if (!isKnownRange &&
            !(allowUnknownTemperature &&
              value.TemperatureKelvin == UnknownCtlDefaultTemperature))
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "CTL temperature must be 800..20000 K; only CTL Default allows 0xFFFF.");
        }
    }

    private static void ValidateCtlRange(AgpCtlRange range)
    {
        if (range.TemperatureMinimumKelvin < MinimumCtlTemperatureKelvin ||
            range.TemperatureMaximumKelvin > MaximumCtlTemperatureKelvin ||
            range.TemperatureMinimumKelvin > range.TemperatureMaximumKelvin)
        {
            throw new ArgumentException(
                "CTL temperature range must be ordered within 800..20000 K.",
                nameof(range));
        }
    }

    private static AgpColorModel DecodeActiveModel(byte value)
    {
        if (value > (byte)AgpColorModel.Ctl)
        {
            throw InvalidValue("Color State contains an unknown active model.");
        }

        return (AgpColorModel)value;
    }

    private static void ValidateStatusEnvelope(AgpMessage message)
    {
        if ((message.MessageType == AgpMessageType.Status && message.TransactionId != 0) ||
            (message.MessageType == AgpMessageType.Response && message.TransactionId == 0) ||
            message.MessageType == AgpMessageType.Request)
        {
            throw InvalidValue(
                "Color Status must be Status/TID 0 or Response/nonzero TID.");
        }
    }

    private static void RequireStatus(
        AgpMessage message,
        AgpOpcode opcode,
        int payloadLength)
    {
        ArgumentNullException.ThrowIfNull(message);
        ValidateStatusEnvelope(message);
        if (message.Opcode != opcode)
        {
            throw InvalidValue($"Expected Color Status opcode {opcode}.");
        }

        if (message.Payload.Length != payloadLength)
        {
            throw new AgpProtocolException(
                AgpResultCode.InvalidLength,
                $"{opcode} payload must be {payloadLength} bytes.");
        }
    }

    private static AgpProtocolException InvalidValue(string message) =>
        new(AgpResultCode.InvalidValue, message);
}
