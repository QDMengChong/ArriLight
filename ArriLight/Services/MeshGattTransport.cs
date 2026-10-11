using ArriOpto.BLE.Mesh;
using Plugin.BLE.Abstractions;

namespace ArriLight;

/// <summary>
/// 用当前按地址的 GATT 会话承载 SIG Mesh PB-GATT/Proxy。
/// </summary>
/// <remarks>
/// 端点 UUID 来自 <see cref="MeshProfile"/>（协议库），本类只负责把它们接到
/// 应用拥有的 <see cref="BluetoothConnectionService"/> 会话上。
/// </remarks>
public sealed class MeshGattTransport : IMeshLink, IDisposable
{
    public const int PreferredMtu = 517;

    private readonly BluetoothConnectionService _connectionService;
    private readonly AppDiagnosticService _diagnostics;
    private readonly string _address;
    private readonly MeshSarReassembler _reassembler = new();
    private Guid _serviceId;
    private Guid _dataInUuid;
    private Guid _dataOutUuid;
    private long _connectionGeneration;
    private long _receivingGeneration = -1;
    private int _maximumWritePayload = 20;
    private bool _messageHandlerAttached;
    private bool _disposed;

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

    public string Address => _address;

    public long ConnectionGeneration => _connectionGeneration;

    public bool IsConnected =>
        !_disposed && _connectionService.IsConnected(_address);

    public event EventHandler<byte[]>? Received;

    public Task WriteAsync(
        ReadOnlySpan<byte> pdu,
        CancellationToken cancellationToken = default) =>
        WriteCoreAsync(pdu.ToArray(), cancellationToken);

    private async Task WriteCoreAsync(
        byte[] pdu,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureConnected();
        if (pdu.Length > _maximumWritePayload)
        {
            throw new InvalidOperationException(
                $"Mesh Proxy PDU 长度 {pdu.Length} 超过当前 ATT 写入上限 " +
                $"{_maximumWritePayload}，请先完成 MTU 协商。");
        }

        await _connectionService.WriteAsync(
            _address,
            _serviceId,
            _dataInUuid,
            pdu,
            CharacteristicWriteType.WithoutResponse,
            cancellationToken,
            _connectionGeneration);
    }

    public async Task RequestMtuAsync(
        int mtu,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureConnected();
        var negotiated = await _connectionService.RequestMtuAsync(
            _address,
            mtu,
            cancellationToken);
        _maximumWritePayload = Math.Max(20, negotiated - 3);
        _diagnostics.Information(
            "Mesh/GATT",
            $"Mesh MTU 已就绪：device={_address}，mtu={negotiated}，writePayload={_maximumWritePayload}。");
    }

    public async Task StartReceivingAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _connectionService.ConnectAsync(_address, cancellationToken);
        _connectionGeneration = _connectionService.GetConnectionGeneration(_address);
        EnsureConnected();

        if (!_messageHandlerAttached)
        {
            _connectionService.MessageReceived += OnMessageReceived;
            _messageHandlerAttached = true;
        }

        await RequestMtuAsync(PreferredMtu, cancellationToken);
        _receivingGeneration = _connectionGeneration;
        await _connectionService.SubscribeAsync(
            _address,
            _serviceId,
            _dataOutUuid,
            cancellationToken,
            _connectionGeneration);
        _diagnostics.Information(
            "Mesh/GATT",
            $"Mesh Data Out 通知已就绪：device={_address}，generation={_connectionGeneration}。");
    }

    public async Task SwitchToProxyModeAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DetachMessageHandler();
        _reassembler.Reset();
        _receivingGeneration = -1;
        SetMode(provisioningMode: false);

        for (var attempt = 1; ; attempt++)
        {
            var services = await _connectionService.EnumerateGattAsync(
                _address,
                refreshCache: true,
                cancellationToken);
            if (services.Any(service =>
                    service.Id == MeshProfile.ProxyServiceId))
                break;

            if (attempt >= 14)
            {
                throw new InvalidOperationException(
                    "配网完成后设备未暴露 Mesh Proxy 服务（0x1828），请确认设备固件状态。");
            }

            _diagnostics.Warning(
                "Mesh/GATT",
                $"Proxy 服务尚未出现：device={_address}，attempt={attempt}。");
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        await StartReceivingAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        DetachMessageHandler();
        _connectionService.StateChanged -= OnConnectionStateChanged;
        _reassembler.Reset();
        _receivingGeneration = -1;
    }

    private void SetMode(bool provisioningMode)
    {
        if (provisioningMode)
        {
            _serviceId = MeshProfile.ProvisioningServiceId;
            _dataInUuid = MeshProfile.ProvisioningDataInId;
            _dataOutUuid = MeshProfile.ProvisioningDataOutId;
        }
        else
        {
            _serviceId = MeshProfile.ProxyServiceId;
            _dataInUuid = MeshProfile.ProxyDataInId;
            _dataOutUuid = MeshProfile.ProxyDataOutId;
        }
    }

    private void OnMessageReceived(object? sender, BluetoothMessage message)
    {
        if (_disposed ||
            !string.Equals(message.Address, _address, StringComparison.OrdinalIgnoreCase) ||
            message.ConnectionGeneration != _receivingGeneration ||
            message.ServiceId != _serviceId ||
            message.CharacteristicId != _dataOutUuid ||
            message.Data.Length == 0)
        {
            return;
        }

        if (_reassembler.Append(message.Data) is { } pdu)
            Received?.Invoke(this, pdu);
    }

    private void OnConnectionStateChanged(
        object? sender,
        BluetoothSessionSnapshot snapshot)
    {
        if (!string.Equals(snapshot.Address, _address, StringComparison.OrdinalIgnoreCase))
            return;

        if (snapshot.IsConnected)
        {
            _connectionGeneration = _connectionService.GetConnectionGeneration(_address);
            return;
        }

        _reassembler.Reset();
        _receivingGeneration = -1;
        _maximumWritePayload = 20;
    }

    private void DetachMessageHandler()
    {
        if (!_messageHandlerAttached)
            return;

        _connectionService.MessageReceived -= OnMessageReceived;
        _messageHandlerAttached = false;
    }

    private void EnsureConnected()
    {
        if (!IsConnected ||
            _connectionService.GetConnectionGeneration(_address) != _connectionGeneration)
        {
            throw new IOException("Mesh GATT 会话已失效，需要重新建立连接。");
        }
    }
}
