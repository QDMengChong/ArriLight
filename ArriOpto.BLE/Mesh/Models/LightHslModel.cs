using ArriOpto.BLE.Mesh;

namespace ArriOpto.BLE.Mesh.Models;

/// <summary>
/// Light HSL model messages (Mesh Model Specification 1.1, Section 6.3.3).
/// </summary>
public static class LightHslModel
{
    /// <summary>构建 Light HSL Get 消息。</summary>
    public static byte[] Get() =>
        MeshMessageWriter.Create(MeshOpcode.LightHslGet).ToArray();

    /// <summary>
    /// Builds a Light HSL Set message.
    /// Payload: Lightness(2) + Hue(2) + Saturation(2) + TID(1) +
    /// [Transition Time(1) + Delay(1)].
    /// </summary>
    /// <param name="lightness">目标亮度（0-65535）。</param>
    /// <param name="hue">目标色相（0-65535，对应 0-360°）。</param>
    /// <param name="saturation">目标饱和度（0-65535）。</param>
    /// <param name="tid">事务标识，同一状态的连续 Set 必须递增。</param>
    /// <param name="transitionTime">可选的过渡时间。</param>
    /// <param name="delay">执行延时（5 ms 步进），仅随 transitionTime 提供。</param>
    /// <param name="unacknowledged">true 时使用 Set Unacknowledged opcode。</param>
    public static byte[] Set(
        ushort lightness,
        ushort hue,
        ushort saturation,
        byte tid,
        TransitionTime? transitionTime = null,
        byte? delay = null,
        bool unacknowledged = false)
    {
        var writer = MeshMessageWriter.Create(
                unacknowledged
                    ? MeshOpcode.LightHslSetUnack
                    : MeshOpcode.LightHslSet)
            .AddU16(lightness)
            .AddU16(hue)
            .AddU16(saturation)
            .AddU8(tid);
        AddTransition(writer, transitionTime, delay);
        return writer.ToArray();
    }

    /// <summary>构建 Light HSL Set Unacknowledged 消息。</summary>
    public static byte[] SetUnacknowledged(
        ushort lightness,
        ushort hue,
        ushort saturation,
        byte tid,
        TransitionTime? transitionTime = null,
        byte? delay = null) =>
        Set(lightness, hue, saturation, tid, transitionTime, delay, true);

    /// <summary>
    /// Parses a Light HSL Status message.
    /// Payload: Lightness(2) + Hue(2) + Saturation(2) + [Remaining Time(1)].
    /// </summary>
    public static bool TryParseStatus(
        ReadOnlySpan<byte> message,
        out LightHslStatus status)
    {
        status = default;
        var reader = new MeshMessageReader(message);
        if (!reader.TryConsumeOpcode(MeshOpcode.LightHslStatus) ||
            reader.Remaining < 6)
        {
            return false;
        }

        var lightness = reader.ReadU16();
        var hue = reader.ReadU16();
        var saturation = reader.ReadU16();
        if (reader.Remaining == 1)
        {
            status = new LightHslStatus(lightness, hue, saturation, reader.ReadU8());
            return true;
        }

        if (reader.Remaining == 0)
        {
            status = new LightHslStatus(lightness, hue, saturation, null);
            return true;
        }

        return false;
    }

    /// <summary>构建 Light HSL Target Get 消息。</summary>
    public static byte[] TargetGet() =>
        MeshMessageWriter.Create(MeshOpcode.LightHslTargetGet).ToArray();

    public static bool TryParseTargetStatus(
        ReadOnlySpan<byte> message,
        out LightHslStatus status) =>
        TryParseStatusCore(message, MeshOpcode.LightHslTargetStatus, out status);

    // ---- Hue ----
    /// <summary>构建 Light HSL Hue Get 消息。</summary>
    public static byte[] HueGet() =>
        MeshMessageWriter.Create(MeshOpcode.LightHslHueGet).ToArray();

