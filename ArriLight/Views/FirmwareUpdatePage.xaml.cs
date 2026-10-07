namespace ArriLight;

public partial class FirmwareUpdatePage : ContentPage, IQueryAttributable
{
    public FirmwareUpdatePageViewModel ViewModel { get; }
    public FirmwareUpdatePage() : this(ServiceHelper.GetRequiredService<FirmwareUpdatePageViewModel>()) { }
    public FirmwareUpdatePage(FirmwareUpdatePageViewModel viewModel)
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
