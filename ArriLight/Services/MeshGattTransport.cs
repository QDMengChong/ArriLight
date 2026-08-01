using ArriOpto.Mesh.Client;
using ArriOpto.Mesh.Proxy;
using Plugin.BLE.Abstractions;

namespace ArriLight;

/// <summary>
/// 用 Plugin.BLE 实现 <see cref="IMeshLink"/>：配网阶段走 0x1827/0x2ADB+0x2ADC，
/// 配网完成后切换到 0x1828/0x2ADD+0x2ADE（设备会移除 Provisioning 服务，
/// 因此切换时会强制刷新 GATT 缓存重新发现）。
/// </summary>
public sealed class MeshGattTransport : IMeshLink, IDisposable
{
    private readonly BluetoothConnectionService _connectionService;
    private readonly AppDiagnosticService _diagnostics;
    private readonly string _address;
    private Guid _serviceId;
    private Guid _dataInUuid;
    private Guid _dataOutUuid;
    private byte[]? _sarBuffer;
    private byte _sarType;
    private int _connectionId;

    public MeshGattTransport(
        BluetoothConnectionService connectionService,
        AppDiagnosticService diagnostics,
        string address,
        bool provisioningMode)
    {
        _connectionService = connectionService;
        _diagnostics = diagnostics;
        _address = address;
        SetMode(provisioningMode);
        _connectionService.StateChanged += OnConnectionStateChanged;
    }

    /// <summary>
    /// 连接代次：每次与该设备建立新的 GATT 连接时递增。
    /// 用于判断代理过滤器是否需要重新配置（过滤器与连接绑定）。
    /// </summary>
    public int ConnectionId => _connectionId;

    /// <summary>从 Data Out 特征收到的完整 Proxy PDU。</summary>
    public event EventHandler<byte[]>? Received;

    /// <summary>写入一个完整 Proxy PDU（Write Without Response）。</summary>
    public Task WriteAsync(
        ReadOnlySpan<byte> pdu,
        CancellationToken cancellationToken = default) =>
        WriteAsyncCore(pdu.ToArray(), cancellationToken);

    private async Task WriteAsyncCore(
        byte[] pdu,
        CancellationToken cancellationToken)
    {
        // 设备 mesh 栈上报的通道 MTU 恒为最小值（20 字节），Proxy 通道收到
        // 超过 20 字节的单包会被丢弃；因此 Proxy 模式下把 Proxy PDU 按
        // SAR（00 完整 / 01 首段 / 10 续段 / 11 末段）切成 ≤20 字节分段。
        // 配网阶段（PB-GATT）无此限制，保持单包写入。
        var segments = _serviceId == MeshLightControlService.MeshProvisioningServiceUuid
            ? (IReadOnlyList<byte[]>)[pdu]
            : MeshProxyPdu.Segment(
                (byte)(pdu[0] & 0x3F),
                pdu.AsSpan(1),
                maxPayload: 19);

        foreach (var segment in segments)
        {
            try
            {
                await _connectionService.WriteAsync(
                    _address,
                    _serviceId,
                    _dataInUuid,
                    segment,
                    CharacteristicWriteType.WithoutResponse,
                    cancellationToken);
            }
            catch (InvalidOperationException exception) when (
                exception.Message.Contains("未提供服务", StringComparison.Ordinal) ||
                exception.Message.Contains("未提供特征", StringComparison.Ordinal))
            {
                // 缓存过期（例如设备从 Provisioning 服务切到 Proxy 服务），刷新后重试
                _diagnostics.Warning(
                    "Mesh/GATT",
                    $"端点缺失，刷新 GATT 缓存后重试：{exception.Message}");
                await _connectionService.EnumerateGattAsync(
                    _address,
                    refreshCache: true,
                    cancellationToken);
                await _connectionService.WriteAsync(
                    _address,
                    _serviceId,
                    _dataInUuid,
                    segment,
                    CharacteristicWriteType.WithoutResponse,
                    cancellationToken);
            }
        }
    }

    /// <summary>协商 ATT MTU。</summary>
    public Task RequestMtuAsync(
        int mtu,
        CancellationToken cancellationToken = default) =>
        _connectionService.RequestMtuAsync(_address, mtu, cancellationToken);

