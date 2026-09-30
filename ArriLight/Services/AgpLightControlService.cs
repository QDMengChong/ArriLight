using ArriOpto.BLE.Agp;

namespace ArriLight;

public enum AgpControlPageKind
{
    Lightness,
    Hsl,
    Ctl,
    HslAndCtl
}

public sealed record AgpDeviceControlProfile(
    AgpCapabilities Capabilities,
    AgpColorStatus? ColorState,
    AgpCtlRange CtlRange,
    AgpLightMode EffectMode = AgpLightMode.Steady,
    ushort? EffectPeriodMilliseconds = null)
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

/// <summary>通过 AGP 私有 GATT 协议执行直连灯控；当前版本不使用 Mesh。</summary>
public sealed class AgpLightControlService : IAsyncDisposable
{
    private const byte TimerScheduleSlot = AgpModelCodec.MaximumScheduleSlots - 1;

    private readonly BluetoothConnectionService _connectionService;
    private readonly AppDiagnosticService _diagnostics;
    private readonly DeviceRepository _repository;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private AgpClientHandle? _handle;

    public AgpLightControlService(
        BluetoothConnectionService connectionService,
        AppDiagnosticService diagnostics,
        DeviceRepository repository)
    {
        _connectionService = connectionService;
        _diagnostics = diagnostics;
        _repository = repository;
    }

    /// <summary>用户明确发起首次绑定时调用，允许进入物理确认流程。</summary>
    public async Task BindAsync(
        string address,
        CancellationToken cancellationToken = default)
        => await BindAsync(
            address,
            bindingPrompt: null,
            cancellationToken: cancellationToken);

