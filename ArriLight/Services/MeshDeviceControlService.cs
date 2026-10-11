using ArriOpto.BLE.Mesh;
using ArriOpto.BLE.Mesh.Models;

namespace ArriLight;

/// <summary>
/// 已配网 Mesh 设备的日常控制（开关与亮度）。
/// </summary>
/// <remarks>
/// 与 <see cref="MeshLightControlService"/> 的分界：后者只负责配网，本服务只负责
/// 已入网设备的命令。
///
/// 连接模型是**按需短连接**：Connect → MTU/通知 → Proxy Filter → 命令 → 断开。
/// PW01-MESH 固件的 BLE 并发连接数为 1，长期占用会把设备锁死，因此这里刻意不复用
/// <see cref="AgpLightControlService"/> 的长生命周期 Runtime。
///
/// 协议事实（来自 PW01-MESH 固件）：Generic OnOff Server 注册并自动绑定 AppKey；
/// Light Lightness Server 虽被 HSL Server 内部注册，但没有被绑定 AppKey，直接下发
/// 会被节点静默丢弃。因此亮度经由已绑定的 <c>LightHslModel</c> 只改写 Lightness
/// 分量，色相与饱和度沿用设备当前值。
/// </remarks>
public class MeshDeviceControlService
{
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromSeconds(20);

    private readonly BluetoothConnectionService _connectionService;
    private readonly MeshNetworkManager _networkManager;
    private readonly AppDiagnosticService _diagnostics;
    private readonly Dictionary<string, byte> _transactionIds =
        new(StringComparer.OrdinalIgnoreCase);

    public MeshDeviceControlService(
        BluetoothConnectionService connectionService,
        MeshNetworkManager networkManager,
        AppDiagnosticService diagnostics)
    {
        _connectionService = connectionService;
        _networkManager = networkManager;
        _diagnostics = diagnostics;
    }

    /// <summary>最近一次成功取样到的设备状态，供 UI 首帧展示（进程内缓存）。</summary>
    public event EventHandler<MeshDeviceState>? StateChanged;

    /// <summary>
    /// 读取设备当前开关状态。连不上或超时返回 <c>null</c>（不抛），调用方据此决定缺省动作。
    /// </summary>
    public async Task<bool?> GetOnOffStateAsync(
        string address,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await RunProxySessionAsync<bool?>(
                address,
                async (client, node, token) =>
                {
                    var response = await SendAndWaitAsync(
                        client,
                        node,
                        MeshOpcode.GenericOnOffStatus,
                        GenericOnOffModel.Get(),
                        "OnOff.Get",
                        token);
                    if (response is null ||
                        !GenericOnOffModel.TryParseStatus(response.AccessPdu, out var status))
                    {
                        return null;
                    }

                    PublishState(address, node, isOn: status.PresentOnOff);
                    return status.PresentOnOff;
                },
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 查询失败不应阻断后续操作：调用方会退回"默认发送 On"。
            _diagnostics.Warning(
                "Mesh/Control",
                $"读取 Mesh 开关状态失败，交由调用方按缺省处理：device={address}，{exception.Message}");
            return null;
        }
    }

    /// <summary>下发绝对开关状态并等待设备确认。</summary>
    public Task<bool> SetOnOffAsync(
        string address,
        bool isOn,
        CancellationToken cancellationToken = default) =>
        RunProxySessionAsync(
            address,
            async (client, node, token) =>
            {
                var response = await SendAndWaitAsync(
                    client,
                    node,
                    MeshOpcode.GenericOnOffStatus,
                    GenericOnOffModel.Set(isOn, NextTid(address)),
                    $"OnOff.Set({(isOn ? "On" : "Off")})",
                    token);
                if (response is not null &&
                    GenericOnOffModel.TryParseStatus(response.AccessPdu, out var status))
                {
                    PublishState(address, node, isOn: status.PresentOnOff);
                    return status.PresentOnOff;
                }

                PublishState(address, node, isOn: isOn);
                return isOn;
            },
            cancellationToken);

