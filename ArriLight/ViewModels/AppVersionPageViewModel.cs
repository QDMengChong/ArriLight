using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ArriLight.ViewModels;

public partial class AppVersionPageViewModel : ViewModelBase
{
    private static readonly TimeSpan TapSequenceTimeout =
        TimeSpan.FromSeconds(2);

    private readonly AppDiagnosticService _diagnostics;
    private int _versionTapCount;
    private DateTimeOffset _lastVersionTapAt;
    private bool _isShowingDebugPrompt;

    public string VersionText => Localization.Format(nameof(AppResources.Version_Display), _application.Version, _application.Build);
    public bool IsDebugModeEnabled => _diagnostics.IsEnabled;

    private readonly IApplicationPresentation _application;
    private readonly IAppNavigator _navigator;
    private readonly IUserInteraction _interaction;
    public AppVersionPageViewModel(AppDiagnosticService diagnostics, IApplicationPresentation application,
        IAppNavigator navigator, IUserInteraction interaction)
    { _diagnostics = diagnostics; _application = application; _navigator = navigator; _interaction = interaction; }

    public override Task OnAppearingAsync()
    {
        OnPropertyChanged(nameof(IsDebugModeEnabled));
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task VersionTapAsync() => HandleVersionTapAsync(DateTimeOffset.UtcNow);

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
            var confirmed = await _interaction.ConfirmAsync(
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
            await _interaction.AlertAsync(
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

    [RelayCommand]
    private Task OpenConfigurationEditorAsync() => _diagnostics.IsEnabled
        ? _navigator.GoToAsync("DebugConfigurationPage")
        : Task.CompletedTask;
}
