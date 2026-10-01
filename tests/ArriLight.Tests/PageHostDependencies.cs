namespace ArriLight;

// In-memory MAUI boundaries let tests exercise the actual page lifecycle source.
// Android's native dialog is checked by the platform build, not rendered here.
public class ContentPage
{
    public object? BindingContext { get; set; }
    protected virtual void OnAppearing() { }
    protected virtual void OnDisappearing() { }
    protected virtual bool OnBackButtonPressed() => false;
    protected void OnPropertyChanged() { }
    protected Task DisplayAlertAsync(string title, string message, string cancel) => Task.CompletedTask;
    protected Task<string?> DisplayPromptAsync(
        string title, string message, string accept, string cancel, string placeholder,
        string initialValue, int maxLength) => Task.FromResult<string?>(initialValue);
}

public static class MainThread
{
    public static void BeginInvokeOnMainThread(Action action) => action();
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
    public static void ShowForDuration(string message, TimeSpan duration) => Messages.Add(message);
}

public sealed class Shell
{
    public static Shell Current { get; } = new();
    public List<string> Routes { get; } = [];
    public Task GoToAsync(string route)
    {
        Routes.Add(route);
        return Task.CompletedTask;
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
