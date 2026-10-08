using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ArriLight.ViewModels;

public partial class SettingsPageViewModel(ILocalizationService localization,
    IApplicationPresentation application, IUserInteraction interaction,
    AgpLightControlService? agpLight = null) : ViewModelBase
{
    [ObservableProperty] private string _selectedLanguage = LanguagePreference.System;
    [ObservableProperty] private ApplicationTheme _selectedTheme;
    [ObservableProperty] private bool _requiresRestart;
    [ObservableProperty] private int _selectedConcurrencyIndex = 4;
    public IReadOnlyList<string> ConcurrencyOptions { get; } = ["1", "2", "3", "4", "5"];

    partial void OnSelectedConcurrencyIndexChanged(int value)
    {
        if (value is >= 0 and <= 4 && agpLight is not null)
            agpLight.RuntimeConcurrency = value + 1;
    }

    public override Task OnAppearingAsync()
    {
        SelectedLanguage = localization.SelectedLanguage;
        SelectedTheme = application.Theme;
        RequiresRestart = localization.RequiresRestart;
        SelectedConcurrencyIndex = (agpLight?.RuntimeConcurrency ?? 5) - 1;
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task ChangeLanguageAsync(string language)
    {
        if (localization.SelectedLanguage == language)
            return;
        localization.SaveLanguagePreference(language);
        SelectedLanguage = localization.SelectedLanguage;
        RequiresRestart = localization.RequiresRestart;
        if (RequiresRestart)
            await interaction.AlertAsync(AppResources.Settings_Language,
                AppResources.Settings_LanguageRestartRequired, AppResources.Common_Ok);
    }

    [RelayCommand]
    private void ChangeTheme(ApplicationTheme theme)
    {
        application.Theme = theme;
        SelectedTheme = theme;
    }
}
