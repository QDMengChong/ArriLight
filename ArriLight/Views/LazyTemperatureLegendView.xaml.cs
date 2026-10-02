namespace ArriLight;

public partial class LazyTemperatureLegendView : TimedLazyContentView
{
    public LazyTemperatureLegendView()
    {
        InitializeComponent();
#if DEBUG && ANDROID
        ControlNavigationTiming.Current?.ObserveLazyContent(this);
#endif
    }
}
