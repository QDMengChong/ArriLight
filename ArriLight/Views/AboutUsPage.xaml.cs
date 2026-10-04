namespace ArriLight;

public partial class AboutUsPage : ContentPage
{
    private const string ContactPhoneNumber = "4000000000";

    public AboutUsPage()
    {
        InitializeComponent();
    }

    private async void OnUserAgreementTapped(object? sender, TappedEventArgs e) =>
        await Shell.Current.GoToAsync($"{nameof(LegalDocumentPage)}?kind=agreement");

    private async void OnPrivacyPolicyTapped(object? sender, TappedEventArgs e) =>
        await Shell.Current.GoToAsync($"{nameof(LegalDocumentPage)}?kind=privacy");

    private async void OnOpenSourceLicensesTapped(object? sender, TappedEventArgs e) =>
        await Shell.Current.GoToAsync(nameof(OpenSourceLicensesPage));

    private async void OnWeChatTapped(object? sender, TappedEventArgs e)
    {
        try
        {
            var opened = await Launcher.Default.TryOpenAsync("weixin://");
            if (!opened)
                await DisplayAlertAsync(AppResources.About_WeChatUnavailable, AppResources.About_InstallWeChat, AppResources.Common_Ok);
        }
        catch
        {
            await DisplayAlertAsync(AppResources.About_WeChatUnavailable, AppResources.About_InstallWeChat, AppResources.Common_Ok);
        }
    }

    private async void OnContactUsTapped(object? sender, TappedEventArgs e)
    {
        try
        {
            await Launcher.Default.OpenAsync($"tel:{ContactPhoneNumber}");
        }
        catch
        {
            await DisplayAlertAsync(AppResources.About_DialerUnavailable, Localization.Format(nameof(AppResources.About_CallManually), ContactPhoneNumber), AppResources.Common_Ok);
        }
    }
}
