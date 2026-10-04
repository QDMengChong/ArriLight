using System.Globalization;

namespace ArriLight;

public interface ILocalizationService
{
    CultureInfo Culture { get; }
    string SelectedLanguage { get; }
    bool RequiresRestart { get; }
    event EventHandler? CultureChanged;
    event EventHandler? LanguagePreferenceChanged;
    string GetString(string resourceKey);
    string Format(string resourceKey, params object?[] arguments);
    void Initialize(string language, CultureInfo systemCulture);
    void SaveLanguagePreference(string language);
}
