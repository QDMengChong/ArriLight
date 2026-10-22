namespace ArriLight;

public partial class DeviceGroupMembersPage : ContentPage, IQueryAttributable
{
    public DeviceGroupMembersPageViewModel ViewModel { get; }
    public bool AreStatusAnimationsEnabled { get; private set; }
    public DeviceGroupMembersPage() : this(ServiceHelper.GetRequiredService<DeviceGroupMembersPageViewModel>()) { }
    public DeviceGroupMembersPage(DeviceGroupMembersPageViewModel viewModel)
    { ViewModel = viewModel; InitializeComponent(); BindingContext = viewModel; }
    public void ApplyQueryAttributes(IDictionary<string, object> query) => ViewModel.ApplyQueryAttributes(query);
    protected override void OnAppearing()
    {
        base.OnAppearing();
        AreStatusAnimationsEnabled = true;
        OnPropertyChanged(nameof(AreStatusAnimationsEnabled));
        _ = ViewModel.OnAppearingAsync();
    }
    protected override void OnDisappearing()
    {
        AreStatusAnimationsEnabled = false;
        OnPropertyChanged(nameof(AreStatusAnimationsEnabled));
        ViewModel.OnDisappearing();
        base.OnDisappearing();
    }
}
