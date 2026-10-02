using System.Text.Json;
using System.Text.Json.Serialization;
using ArriOpto.BLE.Agp;

namespace ArriLight;

public sealed record DeviceShareContent(
    [property: JsonRequired] string Name,
    [property: JsonRequired] string Address,
    [property: JsonRequired] string Token,
    [property: JsonRequired] AgpCapabilities Capabilities);

/// <summary>Only the identity and authorization needed to add a shared device.</summary>
public static class DeviceShareCode
{
    private const string Prefix = "ARRILIGHT:2:";
    // Keep at least two pixels per module in the share page's 260px QR image.
    public const int MaxEncodedLength = 1_200;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static string Encode(SavedDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.Token is null || device.Capabilities is null)
            throw new InvalidOperationException("设备缺少绑定授权或能力信息，请先完成设备绑定后再分享。");

        var content = new DeviceShareContent(device.Name, device.Address, device.Token, device.Capabilities.Value);
        if (!TryNormalize(content, out var normalized))
            throw new InvalidOperationException("设备名称、地址或绑定授权无效，无法生成分享二维码。");

        var value = Prefix + JsonSerializer.Serialize(normalized, JsonOptions);
        if (value.Length > MaxEncodedLength)
            throw new InvalidOperationException("设备信息过多，无法生成清晰的单个分享二维码。");

        return value;
    }

    public static bool TryParse(string? value, out DeviceShareContent content)
    {
        content = null!;
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxEncodedLength)
            return false;

        var candidate = value.Trim();
        if (!candidate.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        try
        {
            var parsed = JsonSerializer.Deserialize<DeviceShareContent>(candidate[Prefix.Length..], JsonOptions);
            return parsed is not null && TryNormalize(parsed, out content);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryNormalize(DeviceShareContent content, out DeviceShareContent normalized)
    {
        normalized = content;
        if (string.IsNullOrWhiteSpace(content.Name) || content.Name.Length > 128 ||
            !BluetoothAddressParser.TryNormalize(content.Address, out var address) ||
            content.Token is null || content.Token.Length != 8 || !content.Token.All(Uri.IsHexDigit))
        {
            return false;
        }

        normalized = content with
        {
            Name = content.Name.Trim(),
            Address = address,
            Token = content.Token.ToUpperInvariant()
        };
        return true;
    }
}
