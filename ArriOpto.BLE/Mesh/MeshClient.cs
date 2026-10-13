using System.Security.Cryptography;
using ArriOpto.BLE.Mesh.Models;
using ArriOpto.BLE.Mesh.Crypto;
using ArriOpto.BLE.Mesh.Provisioning;

namespace ArriOpto.BLE.Mesh;

/// <summary>
/// 高层 Mesh 客户端：负责配网、密钥管理、消息发送与状态接收。
/// </summary>
/// <remarks>
/// 发送链路：访问层消息（ArriOpto.BLE.Mesh 模型）→ 上层/下层传输（必要时分段）→
/// 网络层加密 + 隐私混淆 → Proxy PDU（MessageType=0x00）→ <see cref="IMeshLink"/>。
/// 入站消息沿相反方向解密后按 opcode 分发。
/// 配网后需先调用 <see cref="AddAppKeyAsync(TimeSpan, CancellationToken)"/> 下发 AppKey（EASY_BOUNDING
/// 设备收到 Config AppKey Add 后会自动绑定模型并停止配置超时）。
/// </remarks>
public sealed class MeshClient
{
    private readonly IMeshLink _link;
    private readonly object _receiveLock = new();
    private readonly List<PendingReceive> _pendingReceives = [];
    private MeshNetworkConfig? _config;
    private Action<uint>? _sequenceNumberAllocated;

    public MeshClient(IMeshLink link)
    {
        _link = link;
        _link.Received += OnLinkReceived;
    }

    /// <summary>当前网络配置；配网成功后可用。</summary>
    public MeshNetworkConfig? Config => _config;

    /// <summary>收到并成功解密的访问层消息（opcode + 参数）。</summary>
    public event EventHandler<byte[]>? AccessMessageReceived;

    /// <summary>收到并成功解密的访问层消息，包含网络层源/目标地址。</summary>
    public event EventHandler<MeshAccessMessage>? DetailedAccessMessageReceived;

    /// <summary>
    /// 接收管线诊断日志（供宿主注入日志器，定位入站消息处理失败环节）。
    /// </summary>
    public Action<string>? DiagnosticLog { get; set; }

    /// <summary>
    /// 每次分配新序列号后的回调（参数为分配后的值），用于宿主持久化。
    /// </summary>
    /// <remarks>
    /// 序列号在构造网络 PDU 时即被消耗，写入失败、取消或断连都不会退回。
    /// 宿主必须在此回调里持久化，才能在下次连接继续按 (SRC, SEQ) 单调递增，
    /// 否则设备会因回放保护静默丢弃消息。设置后立即对当前配置生效，
    /// 并在 <see cref="ProvisionAsync"/> 与 <see cref="Restore"/> 之后自动
    /// 传导给新配置；配网握手本身（Proxy Configuration）也消耗序列号，
    /// 因此应在发送任何消息之前设置。
    /// </remarks>
    public Action<uint>? SequenceNumberAllocated
    {
        get => _sequenceNumberAllocated;
        set
        {
            _sequenceNumberAllocated = value;
            if (_config is { } config)
                config.SequenceNumberAllocated = value;
        }
    }

    /// <summary>
    /// 执行配网并保存网络配置。配网完成后设备会从 Provisioning 服务切到
    /// Proxy 服务，宿主应重新发现 GATT 服务并把 <see cref="IMeshLink"/> 的
    /// 端点切换到 0x1828/0x2ADD+0x2ADE，然后调用 <see cref="AddAppKeyAsync(TimeSpan, CancellationToken)"/>。
    /// </summary>
    public async Task<MeshNetworkConfig> ProvisionAsync(
        ProvisioningOptions options,
        CancellationToken cancellationToken = default)
    {
        var provisioning = new MeshProvisioningClient(_link);
        var result = await provisioning.ProvisionAsync(options, cancellationToken);
        _config = AttachSequenceHook(MeshNetworkConfig.FromProvisioning(options, result));
        return _config;
    }

    /// <summary>
    /// 恢复已有网络配置（重复配网前调用）。会同时恢复序列号，避免重启后
    /// 序列号回退被设备回放保护丢弃。
    /// </summary>
    public void Restore(MeshNetworkConfig config)
    {
        _config = AttachSequenceHook(config);
    }

