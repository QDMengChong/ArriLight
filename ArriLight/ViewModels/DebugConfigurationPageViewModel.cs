using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ArriLight.ViewModels;

public partial class DebugConfigurationPageViewModel : ViewModelBase
{
    private readonly AppSettingsService _settings;
    private readonly AppDiagnosticService _diagnostics;
    private string _configurationText = string.Empty;
    [ObservableProperty]
    private string _statusText = string.Empty;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanSave))]
    private bool _isConfigurationBusy;
    private bool _isLoaded;

    private readonly IAppNavigator _navigator;
    private readonly IUserInteraction _interaction;
    public DebugConfigurationPageViewModel(AppSettingsService settings, AppDiagnosticService diagnostics,
        IAppNavigator navigator, IUserInteraction interaction)
    { _settings = settings; _diagnostics = diagnostics; _navigator = navigator; _interaction = interaction; }

    public string ConfigurationText
    {
        get => _configurationText;
        set
        {
            if (_configurationText == value)
                return;
            _configurationText = value;
            OnPropertyChanged();
            StatusText = string.Empty;
        }
    }

    public bool CanSave => _isLoaded && !IsConfigurationBusy && _diagnostics.IsEnabled;

    public override async Task OnAppearingAsync()
    {
        await LoadConfigurationAsync();
    }

    private async Task LoadConfigurationAsync()
    {
        if (!_diagnostics.IsEnabled)
        {
            _interaction.ShowToast(AppResources.Configuration_DebugModeRequired);
            await _navigator.GoToAsync("..");
            return;
        }
        if (_isLoaded || IsConfigurationBusy)
            return;

        IsConfigurationBusy = true;
        try
        {
            ConfigurationText = await _settings.ReadTextAsync();
            _isLoaded = true;
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            StatusText = Localization.Format(nameof(AppResources.Configuration_LoadFailed), exception.Message);
            _diagnostics.Error("Configuration", "读取 appsettings.json 失败。", exception);
        }
        finally
        {
            IsConfigurationBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveConfigurationAsync()
    {
        if (!_diagnostics.IsEnabled)
        {
            StatusText = AppResources.Configuration_DebugModeRequired;
            OnPropertyChanged(nameof(CanSave));
            return;
        }
        if (!_isLoaded || IsConfigurationBusy)
            return;

        IsConfigurationBusy = true;
        try
        {
            await _settings.SaveTextAsync(ConfigurationText);
            StatusText = AppResources.Configuration_Saved;
            _diagnostics.Information("Configuration", "appsettings.json 已保存，后续操作立即使用新配置。");
            _interaction.ShowToast(AppResources.Configuration_Saved);
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            StatusText = Localization.Format(nameof(AppResources.Configuration_SaveFailed), exception.Message);
            _diagnostics.Error("Configuration", "保存 appsettings.json 失败，保留原配置。", exception);
        }
        finally
        {
            IsConfigurationBusy = false;
        }
    }
}
