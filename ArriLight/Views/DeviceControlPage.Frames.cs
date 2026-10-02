namespace ArriLight;

public partial class DeviceControlPage
{
    private TaskCompletionSource _firstControlFrame = NewFrameCompletion();
#if ANDROID
    private DrawCompletion? _firstDrawCompletion;
#endif

    private static TaskCompletionSource NewFrameCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void BeginControlFrameObservation(CancellationToken token)
    {
        _firstControlFrame = NewFrameCompletion();
        HandlerChanged += OnFrameHandlerChanged;
        Loaded += OnFrameLoaded;
        TryObserveFirstFrame(token);
    }

    private void EndControlFrameObservation()
    {
        HandlerChanged -= OnFrameHandlerChanged;
        Loaded -= OnFrameLoaded;
#if ANDROID
        _firstDrawCompletion?.Dispose();
        _firstDrawCompletion = null;
#endif
    }

    private void OnFrameHandlerChanged(object? sender, EventArgs args) =>
        TryObserveFirstFrame(_controlPageAppearanceCancellation?.Token ?? new CancellationToken(true));
    private void OnFrameLoaded(object? sender, EventArgs args) => OnFrameHandlerChanged(sender, args);

    private void TryObserveFirstFrame(CancellationToken token)
    {
        if (token.IsCancellationRequested || _firstControlFrame.Task.IsCompleted) return;
#if ANDROID
        if (_firstDrawCompletion is null && Handler?.PlatformView is Android.Views.View view)
            _firstDrawCompletion = new DrawCompletion(view, _firstControlFrame);
#else
        // Loaded is the portable MAUI boundary. Android uses the actual draw
        // callback above; a dispatcher turn alone is not a first-frame signal.
        if (IsLoaded)
        {
            var completion = _firstControlFrame;
            Dispatcher.Dispatch(() =>
            {
                if (!token.IsCancellationRequested) completion.TrySetResult();
            });
        }
#endif
    }

    private Task WaitForFirstControlFrameAsync(CancellationToken token) =>
        _firstControlFrame.Task.WaitAsync(token);

    private async Task YieldControlFrameAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
#if ANDROID
        if (Handler?.PlatformView is Android.Views.View view)
        {
            var completion = NewFrameCompletion();
            using var draw = new DrawCompletion(view, completion);
            view.Invalidate();
            await completion.Task.WaitAsync(token);
            return;
        }
#endif
        var dispatched = NewFrameCompletion();
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(16), () => dispatched.TrySetResult());
        await dispatched.Task.WaitAsync(token);
    }

#if ANDROID
    // No tree mutations inside OnDraw. Post completion after that draw returns.
    // This observes drawing, not the display compositor's pixel presentation.
    private sealed class DrawCompletion : Java.Lang.Object,
        Android.Views.ViewTreeObserver.IOnDrawListener
    {
        private readonly Android.Views.View _view;
        private readonly TaskCompletionSource _completion;
        private bool _posted;
        private bool _detached;

        internal DrawCompletion(Android.Views.View view, TaskCompletionSource completion)
        {
            _view = view;
            _completion = completion;
            view.ViewTreeObserver?.AddOnDrawListener(this);
        }

        public void OnDraw()
        {
            if (_posted || _detached || !_view.IsShown || _view.Width <= 0 || _view.Height <= 0) return;
            _posted = true;
            _view.Post(() =>
            {
                if (_detached) return;
                Detach();
                _completion.TrySetResult();
            });
        }

        private void Detach()
        {
            if (_detached) return;
            _detached = true;
            if (_view.ViewTreeObserver is { IsAlive: true } observer)
                observer.RemoveOnDrawListener(this);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Detach();
            base.Dispose(disposing);
        }
    }
#endif
}
