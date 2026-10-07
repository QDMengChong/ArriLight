namespace ArriLight;

public partial class DebugConfigurationPage : ContentPage
{
    public DebugConfigurationPageViewModel ViewModel { get; }
    public DebugConfigurationPage() : this(ServiceHelper.GetRequiredService<DebugConfigurationPageViewModel>()) { }
    public DebugConfigurationPage(DebugConfigurationPageViewModel viewModel)
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
