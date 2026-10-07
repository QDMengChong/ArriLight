namespace ArriLight;

public partial class ProfilePage : ContentPage
{
    public ProfilePageViewModel ViewModel { get; }
    public ProfilePage() : this(ServiceHelper.GetRequiredService<ProfilePageViewModel>()) { }
    public ProfilePage(ProfilePageViewModel viewModel)
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

}