    private MeshNetworkConfig AttachSequenceHook(MeshNetworkConfig config)
    {
        config.SequenceNumberAllocated = _sequenceNumberAllocated;
        return config;
    }

    /// <summary>
    /// 配网后下发 AppKey，并等待 Config AppKey Status。
    /// PHY EASY_BOUNDING 固件收到 AppKey Add 后会自动把 AppKey 绑定到模型并停止配置超时。
    /// </summary>
    /// <param name="timeout">
    /// 等待 Status 的超时；<see cref="TimeSpan.Zero"/> 表示使用默认的 6 秒。
    /// </param>
    /// <param name="cancellationToken">取消等待和发送操作的令牌。</param>
    /// <returns>
    /// Config AppKey Status 的状态码（0x00 = 成功）；未收到格式有效且
    /// 索引/目标地址匹配的 Status 时返回 0xFF。
    /// </returns>
    public async Task<byte> AddAppKeyAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var config = RequireConfig();
        var appKeyAdd = ConfigModel.AppKeyAdd(
            config.NetKeyIndex,
            config.AppKeyIndex,
            config.AppKey);
        var statusOpcode = MeshOpcode.Encode(MeshOpcode.ConfigAppKeyStatus);
        var effectiveTimeout = timeout > TimeSpan.Zero
            ? timeout
            : TimeSpan.FromSeconds(6);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            // 先注册等待者再发送，避免响应先于等待者注册到达而被丢弃。
            // 按源单播地址过滤，避免收下网络里其他节点的同名 Status。
            var statusTask = WaitForAccessMessageFromAsync(
                config.DeviceUnicastAddress,
                statusOpcode,
                effectiveTimeout,
                cancellationToken);
            await SendDeviceKeyMessageAsync(appKeyAdd, cancellationToken);
            try
            {
                var status = await statusTask;
                if (TryReadExpectedAppKeyStatus(status, config, out var statusCode))
                    return statusCode;
            }
            catch (TimeoutException)
            {
                // 重试一次（部分固件首包会被丢弃）
            }
        }

        return 0xFF;
    }

    /// <summary>
    /// 兼容旧签名：使用默认的 6 秒等待超时。
    /// </summary>
    public Task<byte> AddAppKeyAsync(
        CancellationToken cancellationToken = default) =>
        AddAppKeyAsync(TimeSpan.Zero, cancellationToken);

    /// <summary>
    /// 校验并读出 Config AppKey Status 的状态码：消息必须来自目标节点、
    /// 发往本地 Provisioner，且索引与当前网络一致。
    /// </summary>
    private static bool TryReadExpectedAppKeyStatus(
        MeshAccessMessage message,
        MeshNetworkConfig config,
        out byte statusCode)
    {
        statusCode = 0xFF;
        if (message.Destination != config.ProvisionerAddress ||
            !ConfigModel.TryParseAppKeyStatus(message.AccessPdu, out var parsed) ||
            parsed.NetKeyIndex != config.NetKeyIndex ||
            parsed.AppKeyIndex != config.AppKeyIndex)
        {
            return false;
        }

        statusCode = parsed.StatusCode;
        return true;
    }

    /// <summary>
    /// 标准节点配置：下发 AppKey 后显式绑定 Generic OnOff Server。
    /// PHY EASY_BOUNDING 应用请直接调用 <see cref="AddAppKeyAsync(TimeSpan, CancellationToken)"/>，以匹配
    /// 官方 App 的配置流程并避免重复绑定。
    /// </summary>
    public async Task<byte> ConfigureNodeAsync(
        CancellationToken cancellationToken = default)
    {
        var statusCode = await AddAppKeyAsync(cancellationToken);
        var config = RequireConfig();
        var appBind = ConfigModel.ModelAppBind(
            config.DeviceUnicastAddress,
            config.AppKeyIndex,
            new ModelIdentifier(SigModelIds.GenericOnOffServer));
        await SendDeviceKeyMessageAsync(appBind, cancellationToken);

        return statusCode;
    }

    /// <summary>
    /// 发送一条应用密钥消息（AKF=1）到已配网节点；访问层消息超过非分段上限时
    /// 自动分段（每段 12 字节，各段使用递增序列号）。
    /// </summary>
    /// <param name="accessPdu">访问层消息（opcode + 参数）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task SendAccessMessageAsync(
        ReadOnlySpan<byte> accessPdu,
        CancellationToken cancellationToken = default) =>
        SendAccessMessageAsync(
            RequireConfig().DeviceUnicastAddress,
            accessPdu,
            cancellationToken);

    /// <summary>
    /// 发送一条应用密钥消息到任意目标地址（单播或分组地址）。用于经一个
    /// Proxy 连接控制其他节点、或向分组地址（0xC000-0xFEFF）组播控制。
    /// </summary>
    /// <param name="destination">目标地址（单播或分组地址）。</param>
    /// <param name="accessPdu">访问层消息（opcode + 参数）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task SendAccessMessageAsync(
        ushort destination,
        ReadOnlySpan<byte> accessPdu,
        CancellationToken cancellationToken = default) =>
        SendMessageAsync(
            destination,
            accessPdu,
            useDeviceKey: false,
            cancellationToken);

    /// <summary>
    /// 发送一条设备密钥消息（AKF=0，AID=0），用于 Config 等设备密钥消息。
    /// 需要设备密钥：分享导入的"仅控制"节点没有 DeviceKey，不能发送配置消息。
    /// </summary>
    public Task SendDeviceKeyMessageAsync(
        ReadOnlySpan<byte> accessPdu,
        CancellationToken cancellationToken = default)
    {
        var config = RequireConfig();
        if (!config.HasDeviceKey)
        {
            throw new InvalidOperationException(
                "缺少设备密钥（DeviceKey），无法发送配置消息（仅控制模式）。");
        }

        return SendMessageAsync(
            config.DeviceUnicastAddress,
            accessPdu,
            useDeviceKey: true,
            cancellationToken);
    }

    /// <summary>
    /// 把节点订阅到指定分组（Config Model Subscription Add）。
    /// 返回 Config Model Subscription Status 状态码（0x00 = 成功）。
    /// </summary>
    /// <param name="groupAddress">分组地址（0xC000-0xFEFF）。</param>
    /// <param name="modelId">要订阅的模型（如 Generic OnOff Server）。</param>
    /// <param name="timeout">等待 Status 的超时。</param>
    /// <param name="cancellationToken">取消等待和发送操作的令牌。</param>
    public async Task<byte> SubscribeToGroupAsync(
        ushort groupAddress,
        ModelIdentifier modelId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var config = RequireConfig();
        if (!ArriOpto.BLE.Mesh.Crypto.MeshAddress.IsValidSubscriptionAddress(groupAddress))
            throw new ArgumentOutOfRangeException(
                nameof(groupAddress),
                "订阅地址必须是分组地址（0xC000-0xFEFF）或虚拟地址。");

        // 先注册等待者再发送，避免响应先于等待者注册到达而被丢弃
        var statusTask = WaitForAccessMessageAsync(
            MeshOpcode.Encode(MeshOpcode.ConfigModelSubscriptionStatus),
            timeout,
            cancellationToken);
        await SendDeviceKeyMessageAsync(
            ConfigModel.ModelSubscriptionAdd(
                config.DeviceUnicastAddress,
                groupAddress,
                modelId),
            cancellationToken);
        var status = await statusTask;
        return ConfigModel.TryParseModelSubscriptionStatus(status, out var parsed)
            ? parsed.StatusCode
            : (byte)0xFF;
    }

    /// <summary>
    /// 把节点从指定分组退订（Config Model Subscription Delete）。
    /// 返回 Config Model Subscription Status 状态码（0x00 = 成功）。
    /// </summary>
    public async Task<byte> UnsubscribeFromGroupAsync(
        ushort groupAddress,
        ModelIdentifier modelId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var config = RequireConfig();
        var statusTask = WaitForAccessMessageAsync(
            MeshOpcode.Encode(MeshOpcode.ConfigModelSubscriptionStatus),
            timeout,
            cancellationToken);
        await SendDeviceKeyMessageAsync(
            ConfigModel.ModelSubscriptionDelete(
                config.DeviceUnicastAddress,
                groupAddress,
                modelId),
            cancellationToken);
        var status = await statusTask;
        return ConfigModel.TryParseModelSubscriptionStatus(status, out var parsed)
            ? parsed.StatusCode
            : (byte)0xFF;
    }

    /// <summary>
    /// 发送 Config Node Reset，让节点离开当前网络并清除网络配置。
    /// 成功后设备会回到未配网状态（0x1827）。
    /// </summary>
    public async Task<bool> ResetNodeAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // 先注册等待者再发送，避免响应先于等待者注册到达而被丢弃
            var statusTask = WaitForAccessMessageAsync(
                MeshOpcode.Encode(MeshOpcode.ConfigNodeResetStatus),
                timeout,
                cancellationToken);
            await SendDeviceKeyMessageAsync(
                ConfigModel.NodeReset(),
                cancellationToken);
            await statusTask;
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>读取节点组成数据（Page 0），失败返回 null。</summary>
    public async Task<CompositionDataStatus?> ReadCompositionDataAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // 先注册等待者再发送，避免响应先于等待者注册到达而被丢弃
            var statusTask = WaitForAccessMessageAsync(
                MeshOpcode.Encode(MeshOpcode.ConfigCompositionDataStatus),
                timeout,
                cancellationToken);
            await SendDeviceKeyMessageAsync(
                ConfigModel.CompositionDataGet(),
                cancellationToken);
            var status = await statusTask;
            return ConfigModel.TryParseCompositionDataStatus(status, out var parsed)
                ? parsed
                : null;
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// 配置代理过滤器：把 Provisioner 地址加入白名单，使设备把发给我们的
    /// 状态消息（如 Generic OnOff Status）经 Proxy Data Out 转发回来。
    /// 默认过滤器为空白名单，不做此配置将收不到任何设备响应。
    /// 过滤器与 GATT 连接绑定，每次（重）连接后都应重新配置。
    /// </summary>
    public async Task ConfigureProxyFilterAsync(
        CancellationToken cancellationToken = default)
    {
        var config = RequireConfig();

        // Set Filter Type（opcode 0x00）：FilterType=0x00（accept/white list）
        var sequenceNumber = config.NextSequenceNumber();
        var lowerSetFilter = new byte[] { 0x00, 0x00, 0x00 };
        var setFilterPdu = MeshNetworkLayer.BuildProxyConfigurationPdu(
            config.EncryptionKey,
            config.PrivacyKey,
            config.Nid,
            config.IvIndex,
            sequenceNumber,
            lowerSetFilter);
        await _link.WriteAsync(
            MeshProxyPdu.Create(
                MeshProxyPdu.MessageTypeProxyConfiguration,
                setFilterPdu),
            cancellationToken);

        // Add Addresses to Filter（opcode 0x01）：加入 Provisioner 单播地址
        var address = config.ProvisionerAddress;
        sequenceNumber = config.NextSequenceNumber();
        var lowerAddAddress = new byte[]
        {
            0x00,
            0x01,
            (byte)(address >> 8),
            (byte)address
        };
        var addAddressPdu = MeshNetworkLayer.BuildProxyConfigurationPdu(
            config.EncryptionKey,
            config.PrivacyKey,
            config.Nid,
            config.IvIndex,
            sequenceNumber,
            lowerAddAddress);
        await _link.WriteAsync(
            MeshProxyPdu.Create(
                MeshProxyPdu.MessageTypeProxyConfiguration,
                addAddressPdu),
            cancellationToken);
    }

    /// <summary>
    /// 发送 Config Default TTL Get 并等待 Status，验证与已配网节点的链路是否
    /// 可用。主要用于自愈：若持久化的序列号落后于设备（如旧版本未保存序列号），
    /// 设备会因回放保护静默丢弃消息，此验证会失败，调用方可前移序列号重试。
    /// </summary>
    public async Task<bool> VerifyLinkAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (_config is null)
            return false;
        try
        {
            // 先注册等待者再发送，避免响应先于等待者注册到达而被丢弃
            var statusTask = WaitForAccessMessageAsync(
                MeshOpcode.Encode(MeshOpcode.ConfigDefaultTtlStatus),
                timeout,
                cancellationToken);
            await SendDeviceKeyMessageAsync(
                ConfigModel.DefaultTtlGet(),
                cancellationToken);
            await statusTask;
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// 等待指定 opcode 的入站访问层消息。
    /// </summary>
    /// <param name="opcode">1 或 2 字节 opcode（完整消息以 opcode 开头）。</param>
    /// <param name="timeout">超时时间。</param>
    /// <param name="cancellationToken">取消等待的令牌。</param>
    public Task<byte[]> WaitForAccessMessageAsync(
        ReadOnlySpan<byte> opcode,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        WaitForAccessPduCoreAsync(opcode.ToArray(), null, timeout, cancellationToken);

    /// <summary>等待来自指定节点且 opcode 匹配的访问消息。</summary>
    public Task<MeshAccessMessage> WaitForAccessMessageFromAsync(
        ushort expectedSource,
        ReadOnlySpan<byte> opcode,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        WaitForAccessMessageCoreAsync(
            opcode.ToArray(),
            expectedSource,
            timeout,
            cancellationToken);

    private async Task<byte[]> WaitForAccessPduCoreAsync(
        byte[] opcodeBytes,
        ushort? expectedSource,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        (await WaitForAccessMessageCoreAsync(
            opcodeBytes,
            expectedSource,
            timeout,
            cancellationToken)).AccessPdu;

    private async Task<MeshAccessMessage> WaitForAccessMessageCoreAsync(
        byte[] opcodeBytes,
        ushort? expectedSource,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<MeshAccessMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_receiveLock)
            _pendingReceives.Add(new PendingReceive(
                opcodeBytes,
                expectedSource,
                completion));

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        using var registration = linked.Token.Register(() =>
            completion.TrySetCanceled(linked.Token));
        try
        {
            return await completion.Task;
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"等待 Mesh 消息（opcode {Convert.ToHexString(opcodeBytes)}" +
                (expectedSource is { } source ? $"，src 0x{source:X4}" : string.Empty) +
                "）超时。");
        }
        finally
        {
            lock (_receiveLock)
                _pendingReceives.RemoveAll(item => item.Completion == completion);
        }
    }

    private Task SendMessageAsync(
        ushort destination,
        ReadOnlySpan<byte> accessPdu,
        bool useDeviceKey,
        CancellationToken cancellationToken) =>
        SendMessageCoreAsync(
            destination,
            accessPdu.ToArray(),
            useDeviceKey,
            cancellationToken);

    private async Task SendMessageCoreAsync(
        ushort destination,
        byte[] accessPdu,
        bool useDeviceKey,
        CancellationToken cancellationToken)
    {
        var config = RequireConfig();
        if (!ArriOpto.BLE.Mesh.Crypto.MeshAddress.IsUnicast(destination) &&
            !ArriOpto.BLE.Mesh.Crypto.MeshAddress.IsGroup(destination) &&
            !ArriOpto.BLE.Mesh.Crypto.MeshAddress.IsAllNodes(destination))
        {
            throw new ArgumentOutOfRangeException(
                nameof(destination),
                "目标地址必须是单播、分组或所有节点地址。");
        }

        var seq0 = config.NextSequenceNumber();

        var upperTransport = useDeviceKey
            ? MeshTransportLayer.EncryptDeviceKeyUpperTransportPdu(
                config.DeviceKey,
                aszmic: false,
                seq0,
                config.ProvisionerAddress,
                destination,
                config.IvIndex,
                accessPdu)
            : MeshTransportLayer.EncryptUpperTransportPdu(
                config.AppKey,
                aszmic: false,
                seq0,
                config.ProvisionerAddress,
                destination,
                config.IvIndex,
                accessPdu);

        IReadOnlyList<byte[]> lowerTransportPdus;
        if (upperTransport.Length <= MeshTransportLayer.MaxUnsegmentedUpperTransportPayload)
        {
            lowerTransportPdus =
            [
                MeshTransportLayer.CreateUnsegmentedLowerTransportPdu(
                    akf: !useDeviceKey,
                    aid: useDeviceKey ? (byte)0 : config.Aid,
                    upperTransport)
            ];
        }
        else
        {
            lowerTransportPdus = MeshTransportLayer.CreateSegmentedLowerTransportPdus(
                akf: !useDeviceKey,
                aid: useDeviceKey ? (byte)0 : config.Aid,
                aszmic: false,
                seq0,
                upperTransport);
        }

        for (var index = 0; index < lowerTransportPdus.Count; index++)
        {
            // 每个分段使用独立序列号：首段 seq0，后续段递增
            var sequenceNumber = index == 0
                ? seq0
                : config.NextSequenceNumber();
            var networkPdu = MeshNetworkLayer.BuildNetworkPdu(
                config.EncryptionKey,
                config.PrivacyKey,
                config.Nid,
                config.IvIndex,
                ctl: 0,
                config.Ttl,
                sequenceNumber,
                config.ProvisionerAddress,
                destination,
                lowerTransportPdus[index]);
            var proxyPdu = MeshProxyPdu.Create(
                MeshProxyPdu.MessageTypeNetwork,
                networkPdu);
            DiagnosticLog?.Invoke(
                $"发送网络 PDU：seq=0x{Convert.ToHexString(sequenceNumber)}，" +
                $"dst=0x{destination:X4}，pdu={Convert.ToHexString(proxyPdu)}");
            await _link.WriteAsync(proxyPdu, cancellationToken);
        }

    }

    private MeshNetworkConfig RequireConfig() =>
        _config ?? throw new InvalidOperationException(
            "尚未配网，请先执行 ProvisionAsync 或 Restore。");

    private void OnLinkReceived(object? sender, byte[] proxyPdu)
    {
        if (proxyPdu.Length < 1)
            return;

        var (_, messageType) = MeshProxyPdu.ParseHeader(proxyPdu[0]);
        DiagnosticLog?.Invoke(
            $"收到 Proxy PDU：len={proxyPdu.Length}, type=0x{messageType:X2}, " +
            $"data={Convert.ToHexString(proxyPdu)}");
        if (messageType != MeshProxyPdu.MessageTypeNetwork)
            return;

        var config = _config;
        if (config is null)
            return;

        if (!MeshNetworkLayer.TryDecryptNetworkPdu(
                proxyPdu.AsSpan(1),
                config.EncryptionKey,
                config.PrivacyKey,
                config.Nid,
                config.IvIndex,
                out var network))
        {
            DiagnosticLog?.Invoke("网络层解密失败（NID/密钥/隐私不匹配）。");
            return;
        }

        var transportPdu = network.TransportPdu;
        if (transportPdu.Length < 1)
            return;

        // 分段入站消息暂不重组（最小闭环的状态消息都是非分段）
        var segmented = (transportPdu[0] & 0x80) != 0;
        if (segmented)
        {
            DiagnosticLog?.Invoke("收到分段入站消息，暂不重组。");
            return;
        }

        var akf = (transportPdu[0] & 0x40) != 0;
        var upperTransport = transportPdu.AsSpan(1);
        byte[] accessPdu;
        try
        {
            accessPdu = akf
                ? MeshCrypto.DecryptCcm(
                    config.AppKey,
                    MeshNonce.CreateApplicationNonce(
                        aszmic: false,
                        network.SequenceNumber,
                        network.Src,
                        network.Dst,
                        config.IvIndex),
                    upperTransport,
                    micSize: 4)
                : MeshCrypto.DecryptCcm(
                    config.DeviceKey,
                    MeshNonce.CreateDeviceNonce(
                        aszmic: false,
                        network.SequenceNumber,
                        network.Src,
                        network.Dst,
                        config.IvIndex),
                    upperTransport,
                    micSize: 4);
        }
        catch (CryptographicException)
        {
            DiagnosticLog?.Invoke(
                $"上层解密失败（AKF={(akf ? 1 : 0)}，seq={Convert.ToHexString(network.SequenceNumber)}）。");
            return;
        }

        DiagnosticLog?.Invoke(
            $"解密成功：access opcode={Convert.ToHexString(accessPdu.AsSpan(0, Math.Min(4, accessPdu.Length)))}");

        var message = new MeshAccessMessage(network.Src, network.Dst, accessPdu);
        lock (_receiveLock)
        {
            for (var index = _pendingReceives.Count - 1; index >= 0; index--)
            {
                var waiter = _pendingReceives[index];
                if (accessPdu.AsSpan().StartsWith(waiter.Opcode) &&
                    (waiter.ExpectedSource is null || waiter.ExpectedSource == network.Src))
                {
                    waiter.Completion.TrySetResult(message);
                }
            }
        }

        AccessMessageReceived?.Invoke(this, accessPdu);
        DetailedAccessMessageReceived?.Invoke(this, message);
    }

    private sealed record PendingReceive(
        byte[] Opcode,
        ushort? ExpectedSource,
        TaskCompletionSource<MeshAccessMessage> Completion);
}
