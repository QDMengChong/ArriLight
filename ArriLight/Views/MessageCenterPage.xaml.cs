namespace ArriLight;

public partial class MessageCenterPage : ContentPage
{
    public MessageCenterPageViewModel ViewModel { get; }
    public MessageCenterPage() : this(ServiceHelper.GetRequiredService<MessageCenterPageViewModel>()) { }
    public MessageCenterPage(MessageCenterPageViewModel viewModel)
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
