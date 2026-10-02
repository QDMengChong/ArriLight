namespace ArriLight;

// In-memory MAUI boundaries let tests exercise the actual page lifecycle source.
// Android's native dialog is checked by the platform build, not rendered here.
public class BindableObject
{
    public object? BindingContext { get; set; }
}

public class Element : BindableObject;

public abstract class RouteFactory
{
    public abstract Element GetOrCreate();
    public virtual Element GetOrCreate(IServiceProvider services) => GetOrCreate();
}

public class ContentPage : Element
{
    public TestDispatcher Dispatcher { get; } = new();
    protected virtual void OnAppearing() { }
    protected virtual void OnDisappearing() { }
    protected virtual void OnNavigatedTo(NavigatedToEventArgs args) { }
    protected virtual bool OnBackButtonPressed() => false;
    protected void OnPropertyChanged(string? propertyName = null) { }
    protected Task DisplayAlertAsync(string title, string message, string cancel) => Task.CompletedTask;
    protected Task<string?> DisplayPromptAsync(
        string title, string message, string accept, string cancel, string placeholder,
        string initialValue, int maxLength) => Task.FromResult<string?>(initialValue);
}

public sealed class NavigatedToEventArgs : EventArgs;
public sealed class TappedEventArgs : EventArgs;

public sealed class TestDispatcher
{
    public event Action? Dispatched;
    public Queue<Action> PendingActions { get; } = new();
    public bool Dispatch(Action action)
    {
        PendingActions.Enqueue(action);
        Dispatched?.Invoke();
        return true;
    }
    public void RunNext() => PendingActions.Dequeue()();
}

public static class MainThread
{
    public static Queue<Action>? PendingActions { get; set; }
    public static void BeginInvokeOnMainThread(Action action)
    {
        if (PendingActions is { } pending)
            pending.Enqueue(action);
        else
            action();
    }
    public static Task InvokeOnMainThreadAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }
}

public static class ServiceHelper
{
    private static readonly Dictionary<Type, object> Services = [];
    public static void Set<T>(T value) where T : class => Services[typeof(T)] = value;
    public static T GetRequiredService<T>() where T : class => (T)Services[typeof(T)];
}

public static class ToastService
{
    public static List<string> Messages { get; } = [];
    public static void Show(string message) => Messages.Add(message);
    public static void ShowForDuration(string message, TimeSpan duration) => Messages.Add(message);
}

public sealed record Color(string Value)
{
    public static Color FromArgb(string value) => new(value);
}

public static class Colors
{
    public static Color Transparent { get; } = new("Transparent");
}

public sealed class QrCodeScanPage;
public sealed class HslCtlDeviceControlPage;

public partial class MainPage
{
    private void InitializeComponent() { }
    internal void AppearForTest() => OnAppearing();
    internal void DisappearForTest() => OnDisappearing();
    internal Task TogglePowerForTestAsync(LightDeviceItem device) => ToggleDevicePowerAsync(device);
    internal Task QueryForTestAsync(LightDeviceItem device, TimeSpan timeout) =>
        QueryDeviceStatusAsync(device, timeout, CancellationToken.None);
    internal void MarkOfflineForTest(LightDeviceItem device) => MarkDeviceOffline(device);
    internal Task OpenControlForTestAsync(LightDeviceItem device) => OpenDeviceControlAsync(device);
    internal void SetHomeRefreshForTest(Task task, CancellationTokenSource cancellation)
    {
        _statusRefreshTask = task;
        _statusRefreshCancellation = cancellation;
    }
}

public sealed class Shell
{
    public static Shell Current { get; } = new();
    public List<string> Routes { get; } = [];
    public List<bool> AnimationRequests { get; } = [];
    public Func<string, Task>? OnNavigate { get; set; }
    public Task GoToAsync(string route, bool animate = true)
    {
        Routes.Add(route);
        AnimationRequests.Add(animate);
        return OnNavigate?.Invoke(route) ?? Task.CompletedTask;
    }
}

internal static class DeviceControlPageRoutes
{
    internal static int PreparationCalls { get; set; }
    internal static bool LastPreparationWasDemo { get; private set; }
    internal static void Prepare(bool isDemo)
    {
        PreparationCalls++;
        LastPreparationWasDemo = isDemo;
    }
}

public sealed class SelectionChangedEventArgs : EventArgs
{
    public IReadOnlyList<object> CurrentSelection { get; init; } = [];
}

public enum ScrollToPosition { Start }

public sealed class TestCollectionView
{
    public object? SelectedItem { get; set; }
    public void ScrollTo(int index, ScrollToPosition position, bool animate) { }
}

public sealed class TestLabel
{
    public string Text { get; set; } = string.Empty;
}

public sealed class TestOverlay
{
    public bool IsVisible { get; set; }
}

public partial class BluetoothScanPage
{
    private readonly TestOverlay BindingPromptOverlay = new();
    private readonly TestLabel BindingPromptStatus = new();
    private readonly TestLabel BindingPromptCountdown = new();
    private readonly TestCollectionView DeviceCollection = new();
    private void InitializeComponent() { }

    internal bool PromptIsVisible => BindingPromptOverlay.IsVisible;
    internal Task? CountdownTask => _bindingCountdownTask;
    internal Task AddForTestAsync(string name, string address) => AddDeviceAsync(name, address);
    internal void CancelForTest() => OnCancelBindingClicked(this, EventArgs.Empty);
    internal void DisappearForTest() => OnDisappearing();
    internal Task ShowProgressForTestAsync(CancellationToken token) =>
        UpdateBindingProgressAsync(AgpBindingStage.Connecting, token, _bindingCancellation!.Token);

    internal void SetPageCancellationForTest(CancellationTokenSource source)
    {
        _bindingCancellation = source;
        _bindingDeadline = DateTimeOffset.UtcNow + AgpLightControlService.BindingTimeout;
    }

    internal void ExpireCountdownForTest() => _bindingDeadline = DateTimeOffset.UtcNow;
}
