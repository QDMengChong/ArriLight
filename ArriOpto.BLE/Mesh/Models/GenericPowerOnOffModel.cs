using ArriOpto.BLE.Mesh;

namespace ArriOpto.BLE.Mesh.Models;

/// <summary>
/// Generic Power OnOff (OnPowerUp) model messages
/// (Mesh Model Specification 1.1, Sections 3.1.4 and 3.2.4).
/// </summary>
public static class GenericPowerOnOffModel
{
    /// <summary>构建 Generic OnPowerUp Get 消息（查询上电行为）。</summary>
    public static byte[] Get() =>
        MeshMessageWriter.Create(MeshOpcode.GenericOnPowerUpGet).ToArray();

    /// <summary>Builds a Generic OnPowerUp Set (acknowledged) message.</summary>
    /// <param name="onPowerUp">上电行为（Off / Default / Restore）。</param>
    public static byte[] Set(OnPowerUpValue onPowerUp) =>
        BuildSet(MeshOpcode.GenericOnPowerUpSet, onPowerUp);

    /// <summary>Builds a Generic OnPowerUp Set Unacknowledged message.</summary>
    /// <param name="onPowerUp">上电行为（Off / Default / Restore）。</param>
    public static byte[] SetUnacknowledged(OnPowerUpValue onPowerUp) =>
        BuildSet(MeshOpcode.GenericOnPowerUpSetUnack, onPowerUp);

    private static byte[] BuildSet(ushort opcode, OnPowerUpValue onPowerUp) =>
        MeshMessageWriter.Create(opcode)
            .AddU8((byte)onPowerUp)
            .ToArray();

    /// <summary>Parses a Generic OnPowerUp Status message (OnPowerUp, 1 octet).</summary>
    public static bool TryParseStatus(
        ReadOnlySpan<byte> message,
        out OnPowerUpValue onPowerUp)
    {
        onPowerUp = default;
        var reader = new MeshMessageReader(message);
        if (!reader.TryConsumeOpcode(MeshOpcode.GenericOnPowerUpStatus) ||
            reader.Remaining != 1)
        {
            return false;
        }

        var value = reader.ReadU8();
        if (value > (byte)OnPowerUpValue.Restore)
            return false;

        onPowerUp = (OnPowerUpValue)value;
        return true;
    }
}

/// <summary>Generic OnPowerUp state values (Section 3.1.4).</summary>
public enum OnPowerUpValue : byte
{
    Off = 0x00,
    Default = 0x01,
    Restore = 0x02
}
