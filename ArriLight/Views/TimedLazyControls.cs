namespace ArriLight;

// Same control types/layout behavior in both configurations. Release has no
// observers, clocks, statistics or diagnostic calls in these subclasses.
public class TimedLazyContentView : ContentView
{
#if DEBUG && ANDROID
    protected override Size MeasureOverride(double widthConstraint, double heightConstraint)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try { return base.MeasureOverride(widthConstraint, heightConstraint); }
        finally { ControlNavigationTiming.Current?.RecordLayout(this, "Measure", started); }
    }
    protected override Size ArrangeOverride(Rect bounds)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try { return base.ArrangeOverride(bounds); }
        finally { ControlNavigationTiming.Current?.RecordLayout(this, "Arrange", started); }
    }
#endif
}

public class TimedPresetBorder : Border
{
#if DEBUG && ANDROID
    public TimedPresetBorder() => ControlNavigationTiming.Current?.ObservePresetElement(this);
    protected override Size MeasureOverride(double widthConstraint, double heightConstraint)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try { return base.MeasureOverride(widthConstraint, heightConstraint); }
        finally { ControlNavigationTiming.Current?.RecordLayout(this, "Measure", started); }
    }
    protected override Size ArrangeOverride(Rect bounds)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try { return base.ArrangeOverride(bounds); }
        finally { ControlNavigationTiming.Current?.RecordLayout(this, "Arrange", started); }
    }
#endif
}

public class TimedPresetButton : Button
{
#if DEBUG && ANDROID
    public TimedPresetButton() => ControlNavigationTiming.Current?.ObservePresetElement(this);
    protected override Size MeasureOverride(double widthConstraint, double heightConstraint)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try { return base.MeasureOverride(widthConstraint, heightConstraint); }
        finally { ControlNavigationTiming.Current?.RecordLayout(this, "Measure", started); }
    }
    protected override Size ArrangeOverride(Rect bounds)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try { return base.ArrangeOverride(bounds); }
        finally { ControlNavigationTiming.Current?.RecordLayout(this, "Arrange", started); }
    }
#endif
}

public class TimedPresetCollectionView : CollectionView
{
#if DEBUG && ANDROID
    public TimedPresetCollectionView() => ControlNavigationTiming.Current?.ObservePresetElement(this);
    protected override Size MeasureOverride(double widthConstraint, double heightConstraint)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try { return base.MeasureOverride(widthConstraint, heightConstraint); }
        finally { ControlNavigationTiming.Current?.RecordLayout(this, "Measure", started); }
    }
    protected override Size ArrangeOverride(Rect bounds)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try { return base.ArrangeOverride(bounds); }
        finally { ControlNavigationTiming.Current?.RecordLayout(this, "Arrange", started); }
    }
#endif
}
