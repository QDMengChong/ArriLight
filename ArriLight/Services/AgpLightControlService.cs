using System.Diagnostics;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ArriOpto.BLE.Agp;
using Plugin.BLE.Abstractions.Exceptions;

namespace ArriLight;

public enum AgpControlPageKind
{
    Lightness,
    Hsl,
    Ctl,
    HslAndCtl
}

public enum AgpBindingStage
{
    Connecting,
    WaitingForConnection,
    ReceivingToken
}

public sealed record AgpDeviceControlProfile(
    AgpCapabilities Capabilities,
    AgpColorStatus? ColorState,
    AgpCtlRange CtlRange,
    AgpLightMode EffectMode = AgpLightMode.Steady,
    ushort? EffectPeriodMilliseconds = null,
    AgpLightState? LightState = null)
{
    public AgpControlPageKind PageKind
    {
        get
        {
            var supportsHsl = Capabilities.HasFlag(AgpCapabilities.Hsl);
            var supportsCtl = Capabilities.HasFlag(AgpCapabilities.Ctl);
            return (supportsHsl, supportsCtl) switch
            {
                (true, true) => AgpControlPageKind.HslAndCtl,
                (true, false) => AgpControlPageKind.Hsl,
                (false, true) => AgpControlPageKind.Ctl,
                _ => AgpControlPageKind.Lightness
            };
        }
    }
}

public sealed class AgpAuthenticationRequiredException : InvalidOperationException
{
    public AgpAuthenticationRequiredException(string address)
        : base(Localization.Format(nameof(AppResources.Agp_DeviceNotAuthenticated), address))
    {
        Address = address;
    }

    public string Address { get; }
}

public sealed record DeviceStatusSnapshot(
    bool IsOnline,
    AgpLightState? LightState,
    AgpDeviceControlProfile? Profile,
    DateTimeOffset QueriedAt);

public sealed record DeviceStatusChange(string Address, DeviceStatusSnapshot Status);

internal sealed record SuspendedConnection(
    string Address,
    bool IsControlSession,
    bool HadShortLease,
    bool WasLongLived);

/// <summary>通过 AGP 私有 GATT 协议执行直连灯控；当前版本不使用 Mesh。</summary>
public sealed partial class AgpLightControlService : IAsyncDisposable
{
    public static readonly TimeSpan BindingTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan BindingRetryInterval = TimeSpan.FromMilliseconds(500);
    // Android may take more than 500 ms to complete a normal GATT connection.
    // The previous value cancelled healthy attempts and then retried while the
    // native stack was still unwinding the cancelled connection.
    private static readonly TimeSpan BindingAttemptTimeout = TimeSpan.FromSeconds(3);

    private readonly BluetoothConnectionService _connectionService;
    private readonly AppDiagnosticService _diagnostics;
    private readonly DeviceRepository _repository;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly object _applicationSync = new();
    private CancellationTokenSource _applicationCancellation = new();
    private readonly ConcurrentDictionary<string, AgpClientHandle> _handles =
        new(StringComparer.OrdinalIgnoreCase);
    private List<SuspendedConnection>? _backgroundConnections;
    private bool _wantsForeground = true;
    private bool _applicationClosing;
    private volatile bool _applicationSuspended;
    private readonly ConcurrentDictionary<string, DeviceStatusSnapshot> _deviceStatuses =
        new(StringComparer.OrdinalIgnoreCase);

    public DeviceStatusSnapshot? GetCachedDeviceStatus(string address) =>
        _deviceStatuses.GetValueOrDefault(address);

    public bool IsApplicationSuspended => _applicationSuspended;

    public event EventHandler<DeviceStatusChange>? DeviceStatusChanged;

    public DeviceStatusSnapshot RecordDeviceOffline(string address)
    {
        var status = _deviceStatuses.AddOrUpdate(
            address,
            _ => new DeviceStatusSnapshot(false, null, null, DateTimeOffset.UtcNow),
            (_, previous) => previous with { IsOnline = false, QueriedAt = DateTimeOffset.UtcNow });
        DeviceStatusChanged?.Invoke(this, new(address, status));
        return status;
    }

    private void PublishDeviceStatus(string address, DeviceStatusSnapshot status)
    {
        _deviceStatuses[address] = status;
        DeviceStatusChanged?.Invoke(this, new(address, status));
    }

    /// <summary>
    /// Entering the control page or issuing a control command takes ownership
    /// of the connection retained by a successful home query.
    /// </summary>
    public void PromoteConnectionToLongLived(string address) =>
        _connectionService.PromoteConnectionToLongLived(address);

    /// <summary>A home control action promotes its target and renews other short leases.</summary>
    public void BeginHomeControlOperation(string address)
    {
        _connectionService.PromoteConnectionToLongLived(address);
        _connectionService.RefreshConnectionLeasesExcept(address);
    }

    /// <summary>
    /// Stop the authenticated AGP session when the application is backgrounded,
    /// remembering the connected device so it can be restored on resume.
    /// </summary>
    public Task SuspendForBackgroundAsync(
        CancellationToken cancellationToken = default) =>
        SuspendApplicationConnectionAsync(
            rememberForResume: true, cancellationToken: cancellationToken);

    /// <summary>
    /// Release the session when the application is closing. A closing window
    /// must not leave a pending resume target behind.
    /// </summary>
    public Task DisconnectForApplicationCloseAsync(
        CancellationToken cancellationToken = default) =>
        SuspendApplicationConnectionAsync(
            rememberForResume: false, cancellationToken: cancellationToken);

    /// <summary>
    /// A new native window can reuse this singleton after the previous window
    /// closed. Allow fresh connections again, waiting for its cleanup to finish.
    /// Connections released by closing are not background-resume targets.
    /// </summary>
    public async Task StartForNewWindowAsync(CancellationToken cancellationToken = default)
    {
        await ResumeApplicationConnectionAsync(isNewWindow: true, cancellationToken);
        if (_runtimeStarted && !_applicationSuspended) await StartRuntimeAsync(cancellationToken);
    }

    /// <summary>
    /// Reconnect the device that was disconnected by a background transition;
    /// an existing control session is authenticated again, while a home-only
    /// lease is restarted. Failure is logged and retained for a later resume
    /// attempt rather than being surfaced from a lifecycle callback.
    /// </summary>
    public async Task ResumeAfterBackgroundAsync(CancellationToken cancellationToken = default)
    {
        await ResumeApplicationConnectionAsync(isNewWindow: false, cancellationToken);
        if (_runtimeStarted && !_applicationSuspended) await StartRuntimeAsync(cancellationToken);
    }

