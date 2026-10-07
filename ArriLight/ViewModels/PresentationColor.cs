using System.Globalization;

namespace ArriLight.ViewModels;

/// <summary>A color value for presentation state, independent of a native UI toolkit.</summary>
public sealed record PresentationColor(float Red, float Green, float Blue, float Alpha = 1)
{
    public static PresentationColor FromArgb(string hex)
    {
        var text = hex.TrimStart('#');
        if (text.Length is not (6 or 8))
            throw new FormatException("A color must contain six or eight hexadecimal digits.");
        var value = uint.Parse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new((value >> 16 & 255) / 255f, (value >> 8 & 255) / 255f, (value & 255) / 255f,
            text.Length == 8 ? (value >> 24) / 255f : 1);
    }

    public static PresentationColor FromRgb(double red, double green, double blue) =>
        new((float)red, (float)green, (float)blue);
    public static PresentationColor FromRgb(int red, int green, int blue) =>
        new(red / 255f, green / 255f, blue / 255f);
    public static PresentationColor Transparent => new(0, 0, 0, 0);
    public static PresentationColor FromHsla(double hue, double saturation, double lightness)
    {
        var chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        var h = hue * 6;
        var x = chroma * (1 - Math.Abs(h % 2 - 1));
        var (r, g, b) = h switch
        {
            < 1 => (chroma, x, 0d), < 2 => (x, chroma, 0d),
            < 3 => (0d, chroma, x), < 4 => (0d, x, chroma),
            < 5 => (x, 0d, chroma), _ => (chroma, 0d, x)
        };
        var offset = lightness - chroma / 2;
        return FromRgb(r + offset, g + offset, b + offset);
    }

    public string ToArgbHex() => $"#{Byte(Alpha):X2}{Byte(Red):X2}{Byte(Green):X2}{Byte(Blue):X2}";
    public string ToRgbHex() => $"#{Byte(Red):X2}{Byte(Green):X2}{Byte(Blue):X2}";
    private static int Byte(float value) => (int)Math.Round(Math.Clamp(value, 0, 1) * 255);
}
