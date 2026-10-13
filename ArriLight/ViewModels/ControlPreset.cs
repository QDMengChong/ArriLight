using ArriOpto.BLE.Agp;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ArriLight.ViewModels;

public sealed partial class ControlPreset : ObservableObject
{
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DetailText))]
    private bool _isReorderMode;
    private bool _supportsBrightness = true;
    private bool _supportsColor = true;
    private bool _supportsTemperature = true;
    private bool _supportsEffect = true;

    private ControlPreset(
        string displayName,
        bool isSaved,
        double? hue,
        double? saturation,
        double? temperature,
        double? brightness,
        PresentationColor? lightColor,
        AgpLightMode? effectMode,
        ushort? effectPeriodMilliseconds,
        AgpColorModel? activeColorModel = null)
    {
        DisplayName = displayName;
        IsSaved = isSaved;
        Hue = hue;
        Saturation = saturation;
        Temperature = temperature;
        Brightness = brightness;
        LightColor = lightColor;
        EffectMode = effectMode;
        EffectPeriodMilliseconds = effectPeriodMilliseconds;
        ActiveColorModel = activeColorModel;
    }

    public string DisplayName { get; }

    public bool IsSaved { get; }

    public double? Hue { get; }

    public double? Saturation { get; }

    public double? Temperature { get; }

    public double? Brightness { get; }

    public PresentationColor? LightColor { get; }

    public AgpLightMode? EffectMode { get; }

    public ushort? EffectPeriodMilliseconds { get; }

    public AgpColorModel? ActiveColorModel { get; }

    public bool HasColor => _supportsColor && LightColor is not null;
    public bool HasTemperature => _supportsTemperature && Temperature is not null;
    public bool HasBrightness => _supportsBrightness && Brightness is not null;
    public bool HasEffect => _supportsEffect && EffectMode is
        AgpLightMode.Blink or AgpLightMode.Breathe;

    public string DetailText =>
        IsReorderMode
            ? AppResources.Preset_Drag
            : !IsSaved
                ? AppResources.Preset_TapToView
                : HasEffect
                    ? EffectName(EffectMode!.Value)
                    : HasBrightness || HasTemperature
                        ? FormatLevels()
                        : ToRgbHex(PreviewColor);

    public PresentationColor PreviewColor =>
        !IsSaved
            ? PresentationColor.FromArgb("#DDE3ED")
            : HasColor
                ? LightColor!
                : CreateTemperatureColor(
                    HasTemperature ? Temperature : null,
                    HasBrightness ? Brightness : null);

    public static ControlPreset Create(
        string name,
        double hue,
        double saturation,
        double temperature,
        double brightness) =>
        new(
            name,
            true,
            hue,
            saturation,
            temperature,
            brightness,
            PresentationColor.FromHsla(hue, saturation, 0.52),
            null,
            null);

    public static ControlPreset Create(
        string name,
        double? hue,
        double? saturation,
        double? temperature,
        double? brightness,
        PresentationColor? lightColor,
        AgpLightMode? effectMode,
        ushort? effectPeriodMilliseconds,
        AgpColorModel? activeColorModel = null) =>
        new(
            name,
            true,
            hue,
            saturation,
            temperature,
            brightness,
            lightColor,
            effectMode,
            effectPeriodMilliseconds,
            activeColorModel);

    public static ControlPreset Empty() => new(
        AppResources.Preset_Empty, false, null, null, null, null, null, null, null);

    internal SavedControlPreset ToSaved(int slot) => new(
        slot,
        DisplayName,
        Hue,
        Saturation,
        Temperature,
        Brightness,
        HasColor ? ToRgbHex(LightColor!) : null,
        EffectMode,
        EffectPeriodMilliseconds,
        ActiveColorModel);

    internal static ControlPreset FromSaved(SavedControlPreset saved)
    {
        PresentationColor? lightColor = null;
        if (!string.IsNullOrWhiteSpace(saved.LightColorHex))
        {
            try
            {
                lightColor = PresentationColor.FromArgb(saved.LightColorHex);
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException)
            {
                // Keep the remaining preset values when an older color value is invalid.
            }
        }

        return new(
            string.IsNullOrWhiteSpace(saved.DisplayName) ? AppResources.Preset_Unnamed : saved.DisplayName,
            true,
            saved.Hue,
            saved.Saturation,
            saved.Temperature,
            saved.Brightness,
            lightColor,
            saved.EffectMode,
            saved.EffectPeriodMilliseconds,
            saved.ActiveColorModel);
    }

    internal void SetSupportedContent(
        bool supportsBrightness,
        bool supportsColor,
        bool supportsTemperature,
        bool supportsEffect)
    {
        if (_supportsBrightness == supportsBrightness && _supportsColor == supportsColor &&
            _supportsTemperature == supportsTemperature && _supportsEffect == supportsEffect)
            return;
        _supportsBrightness = supportsBrightness;
        _supportsColor = supportsColor;
        _supportsTemperature = supportsTemperature;
        _supportsEffect = supportsEffect;
        OnPropertyChanged(nameof(DetailText));
        OnPropertyChanged(nameof(PreviewColor));
    }

    private string FormatLevels()
    {
        var levels = new List<string>(2);
        if (HasBrightness)
            levels.Add($"{Brightness!.Value:0}%");
        if (HasTemperature)
            levels.Add($"{Temperature!.Value * 100:0}%");
        return string.Join('/', levels);
    }

    private static string EffectName(AgpLightMode mode) => mode switch
    {
        AgpLightMode.Blink => AppResources.Effect_Blink,
        AgpLightMode.Breathe => AppResources.Effect_Breathe,
        _ => AppResources.Effect_Steady
    };

    private static PresentationColor CreateTemperatureColor(double? temperature, double? brightness)
    {
        // The picker exposes a normalized cool-to-warm range. Map it to the
        // common 6500 K..2700 K lighting range before applying the standard
        // Kelvin-to-RGB approximation.
        var kelvin = 6500 - Math.Clamp(temperature ?? 0.5, 0, 1) * (6500 - 2700);
        var temperatureHundreds = kelvin / 100;
        var red = temperatureHundreds <= 66
            ? 255
            : 329.698727446 * Math.Pow(temperatureHundreds - 60, -0.1332047592);
        var green = temperatureHundreds <= 66
            ? 99.4708025861 * Math.Log(Math.Max(temperatureHundreds, 1)) - 161.1195681661
            : 288.1221695283 * Math.Pow(temperatureHundreds - 60, -0.0755148492);
        var blue = temperatureHundreds >= 66
            ? 255
            : temperatureHundreds <= 19
                ? 0
                : 138.5177312231 * Math.Log(temperatureHundreds - 10) - 305.0447927307;
        var level = Math.Clamp(brightness ?? 50, 1, 100) / 100;
        var whiteMix = 0.15 + level * 0.75;
        var intensity = 0.65 + level * 0.35;

        return PresentationColor.FromRgb(
            (float)Math.Clamp((red / 255 * (1 - whiteMix) + whiteMix) * intensity, 0, 1),
            (float)Math.Clamp((green / 255 * (1 - whiteMix) + whiteMix) * intensity, 0, 1),
            (float)Math.Clamp((blue / 255 * (1 - whiteMix) + whiteMix) * intensity, 0, 1));
    }

    private static string ToRgbHex(PresentationColor color) =>
        $"#{ToByte(color.Red):X2}{ToByte(color.Green):X2}{ToByte(color.Blue):X2}";

    private static int ToByte(float value) =>
        (int)Math.Round(Math.Clamp(value, 0, 1) * 255);

}
