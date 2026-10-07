namespace ArriLight;

public partial class LegalDocumentPage : ContentPage, IQueryAttributable
{
    public LegalDocumentPageViewModel ViewModel { get; }
    public LegalDocumentPage() : this(ServiceHelper.GetRequiredService<LegalDocumentPageViewModel>()) { }
    public LegalDocumentPage(LegalDocumentPageViewModel viewModel)
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
