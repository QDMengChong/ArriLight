using System.Globalization;

namespace ArriLight;

public sealed class PresentationColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is PresentationColor color
            ? new Color(color.Red, color.Green, color.Blue, color.Alpha)
            : Colors.Transparent;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Color color
            ? new PresentationColor(color.Red, color.Green, color.Blue, color.Alpha)
            : PresentationColor.Transparent;
}
