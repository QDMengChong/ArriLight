using System.Collections.Concurrent;
using System.Text;
using ArriOpto.BLE.Agp;
using Plugin.BLE.Abstractions.Exceptions;

namespace ArriLight;

public sealed partial class AgpLightControlService
{
    private readonly DeviceWorkScheduler _runtimeWork;
    private readonly ConcurrentDictionary<string, DeviceRuntimeEntry> _runtimeEntries = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _runtimeStarted;
    private volatile bool _runtimeStopped;
    private long _runtimeEpoch;
    private readonly AsyncLocal<Guid?> _runtimeOperationId = new();
    private readonly AsyncLocal<CancellationToken?> _runtimePreparationLifetime = new();
    private readonly ConcurrentDictionary<string, byte> _runtimeBindingAddresses = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<DeviceRuntimeChange>? RuntimeStateChanged;
    public int RuntimeConcurrency
    {
        get => int.TryParse(Preferences.Default.Get("device_runtime_concurrency", "5"), out var value) ? Math.Clamp(value, 1, 5) : 5;
        set
        {
            if (value is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(value));
            Preferences.Default.Set("device_runtime_concurrency", value.ToString());
            _runtimeWork.Limit = value;
        }
    }
    public DeviceRuntimeSnapshot? GetRuntimeSnapshot(string address)
    {
        if (!_runtimeEntries.TryGetValue(address, out var entry)) return null;
        lock (entry.Sync) return entry.Snapshot();
    }
    public AgpModeConfig? GetCachedLocalMode(string address) => _handles.GetValueOrDefault(address)?.LocalMode;

