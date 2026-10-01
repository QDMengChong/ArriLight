using System.Diagnostics;
using System.Collections.Concurrent;
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
        : base($"设备 {address} 尚未完成 ArriLight 绑定认证。")
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

/// <summary>通过 AGP 私有 GATT 协议执行直连灯控；当前版本不使用 Mesh。</summary>
public sealed class AgpLightControlService : IAsyncDisposable
{
    public static readonly TimeSpan BindingTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan BindingRetryInterval = TimeSpan.FromMilliseconds(500);

    private readonly BluetoothConnectionService _connectionService;
    private readonly AppDiagnosticService _diagnostics;
    private readonly DeviceRepository _repository;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private AgpClientHandle? _handle;
    private readonly ConcurrentDictionary<string, DeviceStatusSnapshot> _deviceStatuses =
        new(StringComparer.OrdinalIgnoreCase);

    public DeviceStatusSnapshot? GetCachedDeviceStatus(string address) =>
        _deviceStatuses.GetValueOrDefault(address);

    public DeviceStatusSnapshot RecordDeviceOffline(string address) =>
        _deviceStatuses.AddOrUpdate(
            address,
            _ => new DeviceStatusSnapshot(false, null, null, DateTimeOffset.UtcNow),
            (_, previous) => previous with { IsOnline = false, QueriedAt = DateTimeOffset.UtcNow });

    public AgpLightControlService(
        BluetoothConnectionService connectionService,
        AppDiagnosticService diagnostics,
        DeviceRepository repository)
    {
        _connectionService = connectionService;
        _diagnostics = diagnostics;
        _repository = repository;
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
    /// 连接暂时不可用时每隔 500 ms 重试；收到成功 Token 响应并保存后完成绑定。
    /// ATT Indication 接收确认由手机 BLE 栈完成，PW01 随后主动断开供其他 APP 配对。
    /// </summary>
    public async Task BindAsync(
        string address,
        Func<AgpBindingStage, CancellationToken, Task>? bindingProgress,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(address))
            throw new ArgumentException("设备地址不能为空。", nameof(address));

        using var bindingCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bindingCancellation.CancelAfter(BindingTimeout);
        var token = bindingCancellation.Token;

        // 先显示进度，再等待操作锁，用户保存名称后立即看到连接提示。
        if (bindingProgress is not null)
            await bindingProgress(AgpBindingStage.Connecting, token);

        await _operationLock.WaitAsync(token);
        try
        {
            await DisposeHandleAsync();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var attemptStarted = Stopwatch.GetTimestamp();
                using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                // 被其他手机占用时，原生连接可能长时间没有结果。取消本轮
                // 原生连接后再重试，避免它占满整个配对等待时间，也不并发建连。
                attemptCancellation.CancelAfter(BindingRetryInterval);
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
                    _diagnostics.Warning(
                        "AGP/Binding",
                        $"设备 {address} 暂时无法连接，按 500 ms 间隔重试：{exception.Message}");
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
            if (!capabilities.HasFlag(AgpCapabilities.SecureControl))
                throw new NotSupportedException("设备不支持 Token 配对。");

            var provision = await client.GetProvisionTokenAsync(cancellationToken: token);
            if (provision.Result == AgpResultCode.NotAuthorized)
            {
                throw new InvalidOperationException(
                    "设备配对窗口已关闭，请双键长按 1 秒，待蓝色指示灯闪烁后重新添加。");
            }
            EnsureSuccess(provision.Result);
            if (provision.Token is not { Length: 4 } provisionedToken)
                throw new InvalidOperationException("设备返回的 AGP Token 格式无效。");
            if (!_repository.SetToken(address, provisionedToken))
            {
                throw new InvalidOperationException(
                    "设备已返回 Token，但本地设备记录不存在，无法保存绑定信息。");
            }

            // Token 已保存即配对成功；原连接会被设备断开，不能在这里继续
            // AUTH_TOKEN、读取灯状态或校时，也不能因断开而删除已保存的 Token。
            _diagnostics.Information("AGP/Binding", $"设备 {address} 的 Token 已保存，配对完成。");
        }
        finally
        {
            try
            {
                // 新固件会主动断开；同时兼容仍保持连接的设备。清理异常不改变配对结果。
                using var cleanupCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _connectionService.DisconnectAsync(cleanupCancellation.Token);
            }
            catch (Exception exception)
            {
                _diagnostics.Warning("AGP/Binding", $"配对连接清理失败：{exception.Message}");
            }
            finally
            {
                _operationLock.Release();
            }
        }
    }

