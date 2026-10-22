using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;

namespace ArriLight.ViewModels;

public sealed record DeviceGroupListItem(string Id, string Name, string Summary);

public partial class MainPageViewModel
{
    private DeviceGroupService? _groups;
    public ObservableCollection<DeviceGroupListItem> Groups { get; } = [];

    private void InitializeGroups(DeviceGroupService? groups) { _groups = groups; ReloadGroups(); }
    private void StartGroups()
    {
        if (_groups is null) return;
        _groups.Changed -= OnGroupsChanged; _groups.Changed += OnGroupsChanged;
        ReloadGroups();
    }
    private void StopGroups() { if (_groups is not null) _groups.Changed -= OnGroupsChanged; }
    private void OnGroupsChanged(object? sender, EventArgs args) => _dispatcher.Post(() => { if (_isVisible) ReloadGroups(); });
    private void ReloadGroups()
    {
        if (_groups is null) return;
        var rows = _groups.Repository.GetAll().OrderByDescending(group => group.AddedAt).Select(group =>
        {
            var members = _groups.GetMembers(group);
            var reference = members.FirstOrDefault();
            var connected = reference is null ? 0 : members.Count(member => member.ConnectionGeneration is not null &&
                !member.IsLocked && DeviceGroupCompatibility.GetIssue(reference, member) is null);
            var summary = Localization.Format(nameof(AppResources.Group_ConnectedCount), connected, members.Count);
            if (reference is not null) summary += " · " + Localization.Format(nameof(AppResources.Group_ReferenceName), reference.Device.DisplayName);
            return new DeviceGroupListItem(group.Id, group.Name, summary);
        }).ToArray();
        if (Groups.SequenceEqual(rows)) return;
        Groups.Clear(); foreach (var row in rows) Groups.Add(row);
    }
    [RelayCommand] private async Task CreateGroupAsync()
    {
        if (_groups is null) return;
        var name = await _interaction.PromptAsync(AppResources.Group_Create, AppResources.Group_NamePrompt,
            AppResources.Common_Add, AppResources.Common_Cancel, maxLength: 24);
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            var group = _groups.Repository.Create(name);
            await NavigateGroupAsync($"DeviceGroupMembersPage?groupId={Uri.EscapeDataString(group.Id)}");
        }
        catch (Exception exception) { ErrorMessage = exception.Message; }
    }
    [RelayCommand] private async Task OpenGroupAsync(DeviceGroupListItem item) =>
        await NavigateGroupAsync($"DeviceGroupPage?groupId={Uri.EscapeDataString(item.Id)}");

    private async Task NavigateGroupAsync(string route)
    {
        if (!_isVisible || _controlNavigationInFlight || _controlNavigationStarted) return;
        _controlNavigationInFlight = true; _controlNavigationStarted = true;
        _statusQuerySuppressed = true;
        try
        {
            RequestStopStatusRefresh();
            _ = ObserveStatusRefreshCompletionAsync(_statusRefreshTask);
            await _navigator.GoToAsync(route);
        }
        catch (Exception exception)
        {
            _controlNavigationStarted = false;
            if (_isVisible) _statusQuerySuppressed = false;
            ErrorMessage = exception.Message; _interaction.ShowToast(exception.Message);
        }
        finally { _controlNavigationInFlight = false; }
    }
}
