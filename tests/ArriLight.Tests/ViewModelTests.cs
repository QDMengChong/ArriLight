using System.Globalization;
using ArriOpto.BLE.Agp;
using Xunit;

namespace ArriLight.Tests;

public sealed class ViewModelTests
{
    [Fact]
    public async Task LanguageCommandSavesChoiceAndShowsNoticeWithoutChangingRunningLanguage()
    {
        Preferences.Default.Clear();
        var localization = new LocalizationService();
        localization.Initialize(LanguagePreference.SimplifiedChinese, CultureInfo.GetCultureInfo("zh-CN"));
        var interaction = new RecordingInteraction();
        var model = new SettingsPageViewModel(localization, new TestApplicationPresentation(), interaction);
        await model.OnAppearingAsync();
        try
        {
            await model.ChangeLanguageCommand.ExecuteAsync(LanguagePreference.English);
            Assert.Equal(LanguagePreference.English, LanguagePreference.Load());
            Assert.Equal(LanguagePreference.English, model.SelectedLanguage);
            Assert.Equal("zh-Hans", localization.Culture.Name);
            Assert.True(model.RequiresRestart);
            Assert.Single(interaction.Alerts);
            await model.ChangeLanguageCommand.ExecuteAsync(LanguagePreference.English);
            Assert.Single(interaction.Alerts);
            await model.ChangeLanguageCommand.ExecuteAsync(LanguagePreference.SimplifiedChinese);
            Assert.False(model.RequiresRestart);
            Assert.Single(interaction.Alerts);
        }
        finally { Localization.Initialize(); }
    }

    [Fact]
    public async Task ThemeCommandRestoresAndUpdatesApplicationPresentation()
    {
        var application = new TestApplicationPresentation { Theme = ApplicationTheme.Dark };
        var model = new SettingsPageViewModel(new LocalizationService(), application, new RecordingInteraction());
        await model.OnAppearingAsync();
        Assert.Equal(ApplicationTheme.Dark, model.SelectedTheme);
        model.ChangeThemeCommand.Execute(ApplicationTheme.Light);
        Assert.Equal(ApplicationTheme.Light, application.Theme);
        Assert.Equal(ApplicationTheme.Light, model.SelectedTheme);
    }

    [Fact]
    public async Task ControlViewModelRestoresCapabilitiesAndEditsDelayWithoutSendingBle()
    {
        await using var fixture = new PairingFixture();
        fixture.Repository.SetCapabilities(fixture.Address, AgpCapabilities.Lightness | AgpCapabilities.RelativeTimer);
        fixture.Repository.SetTurnOnDelayEnabled(fixture.Address, true);
        fixture.Repository.SetTurnOnDelaySeconds(fixture.Address, 120);
        var model = ControlModel(fixture);
        await model.OnAppearingAsync();
        try
        {
            Assert.True(model.HasKnownCapabilities);
            Assert.True(model.SupportsBrightness);
            Assert.False(model.SupportsColor);
            Assert.True(model.IsTurnOnDelayEnabled);
            Assert.Equal("120", model.TurnOnDelaySecondsText);
            model.TurnOnDelaySecondsText = "300";
            Assert.Equal(300, fixture.Repository.GetTurnOnDelaySeconds(fixture.Address));
            model.TurnOnDelaySecondsText = "0";
            Assert.Equal(300, fixture.Repository.GetTurnOnDelaySeconds(fixture.Address));
            Assert.Empty(fixture.Ble.Requests);
            model.AutomaticConnectionPending = true;
            Assert.False(model.CanRefreshStatus);
            model.AutomaticConnectionPending = false;
            Assert.True(model.CanRefreshStatus);
        }
        finally { model.OnDisappearing(); }
    }

