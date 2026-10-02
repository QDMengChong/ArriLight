#if DEBUG && ANDROID
using System.Diagnostics;
using System.Globalization;
using Android.Views;
using Window = Android.Views.Window;

namespace ArriLight;

// Diagnostic observers only: never change navigation, layout or draw scheduling.
// Excluded entirely from Release and from non-Android builds.
internal sealed class ControlNavigationTiming : IDisposable
{
    private static long _nextId;
    internal static ControlNavigationTiming? Current { get; private set; }
    private readonly AppDiagnosticService _diagnostics;
    private readonly string _address;
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly long _id = Interlocked.Increment(ref _nextId);
    private readonly Dictionary<VisualElement, long> _handlerStarts = [];
    private readonly HashSet<VisualElement> _observed = [];
    private readonly Dictionary<string, (int Count, double Total, double Max)> _handlers = [];
    private DeviceControlPage? _page;
    private Android.Views.View? _nativeView;
    private NativeObserver? _nativeObserver;
    private Window? _window;
    private FrameObserver? _frameObserver;
    private Android.OS.Handler? _frameHandler;
    private bool _disposed;
    private int _layoutCount;
    private int _frameCount;
    private bool _preDrawSeen;
    private bool _drawSeen;
    private long _firstHandlerStart;
    private long _lastHandlerEnd;

    private ControlNavigationTiming(AppDiagnosticService diagnostics, string address)
    {
        _diagnostics = diagnostics;
        _address = address;
        Mark("TapAccepted", "触摸手势已触发；不是手指按下时间");
        if (OperatingSystem.IsAndroidVersionAtLeast(24) &&
            Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Window is { } window)
        {
            _window = window;
            _frameObserver = new FrameObserver(this);
            _frameHandler = new Android.OS.Handler(Android.OS.Looper.MainLooper!);
            try
            {
                window.AddOnFrameMetricsAvailableListener(_frameObserver, _frameHandler);
            }
            catch (Exception exception)
            {
                Mark("FrameMetricsUnavailable", exception.GetType().Name);
                _frameObserver = null;
            }
        }
    }

    internal static void Start(AppDiagnosticService diagnostics, string address)
    {
        Current?.Dispose();
        Current = new ControlNavigationTiming(diagnostics, address);
    }

    internal void Attach(DeviceControlPage page)
    {
        _page = page;
        try
        {
            ObserveTree(page);
            if (Shell.GetTitleView(page) is { } title) ObserveTree(title);
            Mark("RoutePageReady", $"observedElements={_observed.Count}; handlersAlreadyPresent={_observed.Count(v => v.Handler is not null)}");
            AttachNativeObserver();
        }
        catch (Exception exception)
        {
            Mark("ObserversUnavailable", exception.GetType().Name);
        }
    }

    private void ObserveTree(IVisualTreeElement element)
    {
        if (element is VisualElement view && _observed.Add(view))
        {
            view.HandlerChanging += OnHandlerChanging;
            view.HandlerChanged += OnHandlerChanged;
        }
        foreach (var child in element.GetVisualChildren()) ObserveTree(child);
    }

    private void OnHandlerChanging(object? sender, HandlerChangingEventArgs args)
    {
        if (_disposed || sender is not VisualElement view || args.NewHandler is null) return;
        _handlerStarts[view] = Stopwatch.GetTimestamp();
        if (_firstHandlerStart == 0) _firstHandlerStart = _handlerStarts[view];
        if (ReferenceEquals(view, _page)) Mark("Page.HandlerChanging");
    }

    private void OnHandlerChanged(object? sender, EventArgs args)
    {
        if (_disposed || sender is not VisualElement view) return;
        if (view.Handler is not null) _lastHandlerEnd = Stopwatch.GetTimestamp();
        if (_handlerStarts.Remove(view, out var start))
        {
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var type = view.GetType().Name;
            var previous = _handlers.GetValueOrDefault(type);
            _handlers[type] = (previous.Count + 1, previous.Total + elapsed, Math.Max(previous.Max, elapsed));
        }
        if (ReferenceEquals(view, _page))
        {
            Mark("Page.HandlerChanged");
            AttachNativeObserver();
        }
    }

    private void AttachNativeObserver()
    {
        if (_nativeObserver is not null || _page?.Handler?.PlatformView is not Android.Views.View view) return;
        _nativeView = view;
        var observer = view.ViewTreeObserver;
        if (observer is null || !observer.IsAlive) return;
        _nativeObserver = new NativeObserver(this);
        try
        {
            observer.AddOnGlobalLayoutListener(_nativeObserver);
            observer.AddOnPreDrawListener(_nativeObserver);
            observer.AddOnDrawListener(_nativeObserver);
            Mark("NativeObserversAttached", $"nativeType={view.GetType().Name}; attached={view.IsAttachedToWindow}");
        }
        catch (Exception exception)
        {
            Mark("NativeObserversUnavailable", exception.GetType().Name);
        }
    }

    internal void Mark(string stage, string? detail = null)
    {
        if (_disposed) return;
        _diagnostics.Information("UI/Timing", string.Create(CultureInfo.InvariantCulture,
            $"navigation={_id:D6} stage={stage} sinceTap={Stopwatch.GetElapsedTime(_started).TotalMilliseconds:0.0}ms thread={Environment.CurrentManagedThreadId} mainThread={MainThread.IsMainThread} device={_address}") +
            (detail is null ? string.Empty : $" {detail}"));
    }

