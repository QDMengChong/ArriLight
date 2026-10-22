namespace ArriLight;

public partial class DeviceGroupPage : ContentPage, IQueryAttributable
{
    public DeviceGroupPageViewModel ViewModel { get; }
    public DeviceGroupPage() : this(ServiceHelper.GetRequiredService<DeviceGroupPageViewModel>()) { }
    public DeviceGroupPage(DeviceGroupPageViewModel viewModel)
    { ViewModel = viewModel; InitializeComponent(); BindingContext = viewModel; }
    public void ApplyQueryAttributes(IDictionary<string, object> query) => ViewModel.ApplyQueryAttributes(query);
    protected override void OnAppearing() { base.OnAppearing(); _ = ViewModel.OnAppearingAsync(); }
    protected override void OnDisappearing() { ViewModel.OnDisappearing(); base.OnDisappearing(); }
}
