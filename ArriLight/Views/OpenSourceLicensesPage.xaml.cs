namespace ArriLight;

public partial class OpenSourceLicensesPage : ContentPage
{
    public OpenSourceLicensesPageViewModel ViewModel { get; }
    public OpenSourceLicensesPage() : this(ServiceHelper.GetRequiredService<OpenSourceLicensesPageViewModel>()) { }
    public OpenSourceLicensesPage(OpenSourceLicensesPageViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        BindingContext = viewModel;
    }
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await ViewModel.OnAppearingAsync();
    }
    protected override void OnDisappearing()
    {
        ViewModel.OnDisappearing();
        base.OnDisappearing();
    }
    private void OnComponentSelected(object? sender, SelectionChangedEventArgs e)
    {
        LicenseCollection.SelectedItem = null;
        if (e.CurrentSelection.FirstOrDefault() is OpenSourceComponent component)
            ViewModel.OpenComponentCommand.Execute(component);
    }
}
