#if DEBUG && ANDROID
using System.Diagnostics;

namespace ArriLight;

public partial class DeviceControlPage
{
    private ControlNavigationTiming? _navigationTiming;
    private int _navigationSizeSamples;

    internal void AttachNavigationTiming(ControlNavigationTiming timing)
    {
        _navigationTiming?.Dispose();
        _navigationTiming = timing;
        _navigationSizeSamples = 0;
        timing.Attach(this);
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        var start = Stopwatch.GetTimestamp();
        base.OnSizeAllocated(width, height);
        if (_navigationSizeSamples++ < 4)
            _navigationTiming?.Mark("Page.SizeAllocated", string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"size={width:0.0}x{height:0.0}; pageCallback={Stopwatch.GetElapsedTime(start).TotalMilliseconds:0.0}ms; 仅页面尺寸回调，不含所有子控件布局"));
    }
}
#endif
