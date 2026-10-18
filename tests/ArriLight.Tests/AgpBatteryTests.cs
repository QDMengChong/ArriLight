using ArriOpto.BLE.Agp;
using Xunit;

namespace ArriLight.Tests;

public sealed class AgpBatteryTests
{
    // PW01 docs/AGP/test-vectors.json: pw01_battery_80/empty/full/unknown.
    [Theory]
    [InlineData("B0 04", "50", 80)]
    [InlineData("B1 04", "00", 0)]
    [InlineData("B2 04", "64", 100)]
    [InlineData("B3 04", "FF", null)]
    public void DecodesPw01BatteryVectors(string tid, string level, int? percent)
    {
        var message = AgpCodec.Decode(Hex($"01 00 01 00 01 07 {tid} 09 00 {level} FF FF FF FF FF FF FF"));
        var response = AgpModelCodec.DecodeBatteryResponse(message);

        Assert.Equal(AgpResultCode.Success, response.Result);
        var state = Assert.IsType<AgpBatteryState>(response.State);
        Assert.Equal(Hex(level)[0], state.Level);
        Assert.Equal(percent, state.LevelPercent is { } value ? (int?)value : null);
        Assert.Equal(AgpBatteryState.UnknownTimeMinutes, state.TimeToDischargeMinutes);
        Assert.Equal(AgpBatteryState.UnknownTimeMinutes, state.TimeToChargeMinutes);
        Assert.Null(state.KnownTimeToDischargeMinutes);
        Assert.Null(state.KnownTimeToChargeMinutes);
        Assert.Equal(AgpBatteryState.UnknownFlags, state.Flags);
        Assert.Equal(new AgpBatteryFlags(0xFF), state.ParsedFlags);
        Assert.Equal(3, state.ParsedFlags.Presence);
        Assert.Equal(3, state.ParsedFlags.Indicator);
        Assert.Equal(3, state.ParsedFlags.Charging);
        Assert.Equal(3, state.ParsedFlags.Serviceability);
    }

    [Fact]
    public void EncodesExactEmptyRequestAndRecognizesBatteryCapability()
    {
        Assert.Equal(Hex("01 00 00 00 01 07 B0 04 00"),
            AgpCodec.EncodeRequest(AgpOpcode.BatteryGet, 0x04B0));
        var capabilities = AgpModelCodec.DecodeCapabilities(AgpCodec.Decode(
            Hex("01 00 02 00 01 00 00 00 08 00 08 00 00 00 00 00 00")));
        Assert.Equal(AgpCapabilities.Battery, capabilities);
    }

    [Fact]
    public void DecodesFullWidthLittleEndianTimesAndIndependentFlagFields()
    {
        var state = Decode("00 2A 56 34 12 EF CD AB E4").State!;
        Assert.Equal<byte?>(42, state.LevelPercent);
        Assert.Equal(0x123456u, state.TimeToDischargeMinutes);
        Assert.Equal(0xABCDEFu, state.TimeToChargeMinutes);
        Assert.Equal(0x123456u, state.KnownTimeToDischargeMinutes);
        Assert.Equal(0xABCDEFu, state.KnownTimeToChargeMinutes);
        Assert.Equal(0, state.ParsedFlags.Presence);
        Assert.Equal(1, state.ParsedFlags.Indicator);
        Assert.Equal(2, state.ParsedFlags.Charging);
        Assert.Equal(3, state.ParsedFlags.Serviceability);

        var boundaries = Decode("00 50 00 00 00 FE FF FF 6A").State!;
        Assert.Equal(0u, boundaries.KnownTimeToDischargeMinutes);
        Assert.Equal(0xFFFFFEu, boundaries.KnownTimeToChargeMinutes);
        Assert.Equal(1, boundaries.ParsedFlags.Serviceability);
        Assert.Equal(2, Decode("00 50 FF FF FF FF FF FF AA").State!.ParsedFlags.Serviceability);
    }

    [Theory]
    [InlineData(AgpResultCode.UnknownOpcode)]
    [InlineData(AgpResultCode.InvalidLength)]
    [InlineData(AgpResultCode.UnsupportedFeature)]
    [InlineData(AgpResultCode.Busy)]
    [InlineData(AgpResultCode.NotAuthorized)]
    [InlineData(AgpResultCode.InternalError)]
    public void ResultOnlyFailureHasNoBatteryState(AgpResultCode result)
    {
        // Includes PW01's pw01_battery_invalid_payload response (result=3).
        var message = AgpCodec.Decode(Hex($"01 00 01 00 01 07 B4 04 01 {(byte)result:X2}"));
        Assert.Equal(new AgpBatteryResponse(result, null), AgpModelCodec.DecodeBatteryResponse(message));
    }

