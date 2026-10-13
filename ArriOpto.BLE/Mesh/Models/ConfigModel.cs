using ArriOpto.BLE.Mesh;

namespace ArriOpto.BLE.Mesh.Models;

/// <summary>
/// Config model messages (Mesh Protocol Specification 1.1, Section 4.2.x).
/// </summary>
/// <remarks>
/// Message structures follow Mesh Protocol 1.1 and were cross-verified against
/// the Zephyr / ESP-IDF (SIG reference) config client implementations. NetKey
/// and AppKey indexes are 12-bit values packed little-endian; a single 12-bit
/// index with RFU occupies 2 octets, a pair of indexes occupies 3 octets.
/// </remarks>
public static class ConfigModel
{
    // ---- Composition Data ----
    /// <summary>
    /// 构建 Config Composition Data Get 消息。
    /// 载荷：Page(1)，0=完整组成数据，128+ 为大组成数据页。
    /// </summary>
    /// <param name="page">要请求的数据页（默认 0）。</param>
    public static byte[] CompositionDataGet(byte page = 0) =>
        MeshMessageWriter.Create(MeshOpcode.ConfigCompositionDataGet)
            .AddU8(page)
            .ToArray();

    /// <summary>
    /// Parses a Config Composition Data Status message for page 0.
    /// Payload: Page(1) + CID(2) + PID(2) + VID(2) + CRPL(2) + Features(2) +
    /// one or more element descriptors.
    /// </summary>
    /// <param name="message">完整消息（含 opcode 0x02）。</param>
    /// <param name="status">解析结果；失败时为 default。</param>
    /// <returns>opcode 匹配且结构合法时返回 true。</returns>
    public static bool TryParseCompositionDataStatus(
        ReadOnlySpan<byte> message,
        out CompositionDataStatus status)
    {
        status = default;
        var reader = new MeshMessageReader(message);
        if (!reader.TryConsumeOpcode(MeshOpcode.ConfigCompositionDataStatus) ||
            reader.Remaining < 11)
        {
            return false;
        }

        var page = reader.ReadU8();
        var companyId = reader.ReadU16();
        var productId = reader.ReadU16();
        var vendorId = reader.ReadU16();
        var crpl = reader.ReadU16();
        var features = reader.ReadU16();

        var elements = new List<CompositionElement>();
        while (reader.Remaining >= 4)
        {
            var location = reader.ReadU16();
            var numS = reader.ReadU8();
            var numV = reader.ReadU8();
            if (reader.Remaining < numS * 2 + numV * 4)
                return false;

            var sigModels = new List<ushort>();
            for (var i = 0; i < numS; i++)
                sigModels.Add(reader.ReadU16());

            var vendorModels = new List<VendorModelId>();
            for (var i = 0; i < numV; i++)
                vendorModels.Add(new VendorModelId(reader.ReadU16(), reader.ReadU16()));

            elements.Add(new CompositionElement(location, sigModels, vendorModels));
        }

        if (reader.HasRemaining)
            return false;

        status = new CompositionDataStatus(
            page,
            companyId,
            productId,
            vendorId,
            crpl,
            features,
            elements);
        return true;
    }

    // ---- AppKey ----
    /// <summary>
    /// 构建 Config AppKey Add 消息。
    /// 载荷：NetKey Index(12 位) + AppKey Index(12 位) 打包为 3 字节 + AppKey(16)。
    /// </summary>
    /// <param name="netKeyIndex">网络密钥索引（0-0xFFF）。</param>
    /// <param name="appKeyIndex">应用密钥索引（0-0xFFF）。</param>
    /// <param name="appKey">16 字节应用密钥。</param>
    public static byte[] AppKeyAdd(
        ushort netKeyIndex,
        ushort appKeyIndex,
        ReadOnlySpan<byte> appKey) =>
        BuildAppKeySet(MeshOpcode.ConfigAppKeyAdd, netKeyIndex, appKeyIndex, appKey);

    /// <summary>构建 Config AppKey Update 消息（结构同 AppKey Add）。</summary>
    public static byte[] AppKeyUpdate(
        ushort netKeyIndex,
        ushort appKeyIndex,
        ReadOnlySpan<byte> appKey) =>
        BuildAppKeySet(MeshOpcode.ConfigAppKeyUpdate, netKeyIndex, appKeyIndex, appKey);

    /// <summary>
    /// 构建 Config AppKey Delete 消息。
    /// 载荷：NetKey Index + AppKey Index 打包为 3 字节。
    /// </summary>
    public static byte[] AppKeyDelete(ushort netKeyIndex, ushort appKeyIndex) =>
        BuildIndexPair(MeshOpcode.ConfigAppKeyDelete, netKeyIndex, appKeyIndex);

    /// <summary>
    /// 构建 Config AppKey Get 消息。
    /// 载荷：NetKey Index(2，12 位有效 + 4 位 RFU)。
    /// </summary>
    public static byte[] AppKeyGet(ushort netKeyIndex) =>
        BuildSingleIndex(MeshOpcode.ConfigAppKeyGet, netKeyIndex);

    /// <summary>
    /// 解析 Config AppKey Status 消息。
    /// 载荷：Status Code(1) + NetKey Index 与 AppKey Index 打包为 3 字节。
    /// </summary>
    /// <remarks>
    /// 索引布局与 <see cref="AppKeyAdd"/> 的载荷一致：两个 12 位索引打包成 3 字节，
    /// 不能按两个大端 U16 读取。完整访问层消息为 6 字节（2 字节 opcode + 4 字节载荷）。
    /// </remarks>
    public static bool TryParseAppKeyStatus(
        ReadOnlySpan<byte> message,
        out ConfigAppKeyStatus status)
    {
        status = default;
        var reader = new MeshMessageReader(message);
        if (!reader.TryConsumeOpcode(MeshOpcode.ConfigAppKeyStatus) ||
            reader.Remaining != 4)
        {
            return false;
        }

        var statusCode = reader.ReadU8();
        TryReadIndexPair(reader, out var netKeyIndex, out var appKeyIndex);
        status = new ConfigAppKeyStatus(statusCode, netKeyIndex, appKeyIndex);
        return true;
    }