    /// <summary>清除本机绑定后，在设备配对窗口内重新获取 Token。</summary>
    public async Task RebindAsync(
        string address,
        CancellationToken cancellationToken = default)
    {
        _repository.ClearToken(address);
        await BindAsync(address, cancellationToken);
    }

    /// <summary>确保当前 AGP 会话已完成 Token 认证，供 OTA 前置检查使用。</summary>
    public Task EnsureAuthorizedAsync(
        string address,
        CancellationToken cancellationToken = default) =>
        WithClientAsync(address, _ => Task.CompletedTask, cancellationToken);

    public Task SetOnOffAsync(
        string address,
        bool isOn,
        CancellationToken cancellationToken = default) =>
        WithClientAsync(
            address,
            async handle =>
            {
                RequireCapability(handle.Capabilities, AgpCapabilities.OnOff);
                EnsureSuccess(await handle.Client.SetOnOffAsync(
                    isOn,
                    cancellationToken: cancellationToken));
                handle.LastState = handle.LastState with { IsOn = isOn };
                CacheControlState(handle);
            },
            cancellationToken);

    public Task SetLightnessAsync(
        string address,
        byte brightnessPercent,
        CancellationToken cancellationToken = default) =>
        WithClientAsync(
            address,
            async handle =>
            {
                if (handle.Capabilities.HasFlag(AgpCapabilities.Lightness16))
                {
                    EnsureSuccess(await handle.Client.SetLightness16Async(
                        AgpColorModelCodec.BrightnessPercentToLightness16(
                            brightnessPercent),
                        cancellationToken: cancellationToken));
                }
                else
                {
                    RequireCapability(handle.Capabilities, AgpCapabilities.Lightness);
                    EnsureSuccess(await handle.Client.SetLightnessAsync(
                        brightnessPercent,
                        cancellationToken: cancellationToken));
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
            async handle =>
            {
                RequireCapability(handle.Capabilities, AgpCapabilities.Hsl);
                var value = new AgpHsl(
                    AgpColorModelCodec.BrightnessPercentToLightness16(
                        brightnessPercent),
                    RatioToUInt16(hue),
                    RatioToUInt16(saturation));
                EnsureSuccess(await handle.Client.SetHslAsync(
                    value,
                    cancellationToken: cancellationToken));
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
            async handle =>
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
                    cancellationToken: cancellationToken));
            },
            cancellationToken);

    public Task<AgpEffectResponse> SetEffectAsync(
        string address,
        AgpLightMode mode,
        ushort periodMilliseconds,
        CancellationToken cancellationToken = default) =>
        WithClientAsync(
            address,
            async handle =>
            {
                RequireCapability(handle.Capabilities, AgpCapabilities.Effects);
                var response = await handle.Client.SetEffectAsync(
                    mode,
                    periodMilliseconds,
                    cancellationToken);
                EnsureSuccess(response.Result);
                handle.LastState = handle.LastState with { Mode = response.Mode };
                CacheControlState(handle, response.PeriodMilliseconds);
                return response;
            },
            cancellationToken);

