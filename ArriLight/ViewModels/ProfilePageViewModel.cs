using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ArriLight.ViewModels;

public partial class ProfilePageViewModel : ViewModelBase
{
    private readonly IAppNavigator _navigator;
    public ProfilePageViewModel(IAppNavigator navigator) { _navigator = navigator; }

    [RelayCommand]
    private async Task MessageCenterAsync() =>
        await _navigator.GoToAsync("MessageCenterPage");

    [RelayCommand]
    private async Task AppVersionAsync() =>
        await _navigator.GoToAsync("AppVersionPage");

    [RelayCommand]
    private async Task AboutUsAsync() =>
        await _navigator.GoToAsync("AboutUsPage");

    [RelayCommand]
    private async Task SettingsAsync() =>
        await _navigator.GoToAsync("SettingsPage");
}
