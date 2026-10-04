using System.Globalization;

namespace ArriLight;

// XAML resource properties and existing code-behind use this same singleton.
public static class Localization
{
    private static CultureInfo? _systemUiCulture;
    public static ILocalizationService Current { get; } = new LocalizationService();

    public static void Initialize()
    {
        _systemUiCulture = CultureInfo.CurrentUICulture;
        RestoreSavedLanguage();
    }

    internal static void ApplySavedPreferenceForNewWindow()
    {
        // Android can keep the process alive after its activity finishes.
        // Apply a pending selection before constructing the next window's UI.
        if (Current.RequiresRestart)
            RestoreSavedLanguage();
    }

    private static void RestoreSavedLanguage()
    {
        Current.Initialize(LanguagePreference.Load(), _systemUiCulture ?? CultureInfo.CurrentUICulture);
        CultureInfo.CurrentUICulture = Current.Culture;
        CultureInfo.DefaultThreadCurrentUICulture = Current.Culture;
        // Keep CurrentCulture unchanged: protocol and persistence formats
        // must not change merely because the UI language changes.
    }

    public static string GetString(string resourceKey) => Current.GetString(resourceKey);
    public static string Format(string resourceKey, params object?[] arguments) =>
        Current.Format(resourceKey, arguments);
}