    public Task<AgpDeviceControlProfile> GetControlProfileAsync(
        string address,
        CancellationToken cancellationToken = default,
        bool useCachedLightState = false,
        bool refreshLightState = false) =>
        WithClientAsync(
            address,
            async handle =>
            {
                if (handle.Client.HasColorStateCharacteristic)
                {
                    try
                    {
                        handle.LastColorState = await handle.Client.ReadColorStateAsync(
                            cancellationToken);
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
                        var response = await handle.Client.GetCtlRangeAsync(cancellationToken);
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
                        var effect = await handle.Client.GetEffectAsync(cancellationToken);
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

                cancellationToken.ThrowIfCancellationRequested();
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
            async handle =>
            {
                handle.LastState = await handle.Client.ReadLightStateAsync(cancellationToken);
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
            throw new ArgumentException("设备地址不能为空。", nameof(address));

        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            // 主页轮询不应复用控制页留下的已授权 AGP 客户端。
            await DisposeHandleAsync();
            var packet = await _connectionService.ReadAsync(
                address,
                AgpProfile.ServiceId,
                AgpProfile.LightStateCharacteristicId,
                cancellationToken);
            var state = AgpModelCodec.DecodeLightState(
                AgpCodec.Decode(packet.AsSpan()));
            cancellationToken.ThrowIfCancellationRequested();
            var previousProfile = GetCachedDeviceStatus(address)?.Profile;
            var profile = previousProfile is null ? null : previousProfile with
            {
                EffectMode = state.Mode,
                EffectPeriodMilliseconds = previousProfile.EffectMode == state.Mode
                    ? previousProfile.EffectPeriodMilliseconds : null,
                LightState = state
            };
            var snapshot = new DeviceStatusSnapshot(true, state, profile, DateTimeOffset.UtcNow);
            _deviceStatuses[address] = snapshot;
            return snapshot;
        }
        finally
        {
            await DisposeHandleAsync();
            try
            {
                // 每台设备的快速查询结束后立即释放连接，避免下一台设备查询
                // 复用前一台的会话，也让用户操作可以获得干净的连接。
                await _connectionService.DisconnectAsync(CancellationToken.None);
            }
            catch
            {
                // 保留查询本身的异常，断开清理失败不覆盖它。
            }

            _operationLock.Release();
        }
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
            async handle =>
            {
                RequireCapability(handle.Capabilities, AgpCapabilities.RelativeTimer);
                var seconds = checked((uint)Math.Ceiling(delay.TotalSeconds));
                if (turnOn)
                {
                    var response = await handle.Client.SetDelayOnAsync(seconds, cancellationToken);
                    EnsureSuccess(response.Result);
                }
                else
                {
                    var response = await handle.Client.SetDelayOffAsync(seconds, cancellationToken);
                    EnsureSuccess(response.Result);
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
            async handle =>
            {
                RequireCapability(handle.Capabilities, AgpCapabilities.Scheduler);
                EnsureSuccess(await handle.Client.SetScheduleAsync(schedule, cancellationToken));
            },
            cancellationToken);

    public Task DeleteScheduleAsync(
        string address,
        byte slot,
        CancellationToken cancellationToken = default) =>
        WithClientAsync(
            address,
            async handle =>
            {
                RequireCapability(handle.Capabilities, AgpCapabilities.Scheduler);
                var response = await handle.Client.DeleteScheduleAsync(slot, cancellationToken);
                EnsureSuccess(response.Result);
            },
            cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _operationLock.WaitAsync();
        try
        {
            await DisposeHandleAsync();
        }
        finally
        {
            _operationLock.Release();
            _operationLock.Dispose();
        }
    }

    private async Task WithClientAsync(
        string address,
        Func<AgpClientHandle, Task> operation,
        CancellationToken cancellationToken)
    {
        await WithClientAsync<object?>(
            address,
            async handle =>
            {
                await operation(handle);
                return null;
            },
            cancellationToken);
    }

    private async Task<T> WithClientAsync<T>(
        string address,
        Func<AgpClientHandle, Task<T>> operation,
        CancellationToken cancellationToken,
        bool useCachedLightState = false,
        bool refreshLightState = false)
    {
        if (string.IsNullOrWhiteSpace(address))
            throw new ArgumentException("设备地址不能为空。", nameof(address));

        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            var previousHandle = _handle;
            var handle = await GetOrCreateClientAsync(
                address, cancellationToken, useCachedLightState && !refreshLightState);
            try
            {
                // New clients already read state once unless explicitly seeded from cache.
                if (refreshLightState && ReferenceEquals(previousHandle, handle))
                    handle.LastState = await handle.Client.ReadLightStateAsync(cancellationToken);
                return await operation(handle);
            }
            catch
            {
                await DisposeHandleAsync();
                throw;
            }
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task<AgpClientHandle> GetOrCreateClientAsync(
        string address,
        CancellationToken cancellationToken,
        bool useCachedLightState = false)
    {
        if (_handle is not null &&
            string.Equals(_handle.Address, address, StringComparison.OrdinalIgnoreCase) &&
            _connectionService.Snapshot.IsConnected &&
            string.Equals(
                _connectionService.Snapshot.Address,
                address,
                StringComparison.OrdinalIgnoreCase))
        {
            return _handle;
        }

        await DisposeHandleAsync();
        await _connectionService.ConnectAsync(address, cancellationToken);
        var transport = new AgpGattTransport(_connectionService, address);
        await transport.StartAsync(cancellationToken);
        var client = new AgpClient(transport);
        try
        {
            var capabilities = await client.ReadCapabilitiesAsync(cancellationToken);
            var isAuthorized = true;
            if (capabilities.HasFlag(AgpCapabilities.SecureControl))
            {
                var token = _repository.GetTokenBytes(address);
                if (token is null)
                    throw new AgpAuthenticationRequiredException(address);

                var authentication = await client.AuthenticateAsync(
                    token,
                    cancellationToken);
                EnsureSuccess(authentication.Result);
            }

            var cachedState = useCachedLightState
                ? GetCachedDeviceStatus(address) : null;
            var state = cachedState is { IsOnline: true, LightState: { } knownState }
                ? knownState : await client.ReadLightStateAsync(cancellationToken);
            if (cachedState is { IsOnline: true, LightState: not null })
                _diagnostics.Debug("BLE/Status", $"控制页复用已查询灯状态：{address}。");
            if (capabilities.HasFlag(AgpCapabilities.RtcTime))
            {
                var time = await client.SetTimeAsync(DateTimeOffset.UtcNow, cancellationToken);
                EnsureSuccess(time.Result);
            }

            var handle = new AgpClientHandle(
                address, client, capabilities, state, isAuthorized);
            client.LightStateChanged += (_, value) =>
            {
                handle.LastState = value;
                CacheControlState(handle);
            };
            client.ColorStateChanged += (_, value) =>
            {
                handle.LastColorState = value;
                CacheControlState(handle);
            };
            client.InvalidPacketReceived += (_, args) =>
                _diagnostics.Warning("AGP/RX", $"忽略无效 AGP 数据包：{args.Exception.Message}");
            _handle = handle;
            CacheControlState(handle);
            _diagnostics.Information(
                "AGP/Connection",
                $"AGP 客户端已就绪：device={address}，capabilities=0x{(ulong)capabilities:X}。");
            return handle;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    private async Task DisposeHandleAsync()
    {
        var handle = _handle;
        _handle = null;
        if (handle is not null)
            await handle.Client.DisposeAsync();
    }

    private void CacheControlState(AgpClientHandle handle, ushort? effectPeriod = null)
    {
        var previous = GetCachedDeviceStatus(handle.Address)?.Profile;
        var profile = new AgpDeviceControlProfile(
            handle.Capabilities, handle.LastColorState ?? previous?.ColorState,
            handle.CtlRange ?? previous?.CtlRange ?? DefaultCtlRange,
            handle.LastState.Mode,
            effectPeriod ?? (previous?.EffectMode == handle.LastState.Mode
                ? previous.EffectPeriodMilliseconds : null),
            handle.LastState);
        _deviceStatuses[handle.Address] = new DeviceStatusSnapshot(
            true, handle.LastState, profile, DateTimeOffset.UtcNow);
    }

    private static void RequireCapability(
        AgpCapabilities available,
        AgpCapabilities required)
    {
        if (!available.HasFlag(required))
            throw new NotSupportedException($"设备不支持 AGP 能力 {required}。");
    }

    private static void EnsureSuccess(AgpControlResponse response) =>
        EnsureSuccess(response.Result);

    private static void EnsureSuccess(AgpScheduleResponse response) =>
        EnsureSuccess(response.Result);

    private static void EnsureSuccess(AgpResultCode result)
    {
        if (result != AgpResultCode.Success)
            throw new InvalidOperationException($"AGP 命令失败：{result}。");
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
        bool isAuthorized)
    {
        public string Address { get; } = address;
        public AgpClient Client { get; } = client;
        public AgpCapabilities Capabilities { get; } = capabilities;
        public AgpLightState LastState { get; set; } = lastState;
        public bool IsAuthorized { get; } = isAuthorized;
        public AgpColorStatus? LastColorState { get; set; }
        public AgpCtlRange? CtlRange { get; set; }
    }
}
