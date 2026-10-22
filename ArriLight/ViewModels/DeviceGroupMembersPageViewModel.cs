using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ArriLight.ViewModels;

public sealed record DeviceGroupMemberItem(string Address, string Name, string Reference, string Status,
    string Detail, bool CanMoveUp, bool CanMoveDown);

public partial class DeviceGroupMembersPageViewModel : ViewModelBase
{
    private readonly DeviceGroupService _groups;
    private readonly IUiDispatcher _dispatcher;
    private readonly IAppNavigator _navigator;
    private readonly IUserInteraction _interaction;
    private bool _visible;
    private bool _returnToControl;
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
    { _visible = true; _groups.Changed -= OnChanged; _groups.Changed += OnChanged; Reload(); return Task.CompletedTask; }
    public override void OnDisappearing() { _visible = false; _groups.Changed -= OnChanged; }
    private void OnChanged(object? sender, EventArgs args) => _dispatcher.Post(() => { if (_visible) Reload(); });
    private void Reload()
    {
        if (_groups.Repository.Get(GroupId) is not { } group) { ErrorMessage = AppResources.Group_Removed; Members.Clear(); return; }
        GroupName = group.Name;
        var members = _groups.GetMembers(group);
        var rows = members.Select((member, index) => new DeviceGroupMemberItem(member.Device.Address,
            member.Device.DisplayName, index == 0 ? AppResources.Group_Reference : string.Empty,
            (member.ConnectionGeneration is null ? AppResources.Device_Offline : AppResources.Device_Online) +
                (member.IsLocked ? " · " + AppResources.Device_Locked : string.Empty),
            string.Join(" · ", new[] { member.IsOn is { } on ? on ? AppResources.Common_TurnOn : AppResources.Common_TurnOff : AppResources.Common_Unknown,
                member.Brightness is { } brightness ? $"{brightness}%" : null,
                DeviceGroupCompatibility.GetIssue(members[0], member),
                _groups.GetLastResult(GroupId, member.Device.Address), member.Detail }.Where(text => !string.IsNullOrWhiteSpace(text))),
            index > 0, index < members.Count - 1)).ToArray();
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
    [RelayCommand] private async Task DetailsAsync(DeviceGroupMemberItem item) =>
        await _navigator.GoToAsync($"DeviceMorePage?address={Uri.EscapeDataString(item.Address)}");
    [RelayCommand] private async Task OpenControlAsync() =>
        await _navigator.GoToAsync(_returnToControl ? ".." : $"../DeviceGroupPage?groupId={Uri.EscapeDataString(GroupId)}");
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
