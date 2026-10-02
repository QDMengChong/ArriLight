namespace ArriLight;

public partial class LazySavePresetButtonView : TimedLazyContentView
{
    public LazySavePresetButtonView()
    {
        InitializeComponent();
#if DEBUG && ANDROID
        ControlNavigationTiming.Current?.ObserveLazyContent(this);
#endif
    }

    public event EventHandler? SaveClicked;
    public Button SavePresetButton => SavePresetButtonElement;

    private void OnSavePresetClicked(object? sender, EventArgs e) => SaveClicked?.Invoke(sender, e);
}