    private async Task ResumeApplicationConnectionAsync(
        bool isNewWindow,
        CancellationToken cancellationToken)
    {
        lock (_applicationSync)
        {
            if (_applicationClosing)
            {
                if (!isNewWindow)
                    return;
                // Register the new window before awaiting cleanup. If it stops
                // while waiting, a later Resumed must still be able to recover.
                _applicationClosing = false;
                _backgroundConnections = null;
            }
            _wantsForeground = true;
        }
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            using var resumeCancellation = PrepareResumeCancellation(cancellationToken);
            if (resumeCancellation is null)
                return;
            var token = resumeCancellation.Token;
            if (_runtimeStarted)
            {
                lock (_applicationSync) _backgroundConnections = null;
                return;
            }
            await _operationLock.WaitAsync(token);
            try
            {
                List<SuspendedConnection>? previous;
                lock (_applicationSync)
                    previous = _backgroundConnections;
                if (previous is null || previous.Count == 0)
                    return;
                foreach (var suspended in previous.ToList())
                {
                    token.ThrowIfCancellationRequested();
                    var address = suspended.Address;
                    if (!_repository.Contains(address))
                    {
                        _diagnostics.Information("BLE/Lifecycle", $"恢复目标已删除，放弃恢复：device={address}。");
                        lock (_applicationSync)
                            previous.Remove(suspended);
                        continue;
                    }

                    _diagnostics.Information(
                        "BLE/Lifecycle",
                        $"应用回到前台，立即恢复后台断开的设备：device={address}。");
                    using var timing = _diagnostics.BeginBleTiming("AGP.Lifecycle.Resume", address);
                    try
                    {
                        if (suspended.IsControlSession)
                            await GetOrCreateClientAsync(address, token);
                        else
                        {
                            await _connectionService.ConnectAsync(address, token);
                            // A connected PW01 no longer advertises. Restore its public
                            // state here so the home page does not depend on another scan.
                            await ReadPublicDeviceStatusAsync(address, token);
                        }

                        if (suspended.WasLongLived || suspended.IsControlSession)
                            _connectionService.PromoteConnectionToLongLived(address);
                        else if (suspended.HadShortLease)
                            _connectionService.RetainConnectionFor(address);

                        token.ThrowIfCancellationRequested();
                        lock (_applicationSync)
                            previous.Remove(suspended);
                        timing.SetResult("restored");
                        _diagnostics.Information(
                            "BLE/Lifecycle",
                            $"后台断开的设备已恢复：device={address}，authenticated={suspended.IsControlSession}，shortLease={suspended.HadShortLease}。");
                    }
                    catch (Exception exception) when (!token.IsCancellationRequested)
                    {
                        timing.SetResult(TimingResult(exception));
                        await DisposeHandleAsync(address);
                        try { await _connectionService.DisconnectAsync(address, CancellationToken.None); }
                        catch (Exception cleanupException)
                        {
                            _diagnostics.Warning("BLE/Lifecycle", $"恢复失败后的连接清理失败：device={address}，{cleanupException.Message}");
                        }
                        RecordDeviceOffline(address);
                        _diagnostics.Warning(
                            "BLE/Lifecycle",
                            $"设备恢复未完成，保留该目标并继续恢复其他设备：device={address}，{exception.Message}");
                    }
                }
                token.ThrowIfCancellationRequested();
                lock (_applicationSync)
                {
                    if (ReferenceEquals(_backgroundConnections, previous) && previous.Count == 0)
                        _backgroundConnections = null;
                }
            }
            catch (Exception exception)
            {
                _diagnostics.Warning(
                    "BLE/Lifecycle",
                    $"前台恢复未完成，保留目标供下次恢复：{exception.Message}");
                cancellationToken.ThrowIfCancellationRequested();
            }
            finally
            {
                lock (_applicationSync)
                {
                    if (_wantsForeground && !_applicationClosing && !token.IsCancellationRequested)
                        _applicationSuspended = false;
                }
                _operationLock.Release();
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private CancellationTokenSource? PrepareResumeCancellation(CancellationToken cancellationToken)
    {
        lock (_applicationSync)
        {
            if (!_wantsForeground || _applicationClosing)
                return null;
            // A duplicate foreground event must preserve cancellation links
            // held by already running preparation and commands.
            if (_applicationCancellation.IsCancellationRequested)
            {
                _applicationCancellation.Dispose();
                _applicationCancellation = new CancellationTokenSource();
            }
            _applicationSuspended = false;
            _connectionService.SetApplicationSuspended(false);
            return CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _applicationCancellation.Token);
        }
    }

    private void EnsureApplicationForeground()
    {
        if (_applicationSuspended || _applicationClosing)
            throw new OperationCanceledException(
                AppResources.Ble_AppNotInForeground,
                new CancellationToken(true));
    }

    private CancellationTokenSource LinkApplicationCancellation(
        CancellationToken cancellationToken)
    {
        lock (_applicationSync)
        {
            if (_applicationSuspended || _applicationClosing)
                throw new OperationCanceledException(
                    AppResources.Ble_AppNotInForeground,
                    new CancellationToken(true));
            return CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _applicationCancellation.Token);
        }
    }

    public AgpLightControlService(
        BluetoothConnectionService connectionService,
        AppDiagnosticService diagnostics,
        DeviceRepository repository,
        DeviceWorkScheduler runtimeWork)
    {
        _connectionService = connectionService;
        _diagnostics = diagnostics;
        _repository = repository;
        // AGP Runtime 与 Mesh 控制共享同一份设备预算，避免两条链路叠加超出 BLE 能力。
        _runtimeWork = runtimeWork;
        _connectionService.StateChanged += OnRuntimeConnectionChanged;
        _repository.DevicesChanged += OnRuntimeDevicesChanged;
    }

