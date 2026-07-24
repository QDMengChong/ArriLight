namespace ArriLight;

public partial class SettingsPage : ContentPage
{
    private bool _isInitializing;

    public SettingsPage()
    {
        InitializeComponent();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _isInitializing = true;

        var theme = ThemePreference.Load();
        SystemThemeRadio.IsChecked = theme == AppTheme.Unspecified;
        LightThemeRadio.IsChecked = theme == AppTheme.Light;
        DarkThemeRadio.IsChecked = theme == AppTheme.Dark;

        _isInitializing = false;
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
