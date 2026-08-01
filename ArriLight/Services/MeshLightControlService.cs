using System.Collections.Concurrent;
using System.Text.Json;
using ArriOpto.Mesh.Client;
using ArriOpto.Mesh.Codec;
using ArriOpto.Mesh.Models.Generic;
using ArriOpto.Mesh.Models.Light;
using ArriOpto.Mesh.Models.Scheduler;
using ArriOpto.Mesh.Models.Scene;
using ArriOpto.Mesh.Provisioning;

namespace ArriLight;

/// <summary>
/// 把 ArriOpto.Mesh 生成的灯控信号发送给已配网的 Mesh 灯具。
/// </summary>
/// <remarks>
/// 发送链路：访问层消息（ArriOpto.Mesh 模型）→ 上层/下层传输加密 →
/// 网络层加密 + 隐私混淆 → Proxy PDU → <see cref="MeshGattTransport"/> 写入
/// Mesh Proxy Data In（0x2ADD）。
///
/// 首次控制某台设备时自动执行 PB-GATT 配网（0x1827/0x2ADB+0x2ADC），配网
/// 完成后把网络配置（NetKey/AppKey/地址/IV）持久化到本地，之后直接复用。
/// </remarks>
public sealed class MeshLightControlService
{
    // 标准 SIG Mesh GATT 服务与特征（Assigned Numbers）。
    public static readonly Guid MeshProxyServiceUuid =
        Guid.Parse("00001828-0000-1000-8000-00805F9B34FB");
    public static readonly Guid MeshProvisioningServiceUuid =
        Guid.Parse("00001827-0000-1000-8000-00805F9B34FB");
    public static readonly Guid MeshProxyDataInUuid =
        Guid.Parse("00002ADD-0000-1000-8000-00805F9B34FB");
    public static readonly Guid MeshProxyDataOutUuid =
        Guid.Parse("00002ADE-0000-1000-8000-00805F9B34FB");
    public static readonly Guid MeshProvisioningDataInUuid =
        Guid.Parse("00002ADB-0000-1000-8000-00805F9B34FB");
    public static readonly Guid MeshProvisioningDataOutUuid =
        Guid.Parse("00002ADC-0000-1000-8000-00805F9B34FB");

    private const string ConfigPreferencesPrefix = "mesh_network_config_v1_";
    private static readonly TimeSpan ProvisioningTimeout = TimeSpan.FromSeconds(90);

    private readonly BluetoothConnectionService _connectionService;
    private readonly AppDiagnosticService _diagnostics;
    private readonly ConcurrentDictionary<string, MeshClientHandle> _clients = new();
    private byte _tid;

    public MeshLightControlService(
        BluetoothConnectionService connectionService,
        AppDiagnosticService diagnostics)
    {
        _connectionService = connectionService;
        _diagnostics = diagnostics;
    }

    private byte NextTid() => unchecked(_tid++);

    /// <summary>发送 Generic OnOff Set（Unacknowledged）。</summary>
    public Task SendOnOffAsync(
        string address,
        bool onOff,
        TimeSpan? transitionTime = null,
        CancellationToken cancellationToken = default) =>
        SendAccessAsync(
            address,
            GenericOnOffModel.SetUnacknowledged(
                onOff,
                NextTid(),
                transitionTime is { } transition
                    ? TransitionTime.FromMilliseconds(transition.TotalMilliseconds)
                    : null),
            cancellationToken);

    /// <summary>发送 Light Lightness Set（Unacknowledged）。</summary>
    public Task SendLightnessAsync(
        string address,
        ushort lightness,
        TimeSpan? transitionTime = null,
        CancellationToken cancellationToken = default) =>
        SendAccessAsync(
            address,
            LightLightnessModel.SetUnacknowledged(
                lightness,
                NextTid(),
                transitionTime is { } transition
                    ? TransitionTime.FromMilliseconds(transition.TotalMilliseconds)
                    : null),
            cancellationToken);

