namespace ArriLight;

public partial class LazyTimerControlView : TimedLazyContentView
{
    public LazyTimerControlView()
    {
        InitializeComponent();
#if DEBUG && ANDROID
        ControlNavigationTiming.Current?.ObserveLazyContent(this);
#endif
    }

    public event EventHandler<FocusEventArgs>? DelayFocused;
    public event EventHandler<TextChangedEventArgs>? DelayTextChanged;
    public event EventHandler? SendClicked;
    public Border RelativeTimerCard => RelativeTimerCardElement;
    public Picker TimerModePicker => TimerModePickerElement;
    public Entry DelaySecondsEntry => DelaySecondsEntryElement;

    private void OnDelaySecondsFocused(object? sender, FocusEventArgs e) => DelayFocused?.Invoke(sender, e);
    private void OnDelaySecondsTextChanged(object? sender, TextChangedEventArgs e) => DelayTextChanged?.Invoke(sender, e);
    private void OnTimerSendClicked(object? sender, EventArgs e) => SendClicked?.Invoke(sender, e);
}
