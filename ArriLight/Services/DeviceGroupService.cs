using System.Collections.Concurrent;

namespace ArriLight;

/// <summary>Owns dispatch lifetime; pages own only their view and pending slider input.</summary>
public sealed class DeviceGroupService
{
    private readonly DeviceRepository _devices;
    private readonly IDeviceGroupBackend _backend;
    private readonly ConcurrentDictionary<(string Group, string Address), (Guid Id, string Text)> _results = new();
    private readonly ConcurrentDictionary<string, long> _lightingVersions = new();
    private long _sequence;
    public DeviceGroupRepository Repository { get; }
    public event EventHandler? Changed;

    public DeviceGroupService(DeviceGroupRepository repository, DeviceRepository devices, IDeviceGroupBackend backend)
    {
        Repository = repository; _devices = devices; _backend = backend;
        repository.GroupsChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        devices.DevicesChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        backend.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<DeviceGroupMemberSnapshot> GetMembers(SavedDeviceGroup group) => group.Members
        .Select(_devices.GetByAddress).Where(device => device is not null).Select(device => _backend.Read(device!)).ToArray();
    public IReadOnlyList<DeviceGroupMemberSnapshot> GetCandidates(SavedDeviceGroup group) => _devices.GetAll()
        .Where(device => !group.Members.Contains(device.Address, StringComparer.OrdinalIgnoreCase)).Select(_backend.Read).ToArray();
    public string? GetLastResult(string groupId, string address) => _results.GetValueOrDefault((groupId, address)).Text;

    public void AddMember(string groupId, string address)
    {
        var group = RequireGroup(groupId);
        var device = _devices.GetByAddress(address) ?? throw new InvalidOperationException(AppResources.Device_Removed);
        if (group.Members.Contains(address, StringComparer.OrdinalIgnoreCase)) return;
        var candidate = _backend.Read(device);
        var reference = GetMembers(group).FirstOrDefault() ?? candidate;
        var issue = DeviceGroupCompatibility.GetIssue(reference, candidate);
        if (issue is not null) throw new InvalidOperationException(issue);
        Repository.SetMembers(groupId, group.Members.Append(address));
    }

    public void MoveMember(string groupId, string address, int offset)
    {
        var group = RequireGroup(groupId);
        var members = group.Members.ToList();
        var index = members.FindIndex(a => string.Equals(a, address, StringComparison.OrdinalIgnoreCase));
        var target = index + offset;
        if (index < 0 || target < 0 || target >= members.Count) return;
        (members[index], members[target]) = (members[target], members[index]);
        var reordered = GetMembers(group with { Members = members });
        foreach (var member in reordered)
            if (DeviceGroupCompatibility.GetIssue(reordered[0], member) is { } issue)
                throw new InvalidOperationException(issue);
        Repository.SetMembers(groupId, members);
    }

    public void RemoveMember(string groupId, string address)
    {
        var group = RequireGroup(groupId);
        Repository.SetMembers(groupId, group.Members.Where(a => !string.Equals(a, address, StringComparison.OrdinalIgnoreCase)));
    }

    public DeviceGroupDispatch Submit(string groupId, DeviceGroupCommand command)
    {
        command.Validate();
        var group = RequireGroup(groupId);
        var members = GetMembers(group);
        var reference = members.FirstOrDefault() ?? throw new InvalidOperationException(AppResources.Group_NoMembers);
        if (reference.Capabilities is not { } capabilities || !command.IsSupportedBy(capabilities))
            throw new InvalidOperationException(AppResources.Group_Incompatible);
        if (command.Kind == DeviceGroupCommandKind.Lighting && command.ColorModel == ArriOpto.BLE.Agp.AgpColorModel.Ctl &&
            (reference.CtlRange is not { } range || command.TemperatureKelvin < range.TemperatureMinimumKelvin || command.TemperatureKelvin > range.TemperatureMaximumKelvin))
            throw new ArgumentException(AppResources.Group_InvalidParameters);

        var targets = members.Where(member => member.ConnectionGeneration is not null &&
            !member.IsLocked && DeviceGroupCompatibility.GetIssue(reference, member) is null).ToArray();
        if (targets.Length == 0) throw new InvalidOperationException(AppResources.Group_NoConnectedMembers);
        var lightingVersion = command.Kind == DeviceGroupCommandKind.Lighting ? Interlocked.Increment(ref _sequence) : 0;
        if (lightingVersion != 0) _lightingVersions[groupId] = lightingVersion;
        var tasks = targets.Select(member => ExecuteMemberAsync(groupId, member, command, lightingVersion)).ToArray();
        return new(targets.Length, members.Count - targets.Length, Task.WhenAll(tasks));
    }

    private async Task ExecuteMemberAsync(string groupId, DeviceGroupMemberSnapshot member, DeviceGroupCommand command, long lightingVersion)
    {
        var membershipVersion = Repository.GetMembershipVersion(groupId, member.Device.Address);
        var key = (groupId, member.Device.Address);
        var id = Guid.NewGuid();
        _results[key] = (id, AppResources.Group_Submitted);
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            // Only membership is checked again. Newly added/connected devices never join an old dispatch.
            await _backend.ExecuteAsync(member, command,
                () => membershipVersion != 0 && Repository.GetMembershipVersion(groupId, member.Device.Address) == membershipVersion &&
                    (lightingVersion == 0 || _lightingVersions.GetValueOrDefault(groupId) == lightingVersion),
                CancellationToken.None);
            SetResult(AppResources.Group_DeviceConfirmed);
        }
        catch (OperationCanceledException) { SetResult(AppResources.Group_DeviceSkipped); }
        catch (Exception exception) { SetResult(exception.Message); }

        void SetResult(string text)
        {
            if (_results.TryGetValue(key, out var previous) && previous.Id == id)
                _results.TryUpdate(key, (id, text), previous);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private SavedDeviceGroup RequireGroup(string id) => Repository.Get(id)
        ?? throw new InvalidOperationException(AppResources.Group_Removed);
}
