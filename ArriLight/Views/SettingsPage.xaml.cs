namespace ArriLight;

public partial class SettingsPage : ContentPage
{
    private bool _isInitializing;
    private bool _isShowingLanguageNotice;
    private readonly ILocalizationService _localization;

    public SettingsPage()
    {
        InitializeComponent();
        _localization = ServiceHelper.GetRequiredService<ILocalizationService>();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _isInitializing = true;

        var theme = ThemePreference.Load();
        SystemThemeRadio.IsChecked = theme == AppTheme.Unspecified;
        LightThemeRadio.IsChecked = theme == AppTheme.Light;
        DarkThemeRadio.IsChecked = theme == AppTheme.Dark;

        var language = _localization.SelectedLanguage;
        SystemLanguageRadio.IsChecked = language == LanguagePreference.System;
        ChineseLanguageRadio.IsChecked = language == LanguagePreference.SimplifiedChinese;
        EnglishLanguageRadio.IsChecked = language == LanguagePreference.English;
        LanguageRestartNotice.IsVisible = _localization.RequiresRestart;
        _isInitializing = false;
    }

    private async void OnLanguageChecked(object? sender, CheckedChangedEventArgs e)
    {
        if (_isInitializing || !e.Value || sender is not RadioButton { Value: string language })
            return;

        await ChangeLanguageAsync(language);
    }

    private async Task ChangeLanguageAsync(string language)
    {
        if (_localization.SelectedLanguage == language)
            return;

        _localization.SaveLanguagePreference(language);
        LanguageRestartNotice.IsVisible = _localization.RequiresRestart;
        if (!_localization.RequiresRestart || _isShowingLanguageNotice)
            return;

        _isShowingLanguageNotice = true;
        try
        {
            await DisplayAlertAsync(
                AppResources.Settings_Language,
                AppResources.Settings_LanguageRestartRequired,
                AppResources.Common_Ok);
        }
        finally
        {
            _isShowingLanguageNotice = false;
        }
    }

    private void OnSystemThemeChecked(object? sender, CheckedChangedEventArgs e)
    {
        if (e.Value)
            ApplyTheme(AppTheme.Unspecified);
    }

    private void OnLightThemeChecked(object? sender, CheckedChangedEventArgs e)
    {
        if (e.Value)
            ApplyTheme(AppTheme.Light);
    }

    private void OnDarkThemeChecked(object? sender, CheckedChangedEventArgs e)
    {
        if (e.Value)
            ApplyTheme(AppTheme.Dark);
    }

    private void ApplyTheme(AppTheme theme)
    {
        if (_isInitializing || Application.Current is null)
            return;

        Application.Current.UserAppTheme = theme;
        ThemePreference.Save(theme);
    }

}
