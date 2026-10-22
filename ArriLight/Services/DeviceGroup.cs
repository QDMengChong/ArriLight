using ArriOpto.BLE.Agp;

namespace ArriLight;

/// <summary>App-local membership, never a BLE identity or a Mesh subscription address.</summary>
public sealed record SavedDeviceGroup(
    string Id, string Name, IReadOnlyList<string> Members, DateTimeOffset AddedAt,
    IReadOnlyList<SavedControlPreset>? CustomPresets = null,
    ushort BlinkPeriodMilliseconds = 1000, ushort BreathePeriodMilliseconds = 2000,
    int TimerSeconds = 60, bool AutoOffEnabled = false, int AutoOffSeconds = 60);

public enum DeviceGroupCommandKind { Power, Lighting, Effect, Timer, LocalMode }

/// <summary>An immutable user intent; each protocol encodes its own requests.</summary>
public sealed record DeviceGroupCommand(
    DeviceGroupCommandKind Kind, bool IsOn = true, byte Brightness = 100,
    AgpColorModel ColorModel = AgpColorModel.None, double Hue = 0, double Saturation = 0,
    ushort TemperatureKelvin = 4000, AgpLightMode Effect = AgpLightMode.Steady,
    ushort PeriodMilliseconds = 0, int Seconds = 0, AgpModeConfig? LocalMode = null)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Kind) || Brightness > 100 ||
            !double.IsFinite(Hue) || Hue is < 0 or > 1 ||
            !double.IsFinite(Saturation) || Saturation is < 0 or > 1 ||
            !Enum.IsDefined(ColorModel) || !Enum.IsDefined(Effect) || Seconds is < 0 or > 86400)
            throw new ArgumentException(AppResources.Group_InvalidParameters);
        if (Kind == DeviceGroupCommandKind.Lighting && ColorModel == AgpColorModel.Ctl &&
            TemperatureKelvin is < AgpColorModelCodec.MinimumCtlTemperatureKelvin or > AgpColorModelCodec.MaximumCtlTemperatureKelvin)
            throw new ArgumentException(AppResources.Group_InvalidParameters);
        if (Kind == DeviceGroupCommandKind.Effect && Effect != AgpLightMode.Steady &&
            (PeriodMilliseconds < AgpModelCodec.MinimumEffectPeriodMilliseconds || PeriodMilliseconds > AgpModelCodec.MaximumEffectPeriodMilliseconds))
            throw new ArgumentException(AppResources.Group_InvalidParameters);
        if (Kind == DeviceGroupCommandKind.LocalMode)
            _ = AgpModelCodec.EncodeModeSet(LocalMode ?? throw new ArgumentException(AppResources.Group_InvalidParameters));
    }

    public bool IsSupportedBy(AgpCapabilities capabilities) => Kind switch
    {
        DeviceGroupCommandKind.Power => capabilities.HasFlag(AgpCapabilities.OnOff) &&
            (!IsOn || Seconds == 0 || capabilities.HasFlag(AgpCapabilities.RelativeTimer)),
        DeviceGroupCommandKind.Lighting => ColorModel switch
        {
            AgpColorModel.Hsl => capabilities.HasFlag(AgpCapabilities.Hsl),
            AgpColorModel.Ctl => capabilities.HasFlag(AgpCapabilities.Ctl),
            _ => (capabilities & (AgpCapabilities.Lightness | AgpCapabilities.Lightness16)) != 0
        },
        DeviceGroupCommandKind.Effect => capabilities.HasFlag(AgpCapabilities.OnOff | AgpCapabilities.Effects) &&
            (Seconds == 0 || capabilities.HasFlag(AgpCapabilities.RelativeTimer)),
        DeviceGroupCommandKind.Timer => capabilities.HasFlag(AgpCapabilities.RelativeTimer),
        DeviceGroupCommandKind.LocalMode => capabilities.HasFlag(AgpCapabilities.LocalMode),
        _ => false
    };
}

public sealed record DeviceGroupMemberSnapshot(
    SavedDevice Device, AgpCapabilities? Capabilities, AgpCtlRange? CtlRange,
    long? ConnectionGeneration, bool? IsOn = null, byte? Brightness = null,
    double? Hue = null, double? Saturation = null, ushort? TemperatureKelvin = null,
    AgpColorModel ColorModel = AgpColorModel.None, AgpLightMode Effect = AgpLightMode.Steady,
    ushort? EffectPeriodMilliseconds = null, AgpModeConfig? LocalMode = null,
    bool IsLocked = false, string? Detail = null);

public interface IDeviceGroupBackend
{
    event EventHandler? Changed;
    DeviceGroupMemberSnapshot Read(SavedDevice device);
    Task ExecuteAsync(DeviceGroupMemberSnapshot member, DeviceGroupCommand command,
        Func<bool> isStillMember, CancellationToken cancellationToken);
}

public sealed record DeviceGroupDispatch(int TargetCount, int SkippedCount, Task Completion);

public static class DeviceGroupCompatibility
{
    // Read-only features, security and device-specific management are not group controls.
    private const AgpCapabilities Controls = AgpCapabilities.OnOff | AgpCapabilities.Hsl |
        AgpCapabilities.Ctl | AgpCapabilities.Effects | AgpCapabilities.RelativeTimer | AgpCapabilities.LocalMode;

    public static string? GetIssue(DeviceGroupMemberSnapshot reference, DeviceGroupMemberSnapshot member)
    {
        if (reference.Device.IsDemo != member.Device.IsDemo)
            return AppResources.Group_DemoMismatch;
        if (reference.Capabilities is not { } required || member.Capabilities is not { } actual)
            return AppResources.Group_CapabilitiesUnknown;
        if ((actual & (required & Controls)) != (required & Controls) ||
            ((required & (AgpCapabilities.Lightness | AgpCapabilities.Lightness16)) != 0 &&
             (actual & (AgpCapabilities.Lightness | AgpCapabilities.Lightness16)) == 0))
            return AppResources.Group_Incompatible;
        if (required.HasFlag(AgpCapabilities.Ctl) &&
            (reference.CtlRange is not { } range || member.CtlRange is not { } other ||
             other.TemperatureMinimumKelvin > range.TemperatureMinimumKelvin ||
             other.TemperatureMaximumKelvin < range.TemperatureMaximumKelvin))
            return AppResources.Group_TemperatureRangeMismatch;
        return null;
    }
}