    /// <summary>发送 Light CTL Set（Unacknowledged）：亮度 + 色温 + Delta UV。</summary>
    public Task SendCtlAsync(
        string address,
        ushort lightness,
        ushort temperature,
        short deltaUv = 0,
        TimeSpan? transitionTime = null,
        CancellationToken cancellationToken = default) =>
        SendAccessAsync(
            address,
            LightCtlModel.SetUnacknowledged(
                lightness,
                temperature,
                deltaUv,
                NextTid(),
                transitionTime is { } transition
                    ? TransitionTime.FromMilliseconds(transition.TotalMilliseconds)
                    : null),
            cancellationToken);

    /// <summary>发送 Light HSL Set（Unacknowledged）：亮度 + 色相 + 饱和度。</summary>
    public Task SendHslAsync(
        string address,
        ushort lightness,
        ushort hue,
        ushort saturation,
        TimeSpan? transitionTime = null,
        CancellationToken cancellationToken = default) =>
        SendAccessAsync(
            address,
            LightHslModel.SetUnacknowledged(
                lightness,
                hue,
                saturation,
                NextTid(),
                transitionTime is { } transition
                    ? TransitionTime.FromMilliseconds(transition.TotalMilliseconds)
                    : null),
            cancellationToken);

    /// <summary>发送 Scheduler Action Set，用于定时开/关或场景回放。</summary>
    public Task SendSchedulerActionAsync(
        string address,
        ScheduleRegister register,
        CancellationToken cancellationToken = default) =>
        SendAccessAsync(
            address,
            SchedulerModel.ActionSet(register),
            cancellationToken);

    /// <summary>发送 Scene Recall（Unacknowledged）。</summary>
    public Task SendSceneRecallAsync(
        string address,
        ushort sceneNumber,
        TimeSpan? transitionTime = null,
        CancellationToken cancellationToken = default) =>
        SendAccessAsync(
            address,
            SceneModel.RecallUnacknowledged(
                sceneNumber,
                NextTid(),
                transitionTime is { } transition
                    ? TransitionTime.FromMilliseconds(transition.TotalMilliseconds)
                    : null),
            cancellationToken);

    /// <summary>
    /// 把“N 秒后开/关”换算成本地时间点，写入 Scheduler 条目。
    /// </summary>
    public Task SendTimerAsync(
        string address,
        TimeSpan delay,
        bool turnOn,
        CancellationToken cancellationToken = default)
    {
        var fireTime = DateTime.Now.Add(delay);
        var register = new ScheduleRegister(
            index: 0,
            year: (byte)(fireTime.Year % 100),
            month: (ushort)(1 << (fireTime.Month - 1)),
            day: (byte)fireTime.Day,
            hour: (byte)fireTime.Hour,
            minute: (byte)fireTime.Minute,
            second: (byte)fireTime.Second,
            dayOfWeek: 0x7F,
            action: (byte)(turnOn ? SchedulerAction.TurnOn : SchedulerAction.TurnOff),
            transitionTime: TransitionTime.FromMilliseconds(500),
            sceneNumber: 0x0000);
        return SendSchedulerActionAsync(address, register, cancellationToken);
    }

    /// <summary>清除某台设备的 Mesh 网络配置与缓存（用于重新配网）。</summary>
    public void ResetProvisioning(string address)
    {
        if (_clients.TryRemove(address, out var handle))
            handle.Transport.Dispose();
        Preferences.Default.Remove(ConfigPreferencesPrefix + address);
    }

    private async Task SendAccessAsync(
        string address,
        byte[] accessPdu,
        CancellationToken cancellationToken)
    {
        try
        {
            var handle = await GetOrCreateClientAsync(address, cancellationToken);
            _diagnostics.Trace(
                "Mesh/TX",
                $"access opcode={accessPdu[0]:X2}, data={Convert.ToHexString(accessPdu)}");
            await handle.MeshClient.SendAccessMessageAsync(accessPdu, cancellationToken);
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains("未提供服务", StringComparison.Ordinal) ||
            exception.Message.Contains("未提供特征", StringComparison.Ordinal))
        {
            // 会话中设备被重置或服务发生切换：清掉过期配置并重新配网一次
            _diagnostics.Warning(
                "Mesh/Provision",
                $"设备端点失效（{exception.Message}），清空配置后重新配网。");
            ResetProvisioning(address);
            var handle = await GetOrCreateClientAsync(address, cancellationToken);
            await handle.MeshClient.SendAccessMessageAsync(accessPdu, cancellationToken);
        }
    }

