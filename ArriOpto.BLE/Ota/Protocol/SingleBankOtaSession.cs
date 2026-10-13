using ArriOpto.BLE.Ota;

namespace ArriOpto.BLE.Ota.Protocol;

/// <summary>
/// APP 模式切换命令返回给重新扫描阶段的设备身份线索。
/// </summary>
/// <param name="AddressIncrementByte">
/// 官方 Android SDK 用于推导 OTA MAC 的字节位置。跨平台代码不直接依赖 MAC，
/// 但自定义广播匹配器可参考该值。
/// </param>
/// <param name="DeviceProductId">APP 报告的 Product ID，旧 bootloader 可能为空。</param>
/// <param name="DeviceBootVersion">APP 报告的 boot version，旧 bootloader 可能为空。</param>
public sealed record SingleBankAppTransition(
    int AddressIncrementByte,
    string? DeviceProductId,
    string? DeviceBootVersion);

/// <summary>
/// 执行 Single Bank APP 切换或 OTA bootloader 分区传输。
/// </summary>
/// <remarks>
/// APP 和 OTA 是两个明确阶段。APP 重启后，调用方必须释放旧连接、重新扫描并用新
/// 连接调用 <see cref="RunOtaModeAsync"/>。
/// </remarks>
public sealed class SingleBankOtaSession : OtaSessionBase
{
    private static readonly byte[] ReadyForPartitions = [0x00, 0x81];
    private static readonly byte[] ReadyForPartitionData = [0x00, 0x84];
    private static readonly byte[] BlockAccepted = [0x00, 0x87];
    private static readonly byte[] PartitionAccepted = [0x00, 0x85];
    private static readonly byte[] TransferFinished = [0x00, 0x83];
    private static readonly byte[] ResourceAccepted = [0x00, 0x89];

    private readonly SingleBankFirmwareImage _firmware;

    /// <summary>创建一个与当前 APP/OTA 连接和固件绑定的会话。</summary>
    public SingleBankOtaSession(
        IOtaGattConnection connection,
        SingleBankFirmwareImage firmware,
        OtaOptions? options = null,
        IProgress<OtaProgress>? progress = null)
        : base(connection, options ?? new OtaOptions(), progress)
    {
        _firmware = firmware ?? throw new ArgumentNullException(nameof(firmware));
    }

