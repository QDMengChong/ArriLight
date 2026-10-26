using System.Text.Json;
using ArriOpto.BLE.Agp;

namespace ArriLight;

public sealed class DeviceRepository
{
    private const string PreferencesKey = "saved_bluetooth_devices_v1";
    private readonly object _syncRoot = new();

    public event EventHandler? DevicesChanged;

    public DeviceSortOrder GetSortOrder() =>
        Enum.TryParse<DeviceSortOrder>(Preferences.Default.Get("device_sort_order", string.Empty), out var order) &&
        Enum.IsDefined(order) ? order : DeviceSortOrder.AddedAt;

    public void SetSortOrder(DeviceSortOrder order)
    {
        if (!Enum.IsDefined(order))
            throw new ArgumentOutOfRangeException(nameof(order));
        Preferences.Default.Set("device_sort_order", order.ToString());
    }

    public bool RecordOperation(string address, DateTimeOffset? at = null) =>
        Update(address, device => device with { LastOperatedAt = at ?? DateTimeOffset.UtcNow });

    public IReadOnlyList<SavedDevice> GetAll()
    {
        lock (_syncRoot)
            return ReadDevicesUnsafe();
    }

    public SavedDevice? GetByAddress(string address) =>
        GetAll().FirstOrDefault(device =>
            string.Equals(device.Address, address, StringComparison.OrdinalIgnoreCase));

    public bool Contains(string address) => GetByAddress(address) is not null;

    public bool TryAdd(string name, string address) =>
        TryAdd(name, address, BluetoothDeviceProtocol.Agp);

    public bool TryAdd(
        string name,
        string address,
        BluetoothDeviceProtocol protocol) =>
        TryAddDevice(new SavedDevice(
            name,
            address,
            DateTimeOffset.Now,
            Protocol: protocol));

    public bool TryAddShared(DeviceShareContent content) =>
        TryAddDevice(new SavedDevice(
            content.Name,
            content.Address,
            DateTimeOffset.Now,
            FirmwareVersion: SavedDevice.UnknownFirmwareVersion,
            Token: content.Token,
            BlinkEffectParameters: new(),
            BreatheEffectParameters: new(),
            CustomPresets: [],
            Capabilities: content.Capabilities,
            Protocol: BluetoothDeviceProtocol.Agp));

    private bool TryAddDevice(SavedDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        var normalizedName = device.Name.Trim();
        var normalizedAddress = device.Address.Trim().ToUpperInvariant();

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

            devices.Add(device with
            {
                Name = normalizedName,
                Address = normalizedAddress,
                AddedAt = device.AddedAt == default ? DateTimeOffset.Now : device.AddedAt
            });
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

    public bool SetHardwareRevision(string address, string hardwareRevision) =>
        Update(
            address,
            device => device with { HardwareRevision = hardwareRevision });

    public bool SetProtocol(string address, BluetoothDeviceProtocol protocol) =>
        Update(address, device => device with { Protocol = protocol });

    public string? GetToken(string address) =>
        GetByAddress(address)?.Token;

    public byte[]? GetTokenBytes(string address)
    {
        var token = GetToken(address);
        if (string.IsNullOrWhiteSpace(token) || token.Length != 8)
            return null;

        try
        {
            var bytes = Convert.FromHexString(token);
            return bytes.Length == 4 ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public bool SetToken(string address, byte[] token)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (token.Length != 4)
            throw new ArgumentException(AppResources.Agp_TokenLengthInvalid, nameof(token));

        return Update(
            address,
            device => device with
            {
                Token = Convert.ToHexString(token).ToUpperInvariant()
            });
    }

    public AgpCapabilities? GetCapabilities(string address) =>
        GetByAddress(address)?.Capabilities;

    public bool SetCapabilities(string address, AgpCapabilities capabilities) =>
        // Capability migration is internal metadata and does not change the
        // device list shown by the home page. Avoid rebuilding its item while
        // a foreground operation is using that item.
        Update(address, device => device with { Capabilities = capabilities },
            notifyDevicesChanged: false);

    public bool SetMeshControlProfile(string address, MeshControlProfile profile) =>
        Update(address, device => device with
        {
            MeshProfile = profile,
            Capabilities = profile.Capabilities
        }, notifyDevicesChanged: false);

    /// <summary>Explicitly invalidate firmware-dependent metadata after an OTA attempt.</summary>
    public bool InvalidateCapabilities(string address) =>
        Update(address, device => device with { Capabilities = null }, notifyDevicesChanged: false);

    public bool SetTokenAndCapabilities(
        string address,
        byte[] token,
        AgpCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (token.Length != 4)
            throw new ArgumentException(AppResources.Agp_TokenLengthInvalid, nameof(token));

        return Update(
            address,
            device => device with
            {
                Token = Convert.ToHexString(token).ToUpperInvariant(),
                Capabilities = capabilities
            });
    }

    public bool ClearToken(string address) =>
        Update(address, device => device with { Token = null });

    public bool SetBlinkEffectParameters(string address, BlinkEffectParameters parameters) =>
        Update(address, device => device with { BlinkEffectParameters = parameters });

    public bool SetBreatheEffectParameters(string address, BreatheEffectParameters parameters) =>
        Update(address, device => device with { BreatheEffectParameters = parameters });

    public IReadOnlyList<SavedControlPreset> GetCustomPresets(string address) =>
        GetByAddress(address)?.CustomPresets ?? [];

    public bool SetCustomPresets(
        string address,
        IEnumerable<SavedControlPreset> presets)
    {
        ArgumentNullException.ThrowIfNull(presets);
        var normalized = presets
            .Where(preset => preset.Slot is >= 0 and < 6)
            .GroupBy(preset => preset.Slot)
            .Select(group => group.First())
            .OrderBy(preset => preset.Slot)
            .ToArray();
        return Update(address, device => device with { CustomPresets = normalized });
    }

    public int GetTimerSeconds(string address) =>
        GetByAddress(address)?.TimerSeconds is { } seconds && seconds is >= 1 and <= 86400
            ? seconds
            : 60;

    public bool SetTimerSeconds(string address, int seconds)
    {
        if (seconds is < 1 or > 86400)
            return false;

        return Update(address, device => device with { TimerSeconds = seconds });
    }

    public bool GetTurnOnDelayEnabled(string address) =>
        GetByAddress(address)?.TurnOnDelayEnabled ?? false;

    public bool SetTurnOnDelayEnabled(string address, bool enabled) =>
        Update(address, device => device with { TurnOnDelayEnabled = enabled });

    public int GetTurnOnDelaySeconds(string address) =>
        GetByAddress(address)?.TurnOnDelaySeconds is { } seconds && seconds is >= 1 and <= 86400
            ? seconds
            : 60;

    public bool SetTurnOnDelaySeconds(string address, int seconds)
    {
        if (seconds is < 1 or > 86400)
            return false;

        return Update(address, device => device with { TurnOnDelaySeconds = seconds });
    }

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
        Func<SavedDevice, SavedDevice> updateDevice,
        bool notifyDevicesChanged = true)
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

        if (updated && notifyDevicesChanged)
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

                // Older versions persisted a Chinese UI label as the unknown state.
                if (device.FirmwareVersion == "未知")
                {
                    device = device with { FirmwareVersion = SavedDevice.UnknownFirmwareVersion };
                    migrated = true;
                }
                else if (string.IsNullOrWhiteSpace(device.FirmwareVersion))
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
