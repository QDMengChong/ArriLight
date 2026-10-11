using ArriOpto.BLE.Agp;

namespace ArriLight;

public enum DeviceSortOrder
{
    LastOperation,
    AddedAt,
    Name
}

public sealed record SavedControlPreset(
    int Slot,
    string DisplayName,
    double? Hue,
    double? Saturation,
    double? Temperature,
    double? Brightness,
    string? LightColorHex,
    AgpLightMode? EffectMode,
    ushort? EffectPeriodMilliseconds);

public sealed record SavedDevice(
    string Name,
    string Address,
    DateTimeOffset AddedAt = default,
    bool IsPinned = false,
    string FirmwareVersion = "1.0.0",
    bool IsDemo = false,
    string? Token = null,
    BlinkEffectParameters? BlinkEffectParameters = null,
    BreatheEffectParameters? BreatheEffectParameters = null,
    IReadOnlyList<SavedControlPreset>? CustomPresets = null,
    int TimerSeconds = 60,
    AgpCapabilities? Capabilities = null,
    string? HardwareRevision = null,
    // Keep persisted property names so existing checkbox/seconds settings survive the semantic change.
    bool TurnOnDelayEnabled = false,
    int TurnOnDelaySeconds = 60,
    DateTimeOffset? LastOperatedAt = null,
    BluetoothDeviceProtocol Protocol = BluetoothDeviceProtocol.Unknown)
{
    public const string UnknownFirmwareVersion = "unknown";

    public BluetoothDeviceProtocol EffectiveProtocol =>
        Protocol == BluetoothDeviceProtocol.Unknown
            ? BluetoothDeviceProtocol.Agp
            : Protocol;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasKnownFirmwareVersion =>
        !string.IsNullOrWhiteSpace(FirmwareVersion) &&
        FirmwareVersion != UnknownFirmwareVersion;

    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayName => IsDemo ? (Address, Name) switch
    {
        ("02:00:00:00:00:01", "客厅主灯" or "Living room main light") => AppResources.Demo_LivingRoom,
        ("02:00:00:00:00:02", "卧室氛围灯" or "Bedroom ambient light") => AppResources.Demo_Bedroom,
        _ => Name
    } : Name;
}
