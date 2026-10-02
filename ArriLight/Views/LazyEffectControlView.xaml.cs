namespace ArriLight;

public partial class LazyEffectControlView : TimedLazyContentView
{
    public LazyEffectControlView()
    {
        InitializeComponent();
#if DEBUG && ANDROID
        ControlNavigationTiming.Current?.ObserveLazyContent(this);
#endif
    }

    public event EventHandler? EffectClicked;
    public event EventHandler? EffectPressed;
    public event EventHandler? EffectReleased;
    public Button SteadyEffectButton => SteadyEffectButtonElement;
    public Button BlinkEffectButton => BlinkEffectButtonElement;
    public Button BreatheEffectButton => BreatheEffectButtonElement;

    private void OnEffectClicked(object? sender, EventArgs e) => EffectClicked?.Invoke(sender, e);
    private void OnEffectPressed(object? sender, EventArgs e) => EffectPressed?.Invoke(sender, e);
    private void OnEffectReleased(object? sender, EventArgs e) => EffectReleased?.Invoke(sender, e);
}
