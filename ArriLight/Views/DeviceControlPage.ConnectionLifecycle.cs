namespace ArriLight;

public partial class DeviceControlPage
{
    private CancellationTokenSource? _controlPageAppearanceCancellation;
    private bool _controlInitializationScheduled;
    private bool _automaticConnectionPending;
    private Task? _deferredConnectionInitializationTask;

    private void BeginControlPageAppearance()
    {
        EndControlPageAppearance();
        _controlPageAppearanceCancellation = new CancellationTokenSource();
        _controlInitializationScheduled = false;
        _automaticConnectionPending = true;
        PrepareEntranceTransition();
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
        _controlPageAppearanceCancellation?.Cancel();
        _controlPageAppearanceCancellation?.Dispose();
        _controlPageAppearanceCancellation = null;
        _automaticConnectionPending = false;
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
        if (string.IsNullOrWhiteSpace(DeviceAddress) ||
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
        using var timing = _diagnostics.BeginBleTiming("UI.Control.AfterNavigation", DeviceAddress);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Finish the short transition before GATT work can contend with UI
            // frames. Demonstration devices animate without starting BLE work.
            await PlayEntranceTransitionAsync(cancellationToken);
            if (_isDemo)
            {
                timing.SetResult("demo-shown");
                return;
            }
            await _discoveryService.StopAsync();
            cancellationToken.ThrowIfCancellationRequested();
            timing.Mark("页面导航已完成，后台扫描已停止，开始准备 AGP 会话");
            await EnsureConnectedAsync(
                showError: false, cancellationToken: cancellationToken, scanAlreadyStopped: true);
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
                ConnectionStatusText = "连接准备失败，请重试";
                CanConnect = true;
            }
        }
        finally
        {
#if DEBUG && ANDROID
            _navigationTiming?.Mark("AfterNavigation.Finished", $"canceled={cancellationToken.IsCancellationRequested}");
            _navigationTiming?.Dispose();
#endif
            if (_controlPageAppearanceCancellation?.Token == cancellationToken)
            {
                _automaticConnectionPending = false;
                OnPropertyChanged(nameof(CanRefreshStatus));
            }
        }
    }
}