    // ---- Beacon ----
    /// <summary>构建 Config Beacon Get 消息。</summary>
    public static byte[] BeaconGet() =>
        MeshMessageWriter.Create(MeshOpcode.ConfigBeaconGet).ToArray();

    /// <summary>构建 Config Beacon Set 消息（载荷：Beacon(1)，1=启用）。</summary>
    public static byte[] BeaconSet(bool enabled) =>
        MeshMessageWriter.Create(MeshOpcode.ConfigBeaconSet)
            .AddU8((byte)(enabled ? 1 : 0))
            .ToArray();

    /// <summary>解析 Config Beacon Status 消息（载荷：Beacon(1)）。</summary>
    public static bool TryParseBeaconStatus(
        ReadOnlySpan<byte> message,
        out bool enabled)
    {
        enabled = false;
        var reader = new MeshMessageReader(message);
        if (!reader.TryConsumeOpcode(MeshOpcode.ConfigBeaconStatus) ||
            reader.Remaining != 1)
        {
            return false;
        }

        enabled = reader.ReadU8() == 1;
        return true;
    }

    // ---- Default TTL ----
    /// <summary>构建 Config Default TTL Get 消息。</summary>
    public static byte[] DefaultTtlGet() =>
        MeshMessageWriter.Create(MeshOpcode.ConfigDefaultTtlGet).ToArray();

    /// <summary>构建 Config Default TTL Set 消息（载荷：TTL(1)，0x7F=默认）。</summary>
    public static byte[] DefaultTtlSet(byte ttl) =>
        MeshMessageWriter.Create(MeshOpcode.ConfigDefaultTtlSet)
            .AddU8(ttl)
            .ToArray();

    /// <summary>解析 Config Default TTL Status 消息（载荷：TTL(1)）。</summary>
    public static bool TryParseDefaultTtlStatus(
        ReadOnlySpan<byte> message,
        out byte ttl)
    {
        ttl = 0;
        var reader = new MeshMessageReader(message);
        if (!reader.TryConsumeOpcode(MeshOpcode.ConfigDefaultTtlStatus) ||
            reader.Remaining != 1)
        {
            return false;
        }

        ttl = reader.ReadU8();
        return true;
    }

    // ---- Friend ----
    /// <summary>构建 Config Friend Get 消息。</summary>
    public static byte[] FriendGet() =>
        MeshMessageWriter.Create(MeshOpcode.ConfigFriendGet).ToArray();

    /// <summary>构建 Config Friend Set 消息（载荷：Friend(1)）。</summary>
    public static byte[] FriendSet(bool enabled) =>
        MeshMessageWriter.Create(MeshOpcode.ConfigFriendSet)
            .AddU8((byte)(enabled ? 1 : 0))
            .ToArray();

    /// <summary>解析 Config Friend Status 消息（载荷：Friend(1)）。</summary>
    public static bool TryParseFriendStatus(
        ReadOnlySpan<byte> message,
        out bool enabled)
    {
        enabled = false;
        var reader = new MeshMessageReader(message);
        if (!reader.TryConsumeOpcode(MeshOpcode.ConfigFriendStatus) ||
            reader.Remaining != 1)
        {
            return false;
        }

        enabled = reader.ReadU8() == 1;
        return true;
    }

    // ---- GATT Proxy ----
    /// <summary>构建 Config GATT Proxy Get 消息。</summary>
    public static byte[] GattProxyGet() =>
        MeshMessageWriter.Create(MeshOpcode.ConfigGattProxyGet).ToArray();

    /// <summary>构建 Config GATT Proxy Set 消息（载荷：GATT Proxy(1)）。</summary>
    public static byte[] GattProxySet(bool enabled) =>
        MeshMessageWriter.Create(MeshOpcode.ConfigGattProxySet)
            .AddU8((byte)(enabled ? 1 : 0))
            .ToArray();

    /// <summary>解析 Config GATT Proxy Status 消息（载荷：GATT Proxy(1)）。</summary>
    public static bool TryParseGattProxyStatus(
        ReadOnlySpan<byte> message,
        out bool enabled)
    {
        enabled = false;
        var reader = new MeshMessageReader(message);
        if (!reader.TryConsumeOpcode(MeshOpcode.ConfigGattProxyStatus) ||
            reader.Remaining != 1)
        {
            return false;
        }

        enabled = reader.ReadU8() == 1;
        return true;
    }

    // ---- Key Refresh Phase ----
    /// <summary>
    /// 构建 Config Key Refresh Phase Get 消息。
    /// 载荷：NetKey Index(2)。
    /// </summary>
    public static byte[] KeyRefreshPhaseGet(ushort netKeyIndex) =>
        BuildSingleIndex(MeshOpcode.ConfigKeyRefreshPhaseGet, netKeyIndex);

    /// <summary>
    /// 构建 Config Key Refresh Phase Set 消息。
    /// 载荷：NetKey Index(2) + Transition(1)。
    /// </summary>
    public static byte[] KeyRefreshPhaseSet(ushort netKeyIndex, byte transition) =>
        BuildSingleIndex(MeshOpcode.ConfigKeyRefreshPhaseSet, netKeyIndex)
            .Concat(new[] { transition })
            .ToArray();

    /// <summary>
    /// 解析 Config Key Refresh Phase Status 消息。
    /// 载荷：Status Code(1) + NetKey Index(2) + Phase(1)。
    /// </summary>
    public static bool TryParseKeyRefreshPhaseStatus(
        ReadOnlySpan<byte> message,
        out ConfigKeyRefreshPhaseStatus status)
    {
        status = default;
        var reader = new MeshMessageReader(message);
        if (!reader.TryConsumeOpcode(MeshOpcode.ConfigKeyRefreshPhaseStatus) ||
            reader.Remaining != 4)
        {
            return false;
        }

        var statusCode = reader.ReadU8();
        var netKeyIndex = ReadSingleIndex(reader);
        var phase = reader.ReadU8();
        status = new ConfigKeyRefreshPhaseStatus(statusCode, netKeyIndex, phase);
        return true;
    }

