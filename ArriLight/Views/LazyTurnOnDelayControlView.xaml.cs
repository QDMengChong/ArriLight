namespace ArriLight;

public partial class LazyTurnOnDelayControlView : TimedLazyContentView
{
    public LazyTurnOnDelayControlView()
    {
        InitializeComponent();
#if DEBUG && ANDROID
        ControlNavigationTiming.Current?.ObserveLazyContent(this);
#endif
    }

    public event EventHandler<FocusEventArgs>? SecondsFocused;
    public event EventHandler<TextChangedEventArgs>? SecondsTextChanged;
    public Entry SecondsEntry => TurnOnDelaySecondsEntryElement;

    private void OnSecondsFocused(object? sender, FocusEventArgs e) => SecondsFocused?.Invoke(sender, e);
    private void OnSecondsTextChanged(object? sender, TextChangedEventArgs e) => SecondsTextChanged?.Invoke(sender, e);
}