    /// <summary>
    /// 用户按下开关按钮的语义：有设备状态则取其反转；拿不到设备状态时默认发送开。
    /// </summary>
    public async Task<bool> ToggleOrTurnOnAsync(
        string address,
        CancellationToken cancellationToken = default)
    {
        var current = await GetOnOffStateAsync(address, cancellationToken);
        var desired = current is { } known ? !known : true;
        _diagnostics.Information(
            "Mesh/Control",
            $"开关决策：device={address}，current={(current is { } value ? (value ? "On" : "Off") : "未知")}，desired={(desired ? "On" : "Off")}。");
        return await SetOnOffAsync(address, desired, cancellationToken);
    }

    /// <summary>读取当前亮度百分比；不可用时返回 null。</summary>
    public async Task<byte?> GetLightnessPercentAsync(
        string address,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await RunProxySessionAsync<byte?>(
                address,
                async (client, node, token) =>
                {
                    var response = await SendAndWaitAsync(
                        client,
                        node,
                        MeshOpcode.LightHslStatus,
                        LightHslModel.Get(),
                        "HSL.Get",
                        token);
                    if (response is null ||
                        !LightHslModel.TryParseStatus(response.AccessPdu, out var status))
                    {
                        return null;
                    }

                    RememberHsl(node.UnicastAddress, status.Hue, status.Saturation);
                    return ToPercent(status.Lightness);
                },
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _diagnostics.Warning(
                "Mesh/Control",
                $"读取 Mesh 亮度失败：device={address}，{exception.Message}");
            return null;
        }
    }

    /// <summary>
    /// 设置亮度百分比。经由 Light HSL Set 只改写 Lightness 分量，
    /// 色相/饱和度沿用设备当前值，避免把已有颜色洗掉。
    /// </summary>
    public Task<bool> SetLightnessAsync(
        string address,
        byte brightnessPercent,
        CancellationToken cancellationToken = default) =>
        RunProxySessionAsync(
            address,
            async (client, node, token) =>
            {
                var (hue, saturation) = await ResolveHslAsync(client, node, token);
                var lightness = ToLightness(brightnessPercent);
                var response = await SendAndWaitAsync(
                    client,
                    node,
                    MeshOpcode.LightHslStatus,
                    LightHslModel.Set(lightness, hue, saturation, NextTid(address)),
                    $"HSL.Set({brightnessPercent}%)",
                    token);
                if (response is not null &&
                    LightHslModel.TryParseStatus(response.AccessPdu, out var status))
                {
                    RememberHsl(node.UnicastAddress, status.Hue, status.Saturation);
                    PublishState(address, node, lightnessPercent: ToPercent(status.Lightness));
                    return true;
                }

                PublishState(address, node, lightnessPercent: brightnessPercent);
                return true;
            },
            cancellationToken);

