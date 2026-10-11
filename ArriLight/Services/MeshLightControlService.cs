using ArriOpto.BLE.Mesh;
using ArriOpto.BLE.Mesh.Provisioning;

namespace ArriLight;

/// <summary>
/// Mesh 配网阶段的业务服务。当前阶段不承载长期 Mesh 控制 Runtime，
/// 只负责探测、PB-GATT 配网、AppKey 配置和网络身份持久化。
/// </summary>
public sealed class MeshLightControlService
{
    private static readonly TimeSpan ProvisioningTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan AppKeyTimeout = TimeSpan.FromSeconds(6);

    private readonly BluetoothConnectionService _connectionService;
    private readonly MeshNetworkManager _networkManager;
    private readonly AppDiagnosticService _diagnostics;

    public MeshLightControlService(
        BluetoothConnectionService connectionService,
        MeshNetworkManager networkManager,
        AppDiagnosticService diagnostics)
    {
        _connectionService = connectionService;
        _networkManager = networkManager;
        _diagnostics = diagnostics;
    }

    /// <summary>
    /// 连接并枚举设备的 GATT 服务，判定它所处的 Mesh 配网阶段。
    /// 返回的 <see cref="MeshDeviceProbeState.Services"/> 会交给
    /// <see cref="EnsureProvisionedAsync"/> 复用，避免配网时重复枚举。
    /// </summary>
    public async Task<MeshDeviceProbeState> ProbeMeshStateAsync(
        string address,
        CancellationToken cancellationToken = default)
    {
        var services = await _connectionService.EnumerateGattAsync(
            address,
            refreshCache: true,
            cancellationToken);
        return ClassifyMeshServices(services);
    }

    /// <summary>
    /// 用已经探测到的服务表完成配网（或恢复已配网设备）。
    /// </summary>
    public async Task EnsureProvisionedAsync(
        string address,
        MeshDeviceProbeState probeState,
        CancellationToken cancellationToken = default)
    {
        switch (probeState.Kind)
        {
            case MeshDeviceProbeKind.Provisioned:
                await RestoreProvisionedDeviceAsync(address, cancellationToken);
                return;

            case MeshDeviceProbeKind.NotMesh:
                throw new InvalidOperationException(
                    $"设备未暴露 Mesh Provisioning 服务（0x1827）。" +
                    $"实际服务：{DescribeServices(probeState.Services)}");

            case MeshDeviceProbeKind.Unprovisioned:
                await ProvisionUnprovisionedDeviceAsync(address, cancellationToken);
                return;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(probeState),
                    "未知的 Mesh 探测结果。");
        }
    }

    public Task DisconnectAsync(
        string address,
        CancellationToken cancellationToken = default) =>
        _connectionService.DisconnectAsync(address, cancellationToken);

    private MeshDeviceProbeState ClassifyMeshServices(
        IReadOnlyList<GattServiceInfo> services)
    {
        var hasProxy = services.Any(service =>
            service.Id == MeshProfile.ProxyServiceId);
        if (hasProxy)
            return MeshDeviceProbeState.Provisioned(services);

        var hasProvisioning = services.Any(service =>
            service.Id == MeshProfile.ProvisioningServiceId);
        return hasProvisioning
            ? MeshDeviceProbeState.Unprovisioned(services)
            : MeshDeviceProbeState.NotMesh(services);
    }

    private async Task ProvisionUnprovisionedDeviceAsync(
        string address,
        CancellationToken cancellationToken)
    {
        var network = _networkManager.DefaultNetwork;
        var node = _networkManager.EnsureNode(address, network);
        var options = ProvisioningOptions.FromNetwork(
            network,
            node.UnicastAddress);
        _diagnostics.Information(
            "Mesh/Provision",
            $"开始配网：device={address}，network={network.Name}，" +
            $"unicast=0x{node.UnicastAddress:X4}。");

        using var timeout = new CancellationTokenSource(ProvisioningTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        using var transport = new MeshGattTransport(
            _connectionService,
            _diagnostics,
            address,
            provisioningMode: true);

        var client = new MeshClient(transport)
        {
            DiagnosticLog = message => _diagnostics.Debug("Mesh/RX", message),
            // 必须在发送任何 PDU 之前挂上：序列号在构造 PDU 时即被消耗，
            // 写入失败/断连/取消都不能让下次连接重用已经发出的 SEQ。
            SequenceNumberAllocated = sequenceNumber =>
                _networkManager.SyncSequenceFrom(network, sequenceNumber)
        };
        await transport.StartReceivingAsync(linked.Token);

        var config = await client.ProvisionAsync(options, linked.Token);
        _networkManager.UpdateNodeAfterProvision(
            address,
            network,
            config.DeviceKey);

        _diagnostics.Information(
            "Mesh/Provision",
            $"配网完成：device={address}，unicast=0x{config.DeviceUnicastAddress:X4}。");

        // 配网配置刚从 ProvisioningResult 创建时序列号从 0 开始；所有
        // 节点共享网络级序列号，必须从持久化值继续发送。
        config.SequenceNumber = network.SequenceNumber;
        await transport.SwitchToProxyModeAsync(cancellationToken);
        await client.ConfigureProxyFilterAsync(cancellationToken);
        await EnsureAppKeyConfiguredAsync(client, address);
    }

    private async Task RestoreProvisionedDeviceAsync(
        string address,
        CancellationToken cancellationToken)
    {
        var network = _networkManager.FindNetworkForNode(address);
        if (network is null)
        {
            throw new InvalidOperationException(
                "设备已属于 Mesh 网络，但本机没有对应网络密钥；请先导入网络分享或恢复设备出厂设置。");
        }

        using var transport = new MeshGattTransport(
            _connectionService,
            _diagnostics,
            address,
            provisioningMode: false);
        var client = new MeshClient(transport)
        {
            DiagnosticLog = message => _diagnostics.Debug("Mesh/RX", message),
            SequenceNumberAllocated = sequenceNumber =>
                _networkManager.SyncSequenceFrom(network, sequenceNumber)
        };
        await transport.StartReceivingAsync(cancellationToken);
        client.Restore(_networkManager.RestoreConfigFor(address));
        await client.ConfigureProxyFilterAsync(cancellationToken);
        await EnsureAppKeyConfiguredAsync(client, address);

        _diagnostics.Information(
            "Mesh/Provision",
            $"已恢复 Mesh 设备：device={address}，network={network.Name}。");
    }

    private async Task EnsureAppKeyConfiguredAsync(
        MeshClient client,
        string address)
    {
        var appKeyStatus = await client.AddAppKeyAsync(AppKeyTimeout);
        if (appKeyStatus != 0x00)
        {
            throw new InvalidOperationException(
                $"Mesh AppKey 配置失败，状态码 0x{appKeyStatus:X2}。");
        }

        _diagnostics.Information(
            "Mesh/Config",
            $"Mesh AppKey 配置成功：device={address}，status=0x{appKeyStatus:X2}。");
    }

    private static string DescribeServices(
        IReadOnlyList<GattServiceInfo> services) =>
        string.Join(", ", services.Select(service => service.Id.ToString("D")));
}
