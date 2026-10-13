using ArriOpto.BLE.Mesh;

namespace ArriOpto.BLE.Mesh.Models;

/// <summary>
/// Light Lightness model messages (Mesh Model Specification 1.1, Section 6.3.1).
/// </summary>
public static class LightLightnessModel
{
    /// <summary>构建 Light Lightness Get 消息（查询 Actual 状态）。</summary>
    public static byte[] Get() =>
        MeshMessageWriter.Create(MeshOpcode.LightLightnessGet).ToArray();

    /// <summary>
    /// 构建 Light Lightness Set 消息。
    /// 载荷：Lightness(2) + TID(1) + [Transition Time(1) + Delay(1)]。
    /// </summary>
    /// <param name="lightness">目标亮度（0-65535，Actual 语义）。</param>
    /// <param name="tid">事务标识，同一状态的连续 Set 必须递增。</param>
    /// <param name="transitionTime">可选的过渡时间。</param>
    /// <param name="delay">执行延时（5 ms 步进），仅随 transitionTime 提供。</param>
    /// <param name="unacknowledged">true 时使用 Set Unacknowledged opcode。</param>
    public static byte[] Set(
        ushort lightness,
        byte tid,
        TransitionTime? transitionTime = null,
        byte? delay = null,
        bool unacknowledged = false) =>
        BuildSet(
            unacknowledged
                ? MeshOpcode.LightLightnessSetUnack
                : MeshOpcode.LightLightnessSet,
            lightness,
            tid,
            transitionTime,
            delay);

    /// <summary>构建 Light Lightness Set Unacknowledged 消息。</summary>
    public static byte[] SetUnacknowledged(
        ushort lightness,
        byte tid,
        TransitionTime? transitionTime = null,
        byte? delay = null) =>
        Set(lightness, tid, transitionTime, delay, unacknowledged: true);

    /// <summary>
    /// Parses a Light Lightness Status message.
    /// Payload: Present(2) + [Target(2) + Remaining Time(1)].
    /// </summary>
    public static bool TryParseStatus(
        ReadOnlySpan<byte> message,
        out LightLightnessStatus status)
    {
        status = default;
        var reader = new MeshMessageReader(message);
        if (!reader.TryConsumeOpcode(MeshOpcode.LightLightnessStatus) ||
            reader.Remaining < 2)
        {
            return false;
        }

        var present = reader.ReadU16();
        if (reader.Remaining == 3)
        {
            status = new LightLightnessStatus(
                present,
                reader.ReadU16(),
                reader.ReadU8());
            return true;
        }

        if (reader.Remaining == 0)
        {
            status = new LightLightnessStatus(present, null, null);
            return true;
        }

        return false;
    }

    // ---- Linear ----
    /// <summary>构建 Light Lightness Linear Get 消息。</summary>
    public static byte[] LinearGet() =>
        MeshMessageWriter.Create(MeshOpcode.LightLightnessLinearGet).ToArray();

    /// <summary>
    /// 构建 Light Lightness Linear Set 消息（Linear 语义，与 Actual 有换算关系）。
    /// </summary>
    public static byte[] LinearSet(
        ushort lightness,
        byte tid,
        TransitionTime? transitionTime = null,
        byte? delay = null,
        bool unacknowledged = false) =>
        BuildSet(
            unacknowledged
                ? MeshOpcode.LightLightnessLinearSetUnack
                : MeshOpcode.LightLightnessLinearSet,
            lightness,
            tid,
            transitionTime,
            delay);

    /// <summary>构建 Light Lightness Linear Set Unacknowledged 消息。</summary>
    public static byte[] LinearSetUnacknowledged(
        ushort lightness,
        byte tid,
        TransitionTime? transitionTime = null,
        byte? delay = null) =>
        LinearSet(lightness, tid, transitionTime, delay, unacknowledged: true);

    public static bool TryParseLinearStatus(
        ReadOnlySpan<byte> message,
        out LightLightnessStatus status) =>
        TryParseStatusCore(
            message,
            MeshOpcode.LightLightnessLinearStatus,
            out status);

    // ---- Last / Default / Range ----
    /// <summary>构建 Light Lightness Last Get 消息。</summary>
    public static byte[] LastGet() =>
        MeshMessageWriter.Create(MeshOpcode.LightLightnessLastGet).ToArray();

