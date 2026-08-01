using System.Collections.Concurrent;
using ArriOpto.Mesh.Codec;
using ArriOpto.Mesh.Models.Generic;
using ArriOpto.Mesh.Models.Light;
using ArriOpto.Mesh.Models.Scheduler;
using ArriOpto.Mesh.Models.Scene;
using Plugin.BLE.Abstractions;

namespace ArriLight;

/// <summary>
/// 将 ArriOpto.Mesh 生成的灯控信号通过现有 BLE 连接发送给设备。
/// </summary>
/// <remarks>
/// Mesh 通过 GATT 传输时使用标准 Mesh Proxy 服务（0x1828，特征 0x2ADD/0x2ADE）
/// 或未配网阶段的 Mesh Provisioning 服务（0x1827，特征 0x2ADB/0x2ADC）。
/// 本服务发送前会枚举设备服务表并自动选择可用端点（优先级：
/// 显式配置 &gt; 0x1828 &gt; 0x1827），并在端点缺失时刷新 GATT 缓存后重试，
/// 与厂商 PhyMeshPlus Android 客户端的行为一致（其 Mesh Data In 特征使用
/// Write Without Response）。
///
/// 注意：本服务目前发送的是 ArriOpto.Mesh 生成的【访问层消息】。对于符合 SIG
/// 规范的 Mesh 节点，写入 0x2ADD 的必须是 Proxy PDU（SAR 分片 + PDU 类型），
/// 且其中的网络层载荷需要 NetKey 加密、IV Index、序列号等完整协议栈封装，
/// 裸访问层消息无法被直接执行——这需要引入完整的 Mesh 网络栈（或与厂商确认
/// 是否存在自定义裸命令通道）。在拿到真机服务表之前，请先通过诊断日志确认
/// 设备实际暴露的 GATT 布局。
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

    private readonly BluetoothConnectionService _connectionService;
    private readonly AppDiagnosticService _diagnostics;
    private readonly ConcurrentDictionary<string, GattEndpoint> _meshEndpoints = new();
    private byte _tid;

    public MeshLightControlService(
        BluetoothConnectionService connectionService,
        AppDiagnosticService diagnostics)
    {
        _connectionService = connectionService;
        _diagnostics = diagnostics;
    }

    /// <summary>
    /// Mesh GATT 服务 UUID。默认 Mesh Proxy；可改为厂商自定义服务 UUID。
    /// </summary>
    public Guid ServiceUuid { get; set; } = MeshProxyServiceUuid;

    /// <summary>
    /// Mesh GATT 数据写入特征 UUID。默认 Mesh Proxy Data In；可改为厂商自定义特征。
    /// </summary>
    public Guid CharacteristicUuid { get; set; } = MeshProxyDataInUuid;

    private byte NextTid() => unchecked(_tid++);

    /// <summary>发送 Generic OnOff Set（Unacknowledged，适合高频控制）。</summary>
    public Task<int> SendOnOffAsync(
        string address,
        bool onOff,
        TimeSpan? transitionTime = null,
        CancellationToken cancellationToken = default)
    {
        var message = GenericOnOffModel.SetUnacknowledged(
            onOff,
            NextTid(),
            transitionTime is { } transition
                ? TransitionTime.FromMilliseconds(transition.TotalMilliseconds)
                : null);
        return SendRawAsync(address, message, cancellationToken);
    }

    /// <summary>发送 Light Lightness Set（Unacknowledged）。</summary>
    public Task<int> SendLightnessAsync(
        string address,
        ushort lightness,
        TimeSpan? transitionTime = null,
        CancellationToken cancellationToken = default)
    {
        var message = LightLightnessModel.SetUnacknowledged(
            lightness,
            NextTid(),
            transitionTime is { } transition
                ? TransitionTime.FromMilliseconds(transition.TotalMilliseconds)
                : null);
        return SendRawAsync(address, message, cancellationToken);
    }

    /// <summary>发送 Light CTL Set（Unacknowledged）：亮度 + 色温 + Delta UV。</summary>
    public Task<int> SendCtlAsync(
        string address,
        ushort lightness,
        ushort temperature,
        short deltaUv = 0,
        TimeSpan? transitionTime = null,
        CancellationToken cancellationToken = default)
    {
        var message = LightCtlModel.SetUnacknowledged(
            lightness,
            temperature,
            deltaUv,
            NextTid(),
            transitionTime is { } transition
                ? TransitionTime.FromMilliseconds(transition.TotalMilliseconds)
                : null);
        return SendRawAsync(address, message, cancellationToken);
    }

    /// <summary>发送 Light HSL Set（Unacknowledged）：亮度 + 色相 + 饱和度。</summary>
    public Task<int> SendHslAsync(
        string address,
        ushort lightness,
        ushort hue,
        ushort saturation,
        TimeSpan? transitionTime = null,
        CancellationToken cancellationToken = default)
    {
        var message = LightHslModel.SetUnacknowledged(
            lightness,
            hue,
            saturation,
            NextTid(),
            transitionTime is { } transition
                ? TransitionTime.FromMilliseconds(transition.TotalMilliseconds)
                : null);
        return SendRawAsync(address, message, cancellationToken);
    }

    /// <summary>发送 Scheduler Action Set，用于定时开/关或场景回放。</summary>
    public Task<int> SendSchedulerActionAsync(
        string address,
        ScheduleRegister register,
        CancellationToken cancellationToken = default)
    {
        var message = SchedulerModel.ActionSet(register);
        return SendRawAsync(address, message, cancellationToken);
    }

    /// <summary>发送 Scene Recall（Unacknowledged）。</summary>
    public Task<int> SendSceneRecallAsync(
        string address,
        ushort sceneNumber,
        TimeSpan? transitionTime = null,
        CancellationToken cancellationToken = default)
    {
        var message = SceneModel.RecallUnacknowledged(
            sceneNumber,
            NextTid(),
            transitionTime is { } transition
                ? TransitionTime.FromMilliseconds(transition.TotalMilliseconds)
                : null);
        return SendRawAsync(address, message, cancellationToken);
    }

    /// <summary>
    /// 把“N 秒后开/关”换算成本地时间点，写入 Scheduler 条目。
    /// </summary>
    /// <remarks>
    /// Scheduler 表达的是本地时间点而非相对延时，因此这里把计时换算成具体时间。
    /// Year 只取年份后两位，条目会在每年同月同日再次触发；如需真正的一次性
    /// 语义，需要设备端自定义模型或由上层在触发后清除条目。
    /// </remarks>
    public Task<int> SendTimerAsync(
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

    /// <summary>
    /// 把编码好的访问层消息写入设备的 Mesh 特征。
    /// </summary>
    /// <remarks>
    /// 首次发送会做一次强制刷新缓存的 GATT 服务枚举并缓存端点；若写入因
    /// “服务/特征不存在”失败（例如节点从 Provisioning 服务切到 Proxy 服务
    /// 导致缓存过期），会重新枚举并重试一次。
    /// </remarks>
    public async Task<int> SendRawAsync(
        string address,
        byte[] message,
        CancellationToken cancellationToken = default)
    {
        if (!_meshEndpoints.TryGetValue(address, out var endpoint))
        {
            endpoint = await ResolveMeshEndpointAsync(
                address,
                refreshCache: true,
                cancellationToken);
            _meshEndpoints[address] = endpoint;
        }

        _diagnostics.Trace(
            "Mesh/TX",
            $"service={endpoint.ServiceId}, characteristic={endpoint.CharacteristicId}, " +
            $"data={Convert.ToHexString(message)}");
        try
        {
            return await _connectionService.WriteAsync(
                address,
                endpoint.ServiceId,
                endpoint.CharacteristicId,
                message,
                endpoint.WriteType,
                cancellationToken);
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains("未提供服务", StringComparison.Ordinal) ||
            exception.Message.Contains("未提供特征", StringComparison.Ordinal))
        {
            _diagnostics.Warning(
                "Mesh/TX",
                $"端点可能已变化，刷新 GATT 缓存后重试：{exception.Message}");
            endpoint = await ResolveMeshEndpointAsync(
                address,
                refreshCache: true,
                cancellationToken);
            _meshEndpoints[address] = endpoint;
            return await _connectionService.WriteAsync(
                address,
                endpoint.ServiceId,
                endpoint.CharacteristicId,
                message,
                endpoint.WriteType,
                cancellationToken);
        }
    }

    /// <summary>清除缓存的设备端点，下次发送会重新发现。</summary>
    public void InvalidateMeshEndpoint(string address) =>
        _meshEndpoints.TryRemove(address, out _);

    private async Task<GattEndpoint> ResolveMeshEndpointAsync(
        string address,
        bool refreshCache,
        CancellationToken cancellationToken)
    {
        var services = await _connectionService.EnumerateGattAsync(
            address,
            refreshCache,
            cancellationToken);
        var characteristics = services
            .SelectMany(s => s.Characteristics.Select(c => (ServiceId: s.Id, Info: c)))
            .ToList();

        var explicitMatch = characteristics
            .FirstOrDefault(t => t.ServiceId == ServiceUuid && t.Info.Id == CharacteristicUuid);
        var proxyMatch = characteristics
            .FirstOrDefault(t => t.ServiceId == MeshProxyServiceUuid && t.Info.Id == MeshProxyDataInUuid);
        var provisioningMatch = characteristics
            .FirstOrDefault(t => t.ServiceId == MeshProvisioningServiceUuid && t.Info.Id == MeshProvisioningDataInUuid);

        // 优先级：显式配置 > Mesh Proxy（0x1828/0x2ADD） > Mesh Provisioning（0x1827/0x2ADB）。
        var (serviceId, info) = explicitMatch.Info is not null ? explicitMatch
            : proxyMatch.Info is not null ? proxyMatch
            : provisioningMatch.Info is not null ? provisioningMatch
            : (Guid.Empty, null as GattCharacteristicInfo);

        if (info is null)
        {
            var table = string.Join(
                " | ",
                services.Select(s =>
                    $"{s.Id} [{string.Join(", ", s.Characteristics.Select(c => $"{c.Id}:{c.Properties}"))}]"));
            throw new InvalidOperationException(
                $"设备未发现 Mesh GATT 端点（0x1828/0x2ADD 或 0x1827/0x2ADB）。服务表：{table}");
        }

        var writeType = info.Properties.HasFlag(CharacteristicPropertyType.WriteWithoutResponse)
            ? CharacteristicWriteType.WithoutResponse
            : CharacteristicWriteType.WithResponse;
        _diagnostics.Information(
            "Mesh/GATT",
            $"Mesh 端点：service={serviceId}, characteristic={info.Id}, writeType={writeType}");
        return new GattEndpoint(serviceId, info.Id, writeType);
    }

    /// <summary>解析出的 Mesh GATT 写入端点。</summary>
    private sealed record GattEndpoint(
        Guid ServiceId,
        Guid CharacteristicId,
        CharacteristicWriteType WriteType);
}
