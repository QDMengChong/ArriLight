namespace ArriLight;

public partial class DebugConfigurationPage : ContentPage
{
    private readonly AppSettingsService _settings;
    private readonly AppDiagnosticService _diagnostics;
    private string _configurationText = string.Empty;
    private string _statusText = string.Empty;
    private bool _isBusy;
    private bool _isLoaded;

    public DebugConfigurationPage()
    {
        InitializeComponent();
        _settings = ServiceHelper.GetRequiredService<AppSettingsService>();
        _diagnostics = ServiceHelper.GetRequiredService<AppDiagnosticService>();
        BindingContext = this;
    }

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

    public string StatusText
    {
        get => _statusText;
        private set
        {
            _statusText = value;
            OnPropertyChanged();
        }
    }

    public bool IsConfigurationBusy
    {
        get => _isBusy;
        private set
        {
            _isBusy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanSave));
        }
    }

    public bool CanSave => _isLoaded && !IsConfigurationBusy && _diagnostics.IsEnabled;

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadConfigurationAsync();
    }

    private async Task LoadConfigurationAsync()
    {
        if (!_diagnostics.IsEnabled)
        {
            ToastService.Show(AppResources.Configuration_DebugModeRequired);
            await Shell.Current.GoToAsync("..");
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
            StatusText = Localization.Format(nameof(AppResources.Configuration_LoadFailed), exception.Message);
            _diagnostics.Error("Configuration", "读取 appsettings.json 失败。", exception);
        }
        finally
        {
            IsConfigurationBusy = false;
        }
    }

    private async void OnSaveClicked(object? sender, EventArgs e) => await SaveConfigurationAsync();

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
            ToastService.Show(AppResources.Configuration_Saved);
        }
        catch (Exception exception)
        {
            StatusText = Localization.Format(nameof(AppResources.Configuration_SaveFailed), exception.Message);
            _diagnostics.Error("Configuration", "保存 appsettings.json 失败，保留原配置。", exception);
        }
        finally
        {
            IsConfigurationBusy = false;
        }
    }
}