    private void GlobalLayout()
    {
        if (_disposed) return;
        if (++_layoutCount <= 2)
            Mark("Native.GlobalLayout", $"pass={_layoutCount}; size={_nativeView?.Width}x{_nativeView?.Height}; 回调位于布局后，不代表布局全过程耗时");
    }

    private void PreDraw()
    {
        if (_disposed || _preDrawSeen || _nativeView is not { IsShown: true, Width: > 0, Height: > 0 }) return;
        _preDrawSeen = true;
        Mark("Native.FirstPreDraw", "即将绘制；不是屏幕呈现时间");
    }

    private void Draw()
    {
        if (_drawSeen || _disposed || _nativeView is not { IsShown: true, Width: > 0, Height: > 0 }) return;
        _drawSeen = true;
        Mark("Native.FirstDrawCallback", "收到首次绘制观察回调；不保证像素已呈现到屏幕");
        var posted = Stopwatch.GetTimestamp();
        _nativeView?.Post(() => Mark("Native.AfterDrawPosted", string.Create(CultureInfo.InvariantCulture,
            $"queueWait={Stopwatch.GetElapsedTime(posted).TotalMilliseconds:0.0}ms; 绘制回调后的界面队列执行点，不是屏幕呈现确认")));
    }

    private void Frame(FrameMetrics metrics, int dropped)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(24) || _disposed || _frameCount >= 6) return;
        _frameCount++;
        Mark("Android.WindowFrame", string.Create(CultureInfo.InvariantCulture,
            $"frame={_frameCount}; layoutMeasure={metrics.GetMetric((int)FrameMetricsId.LayoutMeasureDuration) / 1_000_000d:0.0}ms; draw={metrics.GetMetric((int)FrameMetricsId.DrawDuration) / 1_000_000d:0.0}ms; total={metrics.GetMetric((int)FrameMetricsId.TotalDuration) / 1_000_000d:0.0}ms; firstWindowDraw={metrics.GetMetric((int)FrameMetricsId.FirstDrawFrame)}; droppedReports={dropped}; window级统计，可能包含主页或过渡帧；sinceTap为回调到达时间"));
    }

    public void Dispose()
    {
        if (_disposed) return;
        Mark("Summary", $"observedElements={_observed.Count}; layouts={_layoutCount}; firstDrawCallback={_drawSeen}; HandlerChanging→Changed仅为事件区间，可能嵌套，不能相加当作原生创建总耗时");
        if (_firstHandlerStart != 0 && _lastHandlerEnd != 0)
            Mark("HandlerWindow", string.Create(CultureInfo.InvariantCulture,
                $"firstChangingSinceTap={Stopwatch.GetElapsedTime(_started, _firstHandlerStart).TotalMilliseconds:0.0}ms; lastChangedSinceTap={Stopwatch.GetElapsedTime(_started, _lastHandlerEnd).TotalMilliseconds:0.0}ms; 仅统计路由返回时已存在的可视树元素"));
        foreach (var entry in _handlers.OrderByDescending(entry => entry.Value.Total).Take(10))
            Mark("HandlerSummary", string.Create(CultureInfo.InvariantCulture,
                $"type={entry.Key}; count={entry.Value.Count}; eventIntervalsTotal={entry.Value.Total:0.0}ms; max={entry.Value.Max:0.0}ms"));
        _disposed = true;
        foreach (var view in _observed)
        {
            view.HandlerChanging -= OnHandlerChanging;
            view.HandlerChanged -= OnHandlerChanged;
        }
        try
        {
            if (_nativeObserver is not null && _nativeView?.ViewTreeObserver is { IsAlive: true } observer)
            {
                observer.RemoveOnGlobalLayoutListener(_nativeObserver);
                observer.RemoveOnPreDrawListener(_nativeObserver);
                observer.RemoveOnDrawListener(_nativeObserver);
            }
            if (OperatingSystem.IsAndroidVersionAtLeast(24) && _frameObserver is not null)
                _window?.RemoveOnFrameMetricsAvailableListener(_frameObserver);
        }
        catch (Exception exception)
        {
            _diagnostics.Debug("UI/Timing", $"计时监听器移除时窗口已失效：{exception.GetType().Name}");
        }
        _frameHandler?.Dispose();
        _observed.Clear();
        _handlerStarts.Clear();
        _page = null;
        _nativeView = null;
        _nativeObserver = null;
        _window = null;
        _frameObserver = null;
        _frameHandler = null;
        if (ReferenceEquals(Current, this)) Current = null;
    }

    private sealed class NativeObserver(ControlNavigationTiming owner) : Java.Lang.Object,
        ViewTreeObserver.IOnGlobalLayoutListener, ViewTreeObserver.IOnPreDrawListener,
        ViewTreeObserver.IOnDrawListener
    {
        public void OnGlobalLayout() => owner.GlobalLayout();
        public bool OnPreDraw() { owner.PreDraw(); return true; }
        public void OnDraw() => owner.Draw();
    }

    private sealed class FrameObserver(ControlNavigationTiming owner) : Java.Lang.Object,
        Window.IOnFrameMetricsAvailableListener
    {
        public void OnFrameMetricsAvailable(Window? window, FrameMetrics? frameMetrics, int dropCountSinceLastInvocation)
        {
            if (frameMetrics is not null) owner.Frame(frameMetrics, dropCountSinceLastInvocation);
        }
    }
}
#endif