    [Theory]
    [InlineData("", AgpResultCode.InvalidLength)]
    [InlineData("00", AgpResultCode.InvalidLength)]
    [InlineData("00 50 FF FF FF FF FF FF", AgpResultCode.InvalidLength)]
    [InlineData("00 50 FF FF FF FF FF FF FF 00", AgpResultCode.InvalidLength)]
    [InlineData("07 50 FF FF FF FF FF FF FF", AgpResultCode.InvalidLength)]
    [InlineData("03 00", AgpResultCode.InvalidLength)]
    [InlineData("00 65 FF FF FF FF FF FF FF", AgpResultCode.InvalidValue)]
    [InlineData("00 FE FF FF FF FF FF FF FF", AgpResultCode.InvalidValue)]
    [InlineData("00 50 FF FF FF FF FF FF 00", AgpResultCode.InvalidValue)]
    [InlineData("00 50 FF FF FF FF FF FF 3F", AgpResultCode.InvalidValue)]
    [InlineData("FF", AgpResultCode.InvalidValue)]
    public void RejectsMalformedBatteryPayload(string payload, AgpResultCode expected)
    {
        Assert.Equal(expected, Assert.Throws<AgpProtocolException>(() => Decode(payload)).ResultCode);
    }

    [Theory]
    [InlineData(AgpMessageType.Request, AgpOpcode.BatteryGet, 1)]
    [InlineData(AgpMessageType.Status, AgpOpcode.BatteryGet, 1)]
    [InlineData(AgpMessageType.Response, AgpOpcode.ModeGet, 1)]
    [InlineData(AgpMessageType.Response, AgpOpcode.BatteryGet, 0)]
    public void RejectsWrongResponseIdentity(AgpMessageType type, AgpOpcode opcode, ushort tid)
    {
        var message = AgpCodec.Decode(AgpCodec.Encode(type, opcode, tid, Hex("00 50 FF FF FF FF FF FF FF")));
        Assert.Equal(AgpResultCode.InvalidValue, Assert.Throws<AgpProtocolException>(() =>
            AgpModelCodec.DecodeBatteryResponse(message)).ResultCode);
    }

    [Fact]
    public async Task ClientQueriesWithoutAuthenticationAndMatchesOpcodeAndTid()
    {
        var connection = new BatteryConnection();
        await using var client = new AgpClient(connection);
        var unmatched = new List<AgpMessage>();
        client.UnmatchedControlMessageReceived += (_, message) => unmatched.Add(message);
        connection.PacketWritten = request =>
        {
            Assert.Equal(Hex("01 00 00 00 01 07 01 00 00"), connection.Writes.Single());
            connection.Indicate(Hex("01 00 01 00 22 01 01 00 02 00 00"));
            connection.Indicate(Hex("01 00 01 00 01 07 02 00 09 00 00 FF FF FF FF FF FF FF"));
            connection.Indicate(Hex("01 00 01 00 01 07 01 00 09 00 50 FF FF FF FF FF FF FF"));
        };

        var response = await client.GetBatteryAsync();
        Assert.Equal<byte?>(80, response.State!.LevelPercent);
        Assert.Equal(2, unmatched.Count);
        Assert.Single(connection.Writes);
    }

    [Theory]
    [InlineData(AgpResultCode.UnknownOpcode)]
    [InlineData(AgpResultCode.Busy)]
    [InlineData(AgpResultCode.NotAuthorized)]
    public async Task ClientPreservesFailureWithoutRetry(AgpResultCode result)
    {
        var connection = new BatteryConnection();
        await using var client = new AgpClient(connection);
        connection.PacketWritten = request => connection.Respond(request, [(byte)result]);
        Assert.Equal(new AgpBatteryResponse(result, null), await client.GetBatteryAsync());
        Assert.Single(connection.Writes);
    }

