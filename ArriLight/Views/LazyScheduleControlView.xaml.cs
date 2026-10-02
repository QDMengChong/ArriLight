namespace ArriLight;

public partial class LazyScheduleControlView : TimedLazyContentView
{
    public LazyScheduleControlView()
    {
        InitializeComponent();
#if DEBUG && ANDROID
        ControlNavigationTiming.Current?.ObserveLazyContent(this);
#endif
    }

    public event EventHandler<TappedEventArgs>? Tapped;
    public Border ScheduleSettingsCard => ScheduleSettingsCardElement;

    private void OnScheduleTapped(object? sender, TappedEventArgs e) => Tapped?.Invoke(sender, e);
}
