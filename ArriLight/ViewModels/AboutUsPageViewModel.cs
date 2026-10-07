using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ArriLight.ViewModels;

public partial class AboutUsPageViewModel : ViewModelBase
{
    private const string ContactPhoneNumber = "4000000000";

    private readonly IAppNavigator _navigator;
    private readonly IUserInteraction _interaction;
    public AboutUsPageViewModel(IAppNavigator navigator, IUserInteraction interaction)
    { _navigator = navigator; _interaction = interaction; }

    [RelayCommand]
    private async Task UserAgreementAsync() =>
        await _navigator.GoToAsync($"LegalDocumentPage?kind=agreement");

    [RelayCommand]
    private async Task PrivacyPolicyAsync() =>
        await _navigator.GoToAsync($"LegalDocumentPage?kind=privacy");

    [RelayCommand]
    private async Task OpenSourceLicensesAsync() =>
        await _navigator.GoToAsync("OpenSourceLicensesPage");

    [RelayCommand]
    private async Task WeChatAsync()
    {
        try
        {
            var opened = await _interaction.OpenUriAsync("weixin://");
            if (!opened)
                await _interaction.AlertAsync(AppResources.About_WeChatUnavailable, AppResources.About_InstallWeChat, AppResources.Common_Ok);
        }
        catch
        {
            await _interaction.AlertAsync(AppResources.About_WeChatUnavailable, AppResources.About_InstallWeChat, AppResources.Common_Ok);
        }
    }

    [RelayCommand]
    private async Task ContactUsAsync()
    {
        try
        {
            if (!await _interaction.OpenUriAsync($"tel:{ContactPhoneNumber}"))
                throw new InvalidOperationException("Dialer unavailable.");
        }
        catch
        {
            await _interaction.AlertAsync(AppResources.About_DialerUnavailable, Localization.Format(nameof(AppResources.About_CallManually), ContactPhoneNumber), AppResources.Common_Ok);
        }
    }
}