    /// <summary>
    /// 解析 Light Lightness Last Status 消息（Lightness，2 字节）。
    /// </summary>
    public static bool TryParseLastStatus(
        ReadOnlySpan<byte> message,
        out ushort lightness)
    {
        lightness = 0;
        var reader = new MeshMessageReader(message);
        if (!reader.TryConsumeOpcode(MeshOpcode.LightLightnessLastStatus) ||
            reader.Remaining != 2)
        {
            return false;
        }

        lightness = reader.ReadU16();
        return true;
    }

    /// <summary>构建 Light Lightness Default Get 消息。</summary>
    public static byte[] DefaultGet() =>
        MeshMessageWriter.Create(MeshOpcode.LightLightnessDefaultGet).ToArray();

    /// <summary>
    /// 构建 Light Lightness Default Set（或 Set Unacknowledged）消息。
    /// 载荷：Lightness(2)。
    /// </summary>
    public static byte[] DefaultSet(ushort lightness, bool unacknowledged = false) =>
        MeshMessageWriter.Create(
                unacknowledged
                    ? MeshOpcode.LightLightnessDefaultSetUnack
                    : MeshOpcode.LightLightnessDefaultSet)
            .AddU16(lightness)
            .ToArray();

    /// <summary>解析 Light Lightness Default Status 消息（Lightness，2 字节）。</summary>
    public static bool TryParseDefaultStatus(
        ReadOnlySpan<byte> message,
        out ushort lightness)
    {
        lightness = 0;
        var reader = new MeshMessageReader(message);
        if (!reader.TryConsumeOpcode(MeshOpcode.LightLightnessDefaultStatus) ||
            reader.Remaining != 2)
        {
            return false;
        }

        lightness = reader.ReadU16();
        return true;
    }

    /// <summary>构建 Light Lightness Range Get 消息。</summary>
    public static byte[] RangeGet() =>
        MeshMessageWriter.Create(MeshOpcode.LightLightnessRangeGet).ToArray();

    /// <summary>
    /// 构建 Light Lightness Range Set（或 Set Unacknowledged）消息。
    /// 载荷：Range Min(2) + Range Max(2)。
    /// </summary>
    public static byte[] RangeSet(
        ushort rangeMin,
        ushort rangeMax,
        bool unacknowledged = false) =>
        MeshMessageWriter.Create(
                unacknowledged
                    ? MeshOpcode.LightLightnessRangeSetUnack
                    : MeshOpcode.LightLightnessRangeSet)
            .AddU16(rangeMin)
            .AddU16(rangeMax)
            .ToArray();

    /// <summary>Parses a Light Lightness Range Status message.</summary>
    public static bool TryParseRangeStatus(
        ReadOnlySpan<byte> message,
        out LightLightnessRangeStatus status)
    {
        status = default;
        var reader = new MeshMessageReader(message);
        if (!reader.TryConsumeOpcode(MeshOpcode.LightLightnessRangeStatus) ||
            reader.Remaining != 5)
        {
            return false;
        }

        status = new LightLightnessRangeStatus(
            reader.ReadU8(),
            reader.ReadU16(),
            reader.ReadU16());
        return true;
    }

    private static byte[] BuildSet(
        ushort opcode,
        ushort lightness,
        byte tid,
        TransitionTime? transitionTime,
        byte? delay)
    {
        var writer = MeshMessageWriter.Create(opcode)
            .AddU16(lightness)
            .AddU8(tid);
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

        return writer.ToArray();
    }

    private static bool TryParseStatusCore(
        ReadOnlySpan<byte> message,
        ushort opcode,
        out LightLightnessStatus status)
    {
        status = default;
        var reader = new MeshMessageReader(message);
        if (!reader.TryConsumeOpcode(opcode) || reader.Remaining < 2)
            return false;

        var present = reader.ReadU16();
        if (reader.Remaining == 3)
        {
            status = new LightLightnessStatus(
                present,
                reader.ReadU16(),
                reader.ReadU8());
            return true;
        }

        if (reader.Remaining == 0)
        {
            status = new LightLightnessStatus(present, null, null);
            return true;
        }

        return false;
    }
}

/// <summary>
/// 解析后的 Light Lightness（或 Linear）Status。
/// Target 与 RemainingTime 仅在过渡进行中存在。
/// </summary>
public readonly record struct LightLightnessStatus(
    ushort PresentLightness,
    ushort? TargetLightness,
    byte? RemainingTime);

/// <summary>解析后的 Light Lightness Range Status（Status Code 见 MshMDL §7.2）。</summary>
public readonly record struct LightLightnessRangeStatus(
    byte StatusCode,
    ushort RangeMin,
    ushort RangeMax);
