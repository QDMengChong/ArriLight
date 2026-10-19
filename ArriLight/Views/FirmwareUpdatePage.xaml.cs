namespace ArriLight;

public partial class FirmwareUpdatePage : ContentPage, IQueryAttributable
{
    public FirmwareUpdatePageViewModel ViewModel { get; }
    public FirmwareUpdatePage() : this(ServiceHelper.GetRequiredService<FirmwareUpdatePageViewModel>()) { }
    public FirmwareUpdatePage(FirmwareUpdatePageViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        BindingContext = viewModel;
    }
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        Shell.Current.Navigating += OnShellNavigating;
        ViewModel.PropertyChanged += OnUpdatePropertyChanged;
        await ViewModel.OnAppearingAsync();
    }
    protected override void OnDisappearing()
    {
        ViewModel.OnDisappearing();
        Shell.Current.Navigating -= OnShellNavigating;
        ViewModel.PropertyChanged -= OnUpdatePropertyChanged;
        base.OnDisappearing();
    }
    public void ApplyQueryAttributes(IDictionary<string, object> query) => ViewModel.ApplyQueryAttributes(query);

    protected override bool OnBackButtonPressed()
    {
        if (ViewModel.IsUpgradeLocked)
            return true;
        if (ViewModel.IsRecoverySelectionVisible)
        {
            ViewModel.CancelRecoverySelectionCommand.Execute(null);
            return true;
        }
        return base.OnBackButtonPressed();
    }

    private void OnShellNavigating(object? sender, ShellNavigatingEventArgs args)
    {
        if (ViewModel.IsUpgradeLocked && args.CanCancel)
            args.Cancel();
        else if (ViewModel.IsRecoverySelectionVisible && args.CanCancel)
        {
            args.Cancel();
            ViewModel.CancelRecoverySelectionCommand.Execute(null);
        }
    }

    private void OnUpdatePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
#if ANDROID || IOS
        if (args.PropertyName == nameof(ViewModel.IsUpgradeLocked))
            DeviceDisplay.Current.KeepScreenOn = ViewModel.IsUpgradeLocked;
#endif
    }

}