    /// <summary>
    /// 在 APP 模式执行可选版本校验并命令设备重启到 OTA bootloader。
    /// </summary>
    /// <returns>重新扫描时可用于匹配同一物理设备的身份线索。</returns>
    public async Task<SingleBankAppTransition> SwitchAppToOtaAsync(
        CancellationToken cancellationToken = default)
    {
        if (Connection.Mode != OtaGattMode.SingleBankApp)
        {
            throw new OtaException(
                OtaState.ErrorServiceState,
                $"Expected Single Bank APP mode, but found {Connection.Mode}.");
        }

        EnsurePlainFirmware();
        using var inbox = new NotificationInbox(Connection);
        Report(OtaState.SingleBankAppReady, "Single Bank APP connection is ready.");
        Diagnostic(
            OtaDiagnosticLevel.Information,
            "singlebank.app.start",
            "singlebank",
            "开始 Single Bank APP 模式检查。",
            OtaProtocol.SingleBank,
            OtaState.SingleBankAppReady);

        string? deviceProductId = null;
        string? deviceBootVersion = null;
        var addressIncrementByte = 0;

        // 0x02 版本查询是可选能力。旧 boot service 不响应时仍允许进入 OTA。
        await Connection.WriteCommandAsync(new byte[] { 0x02 }, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var response = await inbox.WaitAsync(
                    _ => true,
                    Options.CommandTimeout,
                    "Single Bank APP version response",
                    cancellationToken)
                .ConfigureAwait(false);

            if (response.Length == 18)
            {
                var span = response.Span;
                deviceProductId = Convert.ToHexString([span[8], span[7]]);
                deviceBootVersion = Convert.ToHexString(span[9..15]);
                addressIncrementByte = span[16] < 6 ? span[16] : 0;

                if (_firmware.Metadata.IsPresent)
                {
                    if (Options.ValidateProductId
                        && !string.Equals(
                            _firmware.Metadata.ProductId,
                            deviceProductId,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new OtaException(
                            OtaState.ErrorFileMismatch,
                            $"Firmware Product ID {_firmware.Metadata.ProductId} does not match device Product ID {deviceProductId}.");
                    }

                    var versionCheck = new byte[9];
                    versionCheck[0] = 0x04;
                    span[7..9].CopyTo(versionCheck.AsSpan(1, 2));
                    HexCodec.Parse(_firmware.Metadata.BootVersion!).CopyTo(versionCheck, 3);
                    await Connection.WriteCommandAsync(versionCheck, cancellationToken)
                        .ConfigureAwait(false);
                    await inbox.WaitAsync(
                            data => data.Span.SequenceEqual(new byte[] { 0x00 }),
                            Options.CommandTimeout,
                            "Single Bank APP version validation",
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (TimeoutException)
        {
            // Older boot services do not implement the optional version query.
        }

        // 0102 切换程序升级，0103 切换资源升级。
        var switchCommand = _firmware.Kind == FirmwareKind.Resource
            ? new byte[] { 0x01, 0x03 }
            : new byte[] { 0x01, 0x02 };
        await WriteExpectedDisconnectCommandAsync(
                switchCommand,
                "switch from APP mode to Single Bank OTA mode",
                cancellationToken)
            .ConfigureAwait(false);
        Report(
            OtaState.AppModeEndWaitingScan,
            "Device was instructed to switch from APP mode to Single Bank OTA mode.");
        Diagnostic(
            OtaDiagnosticLevel.Information,
            "singlebank.app.switch-requested",
            "singlebank",
            "已请求设备从 APP 重启到 OTA bootloader。",
            OtaProtocol.SingleBank,
            OtaState.AppModeEndWaitingScan,
            properties: new Dictionary<string, string>
            {
                ["upgradeKind"] = _firmware.Kind.ToString(),
                ["deviceProductId"] = deviceProductId ?? string.Empty,
                ["deviceBootVersion"] = deviceBootVersion ?? string.Empty,
                ["addressIncrementByte"] = addressIncrementByte.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)
            });

        return new SingleBankAppTransition(
            addressIncrementByte,
            deviceProductId,
            deviceBootVersion);
    }

    /// <summary>
    /// 在 OTA bootloader 模式发送全部分区并请求设备重启。
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// 固件是尚未通过真机验证的 Single Bank <c>.hexe16</c>。
    /// </exception>
    public async Task RunOtaModeAsync(CancellationToken cancellationToken = default)
    {
        if (Connection.Mode != OtaGattMode.SingleBankOta)
        {
            throw new OtaException(
                OtaState.ErrorServiceState,
                $"Expected Single Bank OTA mode, but found {Connection.Mode}.");
        }

        EnsurePlainFirmware();
        using var inbox = new NotificationInbox(Connection);
        Report(OtaState.SingleBankOtaReady, "Single Bank OTA connection is ready.");
        Diagnostic(
            OtaDiagnosticLevel.Information,
            "singlebank.ota.start",
            "singlebank",
            "Single Bank OTA 状态机开始。",
            OtaProtocol.SingleBank,
            OtaState.SingleBankOtaReady,
            properties: new Dictionary<string, string>
            {
                ["partitionCount"] = _firmware.Partitions.Count.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                ["firmwareLength"] = _firmware.Length.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                ["kind"] = _firmware.Kind.ToString()
            });

        if (_firmware.Metadata.IsPresent)
        {
            var validation = new byte[9];
            validation[0] = 0x21;
            HexCodec.Parse(_firmware.Metadata.ProductId!).CopyTo(validation, 1);
            HexCodec.Parse(_firmware.Metadata.BootVersion!).CopyTo(validation, 3);
            await Connection.WriteCommandAsync(validation, cancellationToken).ConfigureAwait(false);
            var versionResponse = await inbox.WaitAsync(
                    data => IsVersionResponse(data.Span),
                    Options.CommandTimeout,
                    "Single Bank OTA version validation",
                    cancellationToken)
                .ConfigureAwait(false);
            if (!IsVersionAccepted(versionResponse.Span))
            {
                throw FirmwareError(
                    $"Single Bank version validation failed: {Convert.ToHexString(versionResponse.Span)}.");
            }
        }

        await Connection.WriteCommandAsync(
                SingleBankCommands.BuildPartitionCount(_firmware.Partitions.Count),
                cancellationToken)
            .ConfigureAwait(false);
        await WaitForAsync(
                inbox,
                ReadyForPartitions,
                Options.CommandTimeout,
                cancellationToken)
            .ConfigureAwait(false);

        if (_firmware.Kind == FirmwareKind.Resource)
        {
            await Connection.WriteCommandAsync(
                    SingleBankCommands.BuildResource(_firmware),
                    cancellationToken)
                .ConfigureAwait(false);
            await WaitForAsync(
                    inbox,
                    ResourceAccepted,
                    Options.CommandTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var transferred = 0L;
        uint sequentialFlashAddress = 0;
        for (var partitionIndex = 0;
             partitionIndex < _firmware.Partitions.Count;
             partitionIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var partition = _firmware.Partitions[partitionIndex];
            // 0x11000000～0x1107FFFF 是协议允许直接使用运行地址的映射区；其他
            // 分区按 bootloader 约定在连续 flash 空间中排列。
            var commandFlashAddress = partition.RunAddress is >= 0x11000000 and <= 0x1107FFFF
                ? partition.RunAddress
                : sequentialFlashAddress;
            Diagnostic(
                OtaDiagnosticLevel.Information,
                "singlebank.partition.start",
                "singlebank",
                "开始发送 Single Bank 分区。",
                OtaProtocol.SingleBank,
                partitionIndex: partitionIndex,
                properties: new Dictionary<string, string>
                {
                    ["runAddress"] = $"0x{partition.RunAddress:X8}",
                    ["flashAddress"] = $"0x{commandFlashAddress:X8}",
                    ["length"] = partition.Length.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)
                });

            await Connection.WriteCommandAsync(
                    SingleBankCommands.BuildPartition(
                        partitionIndex,
                        _firmware.Kind == FirmwareKind.Resource ? 0 : commandFlashAddress,
                        partition),
                    cancellationToken)
                .ConfigureAwait(false);
            await WaitForAsync(
                    inbox,
                    ReadyForPartitionData,
                    Options.CommandTimeout,
                    cancellationToken)
                .ConfigureAwait(false);

            transferred = await TransferPartitionAsync(
                    inbox,
                    partition,
                    partitionIndex,
                    transferred,
                    cancellationToken)
                .ConfigureAwait(false);
            Diagnostic(
                OtaDiagnosticLevel.Information,
                "singlebank.partition.complete",
                "singlebank",
                "设备确认 Single Bank 分区传输完成。",
                OtaProtocol.SingleBank,
                OtaState.FeedbackProgress,
                transferred,
                partitionIndex);

            if (partition.RunAddress is < 0x11000000 or > 0x1107FFFF)
            {
                sequentialFlashAddress = checked(
                    sequentialFlashAddress + (uint)partition.Length + 8);
            }
        }

        await WriteExpectedDisconnectCommandAsync(
                new byte[] { 0x04 },
                "restart after Single Bank OTA completion",
                cancellationToken)
            .ConfigureAwait(false);
        Report(
            OtaState.UpgradeCompleteWaitingDisconnect,
            "Single Bank upgrade completed; the device was instructed to restart.",
            _firmware.Length,
            _firmware.Length);
        Diagnostic(
            OtaDiagnosticLevel.Information,
            "singlebank.ota.complete",
            "singlebank",
            "Single Bank 全部分区完成，已请求设备重启。",
            OtaProtocol.SingleBank,
            OtaState.UpgradeCompleteWaitingDisconnect,
            _firmware.Length);
    }

    private async Task<long> TransferPartitionAsync(
        NotificationInbox inbox,
        FirmwarePartition partition,
        int partitionIndex,
        long transferredBeforePartition,
        CancellationToken cancellationToken)
    {
        // Single Bank Data 没有额外客户端帧头，负载只需扣除 3 字节 ATT Write。
        var packetSize = Connection.Mtu - 3;
        if (packetSize <= 0)
        {
            throw new OtaException(OtaState.ErrorMtuChange, "Negotiated MTU is too small.");
        }

        var offset = 0;
        while (offset < partition.Length)
        {
            // 官方 bootloader 每最多 16 个 GATT 数据包返回一次 block 确认。
            var packetsInBlock = Math.Min(
                16,
                (partition.Length - offset + packetSize - 1) / packetSize);
            for (var packet = 0; packet < packetsInBlock; packet++)
            {
                var count = Math.Min(packetSize, partition.Length - offset);
                await Connection.WriteDataAsync(
                        partition.Data.Slice(offset, count),
                        cancellationToken)
                    .ConfigureAwait(false);
                offset += count;
                Report(
                    OtaState.FeedbackProgress,
                    "Transferring Single Bank firmware.",
                    transferredBeforePartition + offset,
                    _firmware.Length);
            }

            var hasMoreBlocks = offset < partition.Length;
            var isLastPartition = partitionIndex == _firmware.Partitions.Count - 1;
            var expected = hasMoreBlocks
                ? BlockAccepted
                : isLastPartition
                    ? TransferFinished
                    : PartitionAccepted;
            await WaitForAsync(
                    inbox,
                    expected,
                    Options.TransferAcknowledgementTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return transferredBeforePartition + partition.Length;
    }

    private void EnsurePlainFirmware()
    {
        if (_firmware.IsSecure)
        {
            throw new NotSupportedException(
                "Single Bank .hexe16 authentication is not enabled yet because the available vendor source and protocol document disagree on its response opcodes. SLB security is supported.");
        }
    }

    private async Task WriteExpectedDisconnectCommandAsync(
        ReadOnlyMemory<byte> command,
        string operation,
        CancellationToken cancellationToken)
    {
        try
        {
            await Connection.WriteCommandAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException exception) when (IsGatt133(exception))
        {
            // Android can report GATT 133 because the peripheral resets immediately
            // after accepting the mode-switch/reboot command. These are the only two
            // commands for which an immediate disconnect is part of the protocol.
            Diagnostic(
                OtaDiagnosticLevel.Warning,
                "singlebank.expected-disconnect",
                "singlebank",
                "The device disconnected immediately after an expected Single Bank switch/restart command.",
                OtaProtocol.SingleBank,
                properties: new Dictionary<string, string>
                {
                    ["operation"] = operation,
                    ["command"] = Convert.ToHexString(command.Span),
                    ["error"] = exception.Message
                });
        }
    }

    private static bool IsVersionResponse(ReadOnlySpan<byte> data) =>
        data.SequenceEqual(new byte[] { 0x00, 0x91 })
        || data.SequenceEqual(new byte[] { 0xFF })
        || data.SequenceEqual(new byte[] { 0x05, 0x91 })
        || data.SequenceEqual(new byte[] { 0x09, 0x01 })
        || data.SequenceEqual(new byte[] { 0x2B, 0x91 });

    private static bool IsVersionAccepted(ReadOnlySpan<byte> data) =>
        data.SequenceEqual(new byte[] { 0x00, 0x91 })
        || data.SequenceEqual(new byte[] { 0xFF });

    private static async Task WaitForAsync(
        NotificationInbox inbox,
        ReadOnlyMemory<byte> expected,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var response = await WaitForNotificationAsync(
                inbox,
                data => data.Span.SequenceEqual(expected.Span)
                        || data.Span.SequenceEqual(new byte[] { 0x68, 0x87 }),
                timeout,
                $"Single Bank response {Convert.ToHexString(expected.Span)}",
                cancellationToken)
            .ConfigureAwait(false);
        if (response.Span.SequenceEqual(new byte[] { 0x68, 0x87 }))
        {
            throw FirmwareError("Device returned Single Bank error 0x6887.");
        }
    }

    private static OtaException FirmwareError(string message) =>
        new(OtaState.ErrorFirmwareFeedback, message);
}
