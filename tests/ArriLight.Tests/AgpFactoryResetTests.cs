using ArriOpto.BLE.Agp;
using Xunit;

namespace ArriOpto.BLE.Tests;

public sealed class AgpFactoryResetTests
{
    // PW01 docs/AGP/SPEC.md section 19 and test-vectors.json pw01_factory_reset_*.
    [Fact]
    public void RegistryAndCapabilityMatchPw01()
    {
        Assert.Equal(0x0703, (ushort)AgpOpcode.FactoryReset);
        Assert.Equal(1UL << 23, (ulong)AgpCapabilities.FactoryReset);
        var capabilities = AgpModelCodec.DecodeCapabilities(AgpCodec.Decode(
            Hex("01 00 02 00 01 00 00 00 08 87 2A C0 00 00 00 00 00")));
        Assert.True(capabilities.HasFlag(AgpCapabilities.FactoryReset));
        Assert.False(((AgpCapabilities)0x402A87).HasFlag(AgpCapabilities.FactoryReset));
    }

    [Fact]
    public void EmptyRequestMatchesNormativeVector()
    {
        Assert.Equal(Hex("01 00 00 00 03 07 01 09 00"),
            AgpCodec.EncodeRequest(AgpOpcode.FactoryReset, 0x0901));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AgpCodec.EncodeRequest(AgpOpcode.FactoryReset, 0));
    }

    [Theory]
    [InlineData("01 00 01 00 03 07 01 09 01 00", AgpResultCode.Success)]
    [InlineData("01 00 01 00 03 07 00 09 01 08", AgpResultCode.NotAuthorized)]
    [InlineData("01 00 01 00 03 07 00 09 01 03", AgpResultCode.InvalidLength)]
    [InlineData("01 00 01 00 03 07 00 00 01 04", AgpResultCode.InvalidValue)]
    [InlineData("01 00 01 00 03 07 00 09 01 07", AgpResultCode.Busy)]
    [InlineData("01 00 01 00 03 07 00 09 01 0A", AgpResultCode.InternalError)]
    public void SingleByteResponseMatchesNormativeVectors(string packet, AgpResultCode result)
    {
        Assert.Equal(10, Hex(packet).Length);
        var response = AgpModelCodec.DecodeFactoryResetResponse(AgpCodec.Decode(Hex(packet)));
        Assert.Equal(result, response.Result);
        Assert.Equal(result == AgpResultCode.Success, response.IsAccepted);
    }

    [Theory]
    [InlineData("")]
    [InlineData("00 00")]
    [InlineData("00 01 00 00 00")]
    [InlineData("FF")]
    public void InvalidPayloadIsRejected(string payload)
    {
        var message = AgpCodec.Decode(AgpCodec.Encode(
            AgpMessageType.Response, AgpOpcode.FactoryReset, 1, Hex(payload)));
        Assert.Throws<AgpProtocolException>(() => AgpModelCodec.DecodeFactoryResetResponse(message));
    }

    [Fact]
    public void DecoderRequiresFactoryResetResponse()
    {
        var message = AgpCodec.Decode(Hex("01 00 01 00 03 07 01 09 01 00"));
        Assert.Throws<AgpProtocolException>(() => AgpModelCodec.DecodeFactoryResetResponse(
            message with { MessageType = AgpMessageType.Request }));
        Assert.Throws<AgpProtocolException>(() => AgpModelCodec.DecodeFactoryResetResponse(
            message with { MessageType = AgpMessageType.Status }));
        Assert.Throws<AgpProtocolException>(() => AgpModelCodec.DecodeFactoryResetResponse(
            message with { Opcode = AgpOpcode.AuthToken }));
    }

    [Fact]
    public async Task ClientSendsEmptyRequestAndReturnsAcceptanceWithoutExtraOperations()
    {
        var connection = new Connection();
        await using var client = new AgpClient(connection);
        connection.OnWrite = request =>
        {
            connection.Indicate(Hex("01 00 01 00 03 07 01 00 01 00"));
            return Task.CompletedTask;
        };

        var response = await client.FactoryResetAsync();

        Assert.True(response.IsAccepted);
        Assert.Equal(Hex("01 00 00 00 03 07 01 00 00"), Assert.Single(connection.Packets));
    }

    [Theory]
    [InlineData(AgpResultCode.UnknownOpcode)]
    [InlineData(AgpResultCode.UnsupportedFeature)]
    [InlineData(AgpResultCode.NotAuthorized)]
    [InlineData(AgpResultCode.Busy)]
    [InlineData(AgpResultCode.InternalError)]
    public async Task ClientPreservesRejectionWithoutRetry(AgpResultCode result)
    {
        var connection = new Connection();
        await using var client = new AgpClient(connection);
        connection.OnWrite = request =>
        {
            connection.Reply(request, [(byte)result]);
            return Task.CompletedTask;
        };

        var response = await client.FactoryResetAsync();

        Assert.Equal(result, response.Result);
        Assert.False(response.IsAccepted);
        Assert.Single(connection.Packets);
    }

    [Fact]
    public async Task OnlyMatchingOpcodeTidAndResponseTypeCompleteRequest()
    {
        var connection = new Connection();
        await using var client = new AgpClient(connection);
        var unmatched = new List<AgpMessage>();
        client.UnmatchedControlMessageReceived += (_, message) => unmatched.Add(message);

        var resetting = client.FactoryResetAsync();
        connection.Indicate(Hex("01 00 01 00 04 00 01 00 01 00"));
        connection.Indicate(Hex("01 00 01 00 03 07 02 00 01 00"));
        connection.Indicate(Hex("01 00 02 00 03 07 00 00 01 00"));

        Assert.False(resetting.IsCompleted);
        Assert.Equal(3, unmatched.Count);
        connection.Indicate(Hex("01 00 01 00 03 07 01 00 01 00"));
        Assert.True((await resetting.WaitAsync(TimeSpan.FromSeconds(5))).IsAccepted);
        Assert.Single(connection.Packets);
    }

    [Fact]
    public async Task CancelledBeforeSendingDoesNotWrite()
    {
        var connection = new Connection();
        await using var client = new AgpClient(connection);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.FactoryResetAsync(cancellation.Token));

        Assert.Empty(connection.Packets);
    }

    [Fact]
    public async Task GattWriteWithoutResponseIsNotAcceptanceAndCancellationDoesNotRetry()
    {
        var connection = new Connection();
        await using var client = new AgpClient(connection);
        using var cancellation = new CancellationTokenSource();

        var resetting = client.FactoryResetAsync(cancellation.Token);
        Assert.Single(connection.Packets);
        Assert.False(resetting.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resetting);

        Assert.Single(connection.Packets);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransportFailureIsNotSuccessAndDoesNotRetry(bool timeout)
    {
        var connection = new Connection();
        await using var client = new AgpClient(connection);
        var failure = timeout ? (Exception)new TimeoutException("GATT timeout") : new IOException("disconnected");
        connection.OnWrite = _ => Task.FromException(failure);

        var actual = await Record.ExceptionAsync(() => client.FactoryResetAsync());

        Assert.Same(failure, actual);
        Assert.Single(connection.Packets);
    }

    [Theory]
    [InlineData("")]
    [InlineData("00 00")]
    [InlineData("FF")]
    public async Task ClientRejectsMalformedMatchingResponseWithoutRetry(string payload)
    {
        var connection = new Connection();
        await using var client = new AgpClient(connection);
        connection.OnWrite = request =>
        {
            connection.Reply(request, Hex(payload));
            return Task.CompletedTask;
        };

        await Assert.ThrowsAsync<AgpProtocolException>(() => client.FactoryResetAsync());

        Assert.Single(connection.Packets);
    }

    [Fact]
    public async Task CommandFactoryDecodesTypedBatchAcceptance()
    {
        var connection = new Connection();
        await using var client = new AgpClient(connection);
        var command = AgpCommands.FactoryReset();
        connection.OnWrite = request =>
        {
            connection.Reply(request, [0]);
            return Task.CompletedTask;
        };

        var result = await client.ExecuteBatchAsync([command]);

        Assert.True(result.IsSuccess);
        Assert.True(Assert.Single(result.Steps).GetResponse(command).IsAccepted);
        Assert.Equal(Hex("01 00 00 00 03 07 01 00 00"), Assert.Single(connection.Packets));
    }

    [Fact]
    public async Task BatchBusyDoesNotRetryFactoryResetOrContinueToNextCommand()
    {
        var connection = new Connection();
        await using var client = new AgpClient(connection);
        connection.OnWrite = request =>
        {
            connection.Reply(request, [(byte)AgpResultCode.Busy]);
            return Task.CompletedTask;
        };

        var result = await client.ExecuteBatchAsync(
            [AgpCommands.FactoryReset(), AgpCommands.GetMode()],
            new AgpBatchOptions { MaxBusyRetries = 10 });

        Assert.False(result.IsSuccess);
        var step = Assert.Single(result.Steps);
        Assert.Equal(AgpResultCode.Busy, step.Result);
        Assert.Equal(1, step.Attempts);
        Assert.Single(connection.Packets);
    }

    private static byte[] Hex(string value) => Convert.FromHexString(value.Replace(" ", ""));

    private sealed class Connection : IAgpGattConnection
    {
        public string DeviceId => "factory-reset-test";
        public event EventHandler<AgpPacketEventArgs>? ControlPointIndicated;
        public event EventHandler<AgpPacketEventArgs>? LightStateNotified { add { } remove { } }
        public List<byte[]> Packets { get; } = [];
        public Func<AgpMessage, Task>? OnWrite { get; set; }
        public Task<ReadOnlyMemory<byte>> ReadCapabilitiesAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The command must not read capabilities implicitly.");
        public Task<ReadOnlyMemory<byte>> ReadLightStateAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Acceptance must not be confirmed by reading light state.");
        public Task WriteControlPointAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var copy = packet.ToArray();
            Packets.Add(copy);
            return OnWrite?.Invoke(AgpCodec.Decode(copy)) ?? Task.CompletedTask;
        }
        public void Reply(AgpMessage request, byte[] payload) =>
            Indicate(AgpCodec.Encode(AgpMessageType.Response, request.Opcode, request.TransactionId, payload));
        public void Indicate(byte[] packet) => ControlPointIndicated?.Invoke(this, new(packet));
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
