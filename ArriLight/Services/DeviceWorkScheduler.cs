namespace ArriLight;

/// <summary>Runs one operation per address and enforces an application-wide device budget.</summary>
/// <remarks>
/// 协议无关（只以 <c>string address</c> 分组），是 AGP Runtime 与 Mesh 控制的
/// **共享**串行器：以 DI 单例注册，使两条链路竞争同一份 BLE 适配器预算。
/// PW01-MESH 每次命令都要真实建立 GATT 连接，若不与 AGP 共用上限，两条链路
/// 叠加会超出 Android BLE 的连接能力。
/// </remarks>
public sealed class DeviceWorkScheduler
{
    private readonly object _sync = new();
    private readonly List<Work> _pending = [];
    private readonly HashSet<string> _active = new(StringComparer.OrdinalIgnoreCase);
    private TaskCompletionSource _idle = Completed();
    private long _sequence;
    private int _limit = 5;
    private int _batches;
    public int Limit
    {
        get { lock (_sync) return _limit; }
        set { lock (_sync) { _limit = value; Pump(); } }
    }
    public IDisposable BeginBatch()
    {
        lock (_sync) _batches++;
        return new Batch(this);
    }
    public Task<T> Enqueue<T>(string address, int priority,
        Func<CancellationToken, Task<T>> execute, CancellationToken token)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            if (_idle.Task.IsCompleted) _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var work = new Work(address, priority, ++_sequence, token,
                async () =>
                {
                    try { token.ThrowIfCancellationRequested(); completion.TrySetResult(await execute(token)); }
                    catch (OperationCanceledException ex) { completion.TrySetCanceled(ex.CancellationToken); }
                    catch (Exception ex) { completion.TrySetException(ex); }
                }, () => completion.TrySetCanceled(token), ExecutionContext.Capture());
            _pending.Add(work);
            work.Registration = token.Register(() =>
            {
                lock (_sync)
                {
                    var queued = _pending.FirstOrDefault(item => item.Sequence == work.Sequence);
                    if (queued is null) return; // Active work owns its completion and cleanup.
                    _pending.Remove(queued); queued.Cancel(); Pump();
                }
            });
            Pump();
        }
        return completion.Task;
    }
    public Task DrainAsync() { lock (_sync) { Pump(); return _idle.Task; } }
    public void SetAttention(string address, bool active)
    {
        lock (_sync)
        {
            for (var index = 0; index < _pending.Count; index++)
                if (_pending[index].Priority >= 3 && string.Equals(_pending[index].Address, address, StringComparison.OrdinalIgnoreCase))
                    _pending[index] = _pending[index] with { Priority = active ? 3 : 4 };
            Pump();
        }
    }
    private void Pump()
    {
        foreach (var canceled in _pending.Where(work => work.Token.IsCancellationRequested).ToArray())
        { _pending.Remove(canceled); canceled.Registration.Unregister(); canceled.Cancel(); }
        while (_batches == 0 && _active.Count < _limit)
        {
            var next = _pending.Where(work => !_active.Contains(work.Address))
                .OrderBy(work => work.Priority).ThenBy(work => work.Sequence).FirstOrDefault();
            if (next is null) break;
            _pending.Remove(next);
            next.Registration.Unregister();
            _active.Add(next.Address);
            _ = StartIsolatedAsync(async () =>
            {
                try
                {
                    Task? execution = null;
                    if (next.Context is { } context)
                        ExecutionContext.Run(context, _ => execution = next.Execute(), null);
                    else execution = next.Execute();
                    await execution!;
                }
                finally { lock (_sync) { _active.Remove(next.Address); Pump(); } }
            });
        }
        if (_pending.Count == 0 && _active.Count == 0) _idle.TrySetResult();
    }
    private static TaskCompletionSource Completed()
    { var completion = new TaskCompletionSource(); completion.SetResult(); return completion; }
    private sealed record Work(string Address, int Priority, long Sequence,
        CancellationToken Token, Func<Task> Execute, Action Cancel, ExecutionContext? Context)
    {
        internal CancellationTokenRegistration Registration { get; set; }
    }
    private static Task StartIsolatedAsync(Func<Task> execute)
    {
        if (ExecutionContext.IsFlowSuppressed()) return Task.Run(execute);
        using (ExecutionContext.SuppressFlow()) return Task.Run(execute);
    }
    private sealed class Batch(DeviceWorkScheduler scheduler) : IDisposable
    {
        public void Dispose() { lock (scheduler._sync) { scheduler._batches--; scheduler.Pump(); } }
    }
}
