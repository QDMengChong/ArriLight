namespace ArriLight;

public partial class DeviceSharePage : ContentPage, IQueryAttributable
{
    public DeviceSharePageViewModel ViewModel { get; }
    public DeviceSharePage() : this(ServiceHelper.GetRequiredService<DeviceSharePageViewModel>()) { }
    public DeviceSharePage(DeviceSharePageViewModel viewModel)
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
