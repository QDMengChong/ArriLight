using Plugin.BLE.Abstractions.Contracts;

namespace ArriLight;

public sealed partial class AgpLightControlService
{
    /// <summary>Authenticate and run OTA under the target's shared runtime queue.</summary>
    public async Task RunExclusiveOtaAsync(string address,
        Func<IDevice, CancellationToken, Task> operation, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        ArgumentNullException.ThrowIfNull(operation);
        EnsureApplicationForeground();
        if (!_runtimeStarted)
        {
            using var lifetime = LinkApplicationCancellation(cancellationToken);
            await EnsureAuthorizedAsync(address, lifetime.Token);
            try { await _connectionService.RunExclusiveOtaAsync(address, operation, lifetime.Token); }
            finally { await InvalidateOtaSessionAsync(address); }
            return;
        }

        await RunRuntimeExclusiveAsync(address, AppResources.Firmware_Updating, async token =>
        {
            // Authentication belongs inside the barrier: a prior command or
            // preparation cannot dispose or replace this handle during OTA.
            await GetOrCreateClientAsync(address, token, skipInitialLightStateRead: true);
            try { await _connectionService.RunExclusiveOtaAsync(address, operation, token); }
            finally { await InvalidateOtaSessionAsync(address); }
            return true;
        }, cancellationToken, resumePreparation: true);
    }

    internal async Task RunRuntimeBindingAsync(string address, Func<Task> binding,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        ArgumentNullException.ThrowIfNull(binding);
        if (!_runtimeStarted) { await binding(); return; }
        await RunRuntimeExclusiveAsync(address, AppResources.Pairing_Connecting, async _ =>
        {
            await binding();
            return true;
        }, cancellationToken, resumePreparation: true);
    }

    private Task<bool> RemoveRuntimeDeviceAsync(string address, CancellationToken cancellationToken) =>
        RunRuntimeExclusiveAsync(address, AppResources.Device_Removed, async token =>
        {
            await DisposeHandleAsync(address);
            // If native release fails, retain the record and exclusive failure
            // snapshot so the user can retry the removal.
            await _connectionService.DisconnectDeviceAsync(address, token);
            _deviceStatuses.TryRemove(address, out _);
            _runtimeSchedules.TryRemove(address, out _);
            _scheduleSlotCounts.TryRemove(address, out _);
            var removed = _repository.Remove(address);
            _runtimeEntries.TryRemove(address, out _);
            return removed;
        }, cancellationToken, resumePreparation: false);

    private async Task<T> RunRuntimeExclusiveAsync<T>(string address, string stage,
        Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken,
        bool resumePreparation)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureApplicationForeground();
        using var lifetime = LinkApplicationCancellation(cancellationToken);
        var entry = _runtimeEntries.GetOrAdd(address, key => new(key));
        var epoch = _runtimeEpoch;
        CancellationTokenSource? preparationLifetime;
        lock (entry.Sync)
        {
            if (entry.Exclusive) throw new InvalidOperationException(AppResources.Firmware_Updating);
            entry.Exclusive = true;
            entry.WorkGeneration++;
            entry.Online = false;
            entry.Busy = true;
            entry.Error = null;
            entry.Stage = stage;
            preparationLifetime = entry.Lifetime;
            foreach (var step in entry.Steps.Keys.ToArray())
                entry.Steps[step] = new(DeviceRuntimeStepStatus.Invalidated, DateTimeOffset.UtcNow);
        }
        _ = CancellationDispatch.Request(preparationLifetime, _diagnostics,
            "Runtime.ExclusivePreparation", address: address);
        RecordDeviceOffline(address);
        PublishRuntime(entry);
        Exception? failure = null;
        try
        {
            return await _runtimeWork.Enqueue(address, 0, async token =>
            {
                token.ThrowIfCancellationRequested();
                EnsureApplicationForeground();
                if (_runtimeStopped || epoch != _runtimeEpoch || !_repository.Contains(address))
                    throw new OperationCanceledException(token);
                return await operation(token);
            }, lifetime.Token);
        }
        catch (Exception exception) { failure = exception; throw; }
        finally
        {
            lock (entry.Sync)
            {
                entry.Exclusive = false;
                entry.Busy = false;
                entry.Online = false;
                if (!_runtimeStopped && epoch == _runtimeEpoch)
                {
                    entry.Error = failure?.Message;
                    entry.Stage = failure?.Message;
                }
            }
            if (_runtimeEntries.TryGetValue(address, out var current) && ReferenceEquals(current, entry))
                PublishRuntime(entry);
            if (resumePreparation && !_runtimeStopped && epoch == _runtimeEpoch &&
                !_applicationSuspended && _repository.Contains(address))
                RequestRuntimePreparation(address);
        }
    }

    private async Task InvalidateOtaSessionAsync(string address)
    {
        _repository.InvalidateCapabilities(address);
        _runtimeSchedules.TryRemove(address, out _);
        _scheduleSlotCounts.TryRemove(address, out _);
        RecordDeviceOffline(address);
        await DisposeHandleAsync(address);
        try
        {
            // A completed transfer can reboot immediately. Cleanup uses its
            // own token and must finish before ordinary preparation resumes.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _connectionService.DisconnectAsync(address, BluetoothDisconnectReason.Requested,
                cleanup.Token);
        }
        catch (Exception exception)
        {
            _diagnostics.Warning("BLE/Runtime",
                $"OTA 后释放旧会话失败，保持资料失效并在下次准备验证连接：device={address}，{exception.Message}。");
        }
    }
}
