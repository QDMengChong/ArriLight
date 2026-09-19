namespace ArriLight;

public sealed record SavedDevice(
    string Name,
    string Address,
    DateTimeOffset AddedAt = default,
    bool IsPinned = false,
    string FirmwareVersion = "1.0.0",
    bool IsDemo = false,
    string? Token = null);
