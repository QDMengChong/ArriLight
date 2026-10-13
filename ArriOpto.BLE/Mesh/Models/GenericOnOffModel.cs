using ArriOpto.BLE.Mesh;

namespace ArriOpto.BLE.Mesh.Models;

/// <summary>
/// Generic OnOff model messages (Mesh Model Specification 1.1, Section 3.2.1).
/// </summary>
public static class GenericOnOffModel
{
    public static byte[] Get() =>
        MeshMessageWriter.Create(MeshOpcode.GenericOnOffGet).ToArray();

    /// <summary>Builds a Generic OnOff Set (acknowledged) message.</summary>
    /// <param name="onOff">目标开关状态。</param>
    /// <param name="tid">事务标识，同一状态的连续 Set 必须递增。</param>
    /// <param name="transitionTime">可选的过渡时间；提供后 Delay 才允许出现。</param>
    /// <param name="delay">消息执行延时（5 ms 步进），仅随 transitionTime 提供。</param>
    public static byte[] Set(
        bool onOff,
        byte tid,
        TransitionTime? transitionTime = null,
        byte? delay = null) =>
        BuildSet(MeshOpcode.GenericOnOffSet, onOff, tid, transitionTime, delay);

    /// <summary>Builds a Generic OnOff Set Unacknowledged message.</summary>
    /// <param name="onOff">目标开关状态。</param>
    /// <param name="tid">事务标识，同一状态的连续 Set 必须递增。</param>
    /// <param name="transitionTime">可选的过渡时间；提供后 Delay 才允许出现。</param>
    /// <param name="delay">消息执行延时（5 ms 步进），仅随 transitionTime 提供。</param>
    public static byte[] SetUnacknowledged(
        bool onOff,
        byte tid,
        TransitionTime? transitionTime = null,
        byte? delay = null) =>
        BuildSet(MeshOpcode.GenericOnOffSetUnack, onOff, tid, transitionTime, delay);

    private static byte[] BuildSet(
        ushort opcode,
        bool onOff,
        byte tid,
        TransitionTime? transitionTime,
        byte? delay)
    {
        var writer = MeshMessageWriter.Create(opcode)
            .AddU8((byte)(onOff ? 1 : 0))
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

    /// <summary>
    /// Parses a Generic OnOff Status message.
    /// Payload: Present OnOff(1) + [Target OnOff(1) + Remaining Time(1)].
    /// </summary>
    public static bool TryParseStatus(
        ReadOnlySpan<byte> message,
        out GenericOnOffStatus status)
    {
        status = default;
        var reader = new MeshMessageReader(message);
        if (!reader.TryConsumeOpcode(MeshOpcode.GenericOnOffStatus))
            return false;
        if (!reader.HasRemaining)
            return false;

        var present = reader.ReadU8() != 0;
        if (reader.Remaining == 2)
        {
            var target = reader.ReadU8() != 0;
            var remaining = reader.ReadU8();
            status = new GenericOnOffStatus(present, target, remaining);
            return true;
        }

        if (reader.Remaining == 0)
        {
            status = new GenericOnOffStatus(present, null, null);
            return true;
        }

        return false;
    }
}

/// <summary>Present OnOff, optional Target OnOff and Remaining Time.</summary>
public readonly record struct GenericOnOffStatus(
    bool PresentOnOff,
    bool? TargetOnOff,
    byte? RemainingTime);
