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

    private async void OnWeChatTapped(object? sender, TappedEventArgs e)
    {
        try
        {
            var opened = await Launcher.Default.TryOpenAsync("weixin://");
            if (!opened)
                await DisplayAlertAsync("无法打开微信", "请确认设备已安装微信。", "知道了");
        }
        catch
        {
            await DisplayAlertAsync("无法打开微信", "请确认设备已安装微信。", "知道了");
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
            await DisplayAlertAsync("无法打开拨号", $"请手动拨打 {ContactPhoneNumber}。", "知道了");
        }
    }
}
