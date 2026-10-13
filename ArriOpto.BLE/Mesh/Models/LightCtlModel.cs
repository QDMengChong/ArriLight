using ArriOpto.BLE.Mesh;

namespace ArriOpto.BLE.Mesh.Models;

/// <summary>
/// Light CTL model messages (Mesh Model Specification 1.1, Section 6.3.2).
/// </summary>
public static class LightCtlModel
{
    /// <summary>构建 Light CTL Get 消息。</summary>
    public static byte[] Get() =>
        MeshMessageWriter.Create(MeshOpcode.LightCtlGet).ToArray();

    /// <summary>
    /// Builds a Light CTL Set message.
    /// Payload: Lightness(2) + Temperature(2) + Delta UV(2) + TID(1) +
    /// [Transition Time(1) + Delay(1)].
    /// </summary>
    /// <param name="lightness">目标亮度（0-65535）。</param>
    /// <param name="temperature">目标色温（开尔文，800-20000）。</param>
    /// <param name="deltaUv">目标 Delta UV（有符号 16 位）。</param>
    /// <param name="tid">事务标识，同一状态的连续 Set 必须递增。</param>
    /// <param name="transitionTime">可选的过渡时间。</param>
    /// <param name="delay">执行延时（5 ms 步进），仅随 transitionTime 提供。</param>
    /// <param name="unacknowledged">true 时使用 Set Unacknowledged opcode。</param>
    public static byte[] Set(
        ushort lightness,
        ushort temperature,
        short deltaUv,
        byte tid,
        TransitionTime? transitionTime = null,
        byte? delay = null,
        bool unacknowledged = false)
    {
        var writer = MeshMessageWriter.Create(
                unacknowledged
                    ? MeshOpcode.LightCtlSetUnack
                    : MeshOpcode.LightCtlSet)
            .AddU16(lightness)
            .AddU16(temperature)
            .AddI16(deltaUv)
            .AddU8(tid);
        AddTransition(writer, transitionTime, delay);
        return writer.ToArray();
    }

    /// <summary>构建 Light CTL Set Unacknowledged 消息。</summary>
    public static byte[] SetUnacknowledged(
        ushort lightness,
        ushort temperature,
        short deltaUv,
        byte tid,
        TransitionTime? transitionTime = null,
        byte? delay = null) =>
        Set(lightness, temperature, deltaUv, tid, transitionTime, delay, true);

    /// <summary>
    /// Parses a Light CTL Status message.
    /// Payload: Present Lightness(2) + Present Temperature(2) +
    /// [Target Lightness(2) + Target Temperature(2) + Remaining Time(1)].
    /// </summary>
    public static bool TryParseStatus(
        ReadOnlySpan<byte> message,
        out LightCtlStatus status)
    {
        status = default;
        var reader = new MeshMessageReader(message);
        if (!reader.TryConsumeOpcode(MeshOpcode.LightCtlStatus) ||
            reader.Remaining < 4)
        {
            return false;
        }

        var presentLightness = reader.ReadU16();
        var presentTemperature = reader.ReadU16();
        if (reader.Remaining == 5)
        {
            status = new LightCtlStatus(
                presentLightness,
                presentTemperature,
                reader.ReadU16(),
                reader.ReadU16(),
                reader.ReadU8());
            return true;
        }

        if (reader.Remaining == 0)
        {
            status = new LightCtlStatus(
                presentLightness,
                presentTemperature,
                null,
                null,
                null);
            return true;
        }

        return false;
    }

    // ---- Temperature ----
    /// <summary>构建 Light CTL Temperature Get 消息。</summary>
    public static byte[] TemperatureGet() =>
        MeshMessageWriter.Create(MeshOpcode.LightCtlTemperatureGet).ToArray();

    /// <summary>
    /// 构建 Light CTL Temperature Set（或 Set Unacknowledged）消息。
    /// 载荷：Temperature(2) + Delta UV(2) + TID(1) + [Transition Time(1) + Delay(1)]。
    /// </summary>
    public static byte[] TemperatureSet(
        ushort temperature,
        short deltaUv,
        byte tid,
        TransitionTime? transitionTime = null,
        byte? delay = null,
        bool unacknowledged = false)
    {
        var writer = MeshMessageWriter.Create(
                unacknowledged
                    ? MeshOpcode.LightCtlTemperatureSetUnack
                    : MeshOpcode.LightCtlTemperatureSet)
            .AddU16(temperature)
            .AddI16(deltaUv)
            .AddU8(tid);
        AddTransition(writer, transitionTime, delay);
        return writer.ToArray();
    }

