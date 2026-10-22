using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ArriLight.ViewModels;

public sealed partial class DeviceGroupMemberItem : ObservableObject
{
    public DeviceGroupMemberItem(SavedDevice device) => Device = new(device.DisplayName, device.Address,
        string.Empty, isDemo: device.IsDemo, protocol: device.EffectiveProtocol);

    // Share the home card's presentation rules, including protocol, battery and status colors.
    public LightDeviceItem Device { get; }
    public string Address => Device.Address;
    [ObservableProperty] private string _reference = string.Empty;
    [ObservableProperty] private string _detail = string.Empty;
    [ObservableProperty] private bool _isReference;
    [ObservableProperty] private bool _canMoveUp;
    [ObservableProperty] private bool _canMoveDown;

    public void Update(DeviceGroupMemberSnapshot member, int index, int count, string detail)
    {
        var runtime = member.Runtime;
        var availability = runtime?.Availability ?? (member.ConnectionGeneration is null
            ? DeviceRuntimeAvailability.Offline : DeviceRuntimeAvailability.Online);
        Device.IsOnline = availability is DeviceRuntimeAvailability.Online or DeviceRuntimeAvailability.Ready;
        Device.IsReady = availability == DeviceRuntimeAvailability.Ready;
        Device.Status = availability switch
        {
            DeviceRuntimeAvailability.Ready => AppResources.Device_Ready,
            DeviceRuntimeAvailability.Online => AppResources.Device_Online,
            _ when Device.IsMesh && runtime?.IsBusy == true => AppResources.Ble_Connecting,
            _ => AppResources.Device_Offline
        };
        Device.IsRuntimeBusy = runtime?.IsBusy == true;
        Device.HasPendingUserOperation = runtime?.HasPendingUserOperation == true;
        Device.IsLocked = member.IsLocked;
        Device.BatteryLevelPercent = Device.IsOnline ? member.BatteryLevelPercent : null;
        if (member.IsOn is { } on) Device.IsOn = on;
        IsReference = index == 0;
        Reference = IsReference ? AppResources.Group_Reference : string.Empty;
        Detail = detail;
        CanMoveUp = index > 0;
        CanMoveDown = index < count - 1;
    }
}

