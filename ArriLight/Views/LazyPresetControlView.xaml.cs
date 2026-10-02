namespace ArriLight;

public partial class LazyPresetControlView : TimedLazyContentView
{
    public LazyPresetControlView()
    {
        InitializeComponent();
#if DEBUG && ANDROID
        ControlNavigationTiming.Current?.ObserveLazyContent(this);
#endif
    }

    public event EventHandler? FinishReorderClicked;
    public event EventHandler? PresetClicked;
    public event EventHandler? PresetPressed;
    public event EventHandler? PresetReleased;
    public event EventHandler<DragStartingEventArgs>? PresetDragStarting;
    public event EventHandler<DropEventArgs>? PresetDrop;
    public VerticalStackLayout CustomPresetSection => CustomPresetSectionElement;
    public Label PresetModeLabel => PresetModeLabelElement;
    public Button FinishReorderButton => FinishReorderButtonElement;

    private void OnFinishReorderClicked(object? sender, EventArgs e) => FinishReorderClicked?.Invoke(sender, e);
    private void OnPresetClicked(object? sender, EventArgs e) => PresetClicked?.Invoke(sender, e);
    private void OnPresetPressed(object? sender, EventArgs e) => PresetPressed?.Invoke(sender, e);
    private void OnPresetReleased(object? sender, EventArgs e) => PresetReleased?.Invoke(sender, e);
    private void OnPresetDragStarting(object? sender, DragStartingEventArgs e) => PresetDragStarting?.Invoke(sender, e);
    private void OnPresetDrop(object? sender, DropEventArgs e) => PresetDrop?.Invoke(sender, e);
}
