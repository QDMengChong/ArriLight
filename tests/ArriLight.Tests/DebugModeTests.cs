using Xunit;

namespace ArriLight.Tests;

public sealed class DebugModeTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-07T00:00:00Z");

    [Theory]
    [InlineData(0.25)]
    [InlineData(2)]
    public async Task FifthTapWithinIntervalAsksThenEnablesLoggingAndConfiguration(double seconds)
    {
        var diagnostics = new AppDiagnosticService();
        var page = VersionPage(diagnostics, confirmed: true);
        for (var tap = 0; tap < 4; tap++)
            await page.TapForTestAsync(Start.AddSeconds(tap * seconds));
        Assert.Empty(page.Alerts);
        Assert.False(page.IsDebugModeEnabled);

        await page.TapForTestAsync(Start.AddSeconds(4 * seconds));
        Assert.True(diagnostics.IsEnabled);
        Assert.True(page.IsDebugModeEnabled);
        Assert.Equal(2, page.Alerts.Count);
        Assert.Equal(AppResources.Version_DebugMode, page.Alerts[0].Title);
        Assert.Contains(diagnostics.LogFilePath, page.Alerts[0].Message);
    }

    [Fact]
    public async Task IntervalOverTwoSecondsStartsANewFiveTapSequence()
    {
        var page = VersionPage(new AppDiagnosticService(), confirmed: true);
        for (var tap = 0; tap < 4; tap++)
            await page.TapForTestAsync(Start.AddMilliseconds(tap * 100));
        var restartedAt = Start.AddMilliseconds(2301);
        for (var tap = 0; tap < 4; tap++)
            await page.TapForTestAsync(restartedAt.AddMilliseconds(tap * 100));
        Assert.Empty(page.Alerts);
        await page.TapForTestAsync(restartedAt.AddMilliseconds(400));
        Assert.True(page.IsDebugModeEnabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelKeepsPreviousDebugModeAndNextSequenceCanConfirm(bool enabled)
    {
        var diagnostics = new AppDiagnosticService { IsEnabled = enabled };
        var page = VersionPage(diagnostics, confirmed: false);
        await FiveTapsAsync(page, Start);
        Assert.Equal(enabled, diagnostics.IsEnabled);
        Assert.Single(page.Alerts);

        page.OnConfirmation = (_, _, _, _) => Task.FromResult(true);
        await FiveTapsAsync(page, Start.AddSeconds(1));
        Assert.Equal(!enabled, diagnostics.IsEnabled);
        Assert.Equal(!enabled, page.IsDebugModeEnabled);
    }

    [Fact]
    public async Task TapsWhileConfirmationIsOpenDoNotOpenAnotherDialog()
    {
        var page = VersionPage(new AppDiagnosticService(), confirmed: true);
        var confirmation = new TaskCompletionSource<bool>();
        page.OnConfirmation = (_, _, _, _) => confirmation.Task;
        var firstSequence = FiveTapsAsync(page, Start);
        Assert.Single(page.Alerts);
        Assert.False(firstSequence.IsCompleted);

        await FiveTapsAsync(page, Start.AddSeconds(1));
        Assert.Single(page.Alerts);
        confirmation.SetResult(true);
        await firstSequence;
        Assert.True(page.IsDebugModeEnabled);
    }

    [Fact]
    public async Task ConfigurationEntryOnlyNavigatesInDebugMode()
    {
        ResetNavigation();
        var diagnostics = new AppDiagnosticService();
        var page = VersionPage(diagnostics, confirmed: true);
        await page.OpenConfigurationForTestAsync();
        Assert.Empty(Shell.Current.Routes);
        diagnostics.IsEnabled = true;
        await page.OpenConfigurationForTestAsync();
        Assert.Equal(nameof(DebugConfigurationPage), Assert.Single(Shell.Current.Routes));
    }

    [Fact]
    public async Task EditorRejectsDirectNavigationWhenDebugModeIsOff()
    {
        using var settings = new AppSettingsFixture();
        var page = Editor(settings.Store, enabled: false);
        await page.LoadForTestAsync();
        Assert.Equal("..", Assert.Single(Shell.Current.Routes));
        Assert.Equal(AppResources.Configuration_DebugModeRequired, Assert.Single(ToastService.Messages));
        Assert.False(page.CanSave);
        Assert.False(File.Exists(settings.Store.ConfigurationPath));
    }

    [Fact]
    public async Task EditorSavesTextAndExistingConfigurationImmediatelyReadsIt()
    {
        using var settings = new AppSettingsFixture();
        var configuration = new FirmwareUpdateConfiguration(settings.Store);
        await configuration.LoadWorkerUrlsAsync();
        var page = Editor(settings.Store, enabled: true);
        await page.LoadForTestAsync();
        Assert.Equal(AppSettingsFixture.DefaultText, page.ConfigurationText);
        Assert.True(page.CanSave);

        var edited = AppSettingsFixture.Text("https://changed.example/list", "https://changed.example/get") + "\n";
        page.ConfigurationText = edited;
        await page.SaveForTestAsync();
        Assert.Equal(edited, await settings.Store.ReadTextAsync());
        Assert.Equal(AppResources.Configuration_Saved, page.StatusText);
        Assert.Equal(AppResources.Configuration_Saved, Assert.Single(ToastService.Messages));
        Assert.Equal("https://changed.example/list", (await configuration.LoadWorkerUrlsAsync()).ListWorkerUrl);
        Assert.False(page.IsConfigurationBusy);
        Assert.True(page.CanSave);
    }

    [Fact]
    public async Task InvalidEditorTextIsRetainedForCorrectionAndWorkingFileIsUnchanged()
    {
        using var settings = new AppSettingsFixture();
        var page = Editor(settings.Store, enabled: true);
        await page.LoadForTestAsync();
        page.ConfigurationText = "{";
        await page.SaveForTestAsync();
        Assert.Equal("{", page.ConfigurationText);
        Assert.Equal(AppSettingsFixture.DefaultText, await settings.Store.ReadTextAsync());
        Assert.NotEmpty(page.StatusText);
        Assert.Empty(ToastService.Messages);
        Assert.True(page.CanSave);

        page.ConfigurationText = AppSettingsFixture.DefaultText;
        Assert.Empty(page.StatusText);
        await page.SaveForTestAsync();
        Assert.Equal(AppResources.Configuration_Saved, page.StatusText);
    }

    [Fact]
    public async Task TurningDebugModeOffBlocksEditorSaveButKeepsActiveConfiguration()
    {
        using var settings = new AppSettingsFixture();
        var page = Editor(settings.Store, enabled: true);
        await page.LoadForTestAsync();
        var saved = AppSettingsFixture.Text("https://changed.example/list", "https://changed.example/get");
        page.ConfigurationText = saved;
        await page.SaveForTestAsync();
        ServiceHelper.GetRequiredService<AppDiagnosticService>().IsEnabled = false;
        page.ConfigurationText = AppSettingsFixture.DefaultText;
        await page.SaveForTestAsync();
        Assert.False(page.CanSave);
        Assert.Equal(AppResources.Configuration_DebugModeRequired, page.StatusText);
        Assert.Equal(saved, await settings.Store.ReadTextAsync());
        Assert.Equal("https://changed.example/list", (await settings.Store.LoadWorkerUrlsAsync()).ListWorkerUrl);
    }

    private static AppVersionPage VersionPage(AppDiagnosticService diagnostics, bool confirmed)
    {
        ServiceHelper.Set(diagnostics);
        return new AppVersionPage { OnConfirmation = (_, _, _, _) => Task.FromResult(confirmed) };
    }

    private static DebugConfigurationPage Editor(AppSettingsService settings, bool enabled)
    {
        ResetNavigation();
        ToastService.Messages.Clear();
        ServiceHelper.Set(settings);
        ServiceHelper.Set(new AppDiagnosticService { IsEnabled = enabled });
        return new DebugConfigurationPage();
    }

    private static async Task FiveTapsAsync(AppVersionPage page, DateTimeOffset start)
    {
        for (var tap = 0; tap < 5; tap++)
            await page.TapForTestAsync(start.AddMilliseconds(tap * 100));
    }

    private static void ResetNavigation()
    {
        Shell.Current.Routes.Clear();
        Shell.Current.AnimationRequests.Clear();
        Shell.Current.OnNavigate = null;
    }
}
