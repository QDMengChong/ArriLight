namespace ArriLight;

public partial class AppVersionPage : ContentPage
{
    private static readonly TimeSpan TapSequenceTimeout =
        TimeSpan.FromSeconds(2);

    private readonly AppDiagnosticService _diagnostics;
    private int _versionTapCount;
    private DateTimeOffset _lastVersionTapAt;
    private bool _isShowingDebugPrompt;

    public string VersionText => Localization.Format(nameof(AppResources.Version_Display), AppInfo.Current.VersionString, AppInfo.Current.BuildString);
    public bool IsDebugModeEnabled => _diagnostics.IsEnabled;

    public AppVersionPage()
    {
        InitializeComponent();
        _diagnostics = ServiceHelper.GetRequiredService<AppDiagnosticService>();
        BindingContext = this;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        OnPropertyChanged(nameof(IsDebugModeEnabled));
    }

    private async void OnVersionTapped(object? sender, TappedEventArgs e) =>
        await HandleVersionTapAsync(DateTimeOffset.UtcNow);

    private async Task HandleVersionTapAsync(DateTimeOffset now)
    {
        if (_isShowingDebugPrompt)
            return;
        if (now - _lastVersionTapAt > TapSequenceTimeout)
            _versionTapCount = 0;

        _lastVersionTapAt = now;
        _versionTapCount++;
        if (_versionTapCount < 5)
            return;

        _versionTapCount = 0;
        _isShowingDebugPrompt = true;
        try
        {
            var enabling = !_diagnostics.IsEnabled;
            var confirmed = await DisplayAlertAsync(
                AppResources.Version_DebugMode,
                enabling
                    ? Localization.Format(nameof(AppResources.Version_EnableDebugPrompt), _diagnostics.LogFilePath)
                    : Localization.Format(nameof(AppResources.Version_DisableDebugPrompt), _diagnostics.LogFilePath),
                enabling ? AppResources.Common_Enable : AppResources.Common_TurnOff,
                AppResources.Common_Cancel);

            if (!confirmed)
                return;

            _diagnostics.IsEnabled = enabling;
            OnPropertyChanged(nameof(IsDebugModeEnabled));
            await DisplayAlertAsync(
                AppResources.Version_DebugMode,
                enabling
                    ? Localization.Format(nameof(AppResources.Version_DebugEnabled), _diagnostics.LogFilePath)
                    : AppResources.Version_DebugDisabled,
                AppResources.Common_Ok);
        }
        finally
        {
            _isShowingDebugPrompt = false;
        }
    }

    private async void OnEditConfigurationClicked(object? sender, EventArgs e) =>
        await OpenConfigurationEditorAsync();

    private Task OpenConfigurationEditorAsync() => _diagnostics.IsEnabled
        ? Shell.Current.GoToAsync(nameof(DebugConfigurationPage))
        : Task.CompletedTask;
}
