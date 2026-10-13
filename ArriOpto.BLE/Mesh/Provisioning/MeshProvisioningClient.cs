using System.Security.Cryptography;
using ArriOpto.BLE.Mesh;

namespace ArriOpto.BLE.Mesh.Provisioning;

/// <summary>
/// PB-GATT 配网客户端（Mesh Protocol 1.1 §5.4）：驱动 Invite→Capabilities→
/// Start→公钥交换→Confirmation→Random→Data→Complete 完整状态机。
/// </summary>
/// <remarks>
/// 消息顺序与 Nordic 参考实现一致：Provisioner 先发公钥与确认值。
/// 链路上每条 Provisioning PDU 外层包 Proxy PDU（MessageType=0x03）。
/// </remarks>
public sealed class MeshProvisioningClient
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(10);

    private readonly IMeshLink _link;
    private TaskCompletionSource<byte[]>? _pduSource;

    public MeshProvisioningClient(IMeshLink link)
    {
        _link = link;
        _link.Received += OnReceived;
    }

    /// <summary>执行完整配网流程。</summary>
    /// <returns>配网结果（设备单播地址等）。</returns>
    public async Task<ProvisioningResult> ProvisionAsync(
        ProvisioningOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        using var session = new MeshProvisioningSession();

        // 1. Invite
        await SendAsync(ProvisioningPdu.CreateInvite(), cancellationToken);
        var capabilitiesPdu = await ReceiveAsync(
            ProvisioningPdu.OpcodeCapabilities,
            cancellationToken);
        if (!ProvisioningPdu.TryParseCapabilities(capabilitiesPdu, out var capabilities))
            throw new MeshProvisioningException("无法解析 Provisioning Capabilities。");
        if ((capabilities.SupportedAlgorithms & 0x0001) == 0)
            throw new MeshProvisioningException(
                "设备不支持 BTM_ECDH_P256_CMAC_AES128_AES_CCM 算法。");

        // 2. Start（No OOB）
        await SendAsync(ProvisioningPdu.CreateStart(), cancellationToken);

        // 3. Provisioner 先发公钥，再收设备公钥
        await SendAsync(
            ProvisioningPdu.CreatePublicKey(session.PublicKeyX, session.PublicKeyY),
            cancellationToken);
        var devicePublicKey = await ReceiveAsync(
            ProvisioningPdu.OpcodePublicKey,
            cancellationToken);
        if (!ProvisioningPdu.TryParsePublicKey(devicePublicKey, out var deviceX, out var deviceY))
            throw new MeshProvisioningException("无法解析设备公钥。");

        var ecdhSecret = session.ComputeEcdhSecret(deviceX, deviceY);

        // ConfirmationInputs = InviteValue || CapabilitiesValue || StartValue
        //                     || PublicKeyProvisioner || PublicKeyDevice
        var confirmationInputs = BuildConfirmationInputs(
            capabilitiesPdu,
            session.PublicKeyX,
            session.PublicKeyY,
            deviceX,
            deviceY);
        var randomProvisioner = RandomNumberGenerator.GetBytes(16);

        // 4. 发 Provisioner Confirmation
        var keys = MeshProvisioningSession.DeriveKeys(
            ecdhSecret,
            confirmationInputs,
            randomProvisioner,
            randomDevice: new byte[16]); // 设备随机数未知，先占位，收到后再校验
        await SendAsync(
            ProvisioningPdu.CreateConfirmation(keys.ProvisionerConfirmation),
            cancellationToken);

        // 5. 收设备 Confirmation
        var deviceConfirmationPdu = await ReceiveAsync(
            ProvisioningPdu.OpcodeConfirmation,
            cancellationToken);
        if (!ProvisioningPdu.TryParseConfirmation(deviceConfirmationPdu, out var deviceConfirmation))
            throw new MeshProvisioningException("无法解析设备确认值。");

        // 6. 发 Provisioner Random
        await SendAsync(ProvisioningPdu.CreateRandom(randomProvisioner), cancellationToken);

        // 7. 收设备 Random，并校验设备确认值
        var deviceRandomPdu = await ReceiveAsync(
            ProvisioningPdu.OpcodeRandom,
            cancellationToken);
        if (!ProvisioningPdu.TryParseRandom(deviceRandomPdu, out var deviceRandom))
            throw new MeshProvisioningException("无法解析设备随机数。");

        keys = MeshProvisioningSession.DeriveKeys(
            ecdhSecret,
            confirmationInputs,
            randomProvisioner,
            deviceRandom);
        if (!CryptographicOperations.FixedTimeEquals(deviceConfirmation, keys.DeviceConfirmation))
            throw new MeshProvisioningException("设备确认值校验失败，配网被中止。");

        // 8. 加密并发送配网数据
        var encryptedData = MeshProvisioningSession.EncryptProvisioningData(
            keys.SessionKey,
            keys.SessionNonce,
            options);
        await SendAsync(ProvisioningPdu.CreateData(encryptedData), cancellationToken);

        // 9. 收 Complete / Failed
        var final = await ReceivePduAsync(cancellationToken);
        if (ProvisioningPdu.IsComplete(final))
        {
            return new ProvisioningResult(
                options.DeviceUnicastAddress,
                keys.SessionKey,
                keys.DeviceKey);
        }

        if (ProvisioningPdu.TryParseFailed(final, out var errorCode))
            throw new MeshProvisioningException($"配网失败，设备返回错误码 0x{errorCode:X2}。");

        throw new MeshProvisioningException("配网流程收到未知消息。");
    }

    private Task SendAsync(
        ReadOnlySpan<byte> provisioningPdu,
        CancellationToken cancellationToken) =>
        SendAsyncCore(provisioningPdu.ToArray(), cancellationToken);

    private async Task SendAsyncCore(
        byte[] provisioningPdu,
        CancellationToken cancellationToken)
    {
        var proxyPdu = MeshProxyPdu.Create(
            MeshProxyPdu.MessageTypeProvisioning,
            provisioningPdu);
        await _link.WriteAsync(proxyPdu, cancellationToken);
    }

    private Task<byte[]> ReceiveAsync(byte opcode, CancellationToken cancellationToken) =>
        ReceivePduAsync(cancellationToken, opcode);

    private async Task<byte[]> ReceivePduAsync(
        CancellationToken cancellationToken,
        byte? expectedOpcode = null)
    {
        _pduSource = new TaskCompletionSource<byte[]>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(StepTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        using var registration = linked.Token.Register(() =>
            _pduSource.TrySetCanceled(linked.Token));
        try
        {
            var pdu = await _pduSource.Task;
            if (pdu.Length < 1)
                throw new MeshProvisioningException("收到空配网消息。");

            if (ProvisioningPdu.TryParseFailed(pdu, out var errorCode))
                throw new MeshProvisioningException($"设备返回配网失败错误码 0x{errorCode:X2}。");

            if (expectedOpcode is { } opcode && pdu[0] != opcode)
                throw new MeshProvisioningException(
                    $"配网流程收到意外 opcode 0x{pdu[0]:X2}（期望 0x{opcode:X2}）。");

            return pdu;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new MeshProvisioningException("配网步骤超时，请确认设备处于配网状态。");
        }
    }

    private void OnReceived(object? sender, byte[] pdu)
    {
        if (pdu.Length < 1)
            return;

        var (_, messageType) = MeshProxyPdu.ParseHeader(pdu[0]);
        if (messageType != MeshProxyPdu.MessageTypeProvisioning)
            return;

        // 去掉 Proxy 帧头，得到 Provisioning PDU
        _pduSource?.TrySetResult(pdu[1..]);
    }

    private static byte[] BuildConfirmationInputs(
        ReadOnlySpan<byte> capabilitiesPdu,
        ReadOnlySpan<byte> publicKeyProvisionerX,
        ReadOnlySpan<byte> publicKeyProvisionerY,
        ReadOnlySpan<byte> publicKeyDeviceX,
        ReadOnlySpan<byte> publicKeyDeviceY)
    {
        var inviteValue = new byte[] { 0x00 }; // Attention Duration
        var capabilitiesValue = capabilitiesPdu[1..].ToArray(); // 去掉 opcode
        var startValue = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00 }; // Algorithm/PubKey/Auth
        var provisionerPublicKey = new byte[64];
        publicKeyProvisionerX.CopyTo(provisionerPublicKey);
        publicKeyProvisionerY.CopyTo(provisionerPublicKey.AsSpan(32));
        var devicePublicKey = new byte[64];
        publicKeyDeviceX.CopyTo(devicePublicKey);
        publicKeyDeviceY.CopyTo(devicePublicKey.AsSpan(32));

        var inputs = new byte[
            inviteValue.Length
            + capabilitiesValue.Length
            + startValue.Length
            + provisionerPublicKey.Length
            + devicePublicKey.Length];
        var offset = 0;
        foreach (var part in new[]
                 {
                     inviteValue,
                     capabilitiesValue,
                     startValue,
                     provisionerPublicKey,
                     devicePublicKey
                 })
        {
            part.CopyTo(inputs.AsSpan(offset));
            offset += part.Length;
        }

        return inputs;
    }
}

/// <summary>配网结果。</summary>
public sealed record ProvisioningResult(
    ushort DeviceUnicastAddress,
    byte[] SessionKey,
    byte[] DeviceKey);

/// <summary>配网异常。</summary>
public sealed class MeshProvisioningException(string message)
    : Exception(message);
