using System.Text.Json;

namespace ArriLight;

public sealed class DeviceRepository
{
    private const string PreferencesKey = "saved_bluetooth_devices_v1";
    private readonly object _syncRoot = new();

    public event EventHandler? DevicesChanged;

    public IReadOnlyList<SavedDevice> GetAll()
    {
        lock (_syncRoot)
        {
            var json = Preferences.Default.Get(PreferencesKey, string.Empty);
            if (string.IsNullOrWhiteSpace(json))
                return [];

            try
            {
                return JsonSerializer.Deserialize<List<SavedDevice>>(json) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }
    }

    public bool Contains(string address) =>
        GetAll().Any(device =>
            string.Equals(device.Address, address, StringComparison.OrdinalIgnoreCase));

    public bool TryAdd(string name, string address)
    {
        var normalizedName = name.Trim();
        var normalizedAddress = address.Trim().ToUpperInvariant();

        if (string.IsNullOrWhiteSpace(normalizedName) ||
            string.IsNullOrWhiteSpace(normalizedAddress))
        {
            return false;
        }

        lock (_syncRoot)
        {
            var devices = GetAll().ToList();
            if (devices.Any(device =>
                    string.Equals(device.Address, normalizedAddress,
                        StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            devices.Add(new SavedDevice(normalizedName, normalizedAddress));
            Preferences.Default.Set(
                PreferencesKey,
                JsonSerializer.Serialize(devices));
        }

        DevicesChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }
}