    private async Task RunSessionStepAsync(string address, DeviceRuntimeStep step,
        Func<CancellationToken, Task> operation, CancellationToken token)
    {
        if (!_runtimeStarted) { await operation(token); return; }
        var entry = _runtimeEntries.GetOrAdd(address, key => new(key));
        var epoch = entry.Epoch;
        MarkRuntimeStep(entry, epoch, step, DeviceRuntimeStepStatus.Running);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(step == DeviceRuntimeStep.Connection ? 20 : 10));
        try
        {
            await operation(deadline.Token);
            MarkRuntimeStep(entry, epoch, step, DeviceRuntimeStepStatus.Succeeded);
        }
        catch (Exception exception)
        {
            MarkRuntimeStep(entry, epoch, step, DeviceRuntimeStepStatus.Failed, exception.Message);
            throw;
        }
    }

    private void InvalidateRuntimeSchedules(string address)
    {
        _runtimeSchedules.TryRemove(address, out _);
        if (_runtimeEntries.TryGetValue(address, out var entry))
            MarkRuntimeStep(entry, entry.Epoch, DeviceRuntimeStep.Schedules, DeviceRuntimeStepStatus.Invalidated);
    }

    private void CacheRuntimeDelay(string address, AgpDelayOnResponse response)
    {
        if (!_runtimeEntries.TryGetValue(address, out var entry)) return;
        MutateRuntime(entry, entry.Epoch, () =>
        {
            entry.RelativeDelayOn = response;
            entry.Steps[DeviceRuntimeStep.RelativeDelayOn] = new(DeviceRuntimeStepStatus.Succeeded, DateTimeOffset.UtcNow);
        });
    }
    private void CacheRuntimeDelay(string address, AgpDelayOffResponse response)
    {
        if (!_runtimeEntries.TryGetValue(address, out var entry)) return;
        MutateRuntime(entry, entry.Epoch, () =>
        {
            entry.RelativeDelayOff = response;
            entry.Steps[DeviceRuntimeStep.RelativeDelayOff] = new(DeviceRuntimeStepStatus.Succeeded, DateTimeOffset.UtcNow);
        });
    }
    private void CacheRuntimeClock(string address, AgpTimeResponse response)
    {
        if (!_runtimeEntries.TryGetValue(address, out var entry)) return;
        MutateRuntime(entry, entry.Epoch, () =>
        {
            entry.Clock = response;
            entry.Steps[DeviceRuntimeStep.Clock] = new(response.IsValid ? DeviceRuntimeStepStatus.Succeeded : DeviceRuntimeStepStatus.Failed, DateTimeOffset.UtcNow);
        });
    }

    private async Task<AgpDeviceControlProfile> GetRuntimeProfileAsync(string address, CancellationToken waitToken, bool refresh)
    {
        await EnsureRuntimeReadyAsync(address, waitToken, refresh);
        return GetCachedDeviceStatus(address)?.Profile ?? throw new InvalidOperationException(AppResources.Control_StateUnavailable);
    }
    private async Task<IReadOnlyList<AgpSchedule>> GetRuntimeSchedulesAsync(string address, CancellationToken waitToken)
    {
        await EnsureRuntimeReadyAsync(address, waitToken);
        return _runtimeSchedules.GetValueOrDefault(address) ?? [];
    }

    /// <summary>Enroll saved devices immediately; shared preparation does not belong to a page.</summary>
    public Task StartRuntimeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureApplicationForeground();
        _runtimeStarted = true;
        _runtimeStopped = false;
        _runtimeWork.Limit = RuntimeConcurrency;
        using (_runtimeWork.BeginBatch())
        {
            // Mesh 设备走独立的短连接链路（MeshDeviceControlService），
            // 不能被 AGP Runtime 收编：每次冷启动都会白白连接一次并撞上
            // "找不到 AGP 服务"，而 PW01-MESH 只允许一个 BLE 连接。
            foreach (var device in _repository.GetAll()
                .Where(device => !device.IsDemo && device.EffectiveProtocol != BluetoothDeviceProtocol.Mesh)
                .OrderByDescending(device => device.IsPinned)
                .ThenByDescending(device => device.LastOperatedAt)
                .ThenByDescending(device => device.AddedAt).ThenBy(device => device.Address, StringComparer.OrdinalIgnoreCase))
                RequestRuntimePreparation(device.Address);
        }
        return Task.CompletedTask;
    }

    public void RequestRuntimePreparation(string address)
    {
        if (!_runtimeStarted || _runtimeStopped || _applicationSuspended || !_repository.Contains(address) || _runtimeBindingAddresses.ContainsKey(address)) return;
        if (IsMeshAddress(address)) return;
        var entry = _runtimeEntries.GetOrAdd(address, key => new(key));
        lock (entry.Sync)
        {
            if (entry.Exclusive || _runtimeStopped) return;
            _ = EnsureRuntimePreparation(entry, refresh: false);
        }
    }

    /// <summary>Mesh 设备的控制经由 MeshDeviceControlService 的短连接，不入 AGP Runtime。</summary>
    private bool IsMeshAddress(string address)
    {
        var saved = _repository.GetAll().FirstOrDefault(device =>
            string.Equals(device.Address, address, StringComparison.OrdinalIgnoreCase));
        return saved?.EffectiveProtocol == BluetoothDeviceProtocol.Mesh;
    }

    public async Task EnsureRuntimeReadyAsync(string address, CancellationToken waitToken = default, bool refresh = false)
    {
        waitToken.ThrowIfCancellationRequested();
        if (!_runtimeStarted) await StartRuntimeAsync(waitToken);
        EnsureApplicationForeground();
        if (!_repository.Contains(address)) throw new InvalidOperationException(AppResources.Device_Removed);
        var entry = _runtimeEntries.GetOrAdd(address, key => new(key));
        lock (entry.Sync) entry.Attention++;
        _runtimeWork.SetAttention(address, true);
        try
        {
            // Cancellation only stops this caller's wait, never the shared workflow.
            await EnsureRuntimePreparation(entry, refresh).WaitAsync(waitToken);
        }
        finally
        {
            bool focused;
            lock (entry.Sync) focused = --entry.Attention > 0;
            _runtimeWork.SetAttention(address, focused);
        }
        var snapshot = GetRuntimeSnapshot(address)!;
        if (snapshot.Availability != DeviceRuntimeAvailability.Ready)
            throw new InvalidOperationException(snapshot.Error ?? AppResources.Control_StateUnavailable);
    }

    private Task EnsureRuntimePreparation(DeviceRuntimeEntry entry, bool refresh)
    {
        lock (entry.Sync)
        {
            if (entry.Exclusive) throw new InvalidOperationException(AppResources.Firmware_Updating);
            if (_runtimeStopped) throw new OperationCanceledException(_applicationCancellation.Token);
            if (entry.Preparation is { IsCompleted: false } running &&
                entry.Epoch == _runtimeEpoch && entry.Lifetime?.IsCancellationRequested == false)
                return running;
            if (!refresh && entry.Snapshot().Availability == DeviceRuntimeAvailability.Ready) return Task.CompletedTask;
            _ = CancellationDispatch.Request(entry.Lifetime, _diagnostics, "Runtime.Preparation.Replace", dispose: true, address: entry.Address);
            entry.Lifetime = CancellationTokenSource.CreateLinkedTokenSource(_applicationCancellation.Token);
            entry.Epoch = _runtimeEpoch;
            if (refresh || !entry.Online)
            {
                entry.Steps.Clear();
                foreach (var step in Enum.GetValues<DeviceRuntimeStep>())
                    entry.Steps[step] = new(DeviceRuntimeStepStatus.Pending, DateTimeOffset.UtcNow);
            }
            entry.Error = null;
            entry.Preparation = PrepareRuntimeDeviceAsync(entry, entry.Epoch, entry.Lifetime.Token, refresh);
            // Fire-and-forget startup failures are recorded by the workflow itself.
            _ = entry.Preparation.ContinueWith(task => _ = task.Exception,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            return entry.Preparation;
        }
    }

    private async Task PrepareRuntimeDeviceAsync(DeviceRuntimeEntry entry, long epoch, CancellationToken token, bool refresh)
    {
        _runtimePreparationLifetime.Value = token;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await RunRuntimeWorkAsync(entry, 2, async operationToken =>
                    {
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
                        deadline.CancelAfter(TimeSpan.FromSeconds(40));
                        var handle = await GetOrCreateClientAsync(entry.Address, deadline.Token,
                            skipInitialLightStateRead: true);
                        RequireCapability(handle.Capabilities, AgpCapabilities.OnOff);
                        if (refresh) handle.HasLightState = false;
                        MutateRuntime(entry, epoch, () =>
                        {
                            entry.Generation = handle.ConnectionGeneration;
                            entry.Online = true;
                            foreach (var step in new[] { DeviceRuntimeStep.Connection, DeviceRuntimeStep.ResponseChannel })
                                entry.Steps[step] = new(DeviceRuntimeStepStatus.Succeeded, DateTimeOffset.UtcNow);
                            entry.Steps[DeviceRuntimeStep.Authentication] = new(handle.Capabilities.HasFlag(AgpCapabilities.SecureControl)
                                ? DeviceRuntimeStepStatus.Succeeded : DeviceRuntimeStepStatus.NotApplicable, DateTimeOffset.UtcNow);
                        });
                        return true;
                    }, token);
                    break;
                }
                catch (Exception exception) when (attempt == 0 && !token.IsCancellationRequested &&
                    exception is not AgpAuthenticationRequiredException && exception.Data["AgpResult"] is null &&
                    exception is IOException or TimeoutException or OperationCanceledException or DeviceConnectionException)
                {
                    // Release the budget before a bounded retry; another device can prepare now.
                    _diagnostics.Warning("BLE/Runtime", $"准备重试：{entry.Address}，{exception.Message}");
                    MutateRuntime(entry, epoch, () => entry.Stage = AppResources.Device_RuntimeRetrying);
                    await Task.Delay(TimeSpan.FromMilliseconds(500), token);
                }
            }

            // Each data operation releases this address and the global budget.
            // A queued user command can enter at every boundary, including schedule slots.
            await RuntimeStepAsync(entry, epoch, DeviceRuntimeStep.StateSubscription, null,
                async (handle, operationToken) => await handle.Transport!.EnsureLightStateSubscriptionAsync(operationToken), token);
            await RuntimeStepAsync(entry, epoch, DeviceRuntimeStep.LightState, null,
                async (handle, operationToken) =>
                {
                    if (!handle.HasLightState)
                    {
                        var state = await handle.Client.ReadLightStateAsync(operationToken);
                        handle.TryAcceptLightState(state);
                        if (!handle.HasLightState)
                            throw new InvalidOperationException(AppResources.Control_StateUnavailable);
                    }
                    CacheControlState(handle);
                }, token);
            await RuntimeStepAsync(entry, epoch, DeviceRuntimeStep.Effects, AgpCapabilities.Effects,
                async (handle, operationToken) =>
                {
                    var effect = await handle.Client.GetEffectAsync(operationToken);
                    EnsureSuccess(effect.Result);
                    handle.EffectPeriod = effect.PeriodMilliseconds;
                    handle.SelectedEffectMode = effect.Mode;
                    if (handle.HasLightState) handle.LastState = handle.LastState with { Mode = effect.Mode };
                    CacheControlState(handle, effect.PeriodMilliseconds);
                }, token);
            await RuntimeStepAsync(entry, epoch, DeviceRuntimeStep.LocalMode, AgpCapabilities.LocalMode,
                async (handle, operationToken) =>
                {
                    var mode = await handle.Client.GetModeAsync(operationToken);
                    EnsureSuccess(mode.Result); handle.LocalMode = mode.Config!;
                }, token);
            await RuntimeStepAsync(entry, epoch, DeviceRuntimeStep.RelativeDelayOn, AgpCapabilities.RelativeTimer,
                async (handle, operationToken) =>
                {
                    var delay = await handle.Client.GetDelayOnAsync(operationToken); EnsureSuccess(delay.Result);
                    MutateRuntime(entry, epoch, () => entry.RelativeDelayOn = delay);
                }, token);
            await RuntimeStepAsync(entry, epoch, DeviceRuntimeStep.RelativeDelayOff, AgpCapabilities.RelativeTimer,
                async (handle, operationToken) =>
                {
                    var delay = await handle.Client.GetDelayOffAsync(operationToken); EnsureSuccess(delay.Result);
                    MutateRuntime(entry, epoch, () => entry.RelativeDelayOff = delay);
                }, token);
            await RuntimeStepAsync(entry, epoch, DeviceRuntimeStep.ColorSubscription, null,
                async (handle, operationToken) =>
                {
                    if (HasColorModel(handle)) await handle.Transport!.EnsureColorStateSubscriptionAsync(operationToken);
                    else MarkRuntimeStep(entry, epoch, DeviceRuntimeStep.ColorSubscription, DeviceRuntimeStepStatus.NotApplicable);
                }, token);
            await RuntimeStepAsync(entry, epoch, DeviceRuntimeStep.ColorState, null,
                async (handle, operationToken) =>
                {
                    if (HasColorModel(handle)) { handle.LastColorState = await handle.Client.ReadColorStateAsync(operationToken); CacheControlState(handle); }
                    else MarkRuntimeStep(entry, epoch, DeviceRuntimeStep.ColorState, DeviceRuntimeStepStatus.NotApplicable);
                }, token);
            await RuntimeStepAsync(entry, epoch, DeviceRuntimeStep.CtlRange, AgpCapabilities.Ctl,
                async (handle, operationToken) =>
                {
                    var range = await handle.Client.GetCtlRangeAsync(operationToken); EnsureSuccess(range.Result);
                    handle.CtlRange = range.Value?.Value ?? throw new InvalidOperationException(AppResources.Control_StateUnavailable);
                    CacheControlState(handle);
                }, token);
            await RuntimeStepAsync(entry, epoch, DeviceRuntimeStep.Clock, AgpCapabilities.RtcTime,
                async (handle, operationToken) =>
                {
                    var time = await handle.Client.GetTimeAsync(operationToken); EnsureSuccess(time.Result);
                    var synced = await handle.Client.SetTimeAsync(DateTimeOffset.UtcNow, operationToken); EnsureSuccess(synced.Result);
                    if (!synced.IsValid) throw new InvalidOperationException(AppResources.Control_StateUnavailable);
                    MutateRuntime(entry, epoch, () => entry.Clock = synced);
                }, token);
            await ReadRuntimeSchedulesAsync(entry, epoch, token);
            await RuntimeStepAsync(entry, epoch, DeviceRuntimeStep.DeviceInformation, null,
                (handle, operationToken) => ReadRuntimeInformationAsync(entry, epoch, handle, operationToken), token);
            MutateRuntime(entry, epoch, () => entry.Stage = null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            MutateRuntime(entry, epoch, () =>
            {
                entry.Error = exception.Message;
                entry.Stage = null;
                if (!_connectionService.IsConnected(entry.Address) || !_handles.ContainsKey(entry.Address)) entry.Online = false;
            });
            _diagnostics.Warning("BLE/Runtime", $"设备准备未完成：{entry.Address}，{exception.Message}");
            throw;
        }
    }

    private async Task RuntimeStepAsync(DeviceRuntimeEntry entry, long epoch, DeviceRuntimeStep step,
        AgpCapabilities? capability, Func<AgpClientHandle, CancellationToken, Task> operation, CancellationToken token)
    {
        var snapshot = GetRuntimeSnapshot(entry.Address);
        if (snapshot?.Steps.GetValueOrDefault(step)?.Status is DeviceRuntimeStepStatus.Succeeded or DeviceRuntimeStepStatus.NotApplicable) return;
        try
        {
            await RunRuntimeWorkAsync(entry, RuntimeDataPriority(entry), async operationToken =>
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                var handle = await GetOrCreateClientAsync(entry.Address, deadline.Token, skipInitialLightStateRead: true);
                if (capability is { } required && !handle.Capabilities.HasFlag(required))
                { MarkRuntimeStep(entry, epoch, step, DeviceRuntimeStepStatus.NotApplicable); return true; }
                MarkRuntimeStep(entry, epoch, step, DeviceRuntimeStepStatus.Running);
                await operation(handle, deadline.Token);
                if (GetRuntimeSnapshot(entry.Address)?.Steps.GetValueOrDefault(step)?.Status != DeviceRuntimeStepStatus.NotApplicable)
                    MarkRuntimeStep(entry, epoch, step, DeviceRuntimeStepStatus.Succeeded);
                return true;
            }, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            MarkRuntimeStep(entry, epoch, step, DeviceRuntimeStepStatus.Failed, exception.Message);
            // An optional-data failure must leave a live authenticated session usable.
            _diagnostics.Warning("BLE/Runtime", $"资料读取失败：{entry.Address}，{step}，{exception.Message}");
        }
    }

    private static bool HasColorModel(AgpClientHandle handle) =>
        (handle.Capabilities & (AgpCapabilities.Lightness16 | AgpCapabilities.Hsl | AgpCapabilities.Ctl)) != 0;

    private async Task ReadRuntimeInformationAsync(DeviceRuntimeEntry entry, long epoch, AgpClientHandle handle, CancellationToken token)
    {
        var services = await _connectionService.EnumerateGattAsync(handle.Address, refreshCache: false, token);
        var service = services.FirstOrDefault(service => service.Id == Guid.Parse("0000180a-0000-1000-8000-00805f9b34fb"));
        if (service is null || !service.Characteristics.Any(characteristic =>
            characteristic.Id == Guid.Parse("00002a26-0000-1000-8000-00805f9b34fb") ||
            characteristic.Id == Guid.Parse("00002a27-0000-1000-8000-00805f9b34fb")))
        { MarkRuntimeStep(entry, epoch, DeviceRuntimeStep.DeviceInformation, DeviceRuntimeStepStatus.NotApplicable); return; }
        foreach (var (id, firmware) in new[]
        {
            (Guid.Parse("00002a26-0000-1000-8000-00805f9b34fb"), true),
            (Guid.Parse("00002a27-0000-1000-8000-00805f9b34fb"), false)
        })
        {
            if (!service.Characteristics.Any(characteristic => characteristic.Id == id)) continue;
            var bytes = await _connectionService.ReadAsync(handle.Address, service.Id, id, token, handle.ConnectionGeneration);
            var value = Encoding.UTF8.GetString(bytes.AsSpan()).Trim('\0', ' ', '\r', '\n');
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException(AppResources.Control_StateUnavailable);
            if (firmware) _repository.SetFirmwareVersion(handle.Address, value);
            else _repository.SetHardwareRevision(handle.Address, value);
        }
    }

    private readonly ConcurrentDictionary<string, IReadOnlyList<AgpSchedule>> _runtimeSchedules = new(StringComparer.OrdinalIgnoreCase);
    private async Task ReadRuntimeSchedulesAsync(DeviceRuntimeEntry entry, long epoch, CancellationToken token)
    {
        if (GetRuntimeSnapshot(entry.Address)?.Steps.GetValueOrDefault(DeviceRuntimeStep.Schedules)?.Status is DeviceRuntimeStepStatus.Succeeded or DeviceRuntimeStepStatus.NotApplicable) return;
        byte slots = 0;
        await RuntimeStepAsync(entry, epoch, DeviceRuntimeStep.ScheduleList, AgpCapabilities.Scheduler,
            async (handle, operationToken) =>
            {
                var list = await handle.Client.GetScheduleListAsync(operationToken); EnsureSuccess(list.Result);
                if (list.MaximumSlots is 0 or > AgpModelCodec.MaximumScheduleSlots) throw new InvalidOperationException(AppResources.Schedule_InvalidSlotCount);
                slots = list.MaximumSlots; _scheduleSlotCounts[entry.Address] = slots;
            }, token);
        if (_repository.GetCapabilities(entry.Address)?.HasFlag(AgpCapabilities.Scheduler) != true)
        { MarkRuntimeStep(entry, epoch, DeviceRuntimeStep.Schedules, DeviceRuntimeStepStatus.NotApplicable); return; }
        if (slots == 0) slots = _scheduleSlotCounts.GetValueOrDefault(entry.Address);
        if (slots == 0) { MarkRuntimeStep(entry, epoch, DeviceRuntimeStep.Schedules, DeviceRuntimeStepStatus.Failed); return; }
        var schedules = new List<AgpSchedule>();
        try
        {
            MarkRuntimeStep(entry, epoch, DeviceRuntimeStep.Schedules, DeviceRuntimeStepStatus.Running);
            for (byte slot = 0; slot < slots; slot++)
            {
                var current = slot;
                var schedule = await RunRuntimeWorkAsync(entry, RuntimeDataPriority(entry), async operationToken =>
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
                    deadline.CancelAfter(TimeSpan.FromSeconds(10));
                    var handle = await GetOrCreateClientAsync(entry.Address, deadline.Token, skipInitialLightStateRead: true);
                    var response = await handle.Client.GetScheduleAsync(current, deadline.Token); EnsureSuccess(response.Result);
                    var value = response.Schedule ?? throw new InvalidOperationException(AppResources.Schedule_ContentMissing);
                    if (value.Slot != current) throw new InvalidOperationException(AppResources.Schedule_SlotMismatch);
                    return value;
                }, token);
                if (!(schedule is { Type: AgpScheduleType.Once, IsEnabled: false, Action: AgpScheduleAction.Off,
                    BrightnessPercent: 0, Selector: 0, TimeValue: 0 })) schedules.Add(schedule);
            }
            _runtimeSchedules[entry.Address] = schedules.AsReadOnly();
            MarkRuntimeStep(entry, epoch, DeviceRuntimeStep.Schedules, DeviceRuntimeStepStatus.Succeeded);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) { MarkRuntimeStep(entry, epoch, DeviceRuntimeStep.Schedules, DeviceRuntimeStepStatus.Failed, exception.Message); }
    }

    private Task<T> RunRuntimeWorkAsync<T>(DeviceRuntimeEntry entry, int priority,
        Func<CancellationToken, Task<T>> execute, CancellationToken token)
    {
        var epoch = entry.Epoch;
        long workGeneration;
        lock (entry.Sync) workGeneration = entry.WorkGeneration;
        return _runtimeWork.Enqueue(entry.Address, priority, async operationToken =>
        {
            EnsureApplicationForeground();
            if (_runtimeStopped || epoch != _runtimeEpoch || !_repository.Contains(entry.Address))
                throw new OperationCanceledException(operationToken);
            lock (entry.Sync) if (entry.Exclusive || entry.WorkGeneration != workGeneration) throw new OperationCanceledException(operationToken);
            MutateRuntime(entry, epoch, () => entry.Busy = true);
            try { return await execute(operationToken); }
            finally { MutateRuntime(entry, epoch, () => entry.Busy = false); }
        }, token);
    }

    private static int RuntimeDataPriority(DeviceRuntimeEntry entry)
    { lock (entry.Sync) return entry.Attention > 0 ? 3 : 4; }

    private async Task<T> ExecuteRuntimeOperationAsync<T>(string address,
        Func<AgpClientHandle, CancellationToken, Task<T>> operation, CancellationToken callerToken,
        bool skipInitialRead, bool refresh, string operationName)
    {
        callerToken.ThrowIfCancellationRequested(); EnsureApplicationForeground();
        var user = operationName.StartsWith("Set", StringComparison.Ordinal) || operationName.StartsWith("Toggle", StringComparison.Ordinal) || operationName == nameof(DeleteScheduleAsync);
        var entry = _runtimeEntries.GetOrAdd(address, key => new(key));
        Guid? operationId = user ? Guid.NewGuid() : null;
        long submittedGeneration;
        // The shared per-address workflow owns command cancellation after submission.
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(_applicationCancellation.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        lock (entry.Sync)
        {
            if (entry.Exclusive) { deadline.Dispose(); throw new InvalidOperationException(AppResources.Firmware_Updating); }
            entry.Epoch = _runtimeEpoch;
            submittedGeneration = entry.WorkGeneration;
            if (user)
            {
                if (operationName.StartsWith("Toggle", StringComparison.Ordinal) && entry.UserOperations > 0)
                { deadline.Dispose(); throw new InvalidOperationException(AppResources.Ble_Connecting); }
                entry.UserOperations++;
                entry.Operation = new(operationId!.Value, operationName, DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow.AddSeconds(30), DeviceRuntimeOperationStage.Queued);
            }
        }
        PublishRuntime(entry);
        try
        {
            var work = RunRuntimeWorkAsync(entry, user ? 1 : RuntimeDataPriority(entry), async operationToken =>
            {
                _runtimeOperationId.Value = operationId;
                if (user) SetRuntimeOperationStage(address, DeviceRuntimeOperationStage.WaitingPrerequisites);
                var handle = await GetOrCreateClientAsync(address, operationToken, skipInitialLightStateRead: true);
                MutateRuntime(entry, entry.Epoch, () =>
                {
                    entry.Online = handle.Capabilities.HasFlag(AgpCapabilities.OnOff);
                    entry.Generation = handle.ConnectionGeneration;
                });
                if (!user && !skipInitialRead && (!handle.HasLightState || refresh))
                {
                    await handle.Transport!.EnsureLightStateSubscriptionAsync(operationToken);
                    handle.LastState = await handle.Client.ReadLightStateAsync(operationToken); CacheControlState(handle);
                }
                try
                {
                    if (user && !operationName.StartsWith("Toggle", StringComparison.Ordinal)) SetRuntimeOperationStage(address, DeviceRuntimeOperationStage.Sent);
                    var result = await operation(handle, operationToken);
                    if (user && !operationName.StartsWith("Toggle", StringComparison.Ordinal)) _repository.RecordOperation(address);
                    if (user && !operationName.StartsWith("Toggle", StringComparison.Ordinal)) SetRuntimeOperationStage(address, DeviceRuntimeOperationStage.Confirmed);
                    CacheControlState(handle);
                    return result;
                }
                catch
                {
                    // Only an invalid native session forces disposal. A read/business
                    // failure can be retried without discarding successful authentication.
                    if (!_connectionService.IsConnected(address)) { await DisposeHandleAsync(address); RecordDeviceOffline(address); }
                    throw;
                }
            }, deadline.Token);
            _ = work.ContinueWith(_ => deadline.Dispose(), TaskContinuationOptions.ExecuteSynchronously);
            if (user) return await work; // Navigation cannot retract a submitted mutation.
            return await work.WaitAsync(callerToken);
        }
        catch (Exception exception)
        {
            if (user)
            {
                var stage = GetRuntimeSnapshot(address)?.LastOperation?.Stage;
                var unknown = exception.Data["AgpResult"] is null && stage is (DeviceRuntimeOperationStage.Sent or DeviceRuntimeOperationStage.Accepted or DeviceRuntimeOperationStage.Unknown);
                SetRuntimeOperationStage(address, unknown ? DeviceRuntimeOperationStage.Unknown : exception is OperationCanceledException
                    ? DeviceRuntimeOperationStage.Canceled : DeviceRuntimeOperationStage.Failed, exception.Message, allowStopped: true, operationId);
            }
            throw;
        }
        finally
        {
            if (user) { lock (entry.Sync) entry.UserOperations--; PublishRuntime(entry); }
            lock (entry.Sync)
                if (entry.WorkGeneration == submittedGeneration) RequestRuntimePreparation(address);
        }
    }

    private bool IsCurrentHandle(AgpClientHandle handle) => !_applicationSuspended && !_applicationClosing &&
        _connectionService.IsConnected(handle.Address) && handle.ConnectionGeneration == _connectionService.GetConnectionGeneration(handle.Address) &&
        (!_handles.TryGetValue(handle.Address, out var current) || ReferenceEquals(handle, current));

    private void SetRuntimeOperationStage(string address, DeviceRuntimeOperationStage stage, string? error = null, bool allowStopped = false, Guid? operationId = null)
    {
        if (!_runtimeEntries.TryGetValue(address, out var entry)) return;
        lock (entry.Sync)
        {
            if ((!allowStopped && _runtimeStopped) || entry.Operation is null) return;
            if ((operationId ?? _runtimeOperationId.Value) is { } id && entry.Operation.Id != id) return;
            entry.Operation = entry.Operation with { Stage = stage, Error = error };
        }
        PublishRuntime(entry);
    }

    private void MarkRuntimeStep(DeviceRuntimeEntry entry, long epoch, DeviceRuntimeStep step,
        DeviceRuntimeStepStatus status, string? error = null) => MutateRuntime(entry, epoch, () =>
    {
        entry.Steps[step] = new(status, DateTimeOffset.UtcNow, error);
        if (status == DeviceRuntimeStepStatus.Running)
            entry.Stage = step switch
            {
                DeviceRuntimeStep.Connection => AppResources.Ble_Connecting,
                DeviceRuntimeStep.ResponseChannel => AppResources.Device_RuntimeResponseChannel,
                DeviceRuntimeStep.Authentication => AppResources.Device_RuntimeAuthenticating,
                _ => AppResources.Pairing_ReadingInformation
            };
        if (error is not null) entry.Error = error;
    });

    private void MutateRuntime(DeviceRuntimeEntry entry, long epoch, Action mutate)
    {
        lock (entry.Sync)
        {
            if (_runtimeStopped || entry.Exclusive || entry.Epoch != epoch || epoch != _runtimeEpoch) return;
            if (_runtimePreparationLifetime.Value?.IsCancellationRequested == true) return;
            if (!_runtimeEntries.TryGetValue(entry.Address, out var current) || !ReferenceEquals(entry, current)) return;
            mutate(); entry.Version++;
        }
        PublishRuntime(entry);
    }
    private void PublishRuntime(DeviceRuntimeEntry entry)
    {
        lock (entry.Sync)
        {
            if (!_runtimeEntries.TryGetValue(entry.Address, out var current) || !ReferenceEquals(entry, current)) return;
            entry.Version++;
            RuntimeStateChanged?.Invoke(this, new(entry.Address, entry.Snapshot()));
        }
    }
    private void StopRuntimeWork(string reason)
    {
        if (!_runtimeStarted) return;
        _runtimeStopped = true; Interlocked.Increment(ref _runtimeEpoch);
        foreach (var entry in _runtimeEntries.Values)
        {
            lock (entry.Sync)
            {
                _ = CancellationDispatch.Request(entry.Lifetime, _diagnostics, "Runtime.Stop", address: entry.Address);
                entry.Online = false; entry.Busy = false;
                entry.Stage = reason; entry.Error = null;
                if (entry.Operation?.Stage is DeviceRuntimeOperationStage.Sent or DeviceRuntimeOperationStage.Accepted)
                    entry.Operation = entry.Operation with { Stage = DeviceRuntimeOperationStage.Unknown, Error = reason };
                else if (entry.Operation?.Stage is DeviceRuntimeOperationStage.Queued or DeviceRuntimeOperationStage.WaitingPrerequisites)
                    entry.Operation = entry.Operation with { Stage = DeviceRuntimeOperationStage.Canceled, Error = reason };
                foreach (var step in entry.Steps.Keys.ToArray())
                    entry.Steps[step] = new(DeviceRuntimeStepStatus.Invalidated, DateTimeOffset.UtcNow);
            }
            PublishRuntime(entry);
        }
    }
    private async Task DrainRuntimeWorkAsync()
    {
        await _runtimeWork.DrainAsync();
        var preparations = _runtimeEntries.Values.Select(entry => entry.Preparation).Where(task => task is not null).ToArray();
        foreach (var preparation in preparations)
            try { await preparation!; } catch { /* Failed/canceled state is already recorded. */ }
    }

    private void OnRuntimeConnectionChanged(object? sender, BluetoothSessionSnapshot snapshot)
    {
        if (!_runtimeStarted || !_runtimeEntries.TryGetValue(snapshot.Address, out var entry)) return;
        if (snapshot.State is not (BluetoothSessionState.Disconnected or BluetoothSessionState.Error or BluetoothSessionState.Updating)) return;
        Task? interrupted = null;
        var recover = false;
        lock (entry.Sync)
        {
            recover = entry.Online && !entry.Exclusive && !_runtimeStopped && !_applicationSuspended && snapshot.State != BluetoothSessionState.Updating && !entry.RecoveryPending;
            if (entry.Online || snapshot.State == BluetoothSessionState.Updating)
            {
                _ = CancellationDispatch.Request(entry.Lifetime, _diagnostics, "Runtime.ConnectionLost", address: entry.Address);
                interrupted = entry.Preparation;
            }
            if (recover) entry.RecoveryPending = true;
            entry.Online = false;
            foreach (var step in entry.Steps.Keys.ToArray()) entry.Steps[step] = new(DeviceRuntimeStepStatus.Invalidated, DateTimeOffset.UtcNow);
            entry.Stage = snapshot.Message;
        }
        RecordDeviceOffline(snapshot.Address); PublishRuntime(entry);
        if (recover) _ = RecoverRuntimeSessionAsync(entry, interrupted);
    }

    private async Task RecoverRuntimeSessionAsync(DeviceRuntimeEntry entry, Task? interrupted)
    {
        try
        {
            if (interrupted is not null) try { await interrupted; } catch { }
            // Only prepare a new session. A previous user command is never replayed.
            RequestRuntimePreparation(entry.Address);
        }
        finally { lock (entry.Sync) entry.RecoveryPending = false; }
    }

    private void OnRuntimeDevicesChanged(object? sender, EventArgs eventArgs)
    {
        if (!_runtimeStarted || _runtimeStopped) return;
        foreach (var device in _repository.GetAll().Where(device => !device.IsDemo))
            if (!_runtimeEntries.ContainsKey(device.Address) && device.Capabilities is { } capabilities &&
                (!capabilities.HasFlag(AgpCapabilities.SecureControl) || _repository.GetTokenBytes(device.Address) is not null))
                RequestRuntimePreparation(device.Address);
    }
}
