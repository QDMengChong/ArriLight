namespace ArriLight;

public partial class AppVersionPage : ContentPage
{
    public AppVersionPageViewModel ViewModel { get; }
    public AppVersionPage() : this(ServiceHelper.GetRequiredService<AppVersionPageViewModel>()) { }
    public AppVersionPage(AppVersionPageViewModel viewModel)
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