    [Fact]
    public async Task ControlInitializationUsesSavedTokenAndPublishesBindableState()
    {
        await using var fixture = new PairingFixture();
        fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);
        fixture.Ble.Capabilities |= AgpCapabilities.OnOff;
        var model = ControlModel(fixture);
        await model.OnAppearingAsync();
        var notifications = new List<string?>();
        model.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        try
        {
            await model.EnsureConnectedAsync(showError: true);
            Assert.True(model.IsDeviceOnline);
            Assert.Contains(AgpOpcode.AuthToken, fixture.Ble.Requests);
            Assert.Contains(nameof(model.IsDeviceOnline), notifications);
            Assert.Contains(nameof(model.ControlOpacity), notifications);
            await model.MasterPowerCommand.ExecuteAsync(null);
            Assert.Contains(AgpOpcode.OnOffSet, fixture.Ble.Requests);
            Assert.Equal(fixture.Service.GetCachedDeviceStatus(fixture.Address)!.LightState!.IsOn, model.IsMasterPowerOn);
        }
        finally { model.OnDisappearing(); }
    }

    [Fact]
    public async Task PresetReorderUpdatesSlotsAndPersistsWithoutConnecting()
    {
        await using var fixture = new PairingFixture();
        fixture.Repository.SetCustomPresets(fixture.Address,
        [new SavedControlPreset(0, "A", null, null, null, 20, null, null, null), new SavedControlPreset(1, "B", null, null, null, 80, null, null, null)]);
        var model = ControlModel(fixture);
        await model.OnAppearingAsync();
        try
        {
            model.SetReorderMode(true);
            model.ReorderPreset(model.Presets[0], model.Presets[1]);
            Assert.Equal("B", model.Presets[0].DisplayName);
            Assert.False(model.IsReorderMode);
            Assert.All(model.Presets, p => Assert.False(p.IsReorderMode));
            Assert.Equal("B", fixture.Repository.GetCustomPresets(fixture.Address).Single(p => p.Slot == 0).DisplayName);
            Assert.Empty(fixture.Ble.ConnectAttempts);
        }
        finally { model.OnDisappearing(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidOrDuplicateQrCodeResumesScanningWithoutConnecting(bool duplicate)
    {
        await using var fixture = new PairingFixture();
        var interaction = new RecordingInteraction();
        var model = new QrCodeScanPageViewModel(fixture.Repository, fixture.Service, interaction, new TestAppNavigator());
        var value = "invalid";
        if (duplicate)
        {
            fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);
            fixture.Repository.SetCapabilities(fixture.Address, AgpCapabilities.Lightness);
            value = DeviceShareCode.Encode(fixture.Repository.GetByAddress(fixture.Address)!);
        }
        var scanning = new List<bool>();
        model.ScanningChanged += scanning.Add;
        Assert.True(model.TryBeginCode());
        Assert.False(model.TryBeginCode());
        await model.HandleCodeCommand.ExecuteAsync(value);
        Assert.Equal([false, true], scanning);
        Assert.True(model.TryBeginCode());
        Assert.Single(interaction.Alerts);
        Assert.Empty(fixture.Ble.ConnectAttempts);
        model.OnDisappearing();
    }

    [Fact]
    public async Task LicenseViewModelsFilterOfflineAssetsAndLoadToolkitLicense()
    {
        var catalog = new OpenSourceLicenseCatalog(path => Task.FromResult<Stream>(
            File.OpenRead(Path.Combine(AppContext.BaseDirectory, "AppAssets", path))));
        var interaction = new RecordingInteraction();
        var list = new OpenSourceLicensesPageViewModel(catalog, new TestAppNavigator(), interaction);
        list.SearchText = "CommunityToolkit.Mvvm";
        await list.OnAppearingAsync();
        var component = Assert.Single(list.Components);
        Assert.False(list.IsLoading);
        var detail = new OpenSourceLicensePageViewModel(catalog, interaction);
        detail.ApplyQueryAttributes(new Dictionary<string, object> { ["id"] = component.Id });
        await detail.OnAppearingAsync();
        Assert.True(detail.HasContent);
        Assert.Equal(component.Name, detail.ComponentName);
        Assert.Contains("MIT", detail.LicenseText);
        Assert.Empty(interaction.Alerts);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(101, 100)]
    public async Task BrightnessStateKeepsNativeSliderRangeBeforeLazyViewLoads(double requested, double expected)
    {
        await using var fixture = new PairingFixture();
        var model = ControlModel(fixture);
        model.Brightness = requested;
        Assert.Equal(expected, model.Brightness);
        Assert.Empty(fixture.Ble.Requests);
    }

    private static DeviceControlPageViewModel ControlModel(PairingFixture fixture)
    {
        var model = new DeviceControlPageViewModel(fixture.Repository, fixture.Discovery, fixture.Connection,
            fixture.Service, new AppDiagnosticService(), new TestUiDispatcher(), new TestAppNavigator(), new RecordingInteraction(), new StubMeshDeviceControlService());
        model.ApplyQueryAttributes(new Dictionary<string, object> { ["address"] = fixture.Address });
        return model;
    }

    private sealed class RecordingInteraction : IUserInteraction
    {
        public List<string> Alerts { get; } = [];
        public void ShowToast(string message) { }
        public void ShowToastForDuration(string message, TimeSpan duration) { }
        public Task AlertAsync(string title, string message, string cancel) { Alerts.Add(message); return Task.CompletedTask; }
        public Task<bool> ConfirmAsync(string title, string message, string accept, string cancel) => Task.FromResult(false);
        public Task<string?> PromptAsync(string title, string message, string accept, string cancel,
            string placeholder = "", string initialValue = "", int maxLength = -1, bool numeric = false) => Task.FromResult<string?>(null);
        public Task<string?> ActionSheetAsync(string title, string cancel, params string[] buttons) => Task.FromResult<string?>(null);
        public Task<bool> OpenUriAsync(string uri) => Task.FromResult(true);
    }
}