    // ---- Model Publication ----
    /// <summary>
    /// 构建 Config Model Publication Get 消息。
    /// 载荷：Element Address(2) + Model Identifier(2 或 4)。
    /// </summary>
    public static byte[] ModelPublicationGet(
        ushort elementAddress,
        ModelIdentifier modelId) =>
        BuildElementModelMessage(MeshOpcode.ConfigModelPublicationGet, elementAddress, modelId);

    /// <summary>
    /// 构建 Config Model Publication Set 消息。
    /// 载荷：Element Address(2) + Publish Address(2) + AppKey Index(12 位)+
    /// Credential Flag(1 位)+RFU(3 位) + TTL(1) + Publish Period(1) +
    /// Publish Retransmit(1) + Model Identifier(2 或 4)。
    /// </summary>
    public static byte[] ModelPublicationSet(
        ushort elementAddress,
        ushort publishAddress,
        ushort appKeyIndex,
        bool credentialFlag,
        byte ttl,
        byte publishPeriod,
        byte publishRetransmit,
        ModelIdentifier modelId)
    {
        var writer = MeshMessageWriter.Create(MeshOpcode.ConfigModelPublicationSet)
            .AddU16(elementAddress)
            .AddU16(publishAddress)
            .AddU16(unchecked((ushort)(appKeyIndex | (credentialFlag ? 0x1000 : 0))))
            .AddU8(ttl)
            .AddU8(publishPeriod)
            .AddU8(publishRetransmit);
        WriteModelId(writer, modelId);
        return writer.ToArray();
    }

    /// <summary>
    /// 构建 Config Model Publication Virtual Address Set 消息。
    /// 载荷：Element Address(2) + Label UUID(16) + AppKey Index+Credential 字段 +
    /// TTL(1) + Publish Period(1) + Publish Retransmit(1) + Model Identifier。
    /// </summary>
    public static byte[] ModelPublicationVirtualAddressSet(
        ushort elementAddress,
        ReadOnlySpan<byte> labelUuid,
        ushort appKeyIndex,
        bool credentialFlag,
        byte ttl,
        byte publishPeriod,
        byte publishRetransmit,
        ModelIdentifier modelId)
    {
        if (labelUuid.Length != 16)
            throw new ArgumentException("Label UUID must be 16 octets.", nameof(labelUuid));

        var writer = MeshMessageWriter.Create(
                MeshOpcode.ConfigModelPublicationVirtualAddressSet)
            .AddU16(elementAddress)
            .AddBytes(labelUuid)
            .AddU16(unchecked((ushort)(appKeyIndex | (credentialFlag ? 0x1000 : 0))))
            .AddU8(ttl)
            .AddU8(publishPeriod)
            .AddU8(publishRetransmit);
        WriteModelId(writer, modelId);
        return writer.ToArray();
    }

    /// <summary>
    /// 解析 Config Model Publication Status 消息（Status Code 后字段同 Set）。
    /// </summary>
    public static bool TryParseModelPublicationStatus(
        ReadOnlySpan<byte> message,
        out ConfigModelPublicationStatus status)
    {
        status = default;
        var reader = new MeshMessageReader(message);
        if (!reader.TryConsumeOpcode(MeshOpcode.ConfigModelPublicationStatus) ||
            reader.Remaining < 13)
        {
            return false;
        }

        var statusCode = reader.ReadU8();
        var elementAddress = reader.ReadU16();
        var publishAddress = reader.ReadU16();
        var appKeyAndCredential = reader.ReadU16();
        var ttl = reader.ReadU8();
        var period = reader.ReadU8();
        var retransmit = reader.ReadU8();
        var modelId = ReadModelId(reader);
        status = new ConfigModelPublicationStatus(
            statusCode,
            elementAddress,
            publishAddress,
            (ushort)(appKeyAndCredential & 0x0FFF),
            (appKeyAndCredential & 0x1000) != 0,
            ttl,
            period,
            retransmit,
            modelId);
        return true;
    }

    // ---- Model Subscription ----
    /// <summary>
    /// 构建 Config Model Subscription Add 消息。
    /// 载荷：Element Address(2) + Address(2) + Model Identifier(2 或 4)。
    /// </summary>
    public static byte[] ModelSubscriptionAdd(
        ushort elementAddress,
        ushort address,
        ModelIdentifier modelId) =>
        BuildSubscriptionSet(MeshOpcode.ConfigModelSubscriptionAdd, elementAddress, address, modelId);

    /// <summary>构建 Config Model Subscription Delete 消息（结构同 Add）。</summary>
    public static byte[] ModelSubscriptionDelete(
        ushort elementAddress,
        ushort address,
        ModelIdentifier modelId) =>
        BuildSubscriptionSet(MeshOpcode.ConfigModelSubscriptionDelete, elementAddress, address, modelId);

    /// <summary>构建 Config Model Subscription Overwrite 消息（结构同 Add）。</summary>
    public static byte[] ModelSubscriptionOverwrite(
        ushort elementAddress,
        ushort address,
        ModelIdentifier modelId) =>
        BuildSubscriptionSet(MeshOpcode.ConfigModelSubscriptionOverwrite, elementAddress, address, modelId);

    /// <summary>
    /// 构建 Config Model Subscription Delete All 消息。
    /// 载荷：Element Address(2) + Model Identifier(2 或 4)。
    /// </summary>
    public static byte[] ModelSubscriptionDeleteAll(
        ushort elementAddress,
        ModelIdentifier modelId) =>
        BuildElementModelMessage(MeshOpcode.ConfigModelSubscriptionDeleteAll, elementAddress, modelId);

    /// <summary>
    /// 构建 Config Model Subscription Virtual Address Add 消息。
    /// 载荷：Element Address(2) + Label UUID(16) + Model Identifier。
    /// </summary>
    public static byte[] ModelSubscriptionVirtualAddressAdd(
        ushort elementAddress,
        ReadOnlySpan<byte> labelUuid,
        ModelIdentifier modelId) =>
        BuildVirtualSubscriptionSet(
            MeshOpcode.ConfigModelSubscriptionVirtualAddressAdd,
            elementAddress,
            labelUuid,
            modelId);

