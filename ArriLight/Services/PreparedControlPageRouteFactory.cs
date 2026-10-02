namespace ArriLight;

/// <summary>One unused page per route, consumed once; all calls are on the UI thread.</summary>
internal sealed class PreparedControlPageRouteFactory(string route, Func<Element> create) : RouteFactory
{
    private Element? _preparedPage;

    internal void Prepare()
    {
        if (_preparedPage is not null)
            return;
        using var timing = ServiceHelper.GetRequiredService<AppDiagnosticService>()
            .BeginBleTiming("UI.Control.Preload", detail: $"route={route}");
        // Construction builds managed views only. No device address, appearance
        // callback, native navigation, scan or GATT session is involved here.
        _preparedPage = create();
        timing.SetResult("prepared");
    }

    public override Element GetOrCreate()
    {
        using var timing = ServiceHelper.GetRequiredService<AppDiagnosticService>()
            .BeginBleTiming("UI.Control.RoutePage", detail: $"route={route}; preloaded={_preparedPage is not null}");
        var page = _preparedPage;
        _preparedPage = null;
        page ??= create();
#if DEBUG && ANDROID
        if (page is DeviceControlPage controlPage && ControlNavigationTiming.Current is { } navigationTiming)
            controlPage.AttachNavigationTiming(navigationTiming);
#endif
        timing.SetResult("created-or-consumed");
        return page;
    }

    public override Element GetOrCreate(IServiceProvider services) => GetOrCreate();
}
