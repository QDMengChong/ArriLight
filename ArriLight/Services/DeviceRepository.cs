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
            return ReadDevicesUnsafe();
    }

    public SavedDevice? GetByAddress(string address) =>
        GetAll().FirstOrDefault(device =>
            string.Equals(device.Address, address, StringComparison.OrdinalIgnoreCase));

    public bool Contains(string address) => GetByAddress(address) is not null;

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
            var devices = ReadDevicesUnsafe().ToList();
            if (devices.Any(device =>
                    string.Equals(device.Address, normalizedAddress,
                        StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            devices.Add(new SavedDevice(
                normalizedName,
                normalizedAddress,
                DateTimeOffset.Now));
            SaveDevicesUnsafe(devices);
        }

        DevicesChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Rename(string address, string name)
    {
        var normalizedName = name.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName))
            return false;

        return Update(
            address,
            device => device with { Name = normalizedName });
    }

    public bool SetPinned(string address, bool isPinned) =>
        Update(address, device => device with { IsPinned = isPinned });

    public bool SetFirmwareVersion(string address, string firmwareVersion) =>
        Update(
            address,
            device => device with { FirmwareVersion = firmwareVersion });

    public bool Remove(string address)
    {
        var removed = false;
        lock (_syncRoot)
        {
            var devices = ReadDevicesUnsafe().ToList();
            removed = devices.RemoveAll(device =>
                string.Equals(device.Address, address,
                    StringComparison.OrdinalIgnoreCase)) > 0;

            if (removed)
                SaveDevicesUnsafe(devices);
        }

        if (removed)
            DevicesChanged?.Invoke(this, EventArgs.Empty);

        return removed;
    }

    private bool Update(
        string address,
        Func<SavedDevice, SavedDevice> updateDevice)
    {
        var updated = false;
        lock (_syncRoot)
        {
            var devices = ReadDevicesUnsafe().ToList();
            var index = devices.FindIndex(device =>
                string.Equals(device.Address, address,
                    StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                return false;

            devices[index] = updateDevice(devices[index]);
            SaveDevicesUnsafe(devices);
            updated = true;
        }

        if (updated)
            DevicesChanged?.Invoke(this, EventArgs.Empty);

        return updated;
    }

    private IReadOnlyList<SavedDevice> ReadDevicesUnsafe()
    {
        if (!Preferences.Default.ContainsKey(PreferencesKey))
        {
            var defaults = CreateDefaultDevices();
            SaveDevicesUnsafe(defaults);
            return defaults;
        }

        var json = Preferences.Default.Get(PreferencesKey, string.Empty);
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            var devices = JsonSerializer.Deserialize<List<SavedDevice>>(json) ?? [];
            var migrated = false;
            var migrationTime = DateTimeOffset.Now;

            for (var index = 0; index < devices.Count; index++)
            {
                var device = devices[index];
                if (device.AddedAt == default)
                {
                    device = device with
                    {
                        AddedAt = migrationTime.AddSeconds(-devices.Count + index)
                    };
                    migrated = true;
                }

                if (string.IsNullOrWhiteSpace(device.FirmwareVersion))
                {
                    device = device with { FirmwareVersion = "1.0.0" };
                    migrated = true;
                }

                devices[index] = device;
            }

            if (migrated)
                SaveDevicesUnsafe(devices);

            return devices;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static List<SavedDevice> CreateDefaultDevices()
    {
        var now = DateTimeOffset.Now;
        return
        [
            new(
                "客厅主灯",
                "02:00:00:00:00:01",
                now.AddDays(-7),
                FirmwareVersion: "1.0.0",
                IsDemo: true),
            new(
                "卧室氛围灯",
                "02:00:00:00:00:02",
                now.AddDays(-2),
                FirmwareVersion: "1.0.0",
                IsDemo: true)
        ];
    }

    private static void SaveDevicesUnsafe(IEnumerable<SavedDevice> devices) =>
        Preferences.Default.Set(
            PreferencesKey,
            JsonSerializer.Serialize(devices));
}