    /// <summary>构建 Config Model Subscription Virtual Address Delete 消息。</summary>
    public static byte[] ModelSubscriptionVirtualAddressDelete(
        ushort elementAddress,
        ReadOnlySpan<byte> labelUuid,
        ModelIdentifier modelId) =>
        BuildVirtualSubscriptionSet(
            MeshOpcode.ConfigModelSubscriptionVirtualAddressDelete,
            elementAddress,
            labelUuid,
            modelId);

    /// <summary>构建 Config Model Subscription Virtual Address Overwrite 消息。</summary>
    public static byte[] ModelSubscriptionVirtualAddressOverwrite(
        ushort elementAddress,
        ReadOnlySpan<byte> labelUuid,
        ModelIdentifier modelId) =>
        BuildVirtualSubscriptionSet(
            MeshOpcode.ConfigModelSubscriptionVirtualAddressOverwrite,
            elementAddress,
            labelUuid,
            modelId);

    /// <summary>
    /// 解析 Config Model Subscription Status 消息。
    /// 载荷：Status Code(1) + Element Address(2) + Address(2) + Model Identifier。
    /// </summary>
    public static bool TryParseModelSubscriptionStatus(
        ReadOnlySpan<byte> message,
        out ConfigModelSubscriptionStatus status)
    {
        status = default;
        var reader = new MeshMessageReader(message);
        if (!reader.TryConsumeOpcode(MeshOpcode.ConfigModelSubscriptionStatus) ||
            reader.Remaining < 7)
        {
            return false;
        }

        var statusCode = reader.ReadU8();
        var elementAddress = reader.ReadU16();
        var address = reader.ReadU16();
        var modelId = ReadModelId(reader);
        status = new ConfigModelSubscriptionStatus(
            statusCode,
            elementAddress,
            address,
            modelId);
        return true;
    }

    // ---- Network Transmit ----
    /// <summary>构建 Config Network Transmit Get 消息。</summary>
    public static byte[] NetworkTransmitGet() =>
        MeshMessageWriter.Create(MeshOpcode.ConfigNetworkTransmitGet).ToArray();

    /// <summary>
    /// 构建 Config Network Transmit Set 消息。
    /// 载荷：Count(3 位) + Interval Steps(5 位) 打包为 1 字节。
    /// </summary>
    public static byte[] NetworkTransmitSet(byte count, byte intervalSteps) =>
        MeshMessageWriter.Create(MeshOpcode.ConfigNetworkTransmitSet)
            .AddU8(EncodeRetransmit(count, intervalSteps))
            .ToArray();

    /// <summary>解析 Config Network Transmit Status 消息（1 字节）。</summary>
    public static bool TryParseNetworkTransmitStatus(
        ReadOnlySpan<byte> message,
        out NetworkTransmitStatus status)
    {
        status = default;
        var reader = new MeshMessageReader(message);
        if (!reader.TryConsumeOpcode(MeshOpcode.ConfigNetworkTransmitStatus) ||
            reader.Remaining != 1)
        {
            return false;
        }

        status = DecodeRetransmit(reader.ReadU8());
        return true;
    }

    // ---- Relay ----
    /// <summary>构建 Config Relay Get 消息。</summary>
    public static byte[] RelayGet() =>
        MeshMessageWriter.Create(MeshOpcode.ConfigRelayGet).ToArray();

    /// <summary>
    /// 构建 Config Relay Set 消息。
    /// 载荷：Relay(1) + Retransmit Count(3 位)+Interval Steps(5 位)。
    /// </summary>
    public static byte[] RelaySet(
        bool enabled,
        byte retransmitCount,
        byte retransmitIntervalSteps) =>
        MeshMessageWriter.Create(MeshOpcode.ConfigRelaySet)
            .AddU8((byte)(enabled ? 1 : 0))
            .AddU8(EncodeRetransmit(retransmitCount, retransmitIntervalSteps))
            .ToArray();

    /// <summary>解析 Config Relay Status 消息（Relay(1) + 重传字段(1)）。</summary>
    public static bool TryParseRelayStatus(
        ReadOnlySpan<byte> message,
        out RelayStatus status)
    {
        status = default;
        var reader = new MeshMessageReader(message);
        if (!reader.TryConsumeOpcode(MeshOpcode.ConfigRelayStatus) ||
            reader.Remaining != 2)
        {
            return false;
        }

        var relay = reader.ReadU8();
        var retransmit = DecodeRetransmit(reader.ReadU8());
        status = new RelayStatus(relay, retransmit);
        return true;
    }

    // ---- Model Subscription Get/List (SIG and Vendor) ----
    /// <summary>
    /// 构建 Config SIG Model Subscription Get 消息。
    /// 载荷：Element Address(2) + SIG Model ID(2)。
    /// </summary>
    public static byte[] SigModelSubscriptionGet(
        ushort elementAddress,
        ushort modelId) =>
        BuildElementModelMessage(MeshOpcode.ConfigSigModelSubscriptionGet, elementAddress, new ModelIdentifier(modelId));

    /// <summary>
    /// 构建 Config Vendor Model Subscription Get 消息。
    /// 载荷：Element Address(2) + Company ID(2) + Model ID(2)。
    /// </summary>
    public static byte[] VendorModelSubscriptionGet(
        ushort elementAddress,
        ushort companyId,
        ushort modelId) =>
        BuildElementModelMessage(
            MeshOpcode.ConfigVendorModelSubscriptionGet,
            elementAddress,
            new ModelIdentifier(companyId, modelId));

    /// <summary>
    /// 解析 Config SIG Model Subscription List 消息。
    /// 载荷：Status Code(1) + Element Address(2) + SIG Model ID(2) + 地址列表(2*N)。
    /// </summary>
    public static bool TryParseSigModelSubscriptionList(
        ReadOnlySpan<byte> message,
        out ModelSubscriptionList status) =>
        TryParseSubscriptionList(
            message,
            MeshOpcode.ConfigSigModelSubscriptionList,
            isVendor: false,
            out status);

