using System.Collections.ObjectModel;
namespace ArriLight;
public partial class MainPage : ContentPage
{
    private readonly AppDiagnosticService _diagnostics;
    private CancellationTokenSource? _controlPagePreloadCancellation;
    public MainPageViewModel ViewModel { get; }
    public ObservableCollection<LightDeviceItem> Devices => ViewModel.Devices;
    public MainPage() : this(ServiceHelper.GetRequiredService<MainPageViewModel>()) { }
    public MainPage(MainPageViewModel viewModel)
    {
        InitializeComponent();
        _diagnostics = ServiceHelper.GetRequiredService<AppDiagnosticService>();
        ViewModel = viewModel;
        BindingContext = viewModel;
        viewModel.PreparingControl += OnPreparingControl;
        viewModel.ControlNavigationCompleted += OnControlNavigationCompleted;
        viewModel.ControlNavigationFailed += OnControlNavigationFailed;
    }
    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = ViewModel.OnAppearingAsync();
        CancelControlPagePreload();
        _controlPagePreloadCancellation = new CancellationTokenSource();
        _ = PreloadControlPageAsync(_controlPagePreloadCancellation.Token);
    }
    protected override void OnDisappearing()
    {
        CancelControlPagePreload();
        ViewModel.OnDisappearing();
        base.OnDisappearing();
    }
    private void OnPreparingControl(string address)
    {
        CancelControlPagePreload();
#if DEBUG && ANDROID
        ControlNavigationTiming.Start(_diagnostics, address);
#endif
    }
    private void OnControlNavigationCompleted()
    {
#if DEBUG && ANDROID
        ControlNavigationTiming.Current?.Mark("GoToAsync.Returned");
#endif
    }
    private void OnControlNavigationFailed(Exception exception)
    {
#if DEBUG && ANDROID
        ControlNavigationTiming.Current?.Mark("NavigationFailed", exception.GetType().Name);
        ControlNavigationTiming.Current?.Dispose();
#endif
    }
    private void CancelControlPagePreload()
    {
        _ = CancellationDispatch.Request(_controlPagePreloadCancellation, _diagnostics,
            "Home.Preload", dispose: true);
        _controlPagePreloadCancellation = null;
    }
    private async Task PreloadControlPageAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Let the home page render first. Do not warm every device or keep
            // pages that have already been used for a different device.
            await Task.Delay(250, cancellationToken);
            Dispatcher.Dispatch(() =>
            {
                if (cancellationToken.IsCancellationRequested || !ViewModel.CanPreloadControl)
                    return;
                try { DeviceControlPageRoutes.Prepare(isDemo: Devices.All(device => device.IsDemo)); }
                catch (Exception exception)
                {
                    _diagnostics.Warning("UI/Navigation", $"控制页预加载失败，点击时重新创建：{exception.Message}");
                }
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }
}
