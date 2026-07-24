namespace ArriLight;

public static class ThemePreference
{
    private const string PreferenceKey = "app_theme";

    public static AppTheme Load()
    {
        var savedValue = Preferences.Default.Get(PreferenceKey, nameof(AppTheme.Unspecified));
        return Enum.TryParse<AppTheme>(savedValue, out var theme)
            ? theme
            : AppTheme.Unspecified;
    }

    public static void Save(AppTheme theme) =>
        Preferences.Default.Set(PreferenceKey, theme.ToString());
}
