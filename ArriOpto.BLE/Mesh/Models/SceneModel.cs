using ArriOpto.BLE.Mesh;

namespace ArriOpto.BLE.Mesh.Models;

/// <summary>
/// Scene model messages (Mesh Model Specification 1.1, Section 5.2.2).
/// </summary>
public static class SceneModel
{
    /// <summary>
    /// 构建 Scene Store（或 Store Unacknowledged）消息。
    /// 载荷：Scene Number(2)，0x0000 禁止使用。
    /// </summary>
    /// <param name="sceneNumber">要保存的场景号（1-65535）。</param>
    /// <param name="unacknowledged">true 时使用 Store Unacknowledged opcode。</param>
    public static byte[] Store(ushort sceneNumber, bool unacknowledged = false) =>
        MeshMessageWriter.Create(
                unacknowledged
                    ? MeshOpcode.SceneStoreUnack
                    : MeshOpcode.SceneStore)
            .AddU16(sceneNumber)
            .ToArray();

    /// <summary>构建 Scene Store Unacknowledged 消息。</summary>
    public static byte[] StoreUnacknowledged(ushort sceneNumber) =>
        Store(sceneNumber, unacknowledged: true);

    /// <summary>
    /// Builds a Scene Recall message.
    /// Payload: Scene Number(2) + TID(1) + [Transition Time(1) + Delay(1)].
    /// </summary>
    /// <param name="sceneNumber">要回放的场景号。</param>
    /// <param name="tid">事务标识，同一场景的连续 Recall 必须递增。</param>
    /// <param name="transitionTime">可选的过渡时间。</param>
    /// <param name="delay">执行延时（5 ms 步进），仅随 transitionTime 提供。</param>
    /// <param name="unacknowledged">true 时使用 Recall Unacknowledged opcode。</param>
    public static byte[] Recall(
        ushort sceneNumber,
        byte tid,
        TransitionTime? transitionTime = null,
        byte? delay = null,
        bool unacknowledged = false)
    {
        var writer = MeshMessageWriter.Create(
                unacknowledged
                    ? MeshOpcode.SceneRecallUnack
                    : MeshOpcode.SceneRecall)
            .AddU16(sceneNumber)
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

    /// <summary>构建 Scene Recall Unacknowledged 消息。</summary>
    public static byte[] RecallUnacknowledged(
        ushort sceneNumber,
        byte tid,
        TransitionTime? transitionTime = null,
        byte? delay = null) =>
        Recall(sceneNumber, tid, transitionTime, delay, unacknowledged: true);

    /// <summary>构建 Scene Get 消息。</summary>
    public static byte[] Get() =>
        MeshMessageWriter.Create(MeshOpcode.SceneGet).ToArray();

    /// <summary>
    /// Parses a Scene Status message.
    /// Payload: Status Code(1) + Current Scene(2) +
    /// [Target Scene(2) + Remaining Time(1)].
    /// </summary>
    public static bool TryParseStatus(
        ReadOnlySpan<byte> message,
        out SceneStatus status)
    {
        status = default;
        var reader = new MeshMessageReader(message);
        if (!reader.TryConsumeOpcode(MeshOpcode.SceneStatus) ||
            reader.Remaining < 3)
        {
            return false;
        }

        var statusCode = reader.ReadU8();
        var currentScene = reader.ReadU16();
        if (reader.Remaining == 3)
        {
            status = new SceneStatus(
                statusCode,
                currentScene,
                reader.ReadU16(),
                reader.ReadU8());
            return true;
        }

        if (reader.Remaining == 0)
        {
            status = new SceneStatus(statusCode, currentScene, null, null);
            return true;
        }

        return false;
    }

    /// <summary>构建 Scene Register Get 消息。</summary>
    public static byte[] RegisterGet() =>
        MeshMessageWriter.Create(MeshOpcode.SceneRegisterGet).ToArray();

    /// <summary>
    /// Parses a Scene Register Status message.
    /// Payload: Status Code(1) + Current Scene(2) + Scenes(2*N).
    /// </summary>
    public static bool TryParseRegisterStatus(
        ReadOnlySpan<byte> message,
        out SceneRegisterStatus status)
    {
        status = default;
        var reader = new MeshMessageReader(message);
        if (!reader.TryConsumeOpcode(MeshOpcode.SceneRegisterStatus) ||
            reader.Remaining < 3 || (reader.Remaining - 3) % 2 != 0)
        {
            return false;
        }

        var statusCode = reader.ReadU8();
        var currentScene = reader.ReadU16();
        var scenes = new List<ushort>();
        while (reader.HasRemaining)
            scenes.Add(reader.ReadU16());
        status = new SceneRegisterStatus(statusCode, currentScene, scenes);
        return true;
    }

    /// <summary>
    /// 构建 Scene Delete（或 Delete Unacknowledged）消息。
    /// 载荷：Scene Number(2)。
    /// </summary>
    public static byte[] Delete(ushort sceneNumber, bool unacknowledged = false) =>
        MeshMessageWriter.Create(
                unacknowledged
                    ? MeshOpcode.SceneDeleteUnack
                    : MeshOpcode.SceneDelete)
            .AddU16(sceneNumber)
            .ToArray();

    /// <summary>构建 Scene Delete Unacknowledged 消息。</summary>
    public static byte[] DeleteUnacknowledged(ushort sceneNumber) =>
        Delete(sceneNumber, unacknowledged: true);
}

/// <summary>Scene status code values (Section 5.2.2.11).</summary>
public enum SceneStatusCode : byte
{
    Success = 0x00,
    SceneRegisterFull = 0x01,
    SceneNotFound = 0x02
}

public readonly record struct SceneStatus(
    byte StatusCode,
    ushort CurrentScene,
    ushort? TargetScene,
    byte? RemainingTime);

public readonly record struct SceneRegisterStatus(
    byte StatusCode,
    ushort CurrentScene,
    IReadOnlyList<ushort> Scenes);