    /// <summary>
    /// 构建 Light HSL Hue Set（或 Set Unacknowledged）消息。
    /// 载荷：Hue(2) + TID(1) + [Transition Time(1) + Delay(1)]。
    /// </summary>
    public static byte[] HueSet(
        ushort hue,
        byte tid,
        TransitionTime? transitionTime = null,
        byte? delay = null,
        bool unacknowledged = false)
    {
        var writer = MeshMessageWriter.Create(
                unacknowledged
                    ? MeshOpcode.LightHslHueSetUnack
                    : MeshOpcode.LightHslHueSet)
            .AddU16(hue)
            .AddU8(tid);
        AddTransition(writer, transitionTime, delay);
        return writer.ToArray();
    }

    /// <summary>
    /// Parses a Light HSL Hue Status message.
    /// Payload: Present Hue(2) + [Target Hue(2) + Remaining Time(1)].
    /// </summary>
    public static bool TryParseHueStatus(
        ReadOnlySpan<byte> message,
        out LightHslHueStatus status)
    {
        status = default;
        var reader = new MeshMessageReader(message);
        if (!reader.TryConsumeOpcode(MeshOpcode.LightHslHueStatus) ||
            reader.Remaining < 2)
        {
            return false;
        }

        var presentHue = reader.ReadU16();
        if (reader.Remaining == 3)
        {
            status = new LightHslHueStatus(
                presentHue,
                reader.ReadU16(),
                reader.ReadU8());
            return true;
        }

        if (reader.Remaining == 0)
        {
            status = new LightHslHueStatus(presentHue, null, null);
            return true;
        }

        return false;
    }

    // ---- Saturation ----
    /// <summary>构建 Light HSL Saturation Get 消息。</summary>
    public static byte[] SaturationGet() =>
        MeshMessageWriter.Create(MeshOpcode.LightHslSaturationGet).ToArray();

    /// <summary>
    /// 构建 Light HSL Saturation Set（或 Set Unacknowledged）消息。
    /// 载荷：Saturation(2) + TID(1) + [Transition Time(1) + Delay(1)]。
    /// </summary>
    public static byte[] SaturationSet(
        ushort saturation,
        byte tid,
        TransitionTime? transitionTime = null,
        byte? delay = null,
        bool unacknowledged = false)
    {
        var writer = MeshMessageWriter.Create(
                unacknowledged
                    ? MeshOpcode.LightHslSaturationSetUnack
                    : MeshOpcode.LightHslSaturationSet)
            .AddU16(saturation)
            .AddU8(tid);
        AddTransition(writer, transitionTime, delay);
        return writer.ToArray();
    }

    /// <summary>
    /// Parses a Light HSL Saturation Status message.
    /// Payload: Present Saturation(2) + [Target Saturation(2) + Remaining Time(1)].
    /// </summary>
    public static bool TryParseSaturationStatus(
        ReadOnlySpan<byte> message,
        out LightHslSaturationStatus status)
    {
        status = default;
        var reader = new MeshMessageReader(message);
        if (!reader.TryConsumeOpcode(MeshOpcode.LightHslSaturationStatus) ||
            reader.Remaining < 2)
        {
            return false;
        }

        var presentSaturation = reader.ReadU16();
        if (reader.Remaining == 3)
        {
            status = new LightHslSaturationStatus(
                presentSaturation,
                reader.ReadU16(),
                reader.ReadU8());
            return true;
        }

        if (reader.Remaining == 0)
        {
            status = new LightHslSaturationStatus(presentSaturation, null, null);
            return true;
        }

        return false;
    }

    // ---- Default / Range ----
    /// <summary>构建 Light HSL Default Get 消息。</summary>
    public static byte[] DefaultGet() =>
        MeshMessageWriter.Create(MeshOpcode.LightHslDefaultGet).ToArray();

    /// <summary>
    /// 构建 Light HSL Default Set（或 Set Unacknowledged）消息。
    /// 载荷：Lightness(2) + Hue(2) + Saturation(2)。
    /// </summary>
    public static byte[] DefaultSet(
        ushort lightness,
        ushort hue,
        ushort saturation,
        bool unacknowledged = false) =>
        MeshMessageWriter.Create(
                unacknowledged
                    ? MeshOpcode.LightHslDefaultSetUnack
                    : MeshOpcode.LightHslDefaultSet)
            .AddU16(lightness)
            .AddU16(hue)
            .AddU16(saturation)
            .ToArray();

