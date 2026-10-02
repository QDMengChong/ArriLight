using ArriLight.Controls;

namespace ArriLight;

public partial class LazyColorControlView : TimedLazyContentView
{
    public LazyColorControlView()
    {
        InitializeComponent();
#if DEBUG && ANDROID
        ControlNavigationTiming.Current?.ObserveLazyContent(this);
#endif
    }

    public event EventHandler? SelectionChanged;
    public ColorTemperaturePicker ColorPicker => ColorPickerElement;

    private void OnColorPickerSelectionChanged(object? sender, EventArgs e) =>
        SelectionChanged?.Invoke(sender, e);
}
