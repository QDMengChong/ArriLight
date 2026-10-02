using ArriOpto.BLE.Agp;

namespace ArriLight;

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
    int TimerSeconds = 60);
