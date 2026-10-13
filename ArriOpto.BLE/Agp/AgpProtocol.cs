using ArriOpto.BLE.Protocol;

namespace ArriOpto.BLE.Agp;

/// <summary>ArriOpto GATT Protocol v1 的 BLE Profile 与线格式常量。</summary>
public static class AgpProfile
{
    public const byte ProtocolMajor = 1;
    public const byte ProtocolMinor = 0;
    public const int HeaderSize = 9;
    public const int MaximumPacketSize = 20;
    public const int MaximumPayloadSize = MaximumPacketSize - HeaderSize;

    public static readonly Guid ServiceId =
        Guid.Parse("8f092401-c433-4408-870f-fc60f806f9d7");
    public static readonly Guid CapabilitiesCharacteristicId =
        Guid.Parse("8f092402-c433-4408-870f-fc60f806f9d7");
    public static readonly Guid ControlPointCharacteristicId =
        Guid.Parse("8f092403-c433-4408-870f-fc60f806f9d7");
    public static readonly Guid LightStateCharacteristicId =
        Guid.Parse("8f092404-c433-4408-870f-fc60f806f9d7");
    public static readonly Guid ColorStateCharacteristicId =
        Guid.Parse("8f092405-c433-4408-870f-fc60f806f9d7");
}

public enum AgpMessageType : byte
{
    Request = 0,
    Response = 1,
    Status = 2
}

public enum AgpResultCode : byte
{
    Success = 0,
    UnsupportedVersion = 1,
    UnknownOpcode = 2,
    InvalidLength = 3,
    InvalidValue = 4,
    UnsupportedFeature = 5,
    MalformedTlv = 6,
    Busy = 7,
    NotAuthorized = 8,
    TransactionConflict = 9,
    InternalError = 10,
    AuthTimeout = 11,
    AuthInvalidLength = 12,
    AuthBusy = 13
}

public enum AgpOpcode : ushort
{
    CapabilitiesStatus = 0x0001,
    LightStateStatus = 0x0002,
    AuthGetToken = 0x0003,
    AuthToken = 0x0004,
    Lightness16StateStatus = 0x0090,
    HslStateStatus = 0x0091,
    HslTargetStatus = 0x0092,
    HslDefaultStatus = 0x0093,
    HslRangeStatus = 0x0094,
    CtlStateStatus = 0x0095,
    CtlTargetStatus = 0x0096,
    CtlDefaultStatus = 0x0097,
    CtlRangeStatus = 0x0098,
    OnOffSet = 0x0101,
    OnOffSetUnacknowledged = 0x0102,
    LightnessSet = 0x0111,
    LightnessSetUnacknowledged = 0x0112,
    ModeSet = 0x0121,
    ModeGet = 0x0122,
    DelayOffSet = 0x0201,
    DelayOffGet = 0x0202,
    DelayOnSet = 0x0203,
    DelayOnGet = 0x0204,
    TimeSet = 0x0301,
    TimeGet = 0x0302,
    ScheduleSet = 0x0401,
    ScheduleDelete = 0x0402,
    ScheduleGet = 0x0403,
    ScheduleListGet = 0x0404,

    EffectSet = 0x0501,
    EffectGet = 0x0502,
    Lightness16Set = 0x0901,
    Lightness16Get = 0x0902,
    HslSet = 0x0911,
    HslGet = 0x0912,
    HslTargetGet = 0x0913,
    HslDefaultSet = 0x0914,
    HslDefaultGet = 0x0915,
    HslRangeSet = 0x0916,
    HslRangeGet = 0x0917,
    CtlSet = 0x0921,
    CtlGet = 0x0922,
    CtlTargetGet = 0x0923,
    CtlDefaultSet = 0x0924,
    CtlDefaultGet = 0x0925,
    CtlRangeSet = 0x0926,
    CtlRangeGet = 0x0927
}

[Flags]
public enum AgpCapabilities : ulong
{
    None = 0,
    OnOff = 1UL << 0,
    Lightness = 1UL << 1,
    LastNonzero = 1UL << 2,
    Transition = 1UL << 3,
    DefaultRange = 1UL << 4,
    OnPowerUp = 1UL << 5,
    RtcTime = 1UL << 6,
    /// <summary>支持延时开灯和延时关灯；不依赖 RTC 或 Scheduler。</summary>
    RelativeTimer = 1UL << 7,
    Scheduler = 1UL << 8,
    Effects = 1UL << 9,
    Scenes = 1UL << 10,
    Battery = 1UL << 11,
    GroupLinkage = 1UL << 12,
    SecureControl = 1UL << 13,
    Ota = 1UL << 14,
    LegacyWristV1 = 1UL << 15,
    Fragmentation = 1UL << 16,
    Lightness16 = 1UL << 17,
    Hsl = 1UL << 18,
    Ctl = 1UL << 19,
    CtlDeltaUv = 1UL << 20,
    ColorTransition = 1UL << 21,
    /// <summary>支持独立的本地硬件延时模式配置；与灯效和一次性延时无关。</summary>
    LocalMode = 1UL << 22
}

