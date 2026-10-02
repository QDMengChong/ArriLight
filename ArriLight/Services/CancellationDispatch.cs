namespace ArriLight;

internal static class CancellationDispatch
{
    // CancelAsync sets IsCancellationRequested before returning. Registered
    // callbacks run elsewhere; navigation never waits for their completion.
    internal static Task Request(CancellationTokenSource? source,
        AppDiagnosticService diagnostics, string stage, bool dispose = false, string? address = null)
    {
        if (source is null) return Task.CompletedTask;
#if DEBUG
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        Task callbacks;
        try { callbacks = source.CancelAsync(); }
        catch (ObjectDisposedException) { return Task.CompletedTask; }
#if DEBUG
        var requestMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
#endif
        return Task.Run(ObserveAsync);

        async Task ObserveAsync()
        {
            try { await callbacks.ConfigureAwait(false); }
            catch (Exception exception)
            {
                diagnostics.Warning("Cancellation", $"{stage} 取消回调失败：{exception.Message}");
            }
            finally
            {
                if (dispose) source.Dispose();
#if DEBUG
                diagnostics.Information("UI/Timing", string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"stage={stage}.CancelCallbacks request={requestMilliseconds:0.0}ms completion={System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0.0}ms thread={Environment.CurrentManagedThreadId} device={address ?? "-"}; completion 包含线程池排队及取消回调执行，不阻塞导航"));
#endif
            }
        }
    }
}
