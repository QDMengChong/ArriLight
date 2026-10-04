namespace ArriLight;

// Only the native window boundary is replaced. Tests raise the events handled
// by the real App so an Activity restart reuses the same application services.
public enum AppTheme { Unspecified, Light, Dark }

public interface IActivationState;

public class Application
{
    public AppTheme UserAppTheme { get; set; }
    protected virtual Window CreateWindow(IActivationState? activationState) =>
        throw new NotSupportedException();
}

public sealed class Window(AppShell page)
{
    public AppShell Page { get; } = page;
    public event EventHandler? Created;
    public event EventHandler? Stopped;
    public event EventHandler? Resumed;
    public event EventHandler? Destroying;

    public void CreateNativeWindow() => Created?.Invoke(this, EventArgs.Empty);
    public void Stop() => Stopped?.Invoke(this, EventArgs.Empty);
    public void Resume() => Resumed?.Invoke(this, EventArgs.Empty);
    public void DestroyNativeWindow() => Destroying?.Invoke(this, EventArgs.Empty);
}

public sealed class AppShell;

public partial class App
{
    private void InitializeComponent() { }
    internal Window CreateWindowForTest() => CreateWindow(null);
}
