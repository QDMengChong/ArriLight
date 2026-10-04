namespace ArriLight;

public static class LanguagePreference
{
    public const string System = "system";
    public const string SimplifiedChinese = "zh-Hans";
    public const string English = "en";
    private const string PreferenceKey = "app_language";

    public static string Load()
    {
        var value = Preferences.Default.Get(PreferenceKey, System);
        return IsSupported(value) ? value : System;
    }

    public static void Save(string language)
    {
        if (!IsSupported(language))
            throw new ArgumentException("Unsupported language preference.", nameof(language));
        Preferences.Default.Set(PreferenceKey, language);
    }

    public static bool IsSupported(string? language) =>
        language is System or SimplifiedChinese or English;
}