    /// <summary>
    /// Parses a Light CTL Temperature Status message.
    /// Payload: Present Temp(2) + Present Delta UV(2) +
    /// [Target Temp(2) + Target Delta UV(2) + Remaining Time(1)].
    /// </summary>
    public static bool TryParseTemperatureStatus(
        ReadOnlySpan<byte> message,
        out LightCtlTemperatureStatus status)
    {
        status = default;
        var reader = new MeshMessageReader(message);
        if (!reader.TryConsumeOpcode(MeshOpcode.LightCtlTemperatureStatus) ||
            reader.Remaining < 4)
        {
            return false;
        }

        var presentTemperature = reader.ReadU16();
        var presentDeltaUv = reader.ReadI16();
        if (reader.Remaining == 5)
        {
            status = new LightCtlTemperatureStatus(
                presentTemperature,
                presentDeltaUv,
                reader.ReadU16(),
                reader.ReadI16(),
                reader.ReadU8());
            return true;
        }

        if (reader.Remaining == 0)
        {
            status = new LightCtlTemperatureStatus(
                presentTemperature,
                presentDeltaUv,
                null,
                null,
                null);
            return true;
        }

        return false;
    }

    // ---- Temperature Range ----
    /// <summary>构建 Light CTL Temperature Range Get 消息。</summary>
    public static byte[] TemperatureRangeGet() =>
        MeshMessageWriter.Create(MeshOpcode.LightCtlTemperatureRangeGet).ToArray();

    /// <summary>
    /// 构建 Light CTL Temperature Range Set（或 Set Unacknowledged）消息。
    /// 载荷：Range Min(2) + Range Max(2)。
    /// </summary>
    public static byte[] TemperatureRangeSet(
        ushort rangeMin,
        ushort rangeMax,
        bool unacknowledged = false) =>
        MeshMessageWriter.Create(
                unacknowledged
                    ? MeshOpcode.LightCtlTemperatureRangeSetUnack
                    : MeshOpcode.LightCtlTemperatureRangeSet)
            .AddU16(rangeMin)
            .AddU16(rangeMax)
            .ToArray();

    public static bool TryParseTemperatureRangeStatus(
        ReadOnlySpan<byte> message,
        out LightCtlTemperatureRangeStatus status)
    {
        status = default;
        var reader = new MeshMessageReader(message);
        if (!reader.TryConsumeOpcode(MeshOpcode.LightCtlTemperatureRangeStatus) ||
            reader.Remaining != 5)
        {
            return false;
        }

        status = new LightCtlTemperatureRangeStatus(
            reader.ReadU8(),
            reader.ReadU16(),
            reader.ReadU16());
        return true;
    }

    // ---- Default ----
    /// <summary>构建 Light CTL Default Get 消息。</summary>
    public static byte[] DefaultGet() =>
        MeshMessageWriter.Create(MeshOpcode.LightCtlDefaultGet).ToArray();

    /// <summary>
    /// 构建 Light CTL Default Set（或 Set Unacknowledged）消息。
    /// 载荷：Lightness(2) + Temperature(2) + Delta UV(2)。
    /// </summary>
    public static byte[] DefaultSet(
        ushort lightness,
        ushort temperature,
        short deltaUv,
        bool unacknowledged = false) =>
        MeshMessageWriter.Create(
                unacknowledged
                    ? MeshOpcode.LightCtlDefaultSetUnack
                    : MeshOpcode.LightCtlDefaultSet)
            .AddU16(lightness)
            .AddU16(temperature)
            .AddI16(deltaUv)
            .ToArray();

    public static bool TryParseDefaultStatus(
        ReadOnlySpan<byte> message,
        out LightCtlDefaultStatus status)
    {
        status = default;
        var reader = new MeshMessageReader(message);
        if (!reader.TryConsumeOpcode(MeshOpcode.LightCtlDefaultStatus) ||
            reader.Remaining != 6)
        {
            return false;
        }

        status = new LightCtlDefaultStatus(
            reader.ReadU16(),
            reader.ReadU16(),
            reader.ReadI16());
        return true;
    }

    private static void AddTransition(
        MeshMessageWriter writer,
        TransitionTime? transitionTime,
        byte? delay)
    {
        if (transitionTime is { } transition)
        {
            writer.AddU8(transition.Encode())
                .AddU8(delay ?? 0);
        }
        else if (delay is not null)
        {
            throw new ArgumentException(
                "Delay requires Transition Time to be present.",
                nameof(delay));
        }
    }
}

/// <summary>
/// 解析后的 Light CTL Status。Target 字段与 RemainingTime 仅在过渡进行中存在。
/// </summary>
public readonly record struct LightCtlStatus(
    ushort PresentLightness,
    ushort PresentTemperature,
    ushort? TargetLightness,
    ushort? TargetTemperature,
    byte? RemainingTime);

/// <summary>解析后的 Light CTL Temperature Status。</summary>
public readonly record struct LightCtlTemperatureStatus(
    ushort PresentTemperature,
    short PresentDeltaUv,
    ushort? TargetTemperature,
    short? TargetDeltaUv,
    byte? RemainingTime);

/// <summary>解析后的 Light CTL Temperature Range Status。</summary>
public readonly record struct LightCtlTemperatureRangeStatus(
    byte StatusCode,
    ushort RangeMin,
    ushort RangeMax);

/// <summary>解析后的 Light CTL Default Status。</summary>
public readonly record struct LightCtlDefaultStatus(
    ushort Lightness,
    ushort Temperature,
    short DeltaUv);