    /// <summary>
    /// 解析 Config Vendor Model Subscription List 消息（模型 ID 为 4 字节）。
    /// </summary>
    public static bool TryParseVendorModelSubscriptionList(
        ReadOnlySpan<byte> message,
        out ModelSubscriptionList status) =>
        TryParseSubscriptionList(
            message,
            MeshOpcode.ConfigVendorModelSubscriptionList,
            isVendor: true,
            out status);

    // ---- Low Power Node PollTimeout ----
    /// <summary>
    /// 构建 Config Low Power Node PollTimeout Get 消息。
    /// 载荷：LPN 的单播地址(2)。
    /// </summary>
    public static byte[] LowPowerNodePollTimeoutGet(ushort elementAddress) =>
        MeshMessageWriter.Create(MeshOpcode.ConfigLowPowerNodePollTimeoutGet)
            .AddU16(elementAddress)
            .ToArray();

    /// <summary>解析 Config Low Power Node PollTimeout Status 消息（PollTimeout，3 字节）。</summary>
    public static bool TryParseLowPowerNodePollTimeoutStatus(
        ReadOnlySpan<byte> message,
        out uint pollTimeout)
    {
        pollTimeout = 0;
        var reader = new MeshMessageReader(message);
        if (!reader.TryConsumeOpcode(MeshOpcode.ConfigLowPowerNodePollTimeoutStatus) ||
            reader.Remaining != 3)
        {
            return false;
        }

        pollTimeout = reader.ReadU24();
        return true;
    }

    // ---- Heartbeat Publication ----
    /// <summary>构建 Config Heartbeat Publication Get 消息。</summary>
    public static byte[] HeartbeatPublicationGet() =>
        MeshMessageWriter.Create(MeshOpcode.ConfigHeartbeatPublicationGet).ToArray();

    /// <summary>
    /// 构建 Config Heartbeat Publication Set 消息。
    /// 载荷：Destination(2) + Count Log(1) + Period Log(1) + TTL(1) +
    /// Features(2) + NetKey Index(2，12 位有效)。
    /// </summary>
    public static byte[] HeartbeatPublicationSet(
        ushort destination,
        byte countLog,
        byte periodLog,
        byte ttl,
        ushort features,
        ushort netKeyIndex) =>
        MeshMessageWriter.Create(MeshOpcode.ConfigHeartbeatPublicationSet)
            .AddU16(destination)
            .AddU8(countLog)
            .AddU8(periodLog)
            .AddU8(ttl)
            .AddU16(features)
            .AddU16(unchecked((ushort)(netKeyIndex & 0x0FFF)))
            .ToArray();

    /// <summary>
    /// 解析 Config Heartbeat Publication Status 消息（Status Code 后字段同 Set）。
    /// </summary>
    public static bool TryParseHeartbeatPublicationStatus(
        ReadOnlySpan<byte> message,
        out ConfigHeartbeatPublicationStatus status)
    {
        status = default;
        var reader = new MeshMessageReader(message);
        if (!reader.TryConsumeOpcode(MeshOpcode.ConfigHeartbeatPublicationStatus) ||
            reader.Remaining != 10)
        {
            return false;
        }

        var statusCode = reader.ReadU8();
        var destination = reader.ReadU16();
        var countLog = reader.ReadU8();
        var periodLog = reader.ReadU8();
        var ttl = reader.ReadU8();
        var features = reader.ReadU16();
        var netKeyIndex = (ushort)(reader.ReadU16() & 0x0FFF);
        status = new ConfigHeartbeatPublicationStatus(
            statusCode,
            destination,
            countLog,
            periodLog,
            ttl,
            features,
            netKeyIndex);
        return true;
    }

    // ---- Heartbeat Subscription ----
    /// <summary>构建 Config Heartbeat Subscription Get 消息。</summary>
    public static byte[] HeartbeatSubscriptionGet() =>
        MeshMessageWriter.Create(MeshOpcode.ConfigHeartbeatSubscriptionGet).ToArray();

    /// <summary>
    /// 构建 Config Heartbeat Subscription Set 消息。
    /// 载荷：Source(2) + Destination(2) + Period Log(1)。
    /// </summary>
    public static byte[] HeartbeatSubscriptionSet(
        ushort source,
        ushort destination,
        byte periodLog) =>
        MeshMessageWriter.Create(MeshOpcode.ConfigHeartbeatSubscriptionSet)
            .AddU16(source)
            .AddU16(destination)
            .AddU8(periodLog)
            .ToArray();

    /// <summary>
    /// 解析 Config Heartbeat Subscription Status 消息。
    /// 载荷：Status Code(1) + Source(2) + Destination(2) + Period Log(1) +
    /// Count Log(1) + Min Hops(1) + Max Hops(1)。
    /// </summary>
    public static bool TryParseHeartbeatSubscriptionStatus(
        ReadOnlySpan<byte> message,
        out ConfigHeartbeatSubscriptionStatus status)
    {
        status = default;
        var reader = new MeshMessageReader(message);
        if (!reader.TryConsumeOpcode(MeshOpcode.ConfigHeartbeatSubscriptionStatus) ||
            reader.Remaining != 10)
        {
            return false;
        }

        var statusCode = reader.ReadU8();
        var source = reader.ReadU16();
        var destination = reader.ReadU16();
        var periodLog = reader.ReadU8();
        var countLog = reader.ReadU8();
        var minHops = reader.ReadU8();
        var maxHops = reader.ReadU8();
        status = new ConfigHeartbeatSubscriptionStatus(
            statusCode,
            source,
            destination,
            periodLog,
            countLog,
            minHops,
            maxHops);
        return true;
    }

    // ---- Model App Bind/Unbind ----
    /// <summary>
    /// 构建 Config Model App Bind 消息。
    /// 载荷：Element Address(2) + AppKey Index(2，12 位有效) + Model Identifier。
    /// </summary>
    public static byte[] ModelAppBind(
        ushort elementAddress,
        ushort appKeyIndex,
        ModelIdentifier modelId) =>
        BuildAppBind(MeshOpcode.ConfigModelAppBind, elementAddress, appKeyIndex, modelId);

    /// <summary>构建 Config Model App Unbind 消息（结构同 Bind）。</summary>
    public static byte[] ModelAppUnbind(
        ushort elementAddress,
        ushort appKeyIndex,
        ModelIdentifier modelId) =>
        BuildAppBind(MeshOpcode.ConfigModelAppUnbind, elementAddress, appKeyIndex, modelId);