    /// <summary>Disconnect a forgotten device before deleting its local record and credentials.</summary>
    public async Task<bool> RemoveDeviceAsync(
        string address, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        if (_runtimeStarted)
            return await RemoveRuntimeDeviceAsync(address, cancellationToken);
        using var timing = _diagnostics.BeginBleTiming("AGP.RemoveDevice", address);
        _diagnostics.Information("BLE/Removal", $"开始解除绑定：device={address}。");
        await MeasureAsync("AGP.RemoveDevice.Lock", address,
            () => _operationLock.WaitAsync(cancellationToken));
        try
        {
            await DisposeHandleAsync(address);

            // Keep the saved record if the native disconnect fails, so removal can be retried.
            await _connectionService.DisconnectDeviceAsync(address, cancellationToken);
            _deviceStatuses.TryRemove(address, out _);
            var removed = _repository.Remove(address);
            _diagnostics.Information(
                "BLE/Removal", $"解除绑定完成：device={address}，removed={removed}，目标连接已释放，状态与扫描缓存已清理。");
            timing.SetResult(removed ? "removed" : "already-absent");
            return removed;
        }
        catch (Exception exception)
        {
            timing.SetResult(TimingResult(exception));
            _diagnostics.Error("BLE/Removal", $"解除绑定未完成：device={address}，保留本地记录供重试。", exception);
            throw;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>用户明确发起绑定时调用，在设备配对窗口内获取并保存 Token。</summary>
    public async Task BindAsync(
        string address,
        CancellationToken cancellationToken = default)
        => await BindAsync(
            address,
            bindingProgress: null,
            cancellationToken: cancellationToken);

    /// <summary>
    /// 连接暂时不可用时每隔 500 ms 重试；收到成功 Token 响应并保存
    /// Token 与配对阶段读取的能力快照后完成绑定。
    /// ATT Indication 接收确认由手机 BLE 栈完成，PW01 随后主动断开供其他 APP 配对。
    /// </summary>
    public Task BindAsync(string address,
        Func<AgpBindingStage, CancellationToken, Task>? bindingProgress,
        CancellationToken cancellationToken = default) =>
        RunRuntimeBindingAsync(address, () => BindCoreAsync(address, bindingProgress, cancellationToken), cancellationToken);

    private async Task BindCoreAsync(
        string address,
        Func<AgpBindingStage, CancellationToken, Task>? bindingProgress,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(address))
            throw new ArgumentException(AppResources.Ble_AddressRequired, nameof(address));

        EnsureApplicationForeground();
        using var applicationCancellation = LinkApplicationCancellation(cancellationToken);
        cancellationToken = applicationCancellation.Token;
        using var bindingCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bindingCancellation.CancelAfter(BindingTimeout);
        var token = bindingCancellation.Token;

        // 先显示进度，再等待操作锁，用户保存名称后立即看到连接提示。
        if (bindingProgress is not null)
            await bindingProgress(AgpBindingStage.Connecting, token);

        await _operationLock.WaitAsync(token);
        _runtimeBindingAddresses.TryAdd(address, 0);
        try
        {
            await DisposeHandleAsync(address);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var attemptStarted = Stopwatch.GetTimestamp();
                using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                // 只在一次完整的 Android 连接窗口内没有结果时取消本轮；
                // 500 ms 只用于两轮之间的退避，不能作为原生建连超时。
                attemptCancellation.CancelAfter(BindingAttemptTimeout);
                try
                {
                    await _connectionService.ConnectAsync(address, attemptCancellation.Token);
                    break;
                }
                catch (Exception exception) when (
                    !token.IsCancellationRequested &&
                    exception is DeviceConnectionException or IOException or
                        TimeoutException or OperationCanceledException)
                {
                    // BLE 连接失败不能确定是占用还是链路问题。只重试连接，
                    // 不重试已连接后的协议拒绝、无效 Token 或权限错误。
                    var reason = exception is OperationCanceledException
                        ? Localization.Format(nameof(AppResources.Ble_ConnectionAttemptTimedOut), BindingAttemptTimeout.TotalSeconds)
                        : exception.Message;
                    _diagnostics.Warning(
                        "AGP/Binding",
                        $"设备 {address} 连接未完成，按 {BindingRetryInterval.TotalMilliseconds:0} ms 间隔重试：{reason}");
                    if (bindingProgress is not null)
                        await bindingProgress(AgpBindingStage.WaitingForConnection, token);
                    var retryDelay = BindingRetryInterval - Stopwatch.GetElapsedTime(attemptStarted);
                    if (retryDelay > TimeSpan.Zero)
                        await Task.Delay(retryDelay, token);
                }
            }

            if (bindingProgress is not null)
                await bindingProgress(AgpBindingStage.ReceivingToken, token);

            await using var transport = new AgpGattTransport(_connectionService, address);
            await transport.StartAsync(token, subscribeState: false);
            await using var client = new AgpClient(transport);
            var capabilities = await client.ReadCapabilitiesAsync(token);
            _diagnostics.Information(
                "AGP/Binding",
                $"配对阶段已读取设备能力：device={address}，capabilities=0x{(ulong)capabilities:X}。");
            if (!capabilities.HasFlag(AgpCapabilities.SecureControl))
                throw new NotSupportedException(AppResources.Agp_TokenPairingUnsupported);

            var provision = await client.GetProvisionTokenAsync(cancellationToken: token);
            if (provision.Result == AgpResultCode.NotAuthorized)
            {
                throw new InvalidOperationException(
                    AppResources.Agp_PairingWindowClosed);
            }
            EnsureSuccess(provision.Result);
            if (provision.Token is not { Length: 4 } provisionedToken)
                throw new InvalidOperationException(AppResources.Agp_InvalidToken);
            if (!_repository.SetTokenAndCapabilities(address, provisionedToken, capabilities))
            {
                throw new InvalidOperationException(
                    AppResources.Agp_PairingRecordMissing);
            }

            // Token 已保存即配对成功；原连接会被设备断开，不能在这里继续
            // AUTH_TOKEN、读取灯状态或校时，也不能因断开而删除已保存的 Token。
            _diagnostics.Information(
                "AGP/Binding",
                $"设备 {address} 的 Token 和能力已保存，配对完成。");
        }
        finally
        {
            try
            {
                // 新固件会主动断开；同时兼容仍保持连接的设备。清理异常不改变配对结果。
                using var cleanupCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _connectionService.DisconnectAsync(address, cleanupCancellation.Token);
            }
            catch (Exception exception)
            {
                _diagnostics.Warning("AGP/Binding", $"配对连接清理失败：{exception.Message}");
            }
            finally
            {
                _runtimeBindingAddresses.TryRemove(address, out _);
                _operationLock.Release();
                RequestRuntimePreparation(address);
            }
        }
    }

    /// <summary>确保当前 AGP 会话已完成 Token 认证，供 OTA 前置检查使用。</summary>
    public Task EnsureAuthorizedAsync(
        string address,
        CancellationToken cancellationToken = default) =>
        WithClientAsync(address, (_, _) => Task.CompletedTask, cancellationToken);

    public Task SetOnOffAsync(
        string address,
        bool isOn,
        CancellationToken cancellationToken = default)
    {
        return WithClientAsync(
            address,
            async (handle, operationToken) =>
            {
                RequireCapability(handle.Capabilities, AgpCapabilities.OnOff);
                await MeasureAsync("AGP.Command.OnOff", address, async () =>
                {
                    var response = await handle.Client.SetOnOffAsync(
                        isOn, cancellationToken: operationToken);
                    BleTimingScope.Current?.Mark($"desiredOn={isOn} protocolResult={response.Result}");
                    EnsureSuccess(response);
                }, $"desiredOn={isOn}");
                handle.LastState = handle.LastState with { IsOn = isOn };
                CacheControlState(handle);
            },
            cancellationToken);
    }

    public Task ToggleOnOffAsync(string address, CancellationToken cancellationToken = default) =>
        WithClientAsync(address, async (handle, operationToken) =>
        {
            await TogglePowerCoreAsync(handle, operationToken);
        }, cancellationToken, skipInitialLightStateRead: true);

