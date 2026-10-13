using System.Security.Cryptography;
using ArriOpto.BLE.Ota;
using ArriOpto.BLE.Protocol;

namespace ArriOpto.BLE.Ota.Protocol;

/// <summary>
/// 在一个已订阅通知的 SLB GATT 连接上执行完整 OTA 状态机。
/// </summary>
/// <remarks>
/// 会话实例只能运行一次且不能并发使用。它负责可选 AES 认证、版本/Product ID
/// 校验、断点 offset、窗口传输和最终 CRC 校验，不负责扫描或连接设备。
/// </remarks>
public sealed class SlbOtaSession : OtaSessionBase
{
    private const byte RequestFirmwareVersion = 0x20;
    private const byte ResponseFirmwareVersion = 0x21;
    private const byte RequestUpgrade = 0x22;
    private const byte ResponseUpgrade = 0x23;
    private const byte ResponseDataCheck = 0x24;
    private const byte NotifyTransferComplete = 0x25;
    private const byte ResponseVerification = 0x26;
    private const byte SecurityOne = 0x27;
    private const byte SecurityTwo = 0x28;
    private const byte SecurityThree = 0x29;
    private const byte SecurityFour = 0x2A;
    private const byte SecurityFive = 0x2B;
    private const byte SecuritySix = 0x2C;
    private const byte FirmwarePacket = 0x2F;

    private readonly SlbFirmwareImage _firmware;
    private readonly SlbFrameEncoder _encoder = new();

    /// <summary>创建一个与特定连接和固件绑定的 SLB 会话。</summary>
    public SlbOtaSession(
        IOtaGattConnection connection,
        SlbFirmwareImage firmware,
        OtaOptions? options = null,
        IProgress<OtaProgress>? progress = null)
        : base(connection, options ?? new OtaOptions(), progress)
    {
        _firmware = firmware ?? throw new ArgumentNullException(nameof(firmware));
    }

    /// <summary>
    /// 执行完整 SLB 升级，直到设备确认固件校验成功。
    /// </summary>
    /// <exception cref="OtaException">设备模式、固件匹配或协议反馈错误。</exception>
    /// <exception cref="TimeoutException">等待目标通知超时。</exception>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (Connection.Mode != OtaGattMode.Slb)
        {
            throw new OtaException(
                OtaState.ErrorFileMismatch,
                $"SLB firmware cannot be transferred through a {Connection.Mode} connection.");
        }

        using var inbox = new NotificationInbox(Connection);
        Report(OtaState.SlbReady, "SLB connection is ready.");
        Diagnostic(
            OtaDiagnosticLevel.Information,
            "slb.session.start",
            "slb",
            "SLB 状态机开始。",
            OtaProtocol.Slb,
            OtaState.SlbReady,
            properties: new Dictionary<string, string>
            {
                ["firmwareLength"] = _firmware.Length.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                ["isSecure"] = _firmware.IsSecure.ToString(),
                ["isResource"] = (_firmware.ResourceAddress is not null).ToString()
            });

        if (_firmware.IsSecure)
        {
            await AuthenticateAsync(inbox, cancellationToken).ConfigureAwait(false);
        }

        Report(OtaState.FeedbackBootVersion, "Requesting device firmware version.");
        await SendCommandAsync(RequestFirmwareVersion, new byte[] { 0x00 }, cancellationToken)
            .ConfigureAwait(false);
        var versionFrame = await WaitForFrameAsync(
                inbox,
                ResponseFirmwareVersion,
                Options.CommandTimeout,
                cancellationToken)
            .ConfigureAwait(false);

        var upgradePayload = BuildUpgradeRequest(versionFrame.Payload.Span);
        await SendCommandAsync(RequestUpgrade, upgradePayload, cancellationToken)
            .ConfigureAwait(false);
        var upgradeResponse = await WaitForFrameAsync(
                inbox,
                ResponseUpgrade,
                Options.CommandTimeout,
                cancellationToken)
            .ConfigureAwait(false);

