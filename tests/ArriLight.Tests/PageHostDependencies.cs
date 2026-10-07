namespace ArriLight;

// In-memory MAUI boundaries let tests exercise the actual page lifecycle source.
// Android's native dialog is checked by the platform build, not rendered here.
public class BindableObject
{
    private object? _bindingContext;
    public object? BindingContext
    {
        get => _bindingContext;
        set { _bindingContext = value; if (this is ContentPage page) TestUserInteraction.CurrentPage = page; }
    }
}

public class Element : BindableObject;

public abstract class RouteFactory
{
    public abstract Element GetOrCreate();
    public virtual Element GetOrCreate(IServiceProvider services) => GetOrCreate();
}

public class ContentPage : Element
{
    public Func<string, string, string, string, Task<bool>>? OnConfirmation { get; set; }
    public List<(string Title, string Message)> Alerts { get; } = [];
    public TestDispatcher Dispatcher { get; } = new();
    protected virtual void OnAppearing() { }
    protected virtual void OnDisappearing() { }
    protected virtual void OnNavigatedTo(NavigatedToEventArgs args) { }
    protected virtual bool OnBackButtonPressed() => false;
    protected void OnPropertyChanged(string? propertyName = null) { }
    internal Task DisplayAlertAsync(string title, string message, string cancel)
    {
        Alerts.Add((title, message));
        return Task.CompletedTask;
    }
    internal Task<bool> DisplayAlertAsync(string title, string message, string accept, string cancel)
    {
        Alerts.Add((title, message));
        return OnConfirmation?.Invoke(title, message, accept, cancel) ??
            throw new NotSupportedException("Native OTA confirmation is not available in host tests.");
    }
    protected Task<string?> DisplayPromptAsync(
        string title, string message, string accept, string cancel, string placeholder = "",
        string initialValue = "", int maxLength = -1) => Task.FromResult<string?>(initialValue);
    protected Task<string?> DisplayPromptAsync(
        string title, string message, string accept, string cancel, string placeholder, Keyboard keyboard) =>
        throw new NotSupportedException("Native OTA prompts are not available in host tests.");
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
    public static Task InvokeOnMainThreadAsync(Func<Task> action) => action();
}

public static class ServiceHelper
{
    private static readonly Dictionary<Type, object> Services = [];
    public static void Set<T>(T value) where T : class => Services[typeof(T)] = value;
    public static T GetRequiredService<T>() where T : class
    {
        if (Services.TryGetValue(typeof(T), out var value)) return (T)value;
        if (typeof(T) == typeof(IUiDispatcher)) return (T)(object)new TestUiDispatcher();
        if (typeof(T) == typeof(IAppNavigator)) return (T)(object)new TestAppNavigator();
        if (typeof(T) == typeof(IUserInteraction)) return (T)(object)new TestUserInteraction();
        if (typeof(T) == typeof(IApplicationPresentation)) return (T)(object)new TestApplicationPresentation();
        if (typeof(T) == typeof(IFirmwareFilePicker)) return (T)(object)new TestFirmwareFilePicker();
        if (typeof(T).Namespace == "ArriLight.ViewModels")
        {
            var constructor = typeof(T).GetConstructors().Single();
            var arguments = constructor.GetParameters().Select(parameter =>
                typeof(ServiceHelper).GetMethod(nameof(GetRequiredService))!.MakeGenericMethod(parameter.ParameterType).Invoke(null, null)).ToArray();
            return (T)constructor.Invoke(arguments);
        }
        throw new KeyNotFoundException(typeof(T).FullName);
    }
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
    public static implicit operator PresentationColor(Color color) => PresentationColor.FromArgb(color.Value);
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
    internal Task TogglePowerForTestAsync(LightDeviceItem device) => ViewModel.TogglePowerCommand.ExecuteAsync(device);
    internal Task QueryForTestAsync(LightDeviceItem device, TimeSpan timeout) =>
        ViewModel.QueryForTestAsync(device, timeout);
    internal void MarkOfflineForTest(LightDeviceItem device) => ViewModel.MarkOfflineForTest(device);
    internal Task OpenControlForTestAsync(LightDeviceItem device) => ViewModel.OpenControlCommand.ExecuteAsync(device);
    internal void SetHomeRefreshForTest(Task task, CancellationTokenSource cancellation)
    {
        ViewModel.SetHomeRefreshForTest(task, cancellation);
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
    internal Task? CountdownTask => ViewModel.CountdownForTest;
    internal Task AddForTestAsync(string name, string address) => ViewModel.AddForTestAsync(name, address);
    internal void CancelForTest() => ViewModel.CancelBindingCommand.Execute(null);
    internal void DisappearForTest() => OnDisappearing();
    internal Task ShowProgressForTestAsync(CancellationToken token) =>
        ViewModel.ShowProgressForTestAsync(token);

    internal void SetPageCancellationForTest(CancellationTokenSource source)
    {
        ViewModel.SetPageCancellationForTest(source);
    }

    internal void ExpireCountdownForTest() => ViewModel.ExpireCountdownForTest();
}
