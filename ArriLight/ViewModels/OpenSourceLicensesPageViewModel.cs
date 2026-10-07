using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ArriLight.ViewModels;

public partial class OpenSourceLicensesPageViewModel(OpenSourceLicenseCatalog catalog,
    IAppNavigator navigator, IUserInteraction interaction) : ViewModelBase
{
    private IReadOnlyList<OpenSourceComponent>? _allComponents;
    [ObservableProperty] private IReadOnlyList<OpenSourceComponent> _components = [];
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsListVisible))] private bool _isLoading;
    public bool IsListVisible => !IsLoading;
    partial void OnSearchTextChanged(string value) => FilterComponents();
    private void FilterComponents()
    {
        var search = SearchText.Trim();
        Components = string.IsNullOrEmpty(search) ? _allComponents ?? [] :
            (_allComponents ?? []).Where(c => c.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                c.License.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    public override async Task OnAppearingAsync()
    {
        if (_allComponents is not null || IsLoading)
            return;
        IsLoading = true;
        try { _allComponents = await catalog.GetComponentsAsync(); FilterComponents(); }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            ErrorMessage = exception.Message;
            await interaction.AlertAsync(AppResources.Licenses_Title, AppResources.Licenses_LoadFailed, AppResources.Common_Ok);
        }
        finally { IsLoading = false; }
    }
    [RelayCommand]
    private Task OpenComponentAsync(OpenSourceComponent component) =>
        navigator.GoToAsync($"OpenSourceLicensePage?id={Uri.EscapeDataString(component.Id)}");
}
