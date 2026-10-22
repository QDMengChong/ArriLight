using System.Text.Json;

namespace ArriLight;

public sealed class DeviceGroupRepository
{
    private const string Key = "saved_device_groups_v1";
    private readonly object _sync = new();
    private readonly DeviceRepository _devices;
    private readonly Dictionary<(string Group, string Address), long> _membershipVersions = new();
    private long _membershipVersion;
    public event EventHandler? GroupsChanged;

    public DeviceGroupRepository(DeviceRepository devices)
    {
        _devices = devices;
        // Device deletion cleans references even if no group page is open.
        devices.DevicesChanged += OnDevicesChanged;
        PruneDeletedDevices();
    }

    public IReadOnlyList<SavedDeviceGroup> GetAll()
    {
        lock (_sync) return Read().Select(Copy).ToArray();
    }

    public SavedDeviceGroup? Get(string id) => GetAll().FirstOrDefault(group => group.Id == id);

    public long GetMembershipVersion(string id, string address)
    {
        var key = (id, address.ToUpperInvariant());
        lock (_sync)
        {
            if (!Read().Any(g => g.Id == id && g.Members.Contains(address, StringComparer.OrdinalIgnoreCase))) return 0;
            if (!_membershipVersions.TryGetValue(key, out var version))
                _membershipVersions[key] = version = ++_membershipVersion;
            return version;
        }
    }

    public SavedDeviceGroup Create(string name)
    {
        var group = new SavedDeviceGroup(Guid.NewGuid().ToString("N"), ValidateName(name), [], DateTimeOffset.UtcNow);
        lock (_sync) { var groups = Read(); groups.Add(group); Save(groups); }
        GroupsChanged?.Invoke(this, EventArgs.Empty);
        return group;
    }

    public bool Rename(string id, string name) => Update(id, group => group with { Name = ValidateName(name) });
    public bool SetMembers(string id, IEnumerable<string> addresses)
    {
        var members = addresses.Select(address => address.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (members.Any(address => !_devices.Contains(address)))
            throw new InvalidOperationException(AppResources.Device_Removed);
        return Update(id, group => group with { Members = members });
    }

    public bool SaveSettings(string id, SavedDeviceGroup settings) => Update(id, group => group with
    {
        CustomPresets = (settings.CustomPresets ?? []).Where(p => p.Slot is >= 0 and < 6)
            .GroupBy(p => p.Slot).Select(p => p.First()).ToArray(),
        BlinkPeriodMilliseconds = settings.BlinkPeriodMilliseconds,
        BreathePeriodMilliseconds = settings.BreathePeriodMilliseconds,
        TimerSeconds = Math.Clamp(settings.TimerSeconds, 1, 86400),
        AutoOffEnabled = settings.AutoOffEnabled,
        AutoOffSeconds = Math.Clamp(settings.AutoOffSeconds, 1, 86400)
    });

    public bool Delete(string id)
    {
        bool removed;
        lock (_sync) { var groups = Read(); removed = groups.RemoveAll(g => g.Id == id) > 0; if (removed) Save(groups); }
        if (removed) GroupsChanged?.Invoke(this, EventArgs.Empty);
        return removed;
    }

    private bool Update(string id, Func<SavedDeviceGroup, SavedDeviceGroup> update)
    {
        lock (_sync)
        {
            var groups = Read();
            var index = groups.FindIndex(group => group.Id == id);
            if (index < 0) return false;
            var previous = groups[index];
            groups[index] = Copy(update(previous));
            InvalidateMemberships(previous, groups[index]);
            Save(groups);
        }
        GroupsChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void OnDevicesChanged(object? sender, EventArgs args) => PruneDeletedDevices();
    private void PruneDeletedDevices()
    {
        var addresses = _devices.GetAll().Select(device => device.Address).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool changed = false;
        lock (_sync)
        {
            var groups = Read();
            for (var i = 0; i < groups.Count; i++)
            {
                var members = groups[i].Members.Where(addresses.Contains).ToArray();
                if (members.Length == groups[i].Members.Count) continue;
                var previous = groups[i];
                groups[i] = groups[i] with { Members = members };
                InvalidateMemberships(previous, groups[i]);
                changed = true;
            }
            if (changed) Save(groups);
        }
        if (changed) GroupsChanged?.Invoke(this, EventArgs.Empty);
    }

    private List<SavedDeviceGroup> Read()
    {
        try
        {
            return (JsonSerializer.Deserialize<List<SavedDeviceGroup>>(Preferences.Default.Get(Key, "[]")) ?? [])
                .Where(group => group is not null && !string.IsNullOrWhiteSpace(group.Id) &&
                    !string.IsNullOrWhiteSpace(group.Name) && group.Members is not null)
                .Select(group => group with { Members = group.Members.Where(a => !string.IsNullOrWhiteSpace(a))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray() }).ToList();
        }
        catch (JsonException) { return []; }
    }

    private static SavedDeviceGroup Copy(SavedDeviceGroup group) => group with
    { Members = group.Members.ToArray(), CustomPresets = group.CustomPresets?.ToArray() };
    private static void Save(List<SavedDeviceGroup> groups) => Preferences.Default.Set(Key, JsonSerializer.Serialize(groups));
    private static string ValidateName(string name) => !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= 24
        ? name.Trim() : throw new ArgumentException(AppResources.Group_InvalidName);

    private void InvalidateMemberships(SavedDeviceGroup previous, SavedDeviceGroup current)
    {
        foreach (var address in previous.Members.Except(current.Members, StringComparer.OrdinalIgnoreCase)
            .Concat(current.Members.Except(previous.Members, StringComparer.OrdinalIgnoreCase)))
            _membershipVersions[(previous.Id, address.ToUpperInvariant())] = ++_membershipVersion;
    }
}