    /// <summary>
    /// 解析 Config Model App Status 消息。
    /// 载荷：Status Code(1) + Element Address(2) + AppKey Index(2) + Model Identifier。
    /// </summary>
    public static bool TryParseModelAppStatus(
        ReadOnlySpan<byte> message,
        out ConfigModelAppStatus status)
    {
        status = default;
        var reader = new MeshMessageReader(message);
        if (!reader.TryConsumeOpcode(MeshOpcode.ConfigModelAppStatus) ||
            reader.Remaining < 7)
        {
            return false;
        }

        var statusCode = reader.ReadU8();
        var elementAddress = reader.ReadU16();
        var appKeyIndex = ReadSingleIndex(reader);
        var modelId = ReadModelId(reader);
        status = new ConfigModelAppStatus(statusCode, elementAddress, appKeyIndex, modelId);
        return true;
    }

    // ---- Model App Get/List (SIG and Vendor) ----
    /// <summary>
    /// 构建 Config SIG Model App Get 消息。
    /// 载荷：Element Address(2) + SIG Model ID(2)。
    /// </summary>
    public static byte[] SigModelAppGet(ushort elementAddress, ushort modelId) =>
        BuildElementModelMessage(MeshOpcode.ConfigSigModelAppGet, elementAddress, new ModelIdentifier(modelId));

    /// <summary>
    /// 构建 Config Vendor Model App Get 消息。
    /// 载荷：Element Address(2) + Company ID(2) + Model ID(2)。
    /// </summary>
    public static byte[] VendorModelAppGet(
        ushort elementAddress,
        ushort companyId,
        ushort modelId) =>
        BuildElementModelMessage(
            MeshOpcode.ConfigVendorModelAppGet,
            elementAddress,
            new ModelIdentifier(companyId, modelId));

    /// <summary>
    /// 解析 Config SIG Model App List 消息。
    /// 载荷：Status Code(1) + Element Address(2) + SIG Model ID(2) + AppKey 索引列表。
    /// </summary>
    public static bool TryParseSigModelAppList(
        ReadOnlySpan<byte> message,
        out ModelAppList status) =>
        TryParseModelAppList(
            message,
            MeshOpcode.ConfigSigModelAppList,
            isVendor: false,
            out status);

    /// <summary>解析 Config Vendor Model App List 消息（模型 ID 为 4 字节）。</summary>
    public static bool TryParseVendorModelAppList(
        ReadOnlySpan<byte> message,
        out ModelAppList status) =>
        TryParseModelAppList(
            message,
            MeshOpcode.ConfigVendorModelAppList,
            isVendor: true,
            out status);

    // ---- NetKey ----
    /// <summary>
    /// 构建 Config NetKey Add 消息。
    /// 载荷：NetKey Index(2，12 位有效) + NetKey(16)。
    /// </summary>
    public static byte[] NetKeyAdd(ushort netKeyIndex, ReadOnlySpan<byte> netKey) =>
        BuildNetKeySet(MeshOpcode.ConfigNetKeyAdd, netKeyIndex, netKey);

    /// <summary>构建 Config NetKey Update 消息（结构同 NetKey Add）。</summary>
    public static byte[] NetKeyUpdate(ushort netKeyIndex, ReadOnlySpan<byte> netKey) =>
        BuildNetKeySet(MeshOpcode.ConfigNetKeyUpdate, netKeyIndex, netKey);

    /// <summary>构建 Config NetKey Delete 消息（载荷：NetKey Index(2)）。</summary>
    public static byte[] NetKeyDelete(ushort netKeyIndex) =>
        BuildSingleIndex(MeshOpcode.ConfigNetKeyDelete, netKeyIndex);

    /// <summary>构建 Config NetKey Get 消息。</summary>
    public static byte[] NetKeyGet() =>
        MeshMessageWriter.Create(MeshOpcode.ConfigNetKeyGet).ToArray();

    /// <summary>
    /// 解析 Config NetKey Status 消息。
    /// 载荷：Status Code(1) + NetKey Index(2)。
    /// </summary>
    public static bool TryParseNetKeyStatus(
        ReadOnlySpan<byte> message,
        out ConfigNetKeyStatus status)
    {
        status = default;
        var reader = new MeshMessageReader(message);
        if (!reader.TryConsumeOpcode(MeshOpcode.ConfigNetKeyStatus) ||
            reader.Remaining != 3)
        {
            return false;
        }

        var statusCode = reader.ReadU8();
        var netKeyIndex = ReadSingleIndex(reader);
        status = new ConfigNetKeyStatus(statusCode, netKeyIndex);
        return true;
    }

    // ---- Node Identity ----
    /// <summary>构建 Config Node Identity Get 消息（载荷：NetKey Index(2)）。</summary>
    public static byte[] NodeIdentityGet(ushort netKeyIndex) =>
        BuildSingleIndex(MeshOpcode.ConfigNodeIdentityGet, netKeyIndex);

    /// <summary>
    /// 构建 Config Node Identity Set 消息。
    /// 载荷：NetKey Index(2) + Identity(1)。
    /// </summary>
    public static byte[] NodeIdentitySet(ushort netKeyIndex, byte identity) =>
        BuildSingleIndex(MeshOpcode.ConfigNodeIdentitySet, netKeyIndex)
            .Concat(new[] { identity })
            .ToArray();

    /// <summary>
    /// 解析 Config Node Identity Status 消息。
    /// 载荷：Status Code(1) + NetKey Index(2) + Identity(1)。
    /// </summary>
    public static bool TryParseNodeIdentityStatus(
        ReadOnlySpan<byte> message,
        out ConfigNodeIdentityStatus status)
    {
        status = default;
        var reader = new MeshMessageReader(message);
        if (!reader.TryConsumeOpcode(MeshOpcode.ConfigNodeIdentityStatus) ||
            reader.Remaining != 4)
        {
            return false;
        }

        var statusCode = reader.ReadU8();
        var netKeyIndex = ReadSingleIndex(reader);
        var identity = reader.ReadU8();
        status = new ConfigNodeIdentityStatus(statusCode, netKeyIndex, identity);
        return true;
    }

