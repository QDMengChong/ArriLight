namespace ArriLight;

public partial class LazyBrightnessControlView : TimedLazyContentView
{
    public LazyBrightnessControlView()
    {
        InitializeComponent();
#if DEBUG && ANDROID
        ControlNavigationTiming.Current?.ObserveLazyContent(this);
#endif
    }

    public event EventHandler<ValueChangedEventArgs>? BrightnessChanged;
    public Grid BrightnessControlRow => BrightnessControlRowElement;
    public Slider BrightnessSlider => BrightnessSliderElement;

    private void OnBrightnessChanged(object? sender, ValueChangedEventArgs e) =>
        BrightnessChanged?.Invoke(sender, e);
}
