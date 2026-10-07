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
    internal Task TapForTestAsync(DateTimeOffset now) => HandleVersionTapAsync(now);
    internal Task OpenConfigurationForTestAsync() => OpenConfigurationEditorAsync();
}

public partial class DebugConfigurationPage
{
    private void InitializeComponent() { }
    internal Task LoadForTestAsync() => LoadConfigurationAsync();
    internal Task SaveForTestAsync() => SaveConfigurationAsync();
}
