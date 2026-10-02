namespace ArriLight;

public partial class LazyEffectSettingsOverlayView : TimedLazyContentView
{
    public LazyEffectSettingsOverlayView()
    {
        InitializeComponent();
#if DEBUG && ANDROID
        ControlNavigationTiming.Current?.ObserveLazyContent(this);
#endif
    }

    public event EventHandler<ValueChangedEventArgs>? PeriodChanged;
    public event EventHandler? CancelClicked;
    public event EventHandler? ApplyClicked;
    public Label EffectSettingsTitle => EffectSettingsTitleElement;
    public Label EffectSettingsPeriodLabel => EffectSettingsPeriodLabelElement;
    public Slider EffectSettingsPeriodSlider => EffectSettingsPeriodSliderElement;

    private void OnEffectSettingsPeriodChanged(object? sender, ValueChangedEventArgs e) => PeriodChanged?.Invoke(sender, e);
    private void OnEffectSettingsCancelClicked(object? sender, EventArgs e) => CancelClicked?.Invoke(sender, e);
    private void OnEffectSettingsApplyClicked(object? sender, EventArgs e) => ApplyClicked?.Invoke(sender, e);
}