    // ---- Node Reset ----
    /// <summary>构建 Config Node Reset 消息（无载荷）。</summary>
    public static byte[] NodeReset() =>
        MeshMessageWriter.Create(MeshOpcode.ConfigNodeReset).ToArray();

    /// <summary>
    /// 校验 Config Node Reset Status 消息（无载荷，仅 opcode 0x804A）。
    /// </summary>
    public static bool TryParseNodeResetStatus(ReadOnlySpan<byte> message)
    {
        var reader = new MeshMessageReader(message);
        return reader.TryConsumeOpcode(MeshOpcode.ConfigNodeResetStatus) &&
               reader.Remaining == 0;
    }

    // ---- Shared helpers ----
    private static byte[] BuildAppKeySet(
        byte opcode,
        ushort netKeyIndex,
        ushort appKeyIndex,
        ReadOnlySpan<byte> appKey)
    {
        if (appKey.Length != 16)
            throw new ArgumentException("AppKey must be 16 octets.", nameof(appKey));

        var writer = MeshMessageWriter.Create(opcode);
        WriteIndexPair(writer, netKeyIndex, appKeyIndex);
        return writer.AddBytes(appKey).ToArray();
    }

    private static byte[] BuildIndexPair(ushort opcode, ushort netKeyIndex, ushort appKeyIndex)
    {
        var writer = MeshMessageWriter.Create(opcode);
        WriteIndexPair(writer, netKeyIndex, appKeyIndex);
        return writer.ToArray();
    }

    private static byte[] BuildSingleIndex(ushort opcode, ushort netKeyIndex) =>
        MeshMessageWriter.Create(opcode)
            .AddU16(unchecked((ushort)(netKeyIndex & 0x0FFF)))
            .ToArray();

    private static byte[] BuildElementModelMessage(
        ushort opcode,
        ushort elementAddress,
        ModelIdentifier modelId)
    {
        var writer = MeshMessageWriter.Create(opcode).AddU16(elementAddress);
        WriteModelId(writer, modelId);
        return writer.ToArray();
    }

    private static byte[] BuildSubscriptionSet(
        ushort opcode,
        ushort elementAddress,
        ushort address,
        ModelIdentifier modelId)
    {
        var writer = MeshMessageWriter.Create(opcode)
            .AddU16(elementAddress)
            .AddU16(address);
        WriteModelId(writer, modelId);
        return writer.ToArray();
    }

    private static byte[] BuildVirtualSubscriptionSet(
        ushort opcode,
        ushort elementAddress,
        ReadOnlySpan<byte> labelUuid,
        ModelIdentifier modelId)
    {
        if (labelUuid.Length != 16)
            throw new ArgumentException("Label UUID must be 16 octets.", nameof(labelUuid));

        var writer = MeshMessageWriter.Create(opcode)
            .AddU16(elementAddress)
            .AddBytes(labelUuid);
        WriteModelId(writer, modelId);
        return writer.ToArray();
    }

    private static byte[] BuildAppBind(
        ushort opcode,
        ushort elementAddress,
        ushort appKeyIndex,
        ModelIdentifier modelId)
    {
        var writer = MeshMessageWriter.Create(opcode)
            .AddU16(elementAddress)
            .AddU16(unchecked((ushort)(appKeyIndex & 0x0FFF)));
        WriteModelId(writer, modelId);
        return writer.ToArray();
    }

    private static byte[] BuildNetKeySet(
        ushort opcode,
        ushort netKeyIndex,
        ReadOnlySpan<byte> netKey)
    {
        if (netKey.Length != 16)
            throw new ArgumentException("NetKey must be 16 octets.", nameof(netKey));

        return MeshMessageWriter.Create(opcode)
            .AddU16(unchecked((ushort)(netKeyIndex & 0x0FFF)))
            .AddBytes(netKey)
            .ToArray();
    }

    private static void WriteIndexPair(
        MeshMessageWriter writer,
        ushort netKeyIndex,
        ushort appKeyIndex)
    {
        writer.AddU8((byte)(netKeyIndex & 0xFF))
            .AddU8((byte)(((netKeyIndex >> 8) & 0x0F) | ((appKeyIndex & 0x0F) << 4)))
            .AddU8((byte)((appKeyIndex >> 4) & 0xFF));
    }

    private static void TryReadIndexPair(
        MeshMessageReader reader,
        out ushort netKeyIndex,
        out ushort appKeyIndex)
    {
        var first = reader.ReadU8();
        var second = reader.ReadU8();
        var third = reader.ReadU8();
        netKeyIndex = (ushort)(first | ((second & 0x0F) << 8));
        appKeyIndex = (ushort)(((second >> 4) & 0x0F) | (third << 4));
    }

    private static ushort ReadSingleIndex(MeshMessageReader reader) =>
        (ushort)(reader.ReadU16() & 0x0FFF);

    private static void WriteModelId(
        MeshMessageWriter writer,
        ModelIdentifier modelId)
    {
        if (modelId.CompanyId is { } companyId)
        {
            writer.AddU16(companyId);
            writer.AddU16(modelId.ModelId);
        }
        else
        {
            writer.AddU16(modelId.ModelId);
        }
    }

    private static ModelIdentifier ReadModelId(MeshMessageReader reader)
    {
        var first = reader.ReadU16();
        if (reader.Remaining >= 2)
            return new ModelIdentifier(first, reader.ReadU16());
        return new ModelIdentifier(first);
    }

    private static byte EncodeRetransmit(byte count, byte intervalSteps)
    {
        if (count > 7)
            throw new ArgumentOutOfRangeException(nameof(count), "Count must be 0-7.");
        if (intervalSteps > 31)
            throw new ArgumentOutOfRangeException(nameof(intervalSteps), "Interval steps must be 0-31.");
        return (byte)((count & 0x07) | ((intervalSteps & 0x1F) << 3));
    }

    private static NetworkTransmitStatus DecodeRetransmit(byte value) =>
        new((byte)(value & 0x07), (byte)((value >> 3) & 0x1F));