        var responsePayload = upgradeResponse.Payload.Span;
        if (responsePayload.Length < 6)
        {
            throw FirmwareError("SLB 0x23 response is shorter than six bytes.");
        }

        if (responsePayload[0] != 1)
        {
            throw FirmwareError("The device rejected the SLB upgrade request.");
        }

        // 只有 bootloader 返回的 offset 才代表已经可靠写入的字节。即使客户端之前
        // 发送过更多数据，也必须从该位置恢复。
        var confirmedOffset = BinaryCodec.ReadUInt32LittleEndian(responsePayload[1..5]);
        if (confirmedOffset > _firmware.Data.Length)
        {
            throw FirmwareError("The device returned an invalid SLB resume offset.");
        }

        // 低四位编码“窗口帧数 - 1”，因此设备可请求 1～16 帧。
        var windowSize = (responsePayload[5] & 0x0F) + 1;
        Diagnostic(
            OtaDiagnosticLevel.Information,
            "slb.resume.accepted",
            "slb",
            "设备接受升级请求并返回续传位置。",
            OtaProtocol.Slb,
            offset: confirmedOffset,
            properties: new Dictionary<string, string>
            {
                ["windowSize"] = windowSize.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)
            });
        await TransferAsync(inbox, checked((int)confirmedOffset), windowSize, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await SendCommandAsync(NotifyTransferComplete, new byte[] { 0x01 }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IOException exception) when (IsGatt133(exception))
        {
            // Some SLB bootloaders reset as soon as they accept 0x25. Android then
            // reports GATT 133 even though all data acknowledgements were received.
            ReportUpgradeComplete();
            Diagnostic(
                OtaDiagnosticLevel.Warning,
                "slb.expected-disconnect",
                "slb",
                "The device disconnected immediately after the final SLB verification command.",
                OtaProtocol.Slb,
                OtaState.UpgradeCompleteWaitingDisconnect,
                _firmware.Data.Length,
                properties: new Dictionary<string, string>
                {
                    ["command"] = "25",
                    ["error"] = exception.Message
                });
            return;
        }
        var verification = await WaitForFrameAsync(
                inbox,
                ResponseVerification,
                Options.TransferAcknowledgementTimeout,
                cancellationToken)
            .ConfigureAwait(false);

        if (verification.Payload.Length < 1 || verification.Payload.Span[0] != 1)
        {
            throw FirmwareError("The device reported an SLB firmware verification failure.");
        }

        ReportUpgradeComplete();
        Diagnostic(
            OtaDiagnosticLevel.Information,
            "slb.session.complete",
            "slb",
            "设备确认 SLB 固件校验成功。",
            OtaProtocol.Slb,
            OtaState.UpgradeCompleteWaitingDisconnect,
            _firmware.Data.Length);
    }

    private async Task AuthenticateAsync(
        NotificationInbox inbox,
        CancellationToken cancellationToken)
    {
        if (Options.AesKeyHex is null)
        {
            throw new OtaException(
                OtaState.ErrorFileMismatch,
                "Secure SLB firmware requires a 16-byte AES key.");
        }

        var key = HexCodec.Parse(Options.AesKeyHex);
        var applicationRandom = AesAuthenticator.CreateRandomBlock();
        var encryptedApplicationRandom = AesAuthenticator.EncryptBlock(applicationRandom, key);

        Report(OtaState.SlbReady, "Starting secure SLB authentication.");
        Diagnostic(
            OtaDiagnosticLevel.Information,
            "slb.authentication.start",
            "slb",
            "开始 SLB AES challenge-response。",
            OtaProtocol.Slb);
        await SendCommandAsync(SecurityOne, encryptedApplicationRandom, cancellationToken)
            .ConfigureAwait(false);
        var second = await WaitForFrameAsync(
                inbox,
                SecurityTwo,
                Options.CommandTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureBlock(second.Payload.Span, SecurityTwo);
        var encryptedFirmwareRandom = second.Payload.ToArray();

        await SendCommandAsync(SecurityThree, applicationRandom, cancellationToken)
            .ConfigureAwait(false);
        var fourth = await WaitForFrameAsync(
                inbox,
                SecurityFour,
                Options.CommandTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureBlock(fourth.Payload.Span, SecurityFour);

        var firmwareRandom = AesAuthenticator.DecryptBlock(encryptedFirmwareRandom, key);
        if (!CryptographicOperations.FixedTimeEquals(firmwareRandom, fourth.Payload.Span))
        {
            throw FirmwareError("Secure SLB authentication challenge did not match.");
        }

        var firstConfirmation = AesAuthenticator.EncryptBlock(firmwareRandom, applicationRandom);
        var confirmation = AesAuthenticator.EncryptBlock(firstConfirmation, key);
        await SendCommandAsync(SecurityFive, confirmation, cancellationToken).ConfigureAwait(false);
        await WaitForFrameAsync(
                inbox,
                SecuritySix,
                Options.CommandTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        Report(OtaState.SlbReady, "Secure SLB authentication succeeded.");
        Diagnostic(
            OtaDiagnosticLevel.Information,
            "slb.authentication.complete",
            "slb",
            "SLB AES challenge-response 通过。",
            OtaProtocol.Slb);
    }

    private byte[] BuildUpgradeRequest(ReadOnlySpan<byte> versionPayload)
    {
        if (versionPayload.Length != 5 && versionPayload.Length < 9)
        {
            throw FirmwareError(
                $"Unsupported SLB 0x21 payload length: {versionPayload.Length}.");
        }

        var legacy = versionPayload.Length == 5;
        if (!legacy && _firmware.Metadata.IsPresent)
        {
            var deviceProductId = Convert.ToHexString([versionPayload[2], versionPayload[1]]);
            if (Options.ValidateProductId
                && !string.Equals(
                    deviceProductId,
                    _firmware.Metadata.ProductId,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new OtaException(
                    OtaState.ErrorFileMismatch,
                    $"Firmware Product ID {_firmware.Metadata.ProductId} does not match device Product ID {deviceProductId}.");
            }
        }
        else if (!legacy && !_firmware.Metadata.IsPresent && _firmware.ResourceAddress is null)
        {
            throw new OtaException(
                OtaState.ErrorFileMismatch,
                "The device requires new-format Product ID metadata, but the selected firmware does not contain it.");
        }

        var payload = new byte[legacy ? 12 : 16];
        if (_firmware.ResourceAddress is not null)
        {
            payload[0] = 1;
            HexCodec.Parse(_firmware.ResourceAddress).CopyTo(payload, 1);
        }
        else
        {
            payload[0] = 0;
            if (!legacy)
            {
                versionPayload[1..3].CopyTo(payload.AsSpan(1, 2));
                HexCodec.Parse(_firmware.Metadata.BootVersion!).CopyTo(payload, 3);
            }
        }

        var sizeOffset = legacy ? 5 : 9;
        BinaryCodec.WriteUInt32LittleEndian(
            payload.AsSpan(sizeOffset),
            checked((uint)_firmware.Data.Length));
        BinaryCodec.WriteUInt16LittleEndian(
            payload.AsSpan(sizeOffset + 4),
            CrcAlgorithms.Crc16Ccitt(_firmware.Data.Span));
        payload[sizeOffset + 6] = 0; // Full upgrade.
        return payload;
    }

    private async Task TransferAsync(
        NotificationInbox inbox,
        int initialOffset,
        int requestedWindowSize,
        CancellationToken cancellationToken)
    {
        // ATT Write 占 3 字节，SLB 自身帧头再占 4 字节。
        var payloadCapacity = Connection.Mtu - 3 - SlbFrame.HeaderLength;
        if (payloadCapacity <= 0)
        {
            throw new OtaException(OtaState.ErrorMtuChange, "Negotiated MTU is too small.");
        }

        var confirmed = initialOffset;
        var stalledAcknowledgements = 0;
        while (confirmed < _firmware.Data.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remainingBytes = _firmware.Data.Length - confirmed;
            var framesInWindow = Math.Min(
                requestedWindowSize,
                (remainingBytes + payloadCapacity - 1) / payloadCapacity);

            var sendOffset = confirmed;
            for (var frameIndex = 0; frameIndex < framesInWindow; frameIndex++)
            {
                var count = Math.Min(payloadCapacity, _firmware.Data.Length - sendOffset);
                var data = _firmware.Data.Slice(sendOffset, count).Span;
                var frame = _encoder.Encode(
                    FirmwarePacket,
                    data,
                    Connection.Mtu,
                    framesInWindow,
                    frameIndex);
                await Connection.WriteDataAsync(frame, cancellationToken).ConfigureAwait(false);
                sendOffset += count;
            }

            var acknowledgement = await WaitForFrameAsync(
                    inbox,
                    ResponseDataCheck,
                    Options.TransferAcknowledgementTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            if (acknowledgement.Payload.Length < 5)
            {
                throw FirmwareError("SLB 0x24 response is shorter than five bytes.");
            }

            var newConfirmed = checked((int)BinaryCodec.ReadUInt32LittleEndian(
                acknowledgement.Payload.Span[1..5]));
            if (newConfirmed < 0 || newConfirmed > _firmware.Data.Length)
            {
                throw FirmwareError("The device acknowledged an invalid SLB byte offset.");
            }

            // 允许设备要求重发，但连续三次没有前进通常表示 flash 写入或协议状态
            // 已经异常，继续无限循环只会占用连接。
            if (newConfirmed == confirmed)
            {
                stalledAcknowledgements++;
                if (stalledAcknowledgements >= 3)
                {
                    throw FirmwareError("SLB transfer made no progress after three retries.");
                }
            }
            else
            {
                stalledAcknowledgements = 0;
            }

            confirmed = newConfirmed;
            Diagnostic(
                OtaDiagnosticLevel.Debug,
                "slb.offset.confirmed",
                "slb",
                "设备确认 SLB 数据 offset。",
                OtaProtocol.Slb,
                OtaState.FeedbackProgress,
                confirmed,
                properties: new Dictionary<string, string>
                {
                    ["windowFrames"] = framesInWindow.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)
                });
            Report(
                OtaState.FeedbackProgress,
                "Transferring SLB firmware.",
                confirmed,
                _firmware.Data.Length);
        }
    }

    private void ReportUpgradeComplete() =>
        Report(
            OtaState.UpgradeCompleteWaitingDisconnect,
            "SLB upgrade completed; waiting for the device to restart.",
            _firmware.Data.Length,
            _firmware.Data.Length);

    private async Task SendCommandAsync(
        byte opcode,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        var frame = _encoder.Encode(opcode, payload.Span, Connection.Mtu);
        await Connection.WriteCommandAsync(frame, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<SlbFrame> WaitForFrameAsync(
        NotificationInbox inbox,
        byte opcode,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var message = await WaitForNotificationAsync(
                inbox,
                data =>
                {
                    try
                    {
                        return SlbFrame.Parse(data).Opcode == opcode;
                    }
                    catch (InvalidDataException)
                    {
                        return false;
                    }
                },
                timeout,
                $"SLB response 0x{opcode:X2}",
                cancellationToken)
            .ConfigureAwait(false);
        return SlbFrame.Parse(message);
    }

    private static void EnsureBlock(ReadOnlySpan<byte> payload, byte opcode)
    {
        if (payload.Length != 16)
        {
            throw FirmwareError(
                $"Secure SLB response 0x{opcode:X2} must contain a 16-byte block.");
        }
    }

    private static OtaException FirmwareError(string message) =>
        new(OtaState.ErrorFirmwareFeedback, message);
}