    private async Task<AgpLightState?> TogglePowerCoreAsync(
        AgpClientHandle handle, CancellationToken cancellationToken)
    {
        RequireCapability(handle.Capabilities, AgpCapabilities.OnOff);
        var completion = new TaskCompletionSource<AgpLightState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmationSync = new object();
        uint? baselineRevision = handle.HasLightState ? handle.LastState.Revision : null;
        uint? acceptedRevision = null;
        AgpLightState? candidate = null;
        bool IsCurrentResult(AgpLightState state) => IsCurrentHandle(handle) &&
            state.RemainingTime == 0 &&
            (baselineRevision is not { } baseline || unchecked((int)(state.Revision - baseline)) > 0) &&
            (!handle.HasLightState || unchecked((int)(state.Revision - handle.LastState.Revision)) >= 0) &&
            (acceptedRevision is not { } accepted || unchecked((int)(state.Revision - accepted)) >= 0);
        bool IsCompletedNotification(AgpLightState state) => IsCurrentResult(state) &&
            acceptedRevision is { } accepted && unchecked((int)(state.Revision - accepted)) > 0;
        void OnState(object? sender, AgpLightState state)
        {
            lock (confirmationSync)
            {
                if (!IsCurrentResult(state)) return;
                // Notifications can arrive before the native write completes.
                // Hold the latest candidate until the acceptance revision is known.
                candidate = state;
                if (IsCompletedNotification(state)) completion.TrySetResult(state);
            }
        }
        handle.Client.LightStateChanged += OnState;
        try
        {
            SetRuntimeOperationStage(handle.Address, DeviceRuntimeOperationStage.Sent);
            var response = await handle.Client.ToggleOnOffAsync(cancellationToken: cancellationToken);
            EnsureSuccess(response);
            lock (confirmationSync)
            {
                acceptedRevision = response.StateRevision;
                if (candidate is { } state && IsCompletedNotification(state)) completion.TrySetResult(state);
            }
            SetRuntimeOperationStage(handle.Address, DeviceRuntimeOperationStage.Accepted);
            _repository.RecordOperation(handle.Address);
            if (_runtimeStarted)
            {
                try { await handle.Transport!.EnsureLightStateSubscriptionAsync(cancellationToken); }
                catch (Exception exception) when (exception is not OperationCanceledException)
                { _diagnostics.Warning("AGP/State", $"结果订阅失败，将读取确认：{exception.Message}"); }
            }
            // An acceptance response carries no target power. Use the completed
            // hardware notification, including reversals of an in-flight ramp.
            try
            {
                var state = await completion.Task.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken);
                handle.LastState = state;
                CacheControlState(handle);
                SetRuntimeOperationStage(handle.Address, DeviceRuntimeOperationStage.Confirmed);
                return handle.LastState;
            }
            catch (TimeoutException)
            {
                try
                {
                    var state = await handle.Client.ReadLightStateAsync(cancellationToken);
                    lock (confirmationSync)
                        if (!IsCurrentResult(state)) throw new InvalidOperationException(AppResources.Control_StateUnavailable);
                    handle.LastState = state;
                    CacheControlState(handle);
                    SetRuntimeOperationStage(handle.Address, DeviceRuntimeOperationStage.Confirmed);
                    return handle.LastState;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // A failed read must never resend the non-idempotent toggle.
                    handle.InvalidateLightState(baselineRevision, acceptedRevision);
                    CacheControlState(handle);
                    if (_runtimeEntries.TryGetValue(handle.Address, out var entry))
                        MarkRuntimeStep(entry, entry.Epoch, DeviceRuntimeStep.LightState, DeviceRuntimeStepStatus.Failed, exception.Message);
                    SetRuntimeOperationStage(handle.Address, DeviceRuntimeOperationStage.Unknown, exception.Message);
                    _diagnostics.Warning("AGP/State", $"翻转已接受，读取结果状态失败：{exception.Message}");
                    return null;
                }
            }
        }
        finally { handle.Client.LightStateChanged -= OnState; }
    }

    public Task SetLightnessAsync(
        string address,
        byte brightnessPercent,
        CancellationToken cancellationToken = default) =>
        WithClientAsync(
            address,
            async (handle, operationToken) =>
            {
                if (handle.Capabilities.HasFlag(AgpCapabilities.Lightness16))
                {
                    EnsureSuccess(await handle.Client.SetLightness16Async(
                        AgpColorModelCodec.BrightnessPercentToLightness16(
                            brightnessPercent),
                        cancellationToken: operationToken));
                }
                else
                {
                    RequireCapability(handle.Capabilities, AgpCapabilities.Lightness);
                    EnsureSuccess(await handle.Client.SetLightnessAsync(
                        brightnessPercent,
                        cancellationToken: operationToken));
                }
            },
            cancellationToken);

    public Task SetHslAsync(
        string address,
        byte brightnessPercent,
        double hue,
        double saturation,
        CancellationToken cancellationToken = default) =>
        WithClientAsync(
            address,
            async (handle, operationToken) =>
            {
                RequireCapability(handle.Capabilities, AgpCapabilities.Hsl);
                var value = new AgpHsl(
                    AgpColorModelCodec.BrightnessPercentToLightness16(
                        brightnessPercent),
                    RatioToUInt16(hue),
                    RatioToUInt16(saturation));
                EnsureSuccess(await handle.Client.SetHslAsync(
                    value,
                    cancellationToken: operationToken));
            },
            cancellationToken);

    public Task SetCtlAsync(
        string address,
        byte brightnessPercent,
        ushort temperatureKelvin,
        short deltaUv = 0,
        CancellationToken cancellationToken = default) =>
        WithClientAsync(
            address,
            async (handle, operationToken) =>
            {
                RequireCapability(handle.Capabilities, AgpCapabilities.Ctl);
                EnsureSuccess(await handle.Client.SetCtlAsync(
                    new AgpCtl(
                        AgpColorModelCodec.BrightnessPercentToLightness16(
                            brightnessPercent),
                        temperatureKelvin,
                        handle.Capabilities.HasFlag(AgpCapabilities.CtlDeltaUv)
                            ? deltaUv
                            : (short)0),
                    cancellationToken: operationToken));
            },
            cancellationToken);

    public Task<AgpEffectResponse> SetEffectAsync(
        string address,
        AgpLightMode mode,
        ushort periodMilliseconds,
        CancellationToken cancellationToken = default) =>
        WithClientAsync(
            address,
            async (handle, operationToken) =>
            {
                RequireCapability(handle.Capabilities, AgpCapabilities.Effects);
                var response = await handle.Client.SetEffectAsync(
                    mode,
                    periodMilliseconds,
                    operationToken);
                EnsureSuccess(response.Result);
                // EFFECT_SET selects a mode without changing logical power.
                handle.SelectedEffectMode = response.Mode;
                if (handle.HasLightState) handle.LastState = handle.LastState with { Mode = response.Mode };
                CacheControlState(handle, response.PeriodMilliseconds);
                return response;
            },
            cancellationToken);

    public Task<AgpDeviceControlProfile> GetControlProfileAsync(
        string address, CancellationToken cancellationToken = default,
        bool useCachedLightState = false, bool refreshLightState = false) => _runtimeStarted
            ? GetRuntimeProfileAsync(address, cancellationToken, refreshLightState)
            : GetLegacyControlProfileAsync(address, cancellationToken, useCachedLightState, refreshLightState);

    private Task<AgpDeviceControlProfile> GetLegacyControlProfileAsync(
        string address,
        CancellationToken cancellationToken = default,
        bool useCachedLightState = false,
        bool refreshLightState = false) =>
        WithClientAsync(
            address,
            async (handle, operationToken) =>
            {
                if (handle.Client.HasColorStateCharacteristic)
                {
                    try
                    {
                        handle.LastColorState = await MeasureAsync(
                            "AGP.Profile.ReadColorState", address,
                            () => handle.Client.ReadColorStateAsync(operationToken));
                    }
                    catch (Exception exception)
                    {
                        _diagnostics.Warning(
                            "AGP/ColorState",
                            $"读取 Color State 失败，继续使用能力位选择控制页：{exception.Message}");
                    }
                }

                if (handle.Capabilities.HasFlag(AgpCapabilities.Ctl) &&
                    handle.CtlRange is null)
                {
                    try
                    {
                        var response = await MeasureAsync(
                            "AGP.Profile.GetCtlRange", address,
                            () => handle.Client.GetCtlRangeAsync(operationToken));
                        if (response.IsSuccess && response.Value is not null)
                            handle.CtlRange = response.Value.Value;
                    }
                    catch (Exception exception)
                    {
                        _diagnostics.Warning(
                            "AGP/CTL",
                            $"读取 CTL 范围失败，使用协议默认范围：{exception.Message}");
                    }
                }

                var effectMode = handle.LastState.Mode;
                ushort? effectPeriodMilliseconds = null;
                if (handle.Capabilities.HasFlag(AgpCapabilities.Effects))
                {
                    try
                    {
                        var effect = await MeasureAsync(
                            "AGP.Profile.GetEffect", address,
                            () => handle.Client.GetEffectAsync(operationToken));
                        EnsureSuccess(effect.Result);
                        effectMode = effect.Mode;
                        effectPeriodMilliseconds = effect.PeriodMilliseconds;
                    }
                    catch (Exception exception)
                    {
                        _diagnostics.Warning(
                            "AGP/Effect",
                            $"读取特效参数失败，继续使用 Light State 的模式：{exception.Message}");
                    }
                }

                operationToken.ThrowIfCancellationRequested();
                // The QR carries no firmware or effect settings. Populate these
                // from the device both after import and on later control-page retries.
                var savedDevice = _repository.GetByAddress(address);
                if (savedDevice is { HasKnownFirmwareVersion: false })
                {
                    var firmware = await _connectionService.TryReadFirmwareRevisionAsync(address, operationToken);
                    operationToken.ThrowIfCancellationRequested();
                    if (!string.IsNullOrWhiteSpace(firmware))
                        _repository.SetFirmwareVersion(address, firmware);
                }
                if (effectPeriodMilliseconds is { } period &&
                    period >= AgpModelCodec.MinimumEffectPeriodMilliseconds &&
                    period <= AgpModelCodec.MaximumEffectPeriodMilliseconds)
                {
                    if (effectMode == AgpLightMode.Blink &&
                        savedDevice?.BlinkEffectParameters?.PeriodMilliseconds != period)
                        _repository.SetBlinkEffectParameters(address, new(period));
                    else if (effectMode == AgpLightMode.Breathe &&
                        savedDevice?.BreatheEffectParameters?.PeriodMilliseconds != period)
                        _repository.SetBreatheEffectParameters(address, new(period));
                }
                var profile = new AgpDeviceControlProfile(
                    handle.Capabilities,
                    handle.LastColorState,
                    handle.CtlRange ?? DefaultCtlRange,
                    effectMode,
                    effectPeriodMilliseconds,
                    handle.LastState);
                _deviceStatuses[address] = new DeviceStatusSnapshot(
                    true, handle.LastState, profile, DateTimeOffset.UtcNow);
                return profile;
            },
            cancellationToken, useCachedLightState, refreshLightState);

    public Task<bool?> GetOnOffStateAsync(
        string address,
        CancellationToken cancellationToken = default) =>
        WithClientAsync<bool?>(
            address,
            async (handle, operationToken) =>
            {
                handle.LastState = await handle.Client.ReadLightStateAsync(operationToken);
                CacheControlState(handle);
                return handle.LastState.IsOn;
            },
            cancellationToken);

    /// <summary>
    /// 读取公开 Light State 并缓存，供主页和控制页复用，不执行 Token 认证。
    /// </summary>
    public async Task<DeviceStatusSnapshot> QueryDeviceStatusAsync(
        string address,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(address))
            throw new ArgumentException(AppResources.Ble_AddressRequired, nameof(address));

        if (_runtimeStarted)
            return await WithClientAsync(address, async (handle, operationToken) =>
            {
                await handle.Transport!.EnsureLightStateSubscriptionAsync(operationToken);
                handle.LastState = await handle.Client.ReadLightStateAsync(operationToken);
                CacheControlState(handle);
                return GetCachedDeviceStatus(address)!;
            }, cancellationToken, skipInitialLightStateRead: true);

        EnsureApplicationForeground();
        using var applicationCancellation = LinkApplicationCancellation(cancellationToken);
        cancellationToken = applicationCancellation.Token;
        _connectionService.ResetConnectionLeaseOwnership(address);
        using var timing = _diagnostics.BeginBleTiming("AGP.Home.PublicState", address);
        await MeasureAsync("AGP.Home.Lock", address,
            () => _operationLock.WaitAsync(cancellationToken));
        var queryStarted = false;
        var querySucceeded = false;
        try
        {
            EnsureApplicationForeground();
            if (!_repository.Contains(address))
                throw new InvalidOperationException(AppResources.Device_Removed);

            queryStarted = true;
            // 公开读取不创建授权会话，也不拆除本设备已经存在的持续控制会话。
            var snapshot = await ReadPublicDeviceStatusAsync(address, cancellationToken);
            querySucceeded = true;
            timing.SetResult("success");
            return snapshot;
        }
        catch (Exception exception)
        {
            timing.SetResult(TimingResult(exception));
            throw;
        }
        finally
        {
            try
            {
                // A rejected query for a removed device must leave another device's session intact.
                if (queryStarted)
                {
                    if (querySucceeded &&
                        _connectionService.IsConnected(address))
                    {
                        // 保留一个很短的同设备连接租约，让主页刚完成查询后
                        // 的控制操作直接接管连接；已有持续连接不会被降级。
                        _connectionService.RetainConnectionFor(address);
                        _diagnostics.Information(
                            "BLE/Connection",
                            $"主页查询成功，暂时保留设备 {address} 的连接供后续控制操作接管。");
                    }
                    else
                    {
                        await DisposeHandleAsync(address);
                        try
                        {
                            await MeasureAsync("AGP.Home.Disconnect", address,
                                () => _connectionService.DisconnectAsync(
                                    address,
                                    BluetoothDisconnectReason.QueryFailure,
                                    CancellationToken.None));
                        }
                        catch
                        {
                            // 保留查询本身的异常，断开清理失败不覆盖它。
                        }
                    }
                }
            }
            finally
            {
                _operationLock.Release();
            }
        }
    }

    // Caller owns the business operation lock; lifecycle resume must not acquire
    // it a second time through QueryDeviceStatusAsync.
    private async Task<DeviceStatusSnapshot> ReadPublicDeviceStatusAsync(
        string address, CancellationToken cancellationToken)
    {
        var packet = await _connectionService.ReadAsync(
            address, AgpProfile.ServiceId, AgpProfile.LightStateCharacteristicId,
            cancellationToken);
        var state = AgpModelCodec.DecodeLightState(AgpCodec.Decode(packet.AsSpan()));
        cancellationToken.ThrowIfCancellationRequested();
        if (_handles.TryGetValue(address, out var controlHandle) &&
            controlHandle.ConnectionGeneration == _connectionService.GetConnectionGeneration(address))
            controlHandle.LastState = state;
        var previousProfile = GetCachedDeviceStatus(address)?.Profile;
        var profile = previousProfile is null ? null : previousProfile with
        {
            EffectMode = state.Mode,
            EffectPeriodMilliseconds = previousProfile.EffectMode == state.Mode
                ? previousProfile.EffectPeriodMilliseconds : null,
            LightState = state
        };
        var snapshot = new DeviceStatusSnapshot(true, state, profile, DateTimeOffset.UtcNow);
        PublishDeviceStatus(address, snapshot);
        return snapshot;
    }

    public async Task<bool> GetOnOffStateWithoutAuthorizationAsync(
        string address, CancellationToken cancellationToken = default) =>
        (await QueryDeviceStatusAsync(address, cancellationToken)).LightState!.IsOn;

    public Task SetTimerAsync(
        string address,
        TimeSpan delay,
        bool turnOn,
        CancellationToken cancellationToken = default)
    {
        if (delay < TimeSpan.Zero || delay.TotalSeconds > AgpModelCodec.MaximumDelaySeconds)
            throw new ArgumentOutOfRangeException(nameof(delay));

        return WithClientAsync(
            address,
            async (handle, operationToken) =>
            {
                RequireCapability(handle.Capabilities, AgpCapabilities.RelativeTimer);
                var seconds = checked((uint)Math.Ceiling(delay.TotalSeconds));
                if (turnOn)
                {
                    var response = await handle.Client.SetDelayOnAsync(seconds, cancellationToken: operationToken);
                    EnsureSuccess(response.Result);
                    CacheRuntimeDelay(address, response);
                }
                else
                {
                    var response = await handle.Client.SetDelayOffAsync(seconds, cancellationToken: operationToken);
                    EnsureSuccess(response.Result);
                    CacheRuntimeDelay(address, response);
                }
            },
            cancellationToken);
    }

    public Task SetScheduleAsync(
        string address,
        AgpSchedule schedule,
        CancellationToken cancellationToken = default) =>
        WithClientAsync(
            address,
            async (handle, operationToken) =>
            {
                RequireCapability(handle.Capabilities, AgpCapabilities.Scheduler);
                if (_runtimeStarted && handle.Capabilities.HasFlag(AgpCapabilities.RtcTime))
                {
                    var synced = await handle.Client.SetTimeAsync(DateTimeOffset.UtcNow, operationToken);
                    EnsureSuccess(synced.Result);
                    CacheRuntimeClock(address, synced);
                    if (!synced.IsValid) throw new InvalidOperationException(AppResources.Control_StateUnavailable);
                }
                EnsureSuccess(await handle.Client.SetScheduleAsync(schedule, operationToken));
                InvalidateRuntimeSchedules(address);
            },
            cancellationToken);

    public Task DeleteScheduleAsync(
        string address,
        byte slot,
        CancellationToken cancellationToken = default) =>
        WithClientAsync(
            address,
            async (handle, operationToken) =>
            {
                RequireCapability(handle.Capabilities, AgpCapabilities.Scheduler);
                var response = await handle.Client.DeleteScheduleAsync(slot, operationToken);
                EnsureSuccess(response.Result);
                InvalidateRuntimeSchedules(address);
            },
            cancellationToken);

    public async ValueTask DisposeAsync()
    {
        try
        {
            await DisconnectForApplicationCloseAsync();
        }
        catch (Exception exception)
        {
            _diagnostics.Warning(
                "BLE/Lifecycle",
                $"释放应用关闭时的 AGP 会话失败：{exception.Message}");
        }

        _lifecycleLock.Dispose();
        _operationLock.Dispose();
        _connectionService.StateChanged -= OnRuntimeConnectionChanged;
        _repository.DevicesChanged -= OnRuntimeDevicesChanged;
        _applicationCancellation.Dispose();
    }

    private async Task SuspendApplicationConnectionAsync(
        bool rememberForResume,
        CancellationToken cancellationToken)
    {
        StopRuntimeWork(rememberForResume ? AppResources.Ble_AppNotInForeground : AppResources.Ble_NotConnected);
        var addressesBeforeSuspend = _connectionService.GetConnectedAddresses();
        var shortLeasesBeforeSuspend = addressesBeforeSuspend
            .ToDictionary(
                address => address,
                address => _connectionService.HasActiveConnectionLeaseFor(address),
                StringComparer.OrdinalIgnoreCase);
        lock (_applicationSync)
        {
            _wantsForeground = false;
            _applicationSuspended = true;
            if (!rememberForResume)
            {
                _applicationClosing = true;
                _backgroundConnections = null;
            }
            _applicationCancellation.Cancel();
        }
        _connectionService.SetApplicationSuspended(true);
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            await _operationLock.WaitAsync(cancellationToken);
            try
            {
                var addresses = _connectionService.GetConnectedAddresses();
                if (!rememberForResume)
                {
                    // A previously queued Stopped callback may have recorded
                    // targets after closing was requested. Drop those as well.
                    lock (_applicationSync)
                        _backgroundConnections = null;
                }
                if (rememberForResume && addresses.Count > 0)
                {
                    lock (_applicationSync)
                    {
                        var newlySuspended = addresses
                            .Select(address => new SuspendedConnection(
                                address,
                                _handles.ContainsKey(address),
                                shortLeasesBeforeSuspend.GetValueOrDefault(address),
                                _connectionService.IsLongLived(address)))
                            .ToList();
                        _backgroundConnections = (_backgroundConnections ?? [])
                            .Where(previous => !addresses.Contains(previous.Address, StringComparer.OrdinalIgnoreCase))
                            .Concat(newlySuspended).ToList();
                    }
                    _diagnostics.Information(
                        "BLE/Lifecycle",
                        $"应用进入后台，记录待恢复设备：{string.Join(",", addresses)}。");
                }

                await DrainRuntimeWorkAsync();
                await DisposeAllHandlesAsync();
                List<Exception> failures = [];
                foreach (var address in addresses)
                {
                    try
                    {
                        await _connectionService.DisconnectAsync(
                            address,
                            rememberForResume
                                ? BluetoothDisconnectReason.ApplicationBackground
                                : BluetoothDisconnectReason.ApplicationClosing,
                            CancellationToken.None);
                        RecordDeviceOffline(address);
                    }
                    catch (Exception exception)
                    {
                        failures.Add(exception);
                        _diagnostics.Warning("BLE/Lifecycle", $"生命周期释放设备失败，继续清理其他设备：device={address}，{exception.Message}");
                    }
                    _diagnostics.Information(
                        "BLE/Lifecycle",
                        $"应用生命周期切换释放 BLE 连接：reason={(rememberForResume ? "background" : "closing")}。");
                }
                if (failures.Count > 0)
                    throw new AggregateException(AppResources.Ble_ReleaseFailed, failures);
            }
            finally
            {
                _operationLock.Release();
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task WithClientAsync(
        string address,
        Func<AgpClientHandle, CancellationToken, Task> operation,
        CancellationToken cancellationToken,
        bool skipInitialLightStateRead = false,
        [CallerMemberName] string operationName = "")
    {
        await WithClientAsync<object?>(
            address,
            async (handle, operationToken) =>
            {
                await operation(handle, operationToken);
                return null;
            },
            cancellationToken, skipInitialLightStateRead: skipInitialLightStateRead, operationName: operationName);
    }

    private async Task<T> WithClientAsync<T>(
        string address,
        Func<AgpClientHandle, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken,
        bool useCachedLightState = false,
        bool refreshLightState = false,
        bool skipInitialLightStateRead = false,
        [CallerMemberName] string operationName = "")
    {
        if (string.IsNullOrWhiteSpace(address))
            throw new ArgumentException(AppResources.Ble_AddressRequired, nameof(address));

        if (_runtimeStarted)
            return await ExecuteRuntimeOperationAsync(address, operation, cancellationToken,
                skipInitialLightStateRead, refreshLightState, operationName);

        EnsureApplicationForeground();
        using var applicationCancellation = LinkApplicationCancellation(cancellationToken);
        cancellationToken = applicationCancellation.Token;
        _connectionService.PromoteConnectionToLongLived(address);
        using var timing = _diagnostics.BeginBleTiming(
            $"AGP.Operation.{operationName}", address,
            $"useCachedLightState={useCachedLightState} refreshLightState={refreshLightState}");
        await MeasureAsync("AGP.Operation.Lock", address,
            () => _operationLock.WaitAsync(cancellationToken));
        try
        {
            // A command queued by the old control page must not reconnect a device after removal.
            if (!_repository.Contains(address))
                throw new InvalidOperationException(AppResources.Device_Removed);

            // A lease may have claimed teardown while this operation waited.
            _connectionService.PromoteConnectionToLongLived(address);
            var previousHandle = _handles.GetValueOrDefault(address);
            var handle = await GetOrCreateClientAsync(
                address, cancellationToken, useCachedLightState && !refreshLightState, skipInitialLightStateRead);
            try
            {
                // New clients already read state once unless explicitly seeded from cache.
                if (refreshLightState && ReferenceEquals(previousHandle, handle))
                    handle.LastState = await MeasureAsync("AGP.ReadLightState.Refresh", address,
                        () => handle.Client.ReadLightStateAsync(cancellationToken));
                var result = await operation(handle, cancellationToken);
                if (operationName.StartsWith("Set", StringComparison.Ordinal) ||
                    operationName == nameof(DeleteScheduleAsync))
                    _repository.RecordOperation(address);
                timing.SetResult("success");
                return result;
            }
            catch
            {
                await DisposeHandleAsync(address);
                throw;
            }
        }
        catch (Exception exception)
        {
            timing.SetResult(TimingResult(exception));
            throw;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task<AgpClientHandle> GetOrCreateClientAsync(
        string address,
        CancellationToken cancellationToken,
        bool useCachedLightState = false,
        bool skipInitialLightStateRead = false)
    {
        EnsureApplicationForeground();
        using var timing = _diagnostics.BeginBleTiming("AGP.Session.Prepare", address);
        if (_handles.TryGetValue(address, out var existing) &&
            _connectionService.IsConnected(address) &&
            existing.ConnectionGeneration == _connectionService.GetConnectionGeneration(address))
        {
            if (!existing.HasLightState && !skipInitialLightStateRead)
            {
                existing.LastState = await existing.Client.ReadLightStateAsync(cancellationToken);
                CacheControlState(existing);
            }
            timing.SetResult("reused");
            return existing;
        }

        timing.Mark("reused=false");
        await MeasureAsync("AGP.Session.DisposePrevious", address, () => DisposeHandleAsync(address));
        await RunSessionStepAsync(address, DeviceRuntimeStep.Connection,
            token => _connectionService.ConnectAsync(address, token), cancellationToken);
        var transport = new AgpGattTransport(_connectionService, address);
        try
        {
            await RunSessionStepAsync(address, DeviceRuntimeStep.ResponseChannel,
                token => transport.StartAsync(token, subscribeState: !_runtimeStarted), cancellationToken);
        }
        catch
        {
            await transport.DisposeAsync();
            throw;
        }
        var client = new AgpClient(transport);
        try
        {
            var cachedCapabilities = _repository.GetCapabilities(address);
            AgpCapabilities capabilities;
            if (cachedCapabilities is { } persistedCapabilities)
            {
                capabilities = persistedCapabilities;
                timing.Mark(
                    $"capabilities=0x{(ulong)capabilities:X} source=local secureControl={capabilities.HasFlag(AgpCapabilities.SecureControl)}");
            }
            else
            {
                // Devices added before capability persistence was introduced are
                // migrated on their next authenticated connection.
                capabilities = await MeasureAsync("AGP.ReadCapabilities", address,
                    () => client.ReadCapabilitiesAsync(cancellationToken));
                _repository.SetCapabilities(address, capabilities);
                timing.Mark(
                    $"capabilities=0x{(ulong)capabilities:X} source=ble secureControl={capabilities.HasFlag(AgpCapabilities.SecureControl)}");
            }
            var isAuthorized = true;
            if (capabilities.HasFlag(AgpCapabilities.SecureControl))
            {
                byte[]? token;
                using (var tokenTiming = _diagnostics.BeginBleTiming("AGP.TokenLookup", address))
                {
                    token = _repository.GetTokenBytes(address);
                    tokenTiming.SetResult(token is null ? "missing" : "present");
                }
                if (token is null)
                {
                    if (_runtimeEntries.TryGetValue(address, out var entry))
                        MarkRuntimeStep(entry, entry.Epoch, DeviceRuntimeStep.Authentication, DeviceRuntimeStepStatus.Failed,
                            Localization.Format(nameof(AppResources.Agp_DeviceNotAuthenticated), address));
                    throw new AgpAuthenticationRequiredException(address);
                }

                await RunSessionStepAsync(address, DeviceRuntimeStep.Authentication,
                    sessionToken => MeasureAsync("AGP.Authenticate", address, async () =>
                {
                    var authentication = await client.AuthenticateAsync(token, sessionToken);
                    BleTimingScope.Current?.Mark($"protocolResult={authentication.Result}");
                    EnsureSuccess(authentication.Result);
                }), cancellationToken);
            }

            var cachedState = !_runtimeStarted && (useCachedLightState || skipInitialLightStateRead)
                ? GetCachedDeviceStatus(address) : null;
            var hasLightState = cachedState is { IsOnline: true, LightState: not null } || !skipInitialLightStateRead;
            var state = cachedState is { IsOnline: true, LightState: { } knownState }
                ? knownState : skipInitialLightStateRead
                    ? new AgpLightState(0, false, 0, 0, 100, 0, AgpLightMode.Steady, 0)
                    : await MeasureAsync("AGP.ReadLightState.Initial", address,
                        () => client.ReadLightStateAsync(cancellationToken));
            timing.Mark($"lightStateCacheUsed={cachedState is { IsOnline: true, LightState: not null }} isOn={state.IsOn}");
            if (cachedState is { IsOnline: true, LightState: not null })
                _diagnostics.Debug("BLE/Status", $"控制页复用已查询灯状态：{address}。");
            if (!_runtimeStarted && capabilities.HasFlag(AgpCapabilities.RtcTime))
            {
                var time = await MeasureAsync("AGP.SetTime", address,
                    () => client.SetTimeAsync(DateTimeOffset.UtcNow, cancellationToken));
                EnsureSuccess(time.Result);
            }

            var handle = new AgpClientHandle(
                address, client, capabilities, state, isAuthorized,
                _connectionService.GetConnectionGeneration(address), hasLightState, transport);
            client.LightStateChanged += (_, value) =>
            {
                if (!IsCurrentHandle(handle)) return;
                if (handle.HasLightState && unchecked((int)(value.Revision - handle.LastState.Revision)) < 0) return;
                handle.LastState = value;
                CacheControlState(handle);
            };
            client.ColorStateChanged += (_, value) =>
            {
                if (!IsCurrentHandle(handle)) return;
                handle.LastColorState = value;
                CacheControlState(handle);
            };
            client.InvalidPacketReceived += (_, args) =>
                _diagnostics.Warning("AGP/RX", $"忽略无效 AGP 数据包：{args.Exception.Message}");
            _handles[address] = handle;
            CacheControlState(handle);
            _diagnostics.Information(
                "AGP/Connection",
                $"AGP 客户端已就绪：device={address}，capabilities=0x{(ulong)capabilities:X}。");
            timing.SetResult("ready");
            return handle;
        }
        catch (Exception exception)
        {
            timing.SetResult(TimingResult(exception));
            await client.DisposeAsync();
            throw;
        }
    }

    private async Task DisposeHandleAsync(string address)
    {
        if (_handles.TryRemove(address, out var handle))
            await handle.Client.DisposeAsync();
    }

    private async Task DisposeAllHandlesAsync()
    {
        foreach (var address in _handles.Keys.ToList())
            await DisposeHandleAsync(address);
    }

    private async Task MeasureAsync(
        string stage, string address, Func<Task> action, string? detail = null)
    {
        using var timing = _diagnostics.BeginBleTiming(stage, address, detail);
        try
        {
            await action();
            timing.SetResult("completed");
        }
        catch (Exception exception)
        {
            timing.SetResult(TimingResult(exception));
            throw;
        }
    }

    private async Task<T> MeasureAsync<T>(string stage, string address, Func<Task<T>> action)
    {
        using var timing = _diagnostics.BeginBleTiming(stage, address);
        try
        {
            var result = await action();
            timing.SetResult("returned");
            return result;
        }
        catch (Exception exception)
        {
            timing.SetResult(TimingResult(exception));
            throw;
        }
    }

    private static string TimingResult(Exception exception) =>
        $"{(exception is OperationCanceledException ? "cancelled" : "failed")}:{exception.GetType().Name}";

    private void CacheControlState(AgpClientHandle handle, ushort? effectPeriod = null)
    {
        if (effectPeriod is { } period) handle.EffectPeriod = period;
        var previous = GetCachedDeviceStatus(handle.Address)?.Profile;
        var profile = new AgpDeviceControlProfile(
            handle.Capabilities, handle.LastColorState ?? previous?.ColorState,
            handle.CtlRange ?? previous?.CtlRange ?? DefaultCtlRange,
            handle.SelectedEffectMode ?? handle.LastState.Mode,
            effectPeriod ?? handle.EffectPeriod ?? (previous?.EffectMode == (handle.SelectedEffectMode ?? handle.LastState.Mode)
                ? previous.EffectPeriodMilliseconds : null),
            handle.HasLightState ? handle.LastState : null);
        PublishDeviceStatus(handle.Address, new DeviceStatusSnapshot(
            true, handle.HasLightState ? handle.LastState : null, profile, DateTimeOffset.UtcNow));
    }

    private static void RequireCapability(
        AgpCapabilities available,
        AgpCapabilities required)
    {
        if (!available.HasFlag(required))
            throw new NotSupportedException(Localization.Format(nameof(AppResources.Agp_CapabilityUnsupported), required));
    }

    private static void EnsureSuccess(AgpControlResponse response) =>
        EnsureSuccess(response.Result);

    private static void EnsureSuccess(AgpScheduleResponse response) =>
        EnsureSuccess(response.Result);

    private static void EnsureSuccess(AgpResultCode result)
    {
        if (result != AgpResultCode.Success)
        {
            var exception = new InvalidOperationException(Localization.Format(nameof(AppResources.Agp_CommandFailed), result));
            exception.Data["AgpResult"] = result;
            throw exception;
        }
    }

    private static ushort RatioToUInt16(double ratio) =>
        (ushort)Math.Round(Math.Clamp(ratio, 0, 1) * ushort.MaxValue);

    private static readonly AgpCtlRange DefaultCtlRange = new(
        AgpColorModelCodec.MinimumCtlTemperatureKelvin,
        AgpColorModelCodec.MaximumCtlTemperatureKelvin);

    private sealed class AgpClientHandle(
        string address,
        AgpClient client,
        AgpCapabilities capabilities,
        AgpLightState lastState,
        bool isAuthorized,
        long connectionGeneration,
        bool hasLightState = true,
        AgpGattTransport? transport = null)
    {
        public string Address { get; } = address;
        public long ConnectionGeneration { get; } = connectionGeneration;
        public AgpClient Client { get; } = client;
        public AgpGattTransport? Transport { get; } = transport;
        public AgpCapabilities Capabilities { get; } = capabilities;
        private readonly object _stateSync = new();
        private AgpLightState _lastState = lastState;
        private bool _hasLightState = hasLightState;
        private bool _hasKnownLightRevision = hasLightState;
        private uint? _lightRevisionAfter;
        private uint? _lightRevisionAtLeast;
        public bool HasLightState
        {
            get { lock (_stateSync) return _hasLightState; }
            set { lock (_stateSync) _hasLightState = value; }
        }
        public AgpLightState LastState
        {
            get { lock (_stateSync) return _lastState; }
            set => TryAcceptLightState(value);
        }
        public bool TryAcceptLightState(AgpLightState value)
        {
            lock (_stateSync)
            {
                // Validity can be cleared for refresh or an uncertain command,
                // but the revision history belongs to this connection generation.
                if (_hasKnownLightRevision && unchecked((int)(value.Revision - _lastState.Revision)) < 0)
                    return false;
                if (_lightRevisionAfter is { } after && unchecked((int)(value.Revision - after)) <= 0)
                    return false;
                if (_lightRevisionAtLeast is { } atLeast && unchecked((int)(value.Revision - atLeast)) < 0)
                    return false;
                _lastState = value;
                _hasKnownLightRevision = _hasLightState = true;
                _lightRevisionAfter = _lightRevisionAtLeast = null;
                return true;
            }
        }
        public void InvalidateLightState(uint? baselineRevision, uint? acceptedRevision)
        {
            lock (_stateSync)
            {
                _hasLightState = false;
                if (baselineRevision is { } baseline &&
                    (_lightRevisionAfter is not { } previousAfter || unchecked((int)(baseline - previousAfter)) > 0))
                    _lightRevisionAfter = baseline;
                if (acceptedRevision is { } accepted &&
                    (_lightRevisionAtLeast is not { } previousAtLeast || unchecked((int)(accepted - previousAtLeast)) > 0))
                    _lightRevisionAtLeast = accepted;
            }
        }
        public bool IsAuthorized { get; } = isAuthorized;
        private AgpColorStatus? _lastColorState;
        public AgpColorStatus? LastColorState
        {
            get { lock (_stateSync) return _lastColorState; }
            set
            {
                lock (_stateSync)
                {
                    if (value?.Opcode == _lastColorState?.Opcode && ColorRevision(value) is { } next &&
                        ColorRevision(_lastColorState) is { } current && unchecked((int)(next - current)) < 0) return;
                    _lastColorState = value;
                }
            }
        }
        private static uint? ColorRevision(AgpColorStatus? value) => value switch
        {
            AgpLightness16State state => state.Revision,
            AgpHslState state => state.Revision,
            AgpHslTarget state => state.Revision,
            AgpCtlState state => state.Revision,
            AgpCtlTarget state => state.Revision,
            _ => null
        };
        public AgpCtlRange? CtlRange { get; set; }
        public AgpModeConfig? LocalMode { get; set; }
        public ushort? EffectPeriod { get; set; }
        public AgpLightMode? SelectedEffectMode { get; set; }
    }
}
