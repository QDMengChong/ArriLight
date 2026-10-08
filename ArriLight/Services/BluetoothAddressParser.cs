using System.Text.RegularExpressions;

namespace ArriLight;

public static partial class BluetoothAddressParser
{
    public static Guid ToDeviceId(string address)
    {
        if (!TryNormalize(address, out var normalized))
            throw new ArgumentException(AppResources.Ble_AddressRequired, nameof(address));
        if (Guid.TryParse(normalized, out var id)) return id;
        // Plugin.BLE Android stores the six MAC bytes at offsets 10..15.
        var bytes = new byte[16];
        Convert.FromHexString(normalized.Replace(":", string.Empty)).CopyTo(bytes, 10);
        return new Guid(bytes);
    }

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
