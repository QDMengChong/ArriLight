namespace ArriLight;

public partial class DeviceControlPage
{
    // Navigation scheduling is production code; the session initializer and
    // rendered controls are replaced at the MAUI boundary in these host tests.
    private BluetoothDiscoveryService _discoveryService = null!;
    private bool _isDemo;
    private readonly TestTransitionView ControlPageContent = new();
    internal TestTransitionView TransitionForTest => ControlPageContent;
    public string ConnectionStatusText { get; private set; } = "正在准备设备连接…";
    public bool CanConnect { get; private set; }
    public bool CanRefreshStatus => !_automaticConnectionPending;
    internal int InitializationCalls { get; private set; }
    internal Func<CancellationToken, Task>? OnInitialize { get; set; }
    internal Task? DeferredInitializationForTest => _deferredConnectionInitializationTask;
    internal void AppearConnectionForTest(BluetoothDiscoveryService discovery, bool isDemo = false)
    {
        _discoveryService = discovery;
        _isDemo = isDemo;
        BeginControlPageAppearance();
    }
    internal void NavigatedForTest() => OnNavigatedTo(new NavigatedToEventArgs());
    internal void LeaveConnectionForTest() => EndControlPageAppearance();

    private Task EnsureConnectedAsync(
        bool showError, bool refreshStatus = false, CancellationToken cancellationToken = default,
        bool scanAlreadyStopped = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InitializationCalls++;
        return OnInitialize?.Invoke(cancellationToken) ?? Task.CompletedTask;
    }
}

public sealed class Easing
{
    public static Easing CubicOut { get; } = new();
}

public sealed class TestTransitionView
{
    public double Opacity { get; set; } = 1;
    public double TranslationX { get; set; }
    public double TranslationY { get; set; }
    public TaskCompletionSource<bool>? Completion { get; set; }
    public List<uint> Durations { get; } = [];
    public void CancelAnimations() => Completion?.TrySetResult(true);
    public async Task<bool> FadeToAsync(double opacity, uint length, Easing easing)
    {
        Durations.Add(length);
        var canceled = Completion is not null && await Completion.Task;
        if (!canceled) Opacity = opacity;
        return canceled;
    }
    public async Task<bool> TranslateToAsync(double x, double y, uint length, Easing easing)
    {
        Durations.Add(length);
        var canceled = Completion is not null && await Completion.Task;
        if (!canceled) { TranslationX = x; TranslationY = y; }
        return canceled;
    }
}