/// <summary>一条已经通过 AGP 公共帧校验的消息。</summary>
public sealed record AgpMessage(
    byte Major,
    byte Minor,
    AgpMessageType MessageType,
    byte Flags,
    AgpOpcode Opcode,
    ushort TransactionId,
    ReadOnlyMemory<byte> Payload);

/// <summary>AGP 帧或模型负载不符合线协议时抛出的异常。</summary>
public sealed class AgpProtocolException : Exception
{
    public AgpProtocolException(AgpResultCode resultCode, string message)
        : base(message)
    {
        ResultCode = resultCode;
    }

    public AgpResultCode ResultCode { get; }
}

/// <summary>AGP v1 公共九字节帧的严格编解码器。</summary>
public static class AgpCodec
{
    public static byte[] Encode(
        AgpMessageType messageType,
        AgpOpcode opcode,
        ushort transactionId,
        ReadOnlySpan<byte> payload = default)
    {
        if (!Enum.IsDefined(messageType))
        {
            throw new ArgumentOutOfRangeException(nameof(messageType));
        }

        if (payload.Length > AgpProfile.MaximumPayloadSize)
        {
            throw new ArgumentException(
                $"AGP payload cannot exceed {AgpProfile.MaximumPayloadSize} bytes.",
                nameof(payload));
        }

        var packet = new byte[AgpProfile.HeaderSize + payload.Length];
        packet[0] = AgpProfile.ProtocolMajor;
        packet[1] = AgpProfile.ProtocolMinor;
        packet[2] = (byte)messageType;
        packet[3] = 0;
        BinaryCodec.WriteUInt16LittleEndian(packet.AsSpan(4), (ushort)opcode);
        BinaryCodec.WriteUInt16LittleEndian(packet.AsSpan(6), transactionId);
        packet[8] = (byte)payload.Length;
        payload.CopyTo(packet.AsSpan(AgpProfile.HeaderSize));
        return packet;
    }

    public static byte[] EncodeRequest(
        AgpOpcode opcode,
        ushort transactionId,
        ReadOnlySpan<byte> payload = default)
    {
        if (transactionId == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(transactionId),
                "AGP v1 requests require a non-zero transaction ID.");
        }

        return Encode(AgpMessageType.Request, opcode, transactionId, payload);
    }

    public static AgpMessage Decode(ReadOnlySpan<byte> packet)
    {
        if (!TryDecode(packet, out var message, out var result))
        {
            throw new AgpProtocolException(result, DescribeDecodeFailure(result));
        }

        return message!;
    }

    public static bool TryDecode(
        ReadOnlySpan<byte> packet,
        out AgpMessage? message,
        out AgpResultCode result)
    {
        message = null;
        if (packet.Length is < AgpProfile.HeaderSize or > AgpProfile.MaximumPacketSize)
        {
            result = AgpResultCode.InvalidLength;
            return false;
        }

        var payloadLength = packet[8];
        if (packet.Length != AgpProfile.HeaderSize + payloadLength)
        {
            result = AgpResultCode.InvalidLength;
            return false;
        }

        if (packet[0] != AgpProfile.ProtocolMajor ||
            packet[1] != AgpProfile.ProtocolMinor)
        {
            result = AgpResultCode.UnsupportedVersion;
            return false;
        }

        if (packet[2] > (byte)AgpMessageType.Status || packet[3] != 0)
        {
            result = AgpResultCode.InvalidValue;
            return false;
        }

        message = new AgpMessage(
            packet[0],
            packet[1],
            (AgpMessageType)packet[2],
            packet[3],
            (AgpOpcode)BinaryCodec.ReadUInt16LittleEndian(packet[4..]),
            BinaryCodec.ReadUInt16LittleEndian(packet[6..]),
            packet[AgpProfile.HeaderSize..].ToArray());
        result = AgpResultCode.Success;
        return true;
    }

    private static string DescribeDecodeFailure(AgpResultCode result) => result switch
    {
        AgpResultCode.InvalidLength => "AGP packet length or payload length is invalid.",
        AgpResultCode.UnsupportedVersion => "AGP packet is not protocol version 1.0.",
        AgpResultCode.InvalidValue => "AGP message type or flags are invalid.",
        _ => $"AGP packet could not be decoded: {result}."
    };
}