    private async Task<MeshClientHandle> GetOrCreateClientAsync(
        string address,
        CancellationToken cancellationToken)
    {
        if (_clients.TryGetValue(address, out var cached))
            return cached;

        // 先看设备当前暴露哪个 Mesh 服务：0x1827=未配网，0x1828=已配网
        var services = await _connectionService.EnumerateGattAsync(
            address,
            refreshCache: true,
            cancellationToken);
        var hasProvisioningService = services.Any(service =>
            service.Id == MeshProvisioningServiceUuid);
        var hasProxyService = services.Any(service =>
            service.Id == MeshProxyServiceUuid);

        // 已配网且设备确实有 Proxy 服务：恢复持久化的网络配置，直接控制
        var stored = Preferences.Default.Get(ConfigPreferencesPrefix + address, string.Empty);
        if (hasProxyService && !string.IsNullOrWhiteSpace(stored))
        {
            var config = JsonSerializer.Deserialize<MeshNetworkConfig>(stored);
            if (config is not null)
            {
                var transport = new MeshGattTransport(
                    _connectionService,
                    _diagnostics,
                    address,
                    provisioningMode: false);
                await transport.StartReceivingAsync(cancellationToken);
                var client = new MeshClient(transport);
                client.Restore(config);
                var handle = new MeshClientHandle(transport, client);
                _clients[address] = handle;
                _diagnostics.Information(
                    "Mesh/Provision",
                    $"设备 {address} 已有网络配置，直接进入控制模式。");
                return handle;
            }
        }

        // 设备没有任何 Mesh 服务：给出服务表便于定位
        if (!hasProvisioningService)
        {
            var table = string.Join(
                ", ",
                services.Select(service => service.Id.ToString("D")));
            throw new InvalidOperationException(
                $"设备未暴露 Mesh Provisioning 服务（0x1827），无法配网。实际服务：{table}");
        }

        // 未配网：执行 PB-GATT 配网
        if (!string.IsNullOrWhiteSpace(stored))
        {
            _diagnostics.Warning(
                "Mesh/Provision",
                $"设备 {address} 的本地网络配置已过期，将重新配网。");
            Preferences.Default.Remove(ConfigPreferencesPrefix + address);
        }

        var provisioningTransport = new MeshGattTransport(
            _connectionService,
            _diagnostics,
            address,
            provisioningMode: true);
        await provisioningTransport.StartReceivingAsync(cancellationToken);
        await provisioningTransport.RequestMtuAsync(247, cancellationToken);

        var meshClient = new MeshClient(provisioningTransport);
        var options = ProvisioningOptions.CreateRandom(
            provisionerAddress: 0x0001,
            deviceUnicastAddress: 0x0002);
        _diagnostics.Information(
            "Mesh/Provision",
            $"开始配网：{address}，设备单播地址 0x{options.DeviceUnicastAddress:X4}。");

        using var timeout = new CancellationTokenSource(ProvisioningTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        var config2 = await meshClient.ProvisionAsync(options, linked.Token);

        Preferences.Default.Set(
            ConfigPreferencesPrefix + address,
            JsonSerializer.Serialize(config2));
        _diagnostics.Information(
            "Mesh/Provision",
            $"配网完成：{address}，设备单播地址 0x{config2.DeviceUnicastAddress:X4}。");

        await provisioningTransport.SwitchToProxyModeAsync(cancellationToken);
        var statusCode = await meshClient.ConfigureNodeAsync(cancellationToken);
        _diagnostics.Information(
            "Mesh/Config",
            $"Config AppKey 状态码 0x{statusCode:X2}（0x00=成功）。");
        var provisioned = new MeshClientHandle(provisioningTransport, meshClient);
        _clients[address] = provisioned;
        return provisioned;
    }

    private sealed record MeshClientHandle(
        MeshGattTransport Transport,
        MeshClient MeshClient);
}
