using System.Globalization;

namespace ArriLight;

public sealed class LocalizationService : ILocalizationService
{
    private string _appliedPreference = LanguagePreference.System;

    public CultureInfo Culture { get; private set; } =
        CultureInfo.GetCultureInfo(LanguagePreference.English);
    public string SelectedLanguage { get; private set; } = LanguagePreference.System;
    public bool RequiresRestart => SelectedLanguage != _appliedPreference;
    public event EventHandler? CultureChanged;
    public event EventHandler? LanguagePreferenceChanged;

    public void Initialize(string language, CultureInfo systemCulture)
    {
        ArgumentNullException.ThrowIfNull(systemCulture);
        SelectedLanguage = LanguagePreference.IsSupported(language)
            ? language
            : LanguagePreference.System;
        _appliedPreference = SelectedLanguage;
        var requestedLanguage = SelectedLanguage == LanguagePreference.System
            ? systemCulture.TwoLetterISOLanguageName
            : SelectedLanguage;
        Culture = CultureInfo.GetCultureInfo(
            requestedLanguage is "zh" or LanguagePreference.SimplifiedChinese
                ? LanguagePreference.SimplifiedChinese
                : LanguagePreference.English);
        CultureChanged?.Invoke(this, EventArgs.Empty);
    }

    public string GetString(string resourceKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(resourceKey);
        return AppResources.ResourceManager.GetString(resourceKey, Culture) ?? resourceKey;
    }

    public string Format(string resourceKey, params object?[] arguments) =>
        string.Format(Culture, GetString(resourceKey), arguments);

    public void SaveLanguagePreference(string language)
    {
        LanguagePreference.Save(language);
        if (SelectedLanguage == language)
            return;
        SelectedLanguage = language;
        // Saving a selection never changes the running UI's language.
        LanguagePreferenceChanged?.Invoke(this, EventArgs.Empty);
    }
}