    /// <summary>
    /// 建立一次短暂的 Proxy 会话执行命令，结束后一定断开。
    /// 序列号回调必须在 Restore 之前挂上：Proxy Configuration 自身也消耗序列号。
    /// </summary>
    private async Task<T> RunProxySessionAsync<T>(
        string address,
        Func<MeshClient, MeshNode, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var network = _networkManager.FindNetworkForNode(address)
            ?? throw new InvalidOperationException("设备不在任何 Mesh 网络中。");
        var node = network.FindNode(address)
            ?? throw new InvalidOperationException("设备不在任何 Mesh 网络中。");

        using var timeout = new CancellationTokenSource(SessionTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        using var transport = new MeshGattTransport(
            _connectionService,
            _diagnostics,
            address,
            provisioningMode: false);

        MeshClient? client = null;
        try
        {
            var meshClient = new MeshClient(transport)
            {
                DiagnosticLog = message => _diagnostics.Debug("Mesh/RX", message),
                SequenceNumberAllocated = sequenceNumber =>
                    _networkManager.SyncSequenceFrom(network, sequenceNumber)
            };
            client = meshClient;
            await transport.StartReceivingAsync(linked.Token);
            meshClient.Restore(_networkManager.RestoreConfigFor(address));
            await meshClient.ConfigureProxyFilterAsync(linked.Token);
            return await operation(meshClient, node, linked.Token);
        }
        finally
        {
            await SafeDisconnectAsync(address);
        }
    }

    /// <summary>向目标节点发送访问层消息并等待同名 Status；超时返回 null。</summary>
    private async Task<MeshAccessMessage?> SendAndWaitAsync(
        MeshClient client,
        MeshNode node,
        ushort statusOpcode,
        byte[] accessPdu,
        string label,
        CancellationToken cancellationToken)
    {
        var statusTask = client.WaitForAccessMessageFromAsync(
            node.UnicastAddress,
            MeshOpcode.Encode(statusOpcode),
            StatusTimeout,
            cancellationToken);
        await client.SendAccessMessageAsync(
            node.UnicastAddress,
            accessPdu,
            cancellationToken);
        try
        {
            _diagnostics.Debug("Mesh/Control", $"已发送 {label}：node=0x{node.UnicastAddress:X4}。");
            return await statusTask;
        }
        catch (TimeoutException)
        {
            _diagnostics.Warning(
                "Mesh/Control",
                $"等待 {label} 的 Status 超时：node=0x{node.UnicastAddress:X4}。");
            return null;
        }
    }

    /// <summary>
    /// 色相/饱和度：优先复用上次读到或写回的值；没有记录时向设备查询，
    /// 避免在不了解现有颜色的情况下把它覆盖掉。
    /// </summary>
    private async Task<(ushort Hue, ushort Saturation)> ResolveHslAsync(
        MeshClient client,
        MeshNode node,
        CancellationToken cancellationToken)
    {
        if (_hslMemory.TryGetValue(node.UnicastAddress, out var known))
            return known;

        var response = await SendAndWaitAsync(
            client,
            node,
            MeshOpcode.LightHslStatus,
            LightHslModel.Get(),
            "HSL.Get(前置)",
            cancellationToken);
        if (response is not null &&
            LightHslModel.TryParseStatus(response.AccessPdu, out var status))
        {
            RememberHsl(node.UnicastAddress, status.Hue, status.Saturation);
            return (status.Hue, status.Saturation);
        }

        // 拿不到就保持 0 而不猜测；这是亮度-only 设备时最保守的选择。
        return (0, 0);
    }

    private readonly Dictionary<ushort, (ushort Hue, ushort Saturation)> _hslMemory = [];

    private void RememberHsl(ushort unicastAddress, ushort hue, ushort saturation) =>
        _hslMemory[unicastAddress] = (hue, saturation);

    private byte NextTid(string address)
    {
        _transactionIds.TryGetValue(address, out var previous);
        var next = unchecked((byte)(previous + 1));
        _transactionIds[address] = next;
        return next;
    }

    private async Task SafeDisconnectAsync(string address)
    {
        try
        {
            await _connectionService.DisconnectAsync(address, CancellationToken.None);
        }
        catch (Exception exception)
        {
            _diagnostics.Warning(
                "Mesh/Control",
                $"Mesh 控制会话收尾断开失败：device={address}，{exception.Message}");
        }
    }

    private void PublishState(
        string address,
        MeshNode node,
        bool? isOn = null,
        byte? lightnessPercent = null)
    {
        if (isOn is null && lightnessPercent is null)
            return;

        StateChanged?.Invoke(
            this,
            new MeshDeviceState(address, node.UnicastAddress, isOn, lightnessPercent));
    }

    private static ushort ToLightness(byte percent) =>
        MeshLightness.ToLightness(percent);

    private static byte ToPercent(ushort lightness) =>
        MeshLightness.ToPercent(lightness);
}

/// <summary>
/// Mesh Light HSL 的 Lightness 分量（0-65535）与 UI 百分比（0-100）之间的换算。
/// </summary>
public static class MeshLightness
{
    public static ushort ToLightness(byte percent) =>
        (ushort)Math.Clamp(Math.Round(percent / 100d * ushort.MaxValue), 0, ushort.MaxValue);

    public static byte ToPercent(ushort lightness) =>
        (byte)Math.Clamp(Math.Round(lightness / (double)ushort.MaxValue * 100), 0, 100);
}

/// <summary>一次 Mesh 命令/查询后观察到的设备状态。</summary>
public sealed record MeshDeviceState(
    string Address,
    ushort UnicastAddress,
    bool? IsOn = null,
    byte? LightnessPercent = null);
