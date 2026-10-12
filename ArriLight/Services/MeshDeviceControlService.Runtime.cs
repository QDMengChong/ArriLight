using ArriOpto.BLE.Agp;
using Plugin.BLE.Abstractions.Exceptions;

namespace ArriLight;

public partial class MeshDeviceControlService
{
    private readonly object _lifecycleSync = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly Dictionary<string, Task> _preparations = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource _applicationLifetime = new();
    private bool _runtimeStarted;
    private bool _applicationSuspended;
    private bool _applicationClosing;
    private bool _wantsForeground = true;

    public bool IsRuntimeStarted { get { lock (_lifecycleSync) return _runtimeStarted; } }

    /// <summary>与 GATT 一样，在应用启动时按保存地址准备设备，不依赖扫描或点击。</summary>
    public Task StartRuntimeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lifecycleSync)
        {
            ThrowIfApplicationStopped();
            _runtimeStarted = true;
        }
        using (_work.BeginBatch())
        {
            foreach (var device in (_repository?.GetAll() ?? [])
                .Where(device => !device.IsDemo && device.EffectiveProtocol == BluetoothDeviceProtocol.Mesh)
                .OrderByDescending(device => device.IsPinned)
                .ThenByDescending(device => device.LastOperatedAt)
                .ThenByDescending(device => device.AddedAt))
                RequestRuntimePreparation(device.Address);
        }
        return Task.CompletedTask;
    }

    public Task StartForNewWindowAsync(CancellationToken cancellationToken = default) =>
        ResumeRuntimeAsync(isNewWindow: true, cancellationToken);

    public Task ResumeAfterBackgroundAsync(CancellationToken cancellationToken = default) =>
        ResumeRuntimeAsync(isNewWindow: false, cancellationToken);

    private async Task ResumeRuntimeAsync(bool isNewWindow, CancellationToken cancellationToken)
    {
        lock (_lifecycleSync)
        {
            if (_applicationClosing && !isNewWindow) return;
            if (isNewWindow) _applicationClosing = false;
            _wantsForeground = true;
        }
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            lock (_lifecycleSync)
            {
                if (!_wantsForeground || _applicationClosing || _disposed) return;
                if (_applicationLifetime.IsCancellationRequested)
                {
                    _applicationLifetime.Dispose();
                    _applicationLifetime = new();
                }
                _applicationSuspended = false;
            }
            await StartRuntimeAsync(cancellationToken);
        }
        finally { _lifecycleGate.Release(); }
    }

    public void RequestRuntimePreparation(string address)
    {
        lock (_lifecycleSync)
        {
            if (!_runtimeStarted || _applicationSuspended || _applicationClosing || _disposed || !IsSavedMeshDevice(address)) return;
            _ = EnsureRuntimePreparation(address, refresh: false);
        }
    }

    public async Task EnsureRuntimeReadyAsync(string address, CancellationToken waitToken = default, bool refresh = false)
    {
        waitToken.ThrowIfCancellationRequested();
        if (!IsRuntimeStarted) await StartRuntimeAsync(waitToken);
        Task preparation;
        lock (_lifecycleSync)
        {
            ThrowIfApplicationStopped();
            if (!IsSavedMeshDevice(address)) throw new InvalidOperationException(AppResources.Device_Removed);
            preparation = EnsureRuntimePreparation(address, refresh);
        }
        // 页面只拥有等待；离开页面不会取消已经接受的应用级准备。
        await preparation.WaitAsync(waitToken);
    }

    private Task EnsureRuntimePreparation(string address, bool refresh)
    {
        if (_preparations.TryGetValue(address, out var running) && !running.IsCompleted) return running;
        if (!refresh && _connectionService.IsConnected(address) && GetCachedControlProfile(address) is not null &&
            GetRuntimeSnapshot(address)?.Availability == DeviceRuntimeAvailability.Ready) return Task.CompletedTask;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _preparations[address] = completion.Task;
        _ = completion.Task.ContinueWith(task => _ = task.Exception,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        var token = _applicationLifetime.Token;
        _ = PrepareRuntimeDeviceAsync(address, refresh, completion, token);
        return completion.Task;
    }

    private async Task PrepareRuntimeDeviceAsync(string address, bool refresh,
        TaskCompletionSource completion, CancellationToken token)
    {
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await RunProxySessionAsync(address, "Mesh.Prepare", 2,
                        (_, _, _) => Task.FromResult(true), token);
                    break;
                }
                catch (Exception exception) when (attempt == 0 && !token.IsCancellationRequested &&
                    exception is IOException or TimeoutException or OperationCanceledException or DeviceConnectionException)
                {
                    _diagnostics.Warning("Mesh/Runtime", $"准备重试：{address}，{exception.Message}");
                    await Task.Delay(TimeSpan.FromMilliseconds(500), token);
                }
            }
            token.ThrowIfCancellationRequested();
            if (!IsSavedMeshDevice(address)) throw new OperationCanceledException(token);
            // 每轮新会话重新确认模型，固件更新后不继续使用旧 HSL/CTL 能力缓存。
            var profile = await GetControlProfileAsync(address, refresh: true, token)
                ?? throw new InvalidOperationException(AppResources.Mesh_CapabilitiesUnavailable);
            if (profile.OnOffAddress is not null) await GetOnOffStateAsync(address, token);
            if (profile.Capabilities.HasFlag(AgpCapabilities.Lightness16)) await GetLightingStateAsync(address, token);
            completion.TrySetResult();
        }
        catch (OperationCanceledException exception) { completion.TrySetCanceled(exception.CancellationToken); }
        catch (Exception exception)
        {
            _diagnostics.Warning("Mesh/Runtime", $"设备准备未完成：{address}，{exception.Message}");
            completion.TrySetException(exception);
        }
    }

    private bool IsSavedMeshDevice(string address) => _repository?.GetByAddress(address) is
        { IsDemo: false, EffectiveProtocol: BluetoothDeviceProtocol.Mesh };

    private void OnRuntimeDevicesChanged(object? sender, EventArgs args)
    {
        foreach (var device in _repository?.GetAll() ?? [])
            if (!_runtimeEntries.ContainsKey(device.Address)) RequestRuntimePreparation(device.Address);
    }

    private CancellationTokenSource LinkApplicationLifetime(CancellationToken token)
    {
        lock (_lifecycleSync)
        {
            ThrowIfApplicationStopped();
            return CancellationTokenSource.CreateLinkedTokenSource(token, _applicationLifetime.Token);
        }
    }

    private void ThrowIfApplicationStopped()
    {
        if (_applicationSuspended || _applicationClosing || _disposed)
            throw new OperationCanceledException(AppResources.Ble_AppNotInForeground, new CancellationToken(true));
    }

    private async Task StopRuntimeAsync(bool closing, CancellationToken cancellationToken)
    {
        Task callbacks;
        Task[] preparations;
        lock (_lifecycleSync)
        {
            _wantsForeground = false;
            _applicationSuspended = true;
            if (closing) _applicationClosing = true;
            callbacks = CancellationDispatch.Request(_applicationLifetime, _diagnostics, "Mesh.Runtime.Stop");
            preparations = _preparations.Values.ToArray();
        }
        var reason = closing ? "应用关闭" : "应用进入后台";
        foreach (var address in _runtimeEntries.Keys) RecordDeviceOffline(address, reason);
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            await callbacks;
            foreach (var preparation in preparations)
                try { await preparation; } catch { }
            await DisconnectAllAsync(cancellationToken, reason);
        }
        finally { _lifecycleGate.Release(); }
    }

    private async Task RecoverRuntimeSessionAsync(string address, MeshRuntimeEntry entry)
    {
        try
        {
            Task? interrupted;
            lock (_lifecycleSync) interrupted = _preparations.GetValueOrDefault(address);
            if (interrupted is not null) try { await interrupted; } catch { }
            RequestRuntimePreparation(address);
        }
        finally { lock (entry.Sync) entry.RecoveryPending = false; }
    }
}
