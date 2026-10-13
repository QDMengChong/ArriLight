using ArriOpto.BLE.Mesh;

namespace ArriOpto.BLE.Mesh.Models;

/// <summary>
/// Time model messages (Mesh Model Specification 1.1, Section 5.2.1).
/// </summary>
public static class TimeModel
{
    /// <summary>构建 Time Get 消息。</summary>
    public static byte[] Get() =>
        MeshMessageWriter.Create(MeshOpcode.TimeGet).ToArray();

    /// <summary>
    /// Builds a Time Set message.
    /// Bit layout (80 bits): TAI Seconds(40) + Subsecond(8) + Uncertainty(8) +
    /// Time Authority(1) + TAI-UTC Delta(15) + Time Zone Offset(8).
    /// The TAI-UTC Delta/Time Authority 16-bit word is encoded as
    /// (delta &lt;&lt; 1) | authority, matching SIG reference implementations.
    /// </summary>
    /// <param name="taiSeconds">当前 TAI 秒数（40 位）。</param>
    /// <param name="subsecond">亚秒（1/256 秒）。</param>
    /// <param name="uncertainty">不确定度（10 ms 步进）。</param>
    /// <param name="timeAuthority">是否作为时间权威节点。</param>
    /// <param name="taiUtcDelta">TAI 与 UTC 的差值（15 位，秒）。</param>
    /// <param name="timeZoneOffset">本地时区偏移（15 分钟增量，带符号）。</param>
    public static byte[] Set(
        ulong taiSeconds,
        byte subsecond,
        byte uncertainty,
        bool timeAuthority,
        ushort taiUtcDelta,
        sbyte timeZoneOffset) =>
        MeshMessageWriter.Create(MeshOpcode.TimeSet)
            .AddU40(taiSeconds)
            .AddU8(subsecond)
            .AddU8(uncertainty)
            .AddU16(unchecked((ushort)((taiUtcDelta << 1) | (timeAuthority ? 1 : 0))))
            .AddU8(unchecked((byte)timeZoneOffset))
            .ToArray();

    /// <summary>
    /// Parses a Time Status message. If TAI Seconds is 0 the trailing fields
    /// are omitted.
    /// </summary>
    public static bool TryParseStatus(
        ReadOnlySpan<byte> message,
        out TimeStatus status)
    {
        status = default;
        var reader = new MeshMessageReader(message);
        if (!reader.TryConsumeOpcode(MeshOpcode.TimeStatus))
            return false;

        if (reader.Remaining != 5 && reader.Remaining != 10)
            return false;

        var taiSeconds = reader.ReadU40();
        if (reader.Remaining == 0)
        {
            status = new TimeStatus(taiSeconds, null, null, null, null, null);
            return true;
        }

        var subsecond = reader.ReadU8();
        var uncertainty = reader.ReadU8();
        var authorityAndDelta = reader.ReadU16();
        var timeZoneOffset = unchecked((sbyte)reader.ReadU8());
        status = new TimeStatus(
            taiSeconds,
            subsecond,
            uncertainty,
            (authorityAndDelta & 0x01) != 0,
            (ushort)(authorityAndDelta >> 1),
            timeZoneOffset);
        return true;
    }

    // ---- Time Zone ----
    /// <summary>构建 Time Zone Get 消息。</summary>
    public static byte[] ZoneGet() =>
        MeshMessageWriter.Create(MeshOpcode.TimeZoneGet).ToArray();

    /// <summary>
    /// 构建 Time Zone Set 消息。
    /// 载荷：Time Zone Offset New(1) + TAI of Zone Change(5)。
    /// </summary>
    public static byte[] ZoneSet(
        sbyte timeZoneOffsetNew,
        ulong taiOfZoneChange) =>
        MeshMessageWriter.Create(MeshOpcode.TimeZoneSet)
            .AddU8(unchecked((byte)timeZoneOffsetNew))
            .AddU40(taiOfZoneChange)
            .ToArray();

