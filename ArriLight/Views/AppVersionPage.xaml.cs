namespace ArriLight;

public partial class AppVersionPage : ContentPage
{
    private static readonly TimeSpan TapSequenceTimeout =
        TimeSpan.FromSeconds(2);

    private readonly AppDiagnosticService _diagnostics;
    private int _versionTapCount;
    private DateTimeOffset _lastVersionTapAt;

    public string VersionText => Localization.Format(nameof(AppResources.Version_Display), AppInfo.Current.VersionString, AppInfo.Current.BuildString);

    public AppVersionPage()
    {
        InitializeComponent();
        _diagnostics = ServiceHelper.GetRequiredService<AppDiagnosticService>();
        BindingContext = this;
    }

    private async void OnVersionTapped(object? sender, TappedEventArgs e)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastVersionTapAt > TapSequenceTimeout)
            _versionTapCount = 0;

        _lastVersionTapAt = now;
        _versionTapCount++;
        if (_versionTapCount < 5)
            return;

        _versionTapCount = 0;
        var enabling = !_diagnostics.IsEnabled;
        var confirmed = await DisplayAlertAsync(
            AppResources.Version_DebugFileOutput,
            enabling
                ? Localization.Format(nameof(AppResources.Version_EnableDebugPrompt), _diagnostics.LogFilePath)
                : Localization.Format(nameof(AppResources.Version_DisableDebugPrompt), _diagnostics.LogFilePath),
            enabling ? AppResources.Common_Enable : AppResources.Common_TurnOff,
            AppResources.Common_Cancel);

        if (!confirmed)
            return;

        _diagnostics.IsEnabled = enabling;
        await DisplayAlertAsync(
            AppResources.Version_DebugFileOutput,
            enabling
                ? Localization.Format(nameof(AppResources.Version_DebugEnabled), _diagnostics.LogFilePath)
                : AppResources.Version_DebugDisabled,
            AppResources.Common_Ok);
    }
}
