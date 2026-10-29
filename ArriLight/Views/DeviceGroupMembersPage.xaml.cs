namespace ArriLight;

public partial class DeviceGroupMembersPage : ContentPage, IQueryAttributable
{
    private bool _backNavigationPending;
    public DeviceGroupMembersPageViewModel ViewModel { get; }
    public bool AreStatusAnimationsEnabled { get; private set; }
    public DeviceGroupMembersPage() : this(ServiceHelper.GetRequiredService<DeviceGroupMembersPageViewModel>()) { }
    public DeviceGroupMembersPage(DeviceGroupMembersPageViewModel viewModel)
    { ViewModel = viewModel; InitializeComponent(); BindingContext = viewModel; }
    public void ApplyQueryAttributes(IDictionary<string, object> query) => ViewModel.ApplyQueryAttributes(query);
    protected override void OnAppearing()
    {
        base.OnAppearing();
        Shell.Current.Navigating += OnShellNavigating;
        AreStatusAnimationsEnabled = true;
        OnPropertyChanged(nameof(AreStatusAnimationsEnabled));
        _ = ViewModel.OnAppearingAsync();
    }
    protected override void OnDisappearing()
    {
        AreStatusAnimationsEnabled = false;
        OnPropertyChanged(nameof(AreStatusAnimationsEnabled));
        ViewModel.OnDisappearing();
        Shell.Current.Navigating -= OnShellNavigating;
        base.OnDisappearing();
    }

    private void OnShellNavigating(object? sender, ShellNavigatingEventArgs args)
    {
        // A back tap from members opened directly from home is classified as PopToRoot.
        if (args.Source is not (ShellNavigationSource.Pop or ShellNavigationSource.PopToRoot) ||
            !args.CanCancel || ViewModel.IsNavigating) return;
        args.Cancel();
        if (_backNavigationPending) return;
        _backNavigationPending = true;
        // Finish cancelling the native back action before replacing its destination.
        Dispatcher.Dispatch(async () =>
        {
            try { await ViewModel.BackCommand.ExecuteAsync(null); }
            finally { _backNavigationPending = false; }
        });
    }
}