    private static bool TryParseSubscriptionList(
        ReadOnlySpan<byte> message,
        ushort opcode,
        bool isVendor,
        out ModelSubscriptionList status)
    {
        status = default;
        var reader = new MeshMessageReader(message);
        if (!reader.TryConsumeOpcode(opcode) || reader.Remaining < 5)
            return false;

        var statusCode = reader.ReadU8();
        var elementAddress = reader.ReadU16();
        var modelId = isVendor
            ? new ModelIdentifier(reader.ReadU16(), reader.ReadU16())
            : new ModelIdentifier(reader.ReadU16());

        var addresses = new List<ushort>();
        while (reader.HasRemaining)
            addresses.Add(reader.ReadU16());
        status = new ModelSubscriptionList(statusCode, elementAddress, modelId, addresses);
        return true;
    }

    private static bool TryParseModelAppList(
        ReadOnlySpan<byte> message,
        ushort opcode,
        bool isVendor,
        out ModelAppList status)
    {
        status = default;
        var reader = new MeshMessageReader(message);
        if (!reader.TryConsumeOpcode(opcode) || reader.Remaining < 5)
            return false;

        var statusCode = reader.ReadU8();
        var elementAddress = reader.ReadU16();
        var modelId = isVendor
            ? new ModelIdentifier(reader.ReadU16(), reader.ReadU16())
            : new ModelIdentifier(reader.ReadU16());

        var appKeyIndexes = new List<ushort>();
        while (reader.HasRemaining)
            appKeyIndexes.Add(ReadSingleIndex(reader));
        status = new ModelAppList(statusCode, elementAddress, modelId, appKeyIndexes);
        return true;
    }
}

/// <summary>
/// SIG (2-octet) or vendor (4-octet: company ID + model ID) model identifier.
/// </summary>
/// <remarks>
/// SIG 模型：<see cref="ModelId"/> 直接为 SIG 模型 ID；
/// Vendor 模型：<see cref="CompanyId"/> 非空，消息中先写 Company ID 再写 Model ID。
/// </remarks>
public readonly record struct ModelIdentifier(ushort ModelId, ushort? CompanyId = null)
{
    public bool IsVendor => CompanyId is not null;
}

/// <summary>Composition Data 元素描述符中的 Vendor 模型标识（Company ID + Model ID）。</summary>
public readonly record struct VendorModelId(ushort CompanyId, ushort ModelId);

/// <summary>Composition Data 中的一个元素描述符。</summary>
public readonly record struct CompositionElement(
    ushort Location,
    IReadOnlyList<ushort> SigModelIds,
    IReadOnlyList<VendorModelId> VendorModelIds);

/// <summary>解析后的 Config Composition Data Status（Page 0）。</summary>
public readonly record struct CompositionDataStatus(
    byte Page,
    ushort CompanyId,
    ushort ProductId,
    ushort VendorId,
    ushort Crpl,
    ushort Features,
    IReadOnlyList<CompositionElement> Elements);

/// <summary>解析后的 Config AppKey Status。</summary>
public readonly record struct ConfigAppKeyStatus(
    byte StatusCode,
    ushort NetKeyIndex,
    ushort AppKeyIndex);

/// <summary>解析后的 Config Key Refresh Phase Status。</summary>
public readonly record struct ConfigKeyRefreshPhaseStatus(
    byte StatusCode,
    ushort NetKeyIndex,
    byte Phase);

/// <summary>解析后的 Config Model Publication Status。</summary>
public readonly record struct ConfigModelPublicationStatus(
    byte StatusCode,
    ushort ElementAddress,
    ushort PublishAddress,
    ushort AppKeyIndex,
    bool CredentialFlag,
    byte Ttl,
    byte PublishPeriod,
    byte PublishRetransmit,
    ModelIdentifier ModelId);

/// <summary>解析后的 Config Model Subscription Status。</summary>
public readonly record struct ConfigModelSubscriptionStatus(
    byte StatusCode,
    ushort ElementAddress,
    ushort Address,
    ModelIdentifier ModelId);

/// <summary>网络发送参数：Count(3 位) + Interval Steps(5 位)。</summary>
public readonly record struct NetworkTransmitStatus(
    byte Count,
    byte IntervalSteps);

/// <summary>解析后的 Config Relay Status。</summary>
public readonly record struct RelayStatus(
    byte Relay,
    NetworkTransmitStatus Retransmit);

/// <summary>解析后的 SIG/Vendor Model Subscription List。</summary>
public readonly record struct ModelSubscriptionList(
    byte StatusCode,
    ushort ElementAddress,
    ModelIdentifier ModelId,
    IReadOnlyList<ushort> Addresses);

/// <summary>解析后的 Config Heartbeat Publication Status。</summary>
public readonly record struct ConfigHeartbeatPublicationStatus(
    byte StatusCode,
    ushort Destination,
    byte CountLog,
    byte PeriodLog,
    byte Ttl,
    ushort Features,
    ushort NetKeyIndex);

/// <summary>解析后的 Config Heartbeat Subscription Status。</summary>
public readonly record struct ConfigHeartbeatSubscriptionStatus(
    byte StatusCode,
    ushort Source,
    ushort Destination,
    byte PeriodLog,
    byte CountLog,
    byte MinHops,
    byte MaxHops);

/// <summary>解析后的 Config Model App Status。</summary>
public readonly record struct ConfigModelAppStatus(
    byte StatusCode,
    ushort ElementAddress,
    ushort AppKeyIndex,
    ModelIdentifier ModelId);

/// <summary>解析后的 SIG/Vendor Model App List。</summary>
public readonly record struct ModelAppList(
    byte StatusCode,
    ushort ElementAddress,
    ModelIdentifier ModelId,
    IReadOnlyList<ushort> AppKeyIndexes);

/// <summary>解析后的 Config NetKey Status。</summary>
public readonly record struct ConfigNetKeyStatus(
    byte StatusCode,
    ushort NetKeyIndex);

/// <summary>解析后的 Config Node Identity Status。</summary>
public readonly record struct ConfigNodeIdentityStatus(
    byte StatusCode,
    ushort NetKeyIndex,
    byte Identity);
