namespace ArriLight;

public sealed class AppInfo
{
    public static AppInfo Current { get; } = new();
    public string VersionString => "1.0";
    public string BuildString => "2";
}

public partial class AppVersionPage
{
    private void InitializeComponent() { }
    internal Task TapForTestAsync(DateTimeOffset now) => ViewModel.TapForTestAsync(now);
    internal Task OpenConfigurationForTestAsync() => ViewModel.OpenConfigurationEditorCommand.ExecuteAsync(null);
}

public partial class DebugConfigurationPage
{
    private void InitializeComponent() { }
    internal Task LoadForTestAsync() => ViewModel.OnAppearingAsync();
    internal Task SaveForTestAsync() => ViewModel.SaveConfigurationCommand.ExecuteAsync(null);
}
