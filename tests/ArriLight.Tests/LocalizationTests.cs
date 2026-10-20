using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace ArriLight.Tests;

public sealed class LocalizationTests : IDisposable
{
    private readonly CultureInfo _originalUiCulture = CultureInfo.CurrentUICulture;
    private readonly CultureInfo? _originalDefaultUiCulture = CultureInfo.DefaultThreadCurrentUICulture;

    public LocalizationTests() => Preferences.Default.Clear();

    public void Dispose()
    {
        Preferences.Default.Clear();
        Localization.Current.Initialize(
            LanguagePreference.System, CultureInfo.GetCultureInfo("zh-CN"));
        CultureInfo.CurrentUICulture = _originalUiCulture;
        CultureInfo.DefaultThreadCurrentUICulture = _originalDefaultUiCulture;
    }

    [Fact]
    public void StartupRestoresSavedLanguageBeforeStaticResourcesAreRead()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
        LanguagePreference.Save(LanguagePreference.English);

        Localization.Initialize();

        Assert.Equal("en", CultureInfo.CurrentUICulture.Name);
        Assert.Equal("en", CultureInfo.DefaultThreadCurrentUICulture!.Name);
        Assert.Equal("Devices", AppResources.Tab_Devices);
        Assert.Equal("User guide", AppResources.Manual_Title);
    }

    [Theory]
    [InlineData("en-US", "Devices")]
    [InlineData("fr-FR", "Devices")]
    [InlineData("ja-JP", "Devices")]
    [InlineData("zh-CN", "设备")]
    [InlineData("zh-TW", "设备")]
    public void StartupWithSystemPreferenceUsesCurrentSystemUiCulture(string system, string expected)
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(system);

        Localization.Initialize();

        Assert.Equal(LanguagePreference.System, Localization.Current.SelectedLanguage);
        Assert.Equal(expected, AppResources.Tab_Devices);
    }

    [Fact]
    public void ReopeningWindowAppliesPendingLanguageAndKeepsOriginalSystemLanguage()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
        Localization.Initialize();
        Localization.Current.SaveLanguagePreference(LanguagePreference.English);

        Assert.Equal("设备", AppResources.Tab_Devices);
        Localization.ApplySavedPreferenceForNewWindow();
        Assert.Equal("Devices", AppResources.Tab_Devices);
        Assert.False(Localization.Current.RequiresRestart);

        Localization.Current.SaveLanguagePreference(LanguagePreference.System);
        Assert.Equal("Devices", AppResources.Tab_Devices);
        Localization.ApplySavedPreferenceForNewWindow();
        Assert.Equal("设备", AppResources.Tab_Devices);
        Assert.False(Localization.Current.RequiresRestart);
    }

    [Theory]
    [InlineData("en-US", "en")]
    [InlineData("en-GB", "en")]
    [InlineData("", "en")]
    [InlineData("fr-FR", "en")]
    [InlineData("ja-JP", "en")]
    [InlineData("de-DE", "en")]
    [InlineData("ar-SA", "en")]
    [InlineData("zh", "zh-Hans")]
    [InlineData("zh-Hans", "zh-Hans")]
    [InlineData("zh-Hant", "zh-Hans")]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("zh-TW", "zh-Hans")]
    [InlineData("zh-HK", "zh-Hans")]
    [InlineData("zh-SG", "zh-Hans")]
    public void FollowSystemUsesChineseForChineseCulturesAndEnglishForAllOthers(string system, string expected)
    {
        var service = new LocalizationService();
        service.Initialize(LanguagePreference.System, CultureInfo.GetCultureInfo(system));

        Assert.Equal(expected, service.Culture.Name);
        Assert.Equal(LanguagePreference.System, service.SelectedLanguage);
        Assert.False(service.RequiresRestart);
    }

    [Fact]
    public void NewServiceDefaultsToEnglishBeforeInitialization()
    {
        var service = new LocalizationService();

        Assert.Equal("en", service.Culture.Name);
        Assert.Equal("Devices", service.GetString(nameof(AppResources.Tab_Devices)));
        Assert.Equal(LanguagePreference.System, service.SelectedLanguage);
        Assert.False(service.RequiresRestart);
    }

    [Theory]
    [InlineData("en", "zh-CN", "Devices")]
    [InlineData("en", "zh-TW", "Devices")]
    [InlineData("zh-Hans", "en-US", "设备")]
    [InlineData("zh-Hans", "fr-FR", "设备")]
    public void ExplicitSelectionOverridesSystem(string selected, string system, string expected)
    {
        var service = new LocalizationService();
        service.Initialize(selected, CultureInfo.GetCultureInfo(system));

        Assert.Equal(expected, service.GetString(nameof(AppResources.Tab_Devices)));
        Assert.Equal(selected, service.Culture.Name);
        Assert.Equal(selected, service.SelectedLanguage);
    }

    [Fact]
    public void SavedSelectionIsAppliedOnlyByNextStartup()
    {
        var running = new LocalizationService();
        running.Initialize(LanguagePreference.SimplifiedChinese, CultureInfo.GetCultureInfo("en-US"));
        var preferenceChanges = 0;
        var cultureChanges = 0;
        running.LanguagePreferenceChanged += (_, _) => preferenceChanges++;
        running.CultureChanged += (_, _) => cultureChanges++;

        running.SaveLanguagePreference(LanguagePreference.English);
        running.SaveLanguagePreference(LanguagePreference.English);

        Assert.Equal("en", LanguagePreference.Load());
        Assert.Equal("en", running.SelectedLanguage);
        Assert.Equal("zh-Hans", running.Culture.Name);
        Assert.Equal("设备", running.GetString(nameof(AppResources.Tab_Devices)));
        Assert.True(running.RequiresRestart);
        Assert.Equal(1, preferenceChanges);
        Assert.Equal(0, cultureChanges);

        var restarted = new LocalizationService();
        restarted.Initialize(LanguagePreference.Load(), CultureInfo.GetCultureInfo("zh-CN"));
        Assert.Equal("Devices", restarted.GetString(nameof(AppResources.Tab_Devices)));
        Assert.False(restarted.RequiresRestart);
    }

    [Fact]
    public void SelectingAppliedPreferenceAgainRemovesRestartNotice()
    {
        var service = new LocalizationService();
        service.Initialize(LanguagePreference.System, CultureInfo.GetCultureInfo("zh-CN"));
        service.SaveLanguagePreference(LanguagePreference.English);
        service.SaveLanguagePreference(LanguagePreference.System);

        Assert.False(service.RequiresRestart);
        Assert.Equal(LanguagePreference.System, LanguagePreference.Load());
    }

    [Fact]
    public void InvalidStoredPreferenceFallsBackToSystem()
    {
        Preferences.Default.Set("app_language", "invalid-language");
        Assert.Equal(LanguagePreference.System, LanguagePreference.Load());
        Assert.Throws<ArgumentException>(() => LanguagePreference.Save("fr"));
        Assert.Equal(LanguagePreference.System, LanguagePreference.Load());
    }

    [Fact]
    public void FormattingUsesUiLanguageWithoutChangingSystemNumberFormat()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var service = new LocalizationService();
            service.Initialize(LanguagePreference.English, CultureInfo.GetCultureInfo("zh-CN"));

            Assert.Equal("Transferring firmware… 12.5%",
                service.Format(nameof(AppResources.Firmware_TransferProgress), 12.5));
            Assert.Equal("Delete the “Turn on” schedule at 07:30?",
                service.Format(nameof(AppResources.Schedule_DeleteConfirmation), "07:30", "Turn on"));
            Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void EnglishResourcesHaveEveryDefaultKeyAndMatchingFormatArguments()
    {
        var chinese = AppResources.ResourceManager.GetResourceSet(
            CultureInfo.GetCultureInfo("zh-Hans"), true, true)!;
        var english = AppResources.ResourceManager.GetResourceSet(
            CultureInfo.GetCultureInfo("en"), true, true)!;
        var zh = chinese.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
        var en = english.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);

        Assert.Equal(zh.Keys.Order(), en.Keys.Order());
        Assert.Equal("Devices", en[nameof(AppResources.Tab_Devices)]);
        Assert.Equal("设备", zh[nameof(AppResources.Tab_Devices)]);
        foreach (var (key, value) in zh)
        {
            Assert.False(string.IsNullOrWhiteSpace(en[key]), key);
            Assert.Equal(
                CompositeFormat.Parse(value).MinimumArgumentCount,
                CompositeFormat.Parse(en[key]).MinimumArgumentCount);
            var indices = @"(?<!\{)\{(\d+)(?:,[^}:]+)?(?::[^}]+)?\}(?!\})";
            Assert.Equal(
                Regex.Matches(value, indices).Select(m => m.Groups[1].Value).Order(),
                Regex.Matches(en[key], indices).Select(m => m.Groups[1].Value).Order());
        }
    }

    [Fact]
    public void UnknownResourceKeyHasReadableFallback() =>
        Assert.Equal("Missing_Key", new LocalizationService().GetString("Missing_Key"));

    [Theory]
    [InlineData("en", "Monday, Tuesday, Wednesday")]
    [InlineData("zh-Hans", "Monday、Tuesday、Wednesday")]
    public void ListFormattingKeepsLanguageSpacingWithoutResourceBoundaryWhitespace(
        string language, string expected)
    {
        Localization.Current.Initialize(language, CultureInfo.GetCultureInfo("zh-CN"));

        Assert.Equal(expected, Localization.JoinList(["Monday", "Tuesday", "Wednesday"]));
        Assert.Equal(string.Empty, Localization.JoinList([]));
        Assert.Equal("Monday", Localization.JoinList(["Monday"]));
        Assert.Equal(AppResources.Common_ListJoinFormat.Trim(), AppResources.Common_ListJoinFormat);
    }

    [Fact]
    public void LegacyUnknownFirmwareMigratesWithoutTranslatingStoredUserData()
    {
        Preferences.Default.Set("saved_bluetooth_devices_v1", """
            [{"Name":"我的灯","Address":"AA:BB:CC:DD:EE:FF","FirmwareVersion":"未知"}]
            """);
        Localization.Current.Initialize(LanguagePreference.English, CultureInfo.GetCultureInfo("zh-CN"));

        var saved = new DeviceRepository().GetByAddress("AA:BB:CC:DD:EE:FF")!;

        Assert.Equal(SavedDevice.UnknownFirmwareVersion, saved.FirmwareVersion);
        Assert.False(saved.HasKnownFirmwareVersion);
        Assert.Equal("我的灯", saved.Name);
        Assert.Equal("我的灯", saved.DisplayName);
        Assert.DoesNotContain("未知", Preferences.Default.Get("saved_bluetooth_devices_v1", ""));
        var json = JsonSerializer.Serialize(saved);
        Assert.DoesNotContain("DisplayName", json);
        Assert.DoesNotContain("HasKnownFirmwareVersion", json);
    }

    [Fact]
    public void DemoDefaultsTranslateWhileCustomDeviceNamesArePreserved()
    {
        Localization.Current.Initialize(LanguagePreference.English, CultureInfo.GetCultureInfo("zh-CN"));
        var demo = new SavedDevice("客厅主灯", "02:00:00:00:00:01", IsDemo: true);

        Assert.Equal("Living room main light", demo.DisplayName);
        Assert.Equal("客厅主灯", demo.Name);
        Assert.Equal("我的灯", (demo with { Name = "我的灯" }).DisplayName);
        Assert.Equal("客厅主灯", (demo with { IsDemo = false }).DisplayName);
        Assert.Equal("English", AppResources.Settings_EnglishLanguage);
        Assert.Equal("简体中文", AppResources.Settings_ChineseLanguage);
    }
}