    public static bool TryParseDefaultStatus(
        ReadOnlySpan<byte> message,
        out LightHslDefaultStatus status)
    {
        status = default;
        var reader = new MeshMessageReader(message);
        if (!reader.TryConsumeOpcode(MeshOpcode.LightHslDefaultStatus) ||
            reader.Remaining != 6)
        {
            return false;
        }

        status = new LightHslDefaultStatus(
            reader.ReadU16(),
            reader.ReadU16(),
            reader.ReadU16());
        return true;
    }

    /// <summary>构建 Light HSL Range Get 消息。</summary>
    public static byte[] RangeGet() =>
        MeshMessageWriter.Create(MeshOpcode.LightHslRangeGet).ToArray();

    /// <summary>
    /// 构建 Light HSL Range Set（或 Set Unacknowledged）消息。
    /// 载荷：Hue Min(2) + Hue Max(2) + Saturation Min(2) + Saturation Max(2)。
    /// </summary>
    public static byte[] RangeSet(
        ushort hueRangeMin,
        ushort hueRangeMax,
        ushort saturationRangeMin,
        ushort saturationRangeMax,
        bool unacknowledged = false) =>
        MeshMessageWriter.Create(
                unacknowledged
                    ? MeshOpcode.LightHslRangeSetUnack
                    : MeshOpcode.LightHslRangeSet)
            .AddU16(hueRangeMin)
            .AddU16(hueRangeMax)
            .AddU16(saturationRangeMin)
            .AddU16(saturationRangeMax)
            .ToArray();

    public static bool TryParseRangeStatus(
        ReadOnlySpan<byte> message,
        out LightHslRangeStatus status)
    {
        status = default;
        var reader = new MeshMessageReader(message);
        if (!reader.TryConsumeOpcode(MeshOpcode.LightHslRangeStatus) ||
            reader.Remaining != 9)
        {
            return false;
        }

        status = new LightHslRangeStatus(
            reader.ReadU8(),
            reader.ReadU16(),
            reader.ReadU16(),
            reader.ReadU16(),
            reader.ReadU16());
        return true;
    }

    private static bool TryParseStatusCore(
        ReadOnlySpan<byte> message,
        ushort opcode,
        out LightHslStatus status)
    {
        status = default;
        var reader = new MeshMessageReader(message);
        if (!reader.TryConsumeOpcode(opcode) || reader.Remaining < 6)
            return false;

        var lightness = reader.ReadU16();
        var hue = reader.ReadU16();
        var saturation = reader.ReadU16();
        if (reader.Remaining == 1)
        {
            status = new LightHslStatus(lightness, hue, saturation, reader.ReadU8());
            return true;
        }

        if (reader.Remaining == 0)
        {
            status = new LightHslStatus(lightness, hue, saturation, null);
            return true;
        }

        return false;
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
/// 解析后的 Light HSL（或 Target）Status。RemainingTime 仅在过渡进行中存在。
/// </summary>
public readonly record struct LightHslStatus(
    ushort Lightness,
    ushort Hue,
    ushort Saturation,
    byte? RemainingTime);

/// <summary>解析后的 Light HSL Hue Status。</summary>
public readonly record struct LightHslHueStatus(
    ushort PresentHue,
    ushort? TargetHue,
    byte? RemainingTime);

/// <summary>解析后的 Light HSL Saturation Status。</summary>
public readonly record struct LightHslSaturationStatus(
    ushort PresentSaturation,
    ushort? TargetSaturation,
    byte? RemainingTime);

/// <summary>解析后的 Light HSL Default Status。</summary>
public readonly record struct LightHslDefaultStatus(
    ushort Lightness,
    ushort Hue,
    ushort Saturation);

/// <summary>解析后的 Light HSL Range Status。</summary>
public readonly record struct LightHslRangeStatus(
    byte StatusCode,
    ushort HueRangeMin,
    ushort HueRangeMax,
    ushort SaturationRangeMin,
    ushort SaturationRangeMax);
