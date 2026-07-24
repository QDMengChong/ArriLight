namespace ArriLight;

public partial class ProfilePage : ContentPage
{
    public ProfilePage()
    {
        InitializeComponent();
    }

    private async void OnMessageCenterTapped(object? sender, TappedEventArgs e) =>
        await Shell.Current.GoToAsync(nameof(MessageCenterPage));

    private async void OnAppVersionTapped(object? sender, TappedEventArgs e) =>
        await Shell.Current.GoToAsync(nameof(AppVersionPage));

    private async void OnAboutUsTapped(object? sender, TappedEventArgs e) =>
        await Shell.Current.GoToAsync(nameof(AboutUsPage));

    private async void OnSettingsTapped(object? sender, TappedEventArgs e) =>
        await Shell.Current.GoToAsync(nameof(SettingsPage));
}
