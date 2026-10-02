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
    private readonly Dictionary<VisualElement, string> _groups = [];
    private readonly Dictionary<string, (int Count, double Total, double Max)> _layouts = [];
    private readonly Dictionary<string, double> _creations = [];
    private readonly HashSet<Element> _descendantRoots = [];
    private DeviceControlPage? _page;
    private Android.Views.View? _nativeView;
    private NativeObserver? _nativeObserver;
    private Window? _window;
    private FrameObserver? _frameObserver;
    private Android.OS.Handler? _frameHandler;
    private bool _disposed;
    private int _layoutCount;
    private int _frameCount;
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _frames = new();
    private bool _preDrawSeen;
    private bool _drawSeen;
    private long _firstHandlerStart;
    private long _lastHandlerEnd;

    private ControlNavigationTiming(AppDiagnosticService diagnostics, string address, string origin = "TapAccepted")
    {
        _diagnostics = diagnostics;
        _address = address;
        Mark(origin, "计时起点为应用收到请求，不是手指按下时间");
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

    internal static ControlNavigationTiming StartLazyLoad(AppDiagnosticService diagnostics, string address)
    {
        Current = new ControlNavigationTiming(diagnostics, address, "LazyViewRequested");
        return Current;
    }

    internal void ObserveLazyContent(TimedLazyContentView view)
    {
        if (_disposed) return;
        var group = view.GetType().Name;
        ObserveTree(view, group);
        ObserveDescendants(view);
    }

    // CollectionView template elements are created later than the LazyView's
    // initial visual tree. Their constructors subscribe before handler setup.
    internal void ObservePresetElement(VisualElement view)
    {
        if (_disposed) return;
        ObserveTree(view, nameof(LazyPresetControlView));
        ObserveDescendants(view);
    }

    private void ObserveDescendants(Element root)
    {
        if (_descendantRoots.Add(root)) root.DescendantAdded += OnDescendantAdded;
    }

    private void OnDescendantAdded(object? sender, ElementEventArgs args)
    {
        if (_disposed || sender is not VisualElement root || args.Element is not IVisualTreeElement child) return;
        ObserveTree(child, _groups.GetValueOrDefault(root, "Page"));
    }

    internal void RecordLayout(VisualElement view, string phase, long started)
    {
        if (_disposed) return;
        var key = $"group={_groups.GetValueOrDefault(view, view.GetType().Name)}; type={view.GetType().Name}; phase={phase}";
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var previous = _layouts.GetValueOrDefault(key);
        _layouts[key] = (previous.Count + 1, previous.Total + elapsed, Math.Max(previous.Max, elapsed));
    }

    internal void RecordCreation(string group, long started) =>
        _creations[group] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

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

    private void ObserveTree(IVisualTreeElement element, string group = "Page")
    {
        if (element is VisualElement view && _observed.Add(view))
        {
            _groups[view] = group;
            view.HandlerChanging += OnHandlerChanging;
            view.HandlerChanged += OnHandlerChanged;
        }
        foreach (var child in element.GetVisualChildren()) ObserveTree(child, group);
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
            var type = $"group={_groups.GetValueOrDefault(view, "Page")}; type={view.GetType().Name}";
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
        if (!OperatingSystem.IsAndroidVersionAtLeast(24) || _disposed) return;
        var frame = Interlocked.Increment(ref _frameCount);
        if (frame > 64) return;
        var measure = metrics.GetMetric((int)FrameMetricsId.LayoutMeasureDuration) / 1_000_000d;
        var draw = metrics.GetMetric((int)FrameMetricsId.DrawDuration) / 1_000_000d;
        var total = metrics.GetMetric((int)FrameMetricsId.TotalDuration) / 1_000_000d;
        // Keep the first frames and later slow module-layout frames. Do not
        // print during measurement/drawing or miss presets loaded in later batches.
        if (frame <= 6 || measure >= 16 || total >= 32)
            _frames.Enqueue(string.Create(CultureInfo.InvariantCulture,
                $"frame={frame}; callbackSinceTap={Stopwatch.GetElapsedTime(_started).TotalMilliseconds:0.0}ms; layoutMeasure={measure:0.0}ms; draw={draw:0.0}ms; total={total:0.0}ms; firstWindowDraw={metrics.GetMetric((int)FrameMetricsId.FirstDrawFrame)}; droppedReports={dropped}; window级统计，可能包含主页或过渡帧；callbackSinceTap 为回调到达时间"));
    }

    public void Dispose()
    {
        if (_disposed) return;
        Mark("Summary", $"observedElements={_observed.Count}; layouts={_layoutCount}; firstDrawCallback={_drawSeen}; HandlerChanging→Changed仅为事件区间，可能嵌套，不能相加当作原生创建总耗时");
        if (_firstHandlerStart != 0 && _lastHandlerEnd != 0)
            Mark("HandlerWindow", string.Create(CultureInfo.InvariantCulture,
                $"firstChangingSinceTap={Stopwatch.GetElapsedTime(_started, _firstHandlerStart).TotalMilliseconds:0.0}ms; lastChangedSinceTap={Stopwatch.GetElapsedTime(_started, _lastHandlerEnd).TotalMilliseconds:0.0}ms; 包含首帧后 LazyView 及预设模板实例"));
        foreach (var entry in _handlers.OrderByDescending(entry => entry.Value.Total))
            Mark("HandlerSummary", string.Create(CultureInfo.InvariantCulture,
                $"{entry.Key}; count={entry.Value.Count}; eventIntervalsTotal={entry.Value.Total:0.0}ms; max={entry.Value.Max:0.0}ms"));
        foreach (var entry in _creations)
            Mark("LazyViewCreation", string.Create(CultureInfo.InvariantCulture,
                $"group={entry.Key}; creation={entry.Value:0.0}ms; 包含 XAML 构造、绑定及 Content 赋值；不含后续布局"));
        foreach (var entry in _layouts.OrderByDescending(entry => entry.Value.Total))
            Mark("LayoutSummary", string.Create(CultureInfo.InvariantCulture,
                $"{entry.Key}; count={entry.Value.Count}; inclusiveTotal={entry.Value.Total:0.0}ms; max={entry.Value.Max:0.0}ms; 父子测量/排列可能嵌套，不可相加"));
        foreach (var frame in _frames) Mark("Android.WindowFrame", frame);
        _disposed = true;
        foreach (var view in _observed)
        {
            view.HandlerChanging -= OnHandlerChanging;
            view.HandlerChanged -= OnHandlerChanged;
        }
        foreach (var root in _descendantRoots) root.DescendantAdded -= OnDescendantAdded;
        _descendantRoots.Clear();
        _groups.Clear();
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
