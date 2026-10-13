using ArriOpto.BLE.Mesh;

namespace ArriOpto.BLE.Mesh.Models;

/// <summary>
/// Generic Location model messages (Mesh Model Specification 1.1, Section 3.2.7).
/// </summary>
public static class GenericLocationModel
{
    /// <summary>构建 Generic Location Global Get 消息。</summary>
    public static byte[] GlobalGet() =>
        MeshMessageWriter.Create(MeshOpcode.GenericLocationGlobalGet).ToArray();

    /// <summary>Builds a Generic Location Global Set (acknowledged) message.</summary>
    /// <param name="latitude">全局纬度（1e-7 度，带符号 32 位）。</param>
    /// <param name="longitude">全局经度（1e-7 度，带符号 32 位）。</param>
    /// <param name="altitude">全局海拔（米，带符号 16 位）。</param>
    /// <param name="unacknowledged">true 时使用 Set Unacknowledged opcode。</param>
    public static byte[] GlobalSet(
        int latitude,
        int longitude,
        short altitude,
        bool unacknowledged = false) =>
        MeshMessageWriter.Create(
                unacknowledged
                    ? MeshOpcode.GenericLocationGlobalSetUnack
                    : MeshOpcode.GenericLocationGlobalSet)
            .AddI32(latitude)
            .AddI32(longitude)
            .AddI16(altitude)
            .ToArray();

    /// <summary>
    /// Parses a Generic Location Global Status message.
    /// Payload: Latitude(4) + Longitude(4) + Altitude(2).
    /// </summary>
    public static bool TryParseGlobalStatus(
        ReadOnlySpan<byte> message,
        out GenericLocationGlobalStatus status)
    {
        status = default;
        var reader = new MeshMessageReader(message);
        if (!reader.TryConsumeOpcode(MeshOpcode.GenericLocationGlobalStatus) ||
            reader.Remaining != 10)
        {
            return false;
        }

        status = new GenericLocationGlobalStatus(
            reader.ReadI32(),
            reader.ReadI32(),
            reader.ReadI16());
        return true;
    }

    /// <summary>构建 Generic Location Local Get 消息。</summary>
    public static byte[] LocalGet() =>
        MeshMessageWriter.Create(MeshOpcode.GenericLocationLocalGet).ToArray();

    /// <summary>Builds a Generic Location Local Set (acknowledged) message.</summary>
    /// <param name="north">本地坐标北向（厘米）。</param>
    /// <param name="east">本地坐标东向（厘米）。</param>
    /// <param name="altitude">本地海拔（厘米）。</param>
    /// <param name="floorNumber">楼层号（带符号）。</param>
    /// <param name="uncertainty">位置不确定度。</param>
    /// <param name="unacknowledged">true 时使用 Set Unacknowledged opcode。</param>
    public static byte[] LocalSet(
        short north,
        short east,
        short altitude,
        sbyte floorNumber,
        ushort uncertainty,
        bool unacknowledged = false) =>
        MeshMessageWriter.Create(
                unacknowledged
                    ? MeshOpcode.GenericLocationLocalSetUnack
                    : MeshOpcode.GenericLocationLocalSet)
            .AddI16(north)
            .AddI16(east)
            .AddI16(altitude)
            .AddU8(unchecked((byte)floorNumber))
            .AddU16(uncertainty)
            .ToArray();

    /// <summary>
    /// Parses a Generic Location Local Status message.
    /// Payload: North(2) + East(2) + Altitude(2) + Floor(1) + Uncertainty(2).
    /// </summary>
    public static bool TryParseLocalStatus(
        ReadOnlySpan<byte> message,
        out GenericLocationLocalStatus status)
    {
        status = default;
        var reader = new MeshMessageReader(message);
        if (!reader.TryConsumeOpcode(MeshOpcode.GenericLocationLocalStatus) ||
            reader.Remaining != 9)
        {
            return false;
        }

        status = new GenericLocationLocalStatus(
            reader.ReadI16(),
            reader.ReadI16(),
            reader.ReadI16(),
            unchecked((sbyte)reader.ReadU8()),
            reader.ReadU16());
        return true;
    }
}

public readonly record struct GenericLocationGlobalStatus(
    int Latitude,
    int Longitude,
    short Altitude);

public readonly record struct GenericLocationLocalStatus(
    short North,
    short East,
    short Altitude,
    sbyte FloorNumber,
    ushort Uncertainty);
