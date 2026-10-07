namespace ArriLight;

public partial class AboutUsPage : ContentPage
{
    public AboutUsPageViewModel ViewModel { get; }
    public AboutUsPage() : this(ServiceHelper.GetRequiredService<AboutUsPageViewModel>()) { }
    public AboutUsPage(AboutUsPageViewModel viewModel)
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
