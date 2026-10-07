namespace ArriLight;

public partial class SettingsPage : ContentPage
{
    public SettingsPageViewModel ViewModel { get; }
    public SettingsPage() : this(ServiceHelper.GetRequiredService<SettingsPageViewModel>()) { }
    public SettingsPage(SettingsPageViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        BindingContext = viewModel;
    }
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await ViewModel.OnAppearingAsync();
        RestoreSelection();
    }
    protected override void OnDisappearing()
    {
        ViewModel.OnDisappearing();
        base.OnDisappearing();
    }
    private bool _isInitializing;
    protected void RestoreSelection()
    {
        _isInitializing = true;
        SystemThemeRadio.IsChecked = ViewModel.SelectedTheme == ApplicationTheme.System;
        LightThemeRadio.IsChecked = ViewModel.SelectedTheme == ApplicationTheme.Light;
        DarkThemeRadio.IsChecked = ViewModel.SelectedTheme == ApplicationTheme.Dark;
        SystemLanguageRadio.IsChecked = ViewModel.SelectedLanguage == LanguagePreference.System;
        ChineseLanguageRadio.IsChecked = ViewModel.SelectedLanguage == LanguagePreference.SimplifiedChinese;
        EnglishLanguageRadio.IsChecked = ViewModel.SelectedLanguage == LanguagePreference.English;
        _isInitializing = false;
    }
    private void OnLanguageChecked(object? sender, CheckedChangedEventArgs e)
    {
        if (!_isInitializing && e.Value && sender is RadioButton { Value: string language })
            ViewModel.ChangeLanguageCommand.Execute(language);
    }
    private void OnSystemThemeChecked(object? sender, CheckedChangedEventArgs e) => SetTheme(e, ApplicationTheme.System);
    private void OnLightThemeChecked(object? sender, CheckedChangedEventArgs e) => SetTheme(e, ApplicationTheme.Light);
    private void OnDarkThemeChecked(object? sender, CheckedChangedEventArgs e) => SetTheme(e, ApplicationTheme.Dark);
    private void SetTheme(CheckedChangedEventArgs e, ApplicationTheme theme)
    {
        if (!_isInitializing && e.Value)
            ViewModel.ChangeThemeCommand.Execute(theme);
    }

}