    [Fact]
    public async Task ConcurrentQueriesAreSerialAndReadFreshSnapshots()
    {
        var connection = new BatteryConnection();
        await using var client = new AgpClient(connection);
        var first = client.GetBatteryAsync();
        var second = client.GetBatteryAsync();
        Assert.Single(connection.Writes);
        connection.Respond(AgpCodec.Decode(connection.Writes[0]), Hex("00 50 FF FF FF FF FF FF FF"));
        Assert.Equal<byte?>(80, (await first).State!.LevelPercent);

        // Drain the second queued query without relying on real-time delays.
        var secondRequest = await connection.SecondWrite.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Hex("01 00 00 00 01 07 02 00 00"), connection.Writes[1]);
        connection.Respond(secondRequest, Hex("00 4F FF FF FF FF FF FF FF"));
        Assert.Equal<byte?>(79, (await second).State!.LevelPercent);
        Assert.Equal(2, connection.Writes.Count);
    }

    [Fact]
    public async Task CancelledQueryDoesNotWrite()
    {
        var connection = new BatteryConnection();
        await using var client = new AgpClient(connection);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetBatteryAsync(cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Empty(connection.Writes);
    }

    [Fact]
    public async Task BatchBatteryCommandRetriesBusyAndReturnsTypedState()
    {
        var connection = new BatteryConnection();
        await using var client = new AgpClient(connection);
        var command = AgpCommands.GetBattery();
        Assert.Equal(AgpOpcode.BatteryGet, command.Opcode);
        connection.PacketWritten = request =>
        {
            Assert.Equal(AgpOpcode.BatteryGet, request.Opcode);
            Assert.Empty(request.Payload.ToArray());
            connection.Respond(request, connection.Writes.Count == 1 ? Hex("07") : Hex("00 50 FF FF FF FF FF FF FF"));
        };

        var result = await client.ExecuteBatchAsync([command], new AgpBatchOptions
        {
            InitialBusyRetryDelay = TimeSpan.Zero,
            MaximumBusyRetryDelay = TimeSpan.Zero
        });
        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Steps[0].Attempts);
        Assert.Equal<byte?>(80, result.Steps[0].GetResponse(command).State!.LevelPercent);
        Assert.Equal(Hex("01 00 00 00 01 07 01 00 00"), connection.Writes[0]);
        Assert.Equal(Hex("01 00 00 00 01 07 02 00 00"), connection.Writes[1]);
    }

    [Fact]
    public async Task MalformedBatteryResponseFailsClientAndBatch()
    {
        var connection = new BatteryConnection();
        await using var client = new AgpClient(connection);
        connection.PacketWritten = request => connection.Respond(request, Hex("00"));
        Assert.Equal(AgpResultCode.InvalidLength, (await Assert.ThrowsAsync<AgpProtocolException>(() =>
            client.GetBatteryAsync())).ResultCode);
        var batchError = await Assert.ThrowsAsync<AgpBatchException>(() =>
            client.ExecuteBatchAsync([AgpCommands.GetBattery()]));
        Assert.IsType<AgpProtocolException>(batchError.InnerException);
        Assert.False(batchError.Progress.IsSuccess);
        Assert.Equal(0, batchError.Progress.CompletedCount);
        Assert.Equal(2, connection.Writes.Count);
    }

    private static AgpBatteryResponse Decode(string payload) => AgpModelCodec.DecodeBatteryResponse(
        AgpCodec.Decode(AgpCodec.Encode(AgpMessageType.Response, AgpOpcode.BatteryGet, 0x04B0, Hex(payload))));

    private static byte[] Hex(string value) => Convert.FromHexString(value.Replace(" ", ""));

    private sealed class BatteryConnection : IAgpGattConnection
    {
        public event EventHandler<AgpPacketEventArgs>? ControlPointIndicated;
        public event EventHandler<AgpPacketEventArgs>? LightStateNotified { add { } remove { } }
        public string DeviceId => "battery-test";
        public List<byte[]> Writes { get; } = [];
        public Action<AgpMessage>? PacketWritten { get; set; }
        public TaskCompletionSource<AgpMessage> SecondWrite { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WriteControlPointAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Writes.Add(packet.ToArray());
            var request = AgpCodec.Decode(packet.Span);
            if (Writes.Count == 2)
                SecondWrite.TrySetResult(request);
            PacketWritten?.Invoke(request);
            return Task.CompletedTask;
        }

        public void Respond(AgpMessage request, byte[] payload) =>
            Indicate(AgpCodec.Encode(AgpMessageType.Response, request.Opcode, request.TransactionId, payload));

        public void Indicate(byte[] packet) => ControlPointIndicated?.Invoke(this, new AgpPacketEventArgs(packet));
        public Task<ReadOnlyMemory<byte>> ReadCapabilitiesAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<ReadOnlyMemory<byte>> ReadLightStateAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
