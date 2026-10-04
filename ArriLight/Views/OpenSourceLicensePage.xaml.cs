namespace ArriLight;

public partial class OpenSourceLicensePage : ContentPage, IQueryAttributable
{
    private readonly OpenSourceLicenseCatalog _catalog;
    private OpenSourceComponent? _component;
    private string? _componentId;
    private bool _isLoading;

    public OpenSourceLicensePage()
    {
        InitializeComponent();
        _catalog = ServiceHelper.GetRequiredService<OpenSourceLicenseCatalog>();
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query) =>
        _componentId = query.TryGetValue("id", out var id)
            ? Uri.UnescapeDataString(id?.ToString() ?? string.Empty)
            : null;

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_isLoading || _component?.Id == _componentId && _component is not null)
            return;

        _isLoading = true;
        LoadingIndicator.IsVisible = LoadingIndicator.IsRunning = true;
        LicenseScroll.IsVisible = false;
        try
        {
            var components = await _catalog.GetComponentsAsync();
            var component = components.FirstOrDefault(c => c.Id == _componentId);
            if (component is null)
            {
                await DisplayAlertAsync(AppResources.Licenses_Title, AppResources.Licenses_NotFound, AppResources.Common_Ok);
                return;
            }

            LicenseText.Text = await _catalog.GetLicenseTextAsync(component);
            Title = ComponentName.Text = component.Name;
            ComponentVersion.Text = Localization.Format(nameof(AppResources.Licenses_Version), component.Version);
            ComponentLicense.Text = component.License;
            ComponentAuthors.Text = Localization.Format(nameof(AppResources.Licenses_Authors), component.Authors);
            ComponentCopyright.Text = component.Copyright;
            ComponentCopyright.IsVisible = !string.IsNullOrWhiteSpace(component.Copyright);
            ProjectWebsiteButton.IsVisible = Uri.TryCreate(component.ProjectUrl, UriKind.Absolute, out var url) &&
                                             url.Scheme is "https" or "http";
            _component = component;
            LicenseScroll.IsVisible = true;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"[Licenses] {exception}");
            await DisplayAlertAsync(AppResources.Licenses_Title, AppResources.Licenses_LoadFailed, AppResources.Common_Ok);
        }
        finally
        {
            LoadingIndicator.IsVisible = LoadingIndicator.IsRunning = false;
            _isLoading = false;
        }
    }

    private async void OnProjectWebsiteClicked(object? sender, EventArgs e)
    {
        try
        {
            if (_component is not null && await Launcher.Default.TryOpenAsync(_component.ProjectUrl))
                return;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"[Licenses/Website] {exception}");
        }
        await DisplayAlertAsync(AppResources.Licenses_Title, AppResources.Licenses_LinkFailed, AppResources.Common_Ok);
    }
}
