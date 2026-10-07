namespace ArriLight;

public partial class DeviceMorePage : ContentPage, IQueryAttributable
{
    public DeviceMorePageViewModel ViewModel { get; }
    public DeviceMorePage() : this(ServiceHelper.GetRequiredService<DeviceMorePageViewModel>()) { }
    public DeviceMorePage(DeviceMorePageViewModel viewModel)
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
    private void OnPinnedToggled(object? sender, ToggledEventArgs e) => ViewModel.SetPinnedCommand.Execute(e.Value);

}
