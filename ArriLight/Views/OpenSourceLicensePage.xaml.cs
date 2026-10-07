namespace ArriLight;

public partial class OpenSourceLicensePage : ContentPage, IQueryAttributable
{
    public OpenSourceLicensePageViewModel ViewModel { get; }
    public OpenSourceLicensePage() : this(ServiceHelper.GetRequiredService<OpenSourceLicensePageViewModel>()) { }
    public OpenSourceLicensePage(OpenSourceLicensePageViewModel viewModel)
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
    public void ApplyQueryAttributes(IDictionary<string, object> query) => ViewModel.ApplyQueryAttributes(query);

}