    /// <summary>订阅 Data Out 通知并监听消息。</summary>
    public async Task StartReceivingAsync(CancellationToken cancellationToken = default)
    {
        _connectionService.MessageReceived += OnMessageReceived;
        await _connectionService.SubscribeAsync(
            _address,
            _serviceId,
            _dataOutUuid,
            cancellationToken);
    }

    /// <summary>
    /// 配网完成后切换为 Proxy 模式：更新端点、强制重新发现服务并订阅 0x2ADE。
    /// 设备配网完成后约 3 秒主动断开连接、再以 Proxy 模式重启并挂出 0x1828
    /// （共约 6 秒），之后约 20 秒未完成配置会回退未配网。这里最多重试 14 次
    /// × 1 秒，覆盖切换窗口同时不超时。
    /// </summary>
    public async Task SwitchToProxyModeAsync(CancellationToken cancellationToken = default)
    {
        _connectionService.MessageReceived -= OnMessageReceived;
        SetMode(provisioningMode: false);

        for (var attempt = 1; ; attempt++)
        {
            var services = await _connectionService.EnumerateGattAsync(
                _address,
                refreshCache: true,
                cancellationToken);
            if (services.Any(service => service.Id == MeshLightControlService.MeshProxyServiceUuid))
                break;

            if (attempt >= 14)
            {
                throw new InvalidOperationException(
                    "配网完成后设备未暴露 Mesh Proxy 服务（0x1828），请确认设备固件状态。");
            }

            _diagnostics.Warning(
                "Mesh/GATT",
                $"Proxy 服务尚未出现（第 {attempt} 次），1s 后重试。");
            await Task.Delay(1000, cancellationToken);
        }

        _connectionService.MessageReceived += OnMessageReceived;
        await _connectionService.SubscribeAsync(
            _address,
            _serviceId,
            _dataOutUuid,
            cancellationToken);
        _diagnostics.Information(
            "Mesh/GATT",
            "已切换到 Mesh Proxy 模式（0x1828/0x2ADD+0x2ADE）。");
    }

    private void SetMode(bool provisioningMode)
    {
        if (provisioningMode)
        {
            _serviceId = MeshLightControlService.MeshProvisioningServiceUuid;
            _dataInUuid = MeshLightControlService.MeshProvisioningDataInUuid;
            _dataOutUuid = MeshLightControlService.MeshProvisioningDataOutUuid;
        }
        else
        {
            _serviceId = MeshLightControlService.MeshProxyServiceUuid;
            _dataInUuid = MeshLightControlService.MeshProxyDataInUuid;
            _dataOutUuid = MeshLightControlService.MeshProxyDataOutUuid;
        }
    }

    private void OnMessageReceived(object? sender, BluetoothMessage message)
    {
        if (message.CharacteristicId != _dataOutUuid)
            return;

        var data = message.Data;
        if (data.Length < 1)
            return;

        var (sar, messageType) = MeshProxyPdu.ParseHeader(data[0]);
        switch (sar)
        {
            case MeshProxyPdu.SarComplete:
                DeliverProxyPdu(data);
                break;
            case MeshProxyPdu.SarFirst:
                _sarBuffer = data[1..].ToArray();
                _sarType = messageType;
                break;
            case MeshProxyPdu.SarContinuation:
                if (_sarBuffer is not null)
                    _sarBuffer = [.. _sarBuffer, .. data[1..]];
                break;
            case MeshProxyPdu.SarLast:
                if (_sarBuffer is not null)
                {
                    _sarBuffer = [.. _sarBuffer, .. data[1..]];
                    var full = new byte[1 + _sarBuffer.Length];
                    full[0] = _sarType;
                    _sarBuffer.CopyTo(full, 1);
                    _sarBuffer = null;
                    DeliverProxyPdu(full);
                }
                break;
        }
    }

    private void DeliverProxyPdu(byte[] pdu) =>
        Received?.Invoke(this, pdu);

    public void Dispose()
    {
        _connectionService.MessageReceived -= OnMessageReceived;
        _connectionService.StateChanged -= OnConnectionStateChanged;
    }

    private void OnConnectionStateChanged(
        object? sender,
        BluetoothSessionSnapshot snapshot)
    {
        if (snapshot.IsConnected &&
            string.Equals(snapshot.Address, _address, StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(ref _connectionId);
            _diagnostics.Debug(
                "Mesh/GATT",
                $"连接代次更新为 {_connectionId}。");
        }
    }
}
