using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ArriLight.ViewModels;

public partial class OpenSourceLicensePageViewModel(OpenSourceLicenseCatalog catalog,
    IUserInteraction interaction) : ViewModelBase
{
    private string? _componentId;
    private OpenSourceComponent? _component;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _hasContent;
    [ObservableProperty] private string _componentName = AppResources.Licenses_Title;
    [ObservableProperty] private string _componentVersion = string.Empty;
    [ObservableProperty] private string _componentLicense = string.Empty;
    [ObservableProperty] private string _componentAuthors = string.Empty;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasCopyright))] private string _componentCopyright = string.Empty;
    [ObservableProperty] private string _licenseText = string.Empty;
    [ObservableProperty] private bool _hasProjectWebsite;
    public bool HasCopyright => !string.IsNullOrWhiteSpace(ComponentCopyright);
    public override void ApplyQueryAttributes(IDictionary<string, object> query) =>
        _componentId = query.TryGetValue("id", out var id) ? Uri.UnescapeDataString(id?.ToString() ?? "") : null;
    public override async Task OnAppearingAsync()
    {
        if (IsLoading || _component is not null && _component.Id == _componentId)
            return;
        IsLoading = true;
        HasContent = false;
        try
        {
            var component = (await catalog.GetComponentsAsync()).FirstOrDefault(c => c.Id == _componentId);
            if (component is null)
            {
                await interaction.AlertAsync(AppResources.Licenses_Title, AppResources.Licenses_NotFound, AppResources.Common_Ok);
                return;
            }
            LicenseText = await catalog.GetLicenseTextAsync(component);
            ComponentName = component.Name;
            ComponentVersion = Localization.Format(nameof(AppResources.Licenses_Version), component.Version);
            ComponentLicense = component.License;
            ComponentAuthors = Localization.Format(nameof(AppResources.Licenses_Authors), component.Authors);
            ComponentCopyright = component.Copyright;
            HasProjectWebsite = Uri.TryCreate(component.ProjectUrl, UriKind.Absolute, out var url) && url.Scheme is "https" or "http";
            _component = component;
            HasContent = true;
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            ErrorMessage = exception.Message;
            await interaction.AlertAsync(AppResources.Licenses_Title, AppResources.Licenses_LoadFailed, AppResources.Common_Ok);
        }
        finally { IsLoading = false; }
    }
    [RelayCommand]
    private async Task ProjectWebsiteAsync()
    {
        try { if (_component is not null && await interaction.OpenUriAsync(_component.ProjectUrl)) return; }
        catch (Exception exception) { ErrorMessage = exception.Message; }
        await interaction.AlertAsync(AppResources.Licenses_Title, AppResources.Licenses_LinkFailed, AppResources.Common_Ok);
    }
}
