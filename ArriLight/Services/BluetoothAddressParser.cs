using System.Text.RegularExpressions;

namespace ArriLight;

public static partial class BluetoothAddressParser
{
    public static bool TryNormalize(string? value, out string normalizedAddress)
    {
        normalizedAddress = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var candidate = value.Trim();
        if (MacAddressRegex().IsMatch(candidate))
        {
            normalizedAddress = candidate.Replace('-', ':').ToUpperInvariant();
            return true;
        }

        // Plugin.BLE 在非 Android 平台可能用 UUID 表示设备地址。
        if (Guid.TryParse(candidate, out var id))
        {
            normalizedAddress = id.ToString("D").ToUpperInvariant();
            return true;
        }

        return false;
    }

    [GeneratedRegex(
        @"^(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex MacAddressRegex();
}
