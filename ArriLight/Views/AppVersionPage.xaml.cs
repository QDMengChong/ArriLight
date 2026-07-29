namespace ArriLight;

public partial class AppVersionPage : ContentPage
{
    private static readonly TimeSpan TapSequenceTimeout =
        TimeSpan.FromSeconds(2);

    private readonly AppDiagnosticService _diagnostics;
    private int _versionTapCount;
    private DateTimeOffset _lastVersionTapAt;

    public string VersionText => $"版本 {AppInfo.Current.VersionString}（{AppInfo.Current.BuildString}）";

    public AppVersionPage()
    {
        InitializeComponent();
        _diagnostics = ServiceHelper.GetRequiredService<AppDiagnosticService>();
        BindingContext = this;
    }

    private async void OnVersionTapped(object? sender, TappedEventArgs e)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastVersionTapAt > TapSequenceTimeout)
            _versionTapCount = 0;

        _lastVersionTapAt = now;
        _versionTapCount++;
        if (_versionTapCount < 5)
            return;

        _versionTapCount = 0;
        var enabling = !_diagnostics.IsEnabled;
        var confirmed = await DisplayAlertAsync(
            "调试文件输出",
            enabling
                ? $"是否打开调试文件输出？\n\n日志文件：\n{_diagnostics.LogFilePath}\n"
                : $"调试文件输出当前已开启。\n\n日志文件：\n{_diagnostics.LogFilePath}\n\n是否关闭文件输出？",
            enabling ? "打开" : "关闭",
            "取消");

        if (!confirmed)
            return;

        _diagnostics.IsEnabled = enabling;
        await DisplayAlertAsync(
            "调试文件输出",
            enabling
                ? $"已打开。\n日志将追加写入：\n{_diagnostics.LogFilePath}"
                : "文件输出已关闭，已有日志会保留。",
            "知道了");
    }
}