public partial class DeviceGroupMembersPageViewModel : ViewModelBase
{
    private readonly DeviceGroupService _groups;
    private readonly IUiDispatcher _dispatcher;
    private readonly IAppNavigator _navigator;
    private readonly IUserInteraction _interaction;
    private bool _visible;
    private bool _returnToControl;
    private bool _memberNavigationStarted;
    private bool _memberNavigationInFlight;
    public string GroupId { get; private set; } = string.Empty;
    [ObservableProperty] private string _groupName = string.Empty;
    public ObservableCollection<DeviceGroupMemberItem> Members { get; } = [];
    public DeviceGroupMembersPageViewModel(DeviceGroupService groups, IUiDispatcher dispatcher,
        IAppNavigator navigator, IUserInteraction interaction)
    { _groups = groups; _dispatcher = dispatcher; _navigator = navigator; _interaction = interaction; }
    public override void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("groupId", out var value)) GroupId = Uri.UnescapeDataString(value?.ToString() ?? "");
        _returnToControl = query.TryGetValue("returnToControl", out var returnValue) && returnValue?.ToString() == "true";
    }
    public override Task OnAppearingAsync()
    { _visible = true; _memberNavigationStarted = false; _groups.Changed -= OnChanged; _groups.Changed += OnChanged; Reload(); return Task.CompletedTask; }
    public override void OnDisappearing() { _visible = false; _groups.Changed -= OnChanged; }
    private void OnChanged(object? sender, EventArgs args) => _dispatcher.Post(() => { if (_visible) Reload(); });
    private void Reload()
    {
        if (_groups.Repository.Get(GroupId) is not { } group) { ErrorMessage = AppResources.Group_Removed; Members.Clear(); return; }
        GroupName = group.Name;
        var members = _groups.GetMembers(group);
        var existing = Members.ToDictionary(item => item.Address, StringComparer.OrdinalIgnoreCase);
        var rows = members.Select((member, index) =>
        {
            var row = existing.TryGetValue(member.Device.Address, out var previous) &&
                previous.Device.Name == member.Device.DisplayName && previous.Device.Protocol == member.Device.EffectiveProtocol &&
                previous.Device.IsDemo == member.Device.IsDemo ? previous : new DeviceGroupMemberItem(member.Device);
            row.Update(member, index, members.Count,
                string.Join(" · ", new[] { member.IsOn is { } on ? on ? AppResources.Common_TurnOn : AppResources.Common_TurnOff : AppResources.Common_Unknown,
                member.Brightness is { } brightness ? $"{brightness}%" : null,
                DeviceGroupCompatibility.GetIssue(members[0], member),
                _groups.GetLastResult(GroupId, member.Device.Address), member.Detail }.Where(text => !string.IsNullOrWhiteSpace(text))));
            return row;
        }).ToArray();
        if (Members.SequenceEqual(rows)) return;
        Members.Clear(); foreach (var row in rows) Members.Add(row);
    }
    [RelayCommand] private async Task AddAsync()
    {
        if (_groups.Repository.Get(GroupId) is not { } group) return;
        var reference = _groups.GetMembers(group).FirstOrDefault();
        var candidates = _groups.GetCandidates(group);
        if (candidates.Count == 0) { ErrorMessage = AppResources.Group_NoCandidates; return; }
        var labels = candidates.Select(candidate => $"{candidate.Device.DisplayName} ({candidate.Device.Address})" +
            (DeviceGroupCompatibility.GetIssue(reference ?? candidate, candidate) is { } issue ? $" · {issue}" : string.Empty)).ToArray();
        var selected = await _interaction.ActionSheetAsync(AppResources.Group_AddMember, AppResources.Common_Cancel, labels);
        var index = Array.IndexOf(labels, selected);
        if (index < 0) return;
        TryChange(() => _groups.AddMember(GroupId, candidates[index].Device.Address));
    }
    [RelayCommand] private void MoveUp(DeviceGroupMemberItem item) => TryChange(() => _groups.MoveMember(GroupId, item.Address, -1));
    [RelayCommand] private void MoveDown(DeviceGroupMemberItem item) => TryChange(() => _groups.MoveMember(GroupId, item.Address, 1));
    [RelayCommand] private void Remove(DeviceGroupMemberItem item) => TryChange(() => _groups.RemoveMember(GroupId, item.Address));
    [RelayCommand] private async Task ControlMemberAsync(DeviceGroupMemberItem item)
    {
        if (!_visible || _memberNavigationInFlight || _memberNavigationStarted) return;
        if (_groups.Repository.Get(GroupId) is not { } group) { ErrorMessage = AppResources.Group_Removed; return; }
        var member = _groups.GetMembers(group).FirstOrDefault(member =>
            string.Equals(member.Device.Address, item.Address, StringComparison.OrdinalIgnoreCase));
        if (member is null) { Reload(); return; }
        _memberNavigationInFlight = true;
        _memberNavigationStarted = true;
        ErrorMessage = string.Empty;
        try
        {
            var route = member.Device.IsDemo ? "HslCtlDeviceControlPage" : "DeviceControlPage";
            await _navigator.GoToAsync($"{route}?name={Uri.EscapeDataString(member.Device.DisplayName)}" +
                $"&address={Uri.EscapeDataString(member.Device.Address)}", animate: false);
        }
        catch (Exception exception)
        {
            _memberNavigationStarted = false;
            ErrorMessage = exception.Message;
        }
        finally { _memberNavigationInFlight = false; }
    }
    [RelayCommand] private async Task OpenControlAsync()
    {
        if (_groups.Repository.Get(GroupId) is not { } group) return;
        if (group.Members.Count == 0) { ErrorMessage = AppResources.Group_NoMembers; return; }
        await _navigator.GoToAsync(_returnToControl ? ".." : $"../DeviceControlPage?groupId={Uri.EscapeDataString(GroupId)}");
    }
    [RelayCommand] private async Task RenameAsync()
    {
        var name = await _interaction.PromptAsync(AppResources.Device_Rename, AppResources.Group_NamePrompt,
            AppResources.Common_Save, AppResources.Common_Cancel, initialValue: GroupName, maxLength: 24);
        if (!string.IsNullOrWhiteSpace(name)) TryChange(() => _groups.Repository.Rename(GroupId, name));
    }
    [RelayCommand] private async Task DeleteAsync()
    {
        if (!await _interaction.ConfirmAsync(AppResources.Group_Delete, AppResources.Group_DeleteConfirmation,
            AppResources.Common_Delete, AppResources.Common_Cancel)) return;
        _groups.Repository.Delete(GroupId);
        await _navigator.GoToAsync("//DevicesPage");
    }
    private void TryChange(Action change)
    { try { change(); ErrorMessage = string.Empty; } catch (Exception exception) { ErrorMessage = exception.Message; } }
}
