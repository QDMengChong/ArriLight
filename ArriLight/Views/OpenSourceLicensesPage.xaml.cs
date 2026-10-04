namespace ArriLight;

public partial class OpenSourceLicensesPage : ContentPage
{
    private readonly OpenSourceLicenseCatalog _catalog;
    private IReadOnlyList<OpenSourceComponent>? _allComponents;
    private bool _isLoading;
    private bool _isNavigating;

    public IReadOnlyList<OpenSourceComponent> Components { get; private set; } = [];

    public OpenSourceLicensesPage()
    {
        InitializeComponent();
        _catalog = ServiceHelper.GetRequiredService<OpenSourceLicenseCatalog>();
        BindingContext = this;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_allComponents is not null || _isLoading)
            return;

        _isLoading = true;
        LoadingIndicator.IsVisible = LoadingIndicator.IsRunning = true;
        LicenseCollection.IsVisible = false;
        try
        {
            _allComponents = await _catalog.GetComponentsAsync();
            FilterComponents();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"[Licenses] {exception}");
            await DisplayAlertAsync(AppResources.Licenses_Title, AppResources.Licenses_LoadFailed, AppResources.Common_Ok);
        }
        finally
        {
            LoadingIndicator.IsVisible = LoadingIndicator.IsRunning = false;
            LicenseCollection.IsVisible = true;
            _isLoading = false;
        }
    }

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e) => FilterComponents();

    private void FilterComponents()
    {
        var search = LicenseSearch.Text?.Trim();
        Components = string.IsNullOrEmpty(search)
            ? _allComponents ?? []
            : (_allComponents ?? []).Where(c => c.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                               c.License.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
        OnPropertyChanged(nameof(Components));
    }

    private async void OnComponentSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_isNavigating || e.CurrentSelection.FirstOrDefault() is not OpenSourceComponent component)
            return;
        _isNavigating = true;
        LicenseCollection.SelectedItem = null;
        try
        {
            await Shell.Current.GoToAsync($"{nameof(OpenSourceLicensePage)}?id={Uri.EscapeDataString(component.Id)}");
        }
        finally
        {
            _isNavigating = false;
        }
    }
}