    public static bool TryParseZoneStatus(
        ReadOnlySpan<byte> message,
        out TimeZoneStatus status)
    {
        status = default;
        var reader = new MeshMessageReader(message);
        if (!reader.TryConsumeOpcode(MeshOpcode.TimeZoneStatus) ||
            reader.Remaining != 7)
        {
            return false;
        }

        status = new TimeZoneStatus(
            unchecked((sbyte)reader.ReadU8()),
            unchecked((sbyte)reader.ReadU8()),
            reader.ReadU40());
        return true;
    }

    // ---- TAI-UTC Delta ----
    /// <summary>构建 TAI-UTC Delta Get 消息。</summary>
    public static byte[] TaiUtcDeltaGet() =>
        MeshMessageWriter.Create(MeshOpcode.TaiUtcDeltaGet).ToArray();

    /// <summary>
    /// Builds a TAI-UTC Delta Set message: Delta New(15) + Padding(1) +
    /// TAI of Delta Change(40).
    /// </summary>
    /// <param name="taiUtcDeltaNew">新的 TAI-UTC 差值（15 位）。</param>
    /// <param name="taiOfDeltaChange">差值生效时刻的 TAI 秒数（40 位）。</param>
    public static byte[] TaiUtcDeltaSet(
        ushort taiUtcDeltaNew,
        ulong taiOfDeltaChange) =>
        MeshMessageWriter.Create(MeshOpcode.TaiUtcDeltaSet)
            .AddU16((ushort)(taiUtcDeltaNew << 1))
            .AddU40(taiOfDeltaChange)
            .ToArray();

    /// <summary>
    /// Parses a TAI-UTC Delta Status message: Delta Current(15) + Padding(1) +
    /// Delta New(15) + Padding(1) + TAI of Delta Change(40).
    /// </summary>
    public static bool TryParseTaiUtcDeltaStatus(
        ReadOnlySpan<byte> message,
        out TaiUtcDeltaStatus status)
    {
        status = default;
        var reader = new MeshMessageReader(message);
        if (!reader.TryConsumeOpcode(MeshOpcode.TaiUtcDeltaStatus) ||
            reader.Remaining != 9)
        {
            return false;
        }

        var current = (ushort)(reader.ReadU16() >> 1);
        var deltaNew = (ushort)(reader.ReadU16() >> 1);
        var taiOfDeltaChange = reader.ReadU40();
        status = new TaiUtcDeltaStatus(current, deltaNew, taiOfDeltaChange);
        return true;
    }

    // ---- Time Role ----
    /// <summary>构建 Time Role Get 消息。</summary>
    public static byte[] RoleGet() =>
        MeshMessageWriter.Create(MeshOpcode.TimeRoleGet).ToArray();

    /// <summary>构建 Time Role Set 消息（载荷：Time Role(1)）。</summary>
    public static byte[] RoleSet(TimeRole role) =>
        MeshMessageWriter.Create(MeshOpcode.TimeRoleSet)
            .AddU8((byte)role)
            .ToArray();

    public static bool TryParseRoleStatus(
        ReadOnlySpan<byte> message,
        out TimeRole role)
    {
        role = default;
        var reader = new MeshMessageReader(message);
        if (!reader.TryConsumeOpcode(MeshOpcode.TimeRoleStatus) ||
            reader.Remaining != 1)
        {
            return false;
        }

        var value = reader.ReadU8();
        if (value > (byte)TimeRole.TimeClient)
            return false;

        role = (TimeRole)value;
        return true;
    }
}

/// <summary>Parsed Time Status values (Section 5.1.1).</summary>
public readonly record struct TimeStatus(
    ulong TaiSeconds,
    byte? Subsecond,
    byte? Uncertainty,
    bool? TimeAuthority,
    ushort? TaiUtcDelta,
    sbyte? TimeZoneOffset);

public readonly record struct TimeZoneStatus(
    sbyte TimeZoneOffsetCurrent,
    sbyte TimeZoneOffsetNew,
    ulong TaiOfZoneChange);

public readonly record struct TaiUtcDeltaStatus(
    ushort TaiUtcDeltaCurrent,
    ushort TaiUtcDeltaNew,
    ulong TaiOfDeltaChange);

/// <summary>Time Role state (Section 5.1.2).</summary>
public enum TimeRole : byte
{
    None = 0x00,
    Authoritative = 0x01,
    RelayNode = 0x02,
    TimeClient = 0x03
}
