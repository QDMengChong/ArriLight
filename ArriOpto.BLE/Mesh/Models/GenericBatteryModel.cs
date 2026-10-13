using ArriOpto.BLE.Mesh;

namespace ArriOpto.BLE.Mesh.Models;

/// <summary>
/// Generic Battery model messages (Mesh Model Specification 1.1, Section 3.2.6).
/// </summary>
public static class GenericBatteryModel
{
    /// <summary>构建 Generic Battery Get 消息。</summary>
    public static byte[] Get() =>
        MeshMessageWriter.Create(MeshOpcode.GenericBatteryGet).ToArray();

    /// <summary>
    /// Parses a Generic Battery Status message.
    /// Payload: Level(1) + Time to Discharge(3) + Time to Charge(3) + Flags(1).
    /// </summary>
    public static bool TryParseStatus(
        ReadOnlySpan<byte> message,
        out GenericBatteryStatus status)
    {
        status = default;
        var reader = new MeshMessageReader(message);
        if (!reader.TryConsumeOpcode(MeshOpcode.GenericBatteryStatus) ||
            reader.Remaining != 8)
        {
            return false;
        }

        var level = reader.ReadU8();
        var timeToDischarge = reader.ReadU24();
        var timeToCharge = reader.ReadU24();
        var flags = reader.ReadU8();
        status = new GenericBatteryStatus(
            level,
            timeToDischarge,
            timeToCharge,
            flags,
            new BatteryFlags(flags));
        return true;
    }
}

/// <summary>Parsed Generic Battery Status values (Section 3.1.6).</summary>
public readonly record struct GenericBatteryStatus(
    byte Level,
    uint TimeToDischargeMinutes,
    uint TimeToChargeMinutes,
    byte Flags,
    BatteryFlags ParsedFlags);

/// <summary>
/// Generic Battery Flags: four 2-bit fields
/// (Section 3.1.6.4): Presence(0-1), Indicator(2-3), Charging(4-5),
/// Serviceability(6-7).
/// </summary>
public readonly record struct BatteryFlags(byte Raw)
{
    public byte Presence => (byte)(Raw & 0x03);
    public byte Indicator => (byte)((Raw >> 2) & 0x03);
    public byte Charging => (byte)((Raw >> 4) & 0x03);
    public byte Serviceability => (byte)((Raw >> 6) & 0x03);
}
