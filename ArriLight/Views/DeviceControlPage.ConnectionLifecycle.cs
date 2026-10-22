namespace ArriLight;

public partial class DeviceControlPage
{
    private CancellationTokenSource? _controlPageAppearanceCancellation;
    private bool _controlInitializationScheduled;
    private bool _automaticConnectionPending;
    private Task? _deferredConnectionInitializationTask;
    private Task? _blePreparationTask;

    private void BeginControlPageAppearance()
    {
        EndControlPageAppearance();
        _controlPageAppearanceCancellation = new CancellationTokenSource();
        _controlInitializationScheduled = false;
        _automaticConnectionPending = true;
        NotifyAutomaticConnectionPending(true);
        PrepareEntranceTransition();
        BeginControlFrameObservation(_controlPageAppearanceCancellation.Token);
    }

    private void EndControlPageAppearance()
    {
#if DEBUG && ANDROID
        // BeginControlPageAppearance calls this before installing its new token.
        // Dispose only when an existing appearance really ends.
        if (_controlPageAppearanceCancellation is not null)
        {
            _navigationTiming?.Mark("Page.Left");
            _navigationTiming?.Dispose();
        }
#endif
        EndControlFrameObservation();
        _ = CancellationDispatch.Request(_controlPageAppearanceCancellation,
            _diagnostics, "Control.Appearance", dispose: true, address: DeviceAddress);
        _controlPageAppearanceCancellation = null;
        _automaticConnectionPending = false;
        NotifyAutomaticConnectionPending(false);
        ResetEntranceTransition();
    }

    protected override void OnNavigatedTo(NavigatedToEventArgs args)
    {
#if DEBUG && ANDROID
        _navigationTiming?.Mark("NavigatedTo.Enter");
#endif
        base.OnNavigatedTo(args);
#if DEBUG && ANDROID
        _navigationTiming?.Mark("NavigatedTo.AfterBase");
#endif
        if ((!_isGroup && string.IsNullOrWhiteSpace(DeviceAddress)) ||
            _controlPageAppearanceCancellation is null || _controlInitializationScheduled)
            return;

        _controlInitializationScheduled = true;
        var token = _controlPageAppearanceCancellation.Token;
        // Post after the navigation-completed event; never make navigation wait
        // for scan teardown, the old home query, GATT discovery or authentication.
#if DEBUG && ANDROID
        var dispatchQueuedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        var navigationTiming = _navigationTiming;
        navigationTiming?.Mark("Dispatcher.Queued");
#endif
        Dispatcher.Dispatch(() =>
        {
#if DEBUG && ANDROID
            navigationTiming?.Mark("Dispatcher.Executing", string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"queueWait={System.Diagnostics.Stopwatch.GetElapsedTime(dispatchQueuedAt).TotalMilliseconds:0.0}ms; canceled={token.IsCancellationRequested}"));
#endif
            _deferredConnectionInitializationTask = InitializeAfterNavigationAsync(token);
        });
#if DEBUG && ANDROID
        _navigationTiming?.Mark("NavigatedTo.Exit");
#endif
    }

    private async Task InitializeAfterNavigationAsync(CancellationToken cancellationToken)
    {
#if DEBUG && ANDROID
        var navigationTiming = _navigationTiming;
#endif
        using var timing = _diagnostics.BeginBleTiming("UI.Control.AfterNavigation", DeviceAddress);
        try
        {
            await WaitForFirstControlFrameAsync(cancellationToken);
#if DEBUG && ANDROID
            navigationTiming?.Mark("FirstFrame.WorkReleased");
#endif
            // Each branch yields before its work. A synchronous LazyView
            // constructor cannot prevent the BLE branch from being scheduled.
            var connection = PrepareBleAfterFirstFrameAsync(cancellationToken);
            _blePreparationTask = connection;
            var controls = LoadDeferredCapabilityViewsAsync(connection, cancellationToken);
            var transition = PlayEntranceTransitionAsync(cancellationToken);
            await Task.WhenAll(connection, controls, transition);
            timing.SetResult("finished");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            timing.SetResult("page-left");
        }
        catch (Exception exception)
        {
            timing.SetResult($"failed:{exception.GetType().Name}");
            _diagnostics.Error("UI/Control", $"控制页延迟内容加载失败：{DeviceAddress}。", exception);
        }
        finally
        {
#if DEBUG && ANDROID
            navigationTiming?.Mark("AfterNavigation.Finished", $"canceled={cancellationToken.IsCancellationRequested}");
            navigationTiming?.Dispose();
#endif
        }
    }

    private async Task PrepareBleAfterFirstFrameAsync(CancellationToken cancellationToken)
    {
        using var timing = _diagnostics.BeginBleTiming("UI.Control.BleAfterFirstFrame", DeviceAddress);
        try
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (_isDemo)
            {
                timing.SetResult("demo-shown");
                return;
            }
            timing.Mark("页面首帧已绘制，开始独立准备 AGP 会话；不等待控件加载");
            await PrepareControlSessionAsync(cancellationToken);
            timing.SetResult("finished");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            timing.SetResult("page-left");
        }
        catch (Exception exception)
        {
            timing.SetResult($"failed:{exception.GetType().Name}");
            _diagnostics.Error("BLE/Connection", $"控制页显示后的连接准备失败：{DeviceAddress}。", exception);
            if (!cancellationToken.IsCancellationRequested)
            {
                ConnectionStatusText = AppResources.Ble_PreparationFailed;
                CanConnect = true;
            }
        }
        finally
        {
            if (_controlPageAppearanceCancellation?.Token == cancellationToken)
            {
                _automaticConnectionPending = false;
                NotifyAutomaticConnectionPending(false);
                OnPropertyChanged(nameof(CanRefreshStatus));
            }
        }
    }
}