    /// <summary>
    /// 用户明确发起首次绑定时调用，允许由调用方提供带倒计时的物理确认提示。
    /// 提示回调返回后才会发送 AuthGetToken 请求，设备可在等待期间完成物理触摸。
    /// </summary>
    public async Task BindAsync(
        string address,
        Func<CancellationToken, Task>? bindingPrompt,
        CancellationToken cancellationToken = default)
    {
        // 先显示宿主提供的确认提示，再等待 AGP 操作锁；这样页面不会被
        // 既有 BLE 数据交换阻塞，用户点击“保存”后可以立即看到提示。
        var bindingPromptAlreadyShown = bindingPrompt is not null;
        if (bindingPrompt is not null)
            await bindingPrompt(cancellationToken);

        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            await DisposeHandleAsync();
            await GetOrCreateClientAsync(
                address,
                cancellationToken,
                allowProvisioning: true,
                bindingPromptAlreadyShown: bindingPromptAlreadyShown);
        }
        catch
        {
            // 绑定超时或用户取消后，不保留半开的 GATT 会话，避免下一次添加
            // 误复用上一次未完成认证的连接。
            await DisposeHandleAsync();
            try
            {
                await _connectionService.DisconnectAsync();
            }
            catch
            {
                // 保留原始绑定异常，清理失败不覆盖它。
            }

            throw;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>清除本机绑定后重新执行一次物理确认流程。</summary>
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
                return response;
            },
            cancellationToken);

    public Task<AgpDeviceControlProfile> GetControlProfileAsync(
        string address,
        CancellationToken cancellationToken = default) =>
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

                return new AgpDeviceControlProfile(
                    handle.Capabilities,
                    handle.LastColorState,
                    handle.CtlRange ?? DefaultCtlRange,
                    effectMode,
                    effectPeriodMilliseconds);
            },
            cancellationToken);

    public Task<bool?> GetOnOffStateAsync(
        string address,
        CancellationToken cancellationToken = default) =>
        WithClientAsync<bool?>(
            address,
            async handle =>
            {
                handle.LastState = await handle.Client.ReadLightStateAsync(cancellationToken);
                return handle.LastState.IsOn;
            },
            cancellationToken);

    /// <summary>
    /// 快速读取 Light State，只建立 GATT 连接并读取公开状态特征，不读取能力位，
    /// 也不执行 AGP Token 认证。主页状态轮询使用此方法，避免轮询阻塞在授权流程。
    /// </summary>
    public async Task<bool> GetOnOffStateWithoutAuthorizationAsync(
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
            return state.IsOn;
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

    public Task SetTimerAsync(
        string address,
        TimeSpan delay,
        bool turnOn,
        CancellationToken cancellationToken = default)
    {
        if (delay <= TimeSpan.Zero || delay.TotalSeconds > AgpModelCodec.MaximumDelayOffSeconds)
            throw new ArgumentOutOfRangeException(nameof(delay));

        return WithClientAsync(
            address,
            async handle =>
            {
                if (!turnOn)
                {
                    RequireCapability(handle.Capabilities, AgpCapabilities.RelativeTimer);
                    var response = await handle.Client.SetDelayOffAsync(
                        checked((uint)Math.Ceiling(delay.TotalSeconds)),
                        cancellationToken);
                    EnsureSuccess(response.Result);
                    return;
                }

                RequireCapability(handle.Capabilities, AgpCapabilities.Scheduler);
                RequireCapability(handle.Capabilities, AgpCapabilities.RtcTime);
                var executeAt = DateTimeOffset.UtcNow.Add(delay).ToUnixTimeSeconds();
                var brightness = handle.LastState.LastNonzeroBrightness is >= 1 and <= 100
                    ? handle.LastState.LastNonzeroBrightness
                    : (byte)100;
                var schedule = AgpSchedule.Once(
                    TimerScheduleSlot,
                    isEnabled: true,
                    AgpScheduleAction.On,
                    brightness,
                    checked((uint)executeAt));
                EnsureSuccess(await handle.Client.SetScheduleAsync(schedule, cancellationToken));
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
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(address))
            throw new ArgumentException("设备地址不能为空。", nameof(address));

        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            var handle = await GetOrCreateClientAsync(address, cancellationToken);
            try
            {
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
        bool allowProvisioning = false,
        bool bindingPromptAlreadyShown = false)
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
                {
                    if (!allowProvisioning)
                        throw new AgpAuthenticationRequiredException(address);

                    if (!bindingPromptAlreadyShown)
                        await ShowBindingPromptAsync(address);
                    _diagnostics.Information(
                        "AGP/Binding",
                        $"等待设备 {address} 的物理触摸确认，绑定窗口 {TimeSpan.FromSeconds(60).TotalSeconds:0} 秒。发送 AuthGetToken 请求。");
                    var provision = await client.GetProvisionTokenAsync(
                        cancellationToken: cancellationToken);
                    EnsureSuccess(provision.Result);
                    if (provision.Token is not { Length: 4 } provisionedToken)
                    {
                        throw new InvalidOperationException(
                            "设备返回的 AGP Token 格式无效。");
                    }

                    if (!_repository.SetToken(address, provisionedToken))
                    {
                        throw new InvalidOperationException(
                            "设备已返回 Token，但本地设备记录不存在，无法保存绑定信息。");
                    }
                    token = provisionedToken;
                }

                var authentication = await client.AuthenticateAsync(
                    token,
                    cancellationToken);
                EnsureSuccess(authentication.Result);
            }

            var state = await client.ReadLightStateAsync(cancellationToken);
            if (capabilities.HasFlag(AgpCapabilities.RtcTime))
            {
                var time = await client.SetTimeAsync(DateTimeOffset.UtcNow, cancellationToken);
                EnsureSuccess(time.Result);
            }

            var handle = new AgpClientHandle(
                address, client, capabilities, state, isAuthorized);
            client.LightStateChanged += (_, value) => handle.LastState = value;
            client.ColorStateChanged += (_, value) => handle.LastColorState = value;
            client.InvalidPacketReceived += (_, args) =>
                _diagnostics.Warning("AGP/RX", $"忽略无效 AGP 数据包：{args.Exception.Message}");
            _handle = handle;
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

    private static Task ShowBindingPromptAsync(string address) =>
        MainThread.InvokeOnMainThreadAsync(() =>
            Shell.Current.DisplayAlertAsync(
                "确认绑定设备",
                $"请确认这是要绑定的灯具（{address}），然后轻触灯身按键。\n\n设备指示灯闪烁后，App 将完成绑定。",
                "等待触摸"));

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
