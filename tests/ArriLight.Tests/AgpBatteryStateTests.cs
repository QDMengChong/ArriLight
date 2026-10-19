using ArriOpto.BLE.Agp;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
using Xunit;

namespace ArriLight.Tests;

public sealed class AgpBatteryStateTests
{
    private const string Status80 = "01 00 02 00 02 07 00 00 08 50 FF FF FF FF FF FF FF";

    // PW01 test-vectors.json: pw01_battery_status_80 and pw01_battery_status_unknown.
    [Theory]
    [InlineData(Status80, 80)]
    [InlineData("01 00 02 00 02 07 00 00 08 FF FF FF FF FF FF FF FF", null)]
    [InlineData("01 00 02 00 02 07 00 00 08 00 FF FF FF FF FF FF FF", 0)]
    [InlineData("01 00 02 00 02 07 00 00 08 64 FF FF FF FF FF FF FF", 100)]
    public void DecodesBatteryStatusWithoutResultPrefix(string packet, int? percent)
    {
        var state = AgpModelCodec.DecodeBatteryStatus(AgpCodec.Decode(Hex(packet)));
        Assert.Equal(percent, (int?)state.LevelPercent);
        Assert.Null(state.KnownTimeToDischargeMinutes);
        Assert.Null(state.KnownTimeToChargeMinutes);
        Assert.Equal(AgpBatteryState.UnknownFlags, state.Flags);
        Assert.Equal(Guid.Parse("8f092406-c433-4408-870f-fc60f806f9d7"), AgpProfile.BatteryStateCharacteristicId);
    }

    [Fact]
    public void StatusAndQueryShareTheSameFieldSemantics()
    {
        var status = AgpModelCodec.DecodeBatteryStatus(AgpCodec.Decode(
            Hex("01 00 02 00 02 07 00 00 08 2A 56 34 12 EF CD AB E4")));
        var response = AgpModelCodec.DecodeBatteryResponse(AgpCodec.Decode(
            Hex("01 00 01 00 01 07 B0 04 09 00 2A 56 34 12 EF CD AB E4")));
        Assert.Equal(new AgpBatteryState(42, 0x123456, 0xABCDEF, 0xE4), status);
        Assert.Equal(status, response.State);
    }

    [Theory]
    [InlineData("01 00 00 00 02 07 00 00 08 50 FF FF FF FF FF FF FF", AgpResultCode.InvalidValue)]
    [InlineData("01 00 01 00 02 07 00 00 08 50 FF FF FF FF FF FF FF", AgpResultCode.InvalidValue)]
    [InlineData("01 00 02 00 01 07 00 00 08 50 FF FF FF FF FF FF FF", AgpResultCode.InvalidValue)]
    [InlineData("01 00 02 00 02 07 01 00 08 50 FF FF FF FF FF FF FF", AgpResultCode.InvalidValue)]
    [InlineData("01 00 02 00 02 07 00 00 07 50 FF FF FF FF FF FF", AgpResultCode.InvalidLength)]
    [InlineData("01 00 02 00 02 07 00 00 09 00 50 FF FF FF FF FF FF FF", AgpResultCode.InvalidLength)]
    [InlineData("01 00 02 00 02 07 00 00 08 65 FF FF FF FF FF FF FF", AgpResultCode.InvalidValue)]
    [InlineData("01 00 02 00 02 07 00 00 08 FE FF FF FF FF FF FF FF", AgpResultCode.InvalidValue)]
    [InlineData("01 00 02 00 02 07 00 00 08 50 FF FF FF FF FF FF 3F", AgpResultCode.InvalidValue)]
    [InlineData("02 00 02 00 02 07 00 00 08 50 FF FF FF FF FF FF FF", AgpResultCode.UnsupportedVersion)]
    [InlineData("01 00 02 01 02 07 00 00 08 50 FF FF FF FF FF FF FF", AgpResultCode.InvalidValue)]
    [InlineData("01 00 02 00 02 07 00 00 08 50", AgpResultCode.InvalidLength)]
    public void RejectsMalformedStatus(string packet, AgpResultCode result)
    {
        Assert.Equal(result, Assert.Throws<AgpProtocolException>(() =>
            AgpModelCodec.DecodeBatteryStatus(AgpCodec.Decode(Hex(packet)))).ResultCode);
    }

    [Fact]
    public async Task ReadsAndNotifiesThroughTheDiscoveredBatteryCharacteristic()
    {
        var radio = new BatteryGatt();
        var connection = await PluginBleAgpConnection.OpenAsync(radio.Adapter, radio.Device);
        await using var client = new AgpClient(connection);
        var changes = new List<AgpBatteryState>();
        var rawNotifications = new List<AgpPacketEventArgs>();
        client.BatteryStateChanged += (_, state) => changes.Add(state);
        connection.BatteryStateNotified += (_, packet) => rawNotifications.Add(packet);

        Assert.True(connection.HasBatteryStateCharacteristic);
        Assert.True(client.HasBatteryStateCharacteristic);
        Assert.False(client.HasColorStateCharacteristic);
        Assert.Equal(new[] { AgpProfile.ControlPointCharacteristicId, AgpProfile.LightStateCharacteristicId,
            AgpProfile.BatteryStateCharacteristicId }, radio.Starts);
        var first = await client.ReadBatteryStateAsync();
        Assert.Equal<byte?>(80, first.LevelPercent);
        Assert.Equal(new[] { AgpProfile.BatteryStateCharacteristicId }, radio.Reads);
        Assert.Empty(changes); // Read returns a snapshot; only Notify raises the event.

        var packet = Hex("01 00 02 00 02 07 00 00 08 FF FF FF FF FF FF FF FF");
        radio.NotifyBattery(packet);
        packet[9] = 0; // The connection owns a copy of callback data.
        Assert.Null(Assert.Single(changes).LevelPercent);
        Assert.Equal(0xFF, Assert.Single(rawNotifications).Data.Span[9]);
        Assert.Empty(radio.Writes);
    }

    [Fact]
    public async Task BatteryNotifyDoesNotCompleteAnInFlightQueryOrRaiseLightEvents()
    {
        var radio = new BatteryGatt { AutoRespond = false };
        var connection = await PluginBleAgpConnection.OpenAsync(radio.Adapter, radio.Device);
        await using var client = new AgpClient(connection);
        var batteryEvents = 0;
        var lightEvents = 0;
        var unmatched = 0;
        client.BatteryStateChanged += (_, _) => batteryEvents++;
        client.LightStateChanged += (_, _) => lightEvents++;
        client.UnmatchedControlMessageReceived += (_, _) => unmatched++;
        var query = client.GetBatteryAsync();
        radio.NotifyBattery(Hex(Status80));
        Assert.False(query.IsCompleted);
        Assert.Equal(1, batteryEvents);
        Assert.Equal(0, lightEvents);
        Assert.Equal(0, unmatched);
        Assert.Equal(Hex("01 00 00 00 01 07 01 00 00"), Assert.Single(radio.Writes));
        radio.Indicate(Hex("01 00 01 00 01 07 01 00 09 00 4F FF FF FF FF FF FF FF"));
        Assert.Equal<byte?>(79, (await query).State!.LevelPercent);
        Assert.Equal(1, batteryEvents);
    }

    [Fact]
    public async Task InvalidNotificationIsReportedAndDoesNotPreventTheNextValidStatus()
    {
        var radio = new BatteryGatt();
        var connection = await PluginBleAgpConnection.OpenAsync(radio.Adapter, radio.Device);
        await using var client = new AgpClient(connection);
        var changes = new List<AgpBatteryState>();
        var errors = new List<AgpInvalidPacketEventArgs>();
        client.BatteryStateChanged += (_, state) => changes.Add(state);
        client.InvalidPacketReceived += (_, error) => errors.Add(error);
        var malformed = Hex("01 00 02 00 02 07 00 00 09 00 50 FF FF FF FF FF FF FF");
        radio.NotifyBattery(malformed);
        Assert.Empty(changes);
        var error = Assert.Single(errors);
        Assert.Equal(malformed, error.Data.ToArray());
        Assert.Equal(AgpResultCode.InvalidLength, Assert.IsType<AgpProtocolException>(error.Exception).ResultCode);
        radio.NotifyBattery(Hex(Status80));
        Assert.Equal<byte?>(80, Assert.Single(changes).LevelPercent);
    }

    [Fact]
    public async Task OldFirmwareWithBatteryCapabilityStillUsesGetWithoutTheOptionalCharacteristic()
    {
        var radio = new BatteryGatt(hasBattery: false);
        var connection = await PluginBleAgpConnection.OpenAsync(radio.Adapter, radio.Device);
        await using var client = new AgpClient(connection);
        Assert.True((await client.ReadCapabilitiesAsync()).HasFlag(AgpCapabilities.Battery));
        Assert.False(client.HasBatteryStateCharacteristic);
        await Assert.ThrowsAsync<NotSupportedException>(() => client.ReadBatteryStateAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => connection.ReadBatteryStateAsync());
        Assert.DoesNotContain(AgpProfile.BatteryStateCharacteristicId, radio.Starts);
        Assert.DoesNotContain(AgpProfile.BatteryStateCharacteristicId, radio.Reads);
        Assert.Equal<byte?>(80, (await client.GetBatteryAsync()).State!.LevelPercent);
    }

    [Fact]
    public async Task ExistingConnectionImplementationsDoNotNeedTheNewInterface()
    {
        await using var client = new AgpClient(new LegacyConnection());
        Assert.False(client.HasBatteryStateCharacteristic);
        await Assert.ThrowsAsync<NotSupportedException>(() => client.ReadBatteryStateAsync());
    }

    [Fact]
    public async Task ReadPreservesCancellationGattErrorsAndProtocolErrors()
    {
        var radio = new BatteryGatt();
        var connection = await PluginBleAgpConnection.OpenAsync(radio.Adapter, radio.Device);
        await using var client = new AgpClient(connection);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.ReadBatteryStateAsync(cancellation.Token));
        Assert.Equal(cancellation.Token, cancelled.CancellationToken);
        Assert.Empty(radio.Reads);

        radio.ReadResult = 5;
        await Assert.ThrowsAsync<IOException>(() => client.ReadBatteryStateAsync());
        radio.ReadResult = 0;
        radio.BatteryValue = Hex("01 00 02 00 02 07 00 00 08 65 FF FF FF FF FF FF FF");
        Assert.Equal(AgpResultCode.InvalidValue, (await Assert.ThrowsAsync<AgpProtocolException>(() =>
            client.ReadBatteryStateAsync())).ResultCode);
    }

    [Theory]
    [InlineData(CharacteristicPropertyType.Read)]
    [InlineData(CharacteristicPropertyType.Notify)]
    [InlineData(CharacteristicPropertyType.Read | CharacteristicPropertyType.Indicate)]
    public async Task PresentBatteryCharacteristicRequiresReadAndNotify(CharacteristicPropertyType properties)
    {
        var radio = new BatteryGatt(batteryProperties: properties);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PluginBleAgpConnection.OpenAsync(radio.Adapter, radio.Device));
        Assert.Empty(radio.Starts);
        Assert.Equal(1, radio.Disconnects);
    }

    [Fact]
    public async Task FailedBatterySubscriptionCleansUpAllStartedChannelsAndDisconnects()
    {
        var failure = new IOException("Battery CCC failed");
        var radio = new BatteryGatt(hasColor: true) { SubscriptionFailure = failure };
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() =>
            PluginBleAgpConnection.OpenAsync(radio.Adapter, radio.Device)));
        Assert.Equal(new[] { AgpProfile.BatteryStateCharacteristicId, AgpProfile.ColorStateCharacteristicId,
            AgpProfile.LightStateCharacteristicId, AgpProfile.ControlPointCharacteristicId }, radio.Stops);
        Assert.Equal(1, radio.Disconnects);
    }

    [Fact]
    public async Task DisposalDetachesNotificationsAndContinuesCleanupAfterBatteryStopFailure()
    {
        var radio = new BatteryGatt(hasColor: true) { FailBatteryStop = true };
        var connection = await PluginBleAgpConnection.OpenAsync(radio.Adapter, radio.Device);
        var client = new AgpClient(connection);
        Assert.True(client.HasColorStateCharacteristic);
        Assert.True(client.HasBatteryStateCharacteristic);
        var rawEvents = 0;
        var stateEvents = 0;
        connection.BatteryStateNotified += (_, _) => rawEvents++;
        client.BatteryStateChanged += (_, _) => stateEvents++;
        await client.DisposeAsync();
        radio.NotifyBattery(Hex(Status80));
        Assert.Equal(0, rawEvents);
        Assert.Equal(0, stateEvents);
        Assert.Equal(new[] { AgpProfile.BatteryStateCharacteristicId, AgpProfile.ColorStateCharacteristicId,
            AgpProfile.LightStateCharacteristicId, AgpProfile.ControlPointCharacteristicId }, radio.Stops);
        Assert.Equal(1, radio.Disconnects);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ReadBatteryStateAsync());
        await client.DisposeAsync();
        Assert.Equal(1, radio.Disconnects);
    }

    [Fact]
    public async Task ReopeningAConnectionSubscribesToBatteryAgainAndReadsTheLatestSnapshot()
    {
        var radio = new BatteryGatt();
        await using (var first = new AgpClient(await PluginBleAgpConnection.OpenAsync(radio.Adapter, radio.Device)))
            Assert.Equal<byte?>(80, (await first.ReadBatteryStateAsync()).LevelPercent);
        radio.BatteryValue = Hex("01 00 02 00 02 07 00 00 08 4F FF FF FF FF FF FF FF");
        await using var second = new AgpClient(await PluginBleAgpConnection.OpenAsync(radio.Adapter, radio.Device));
        Assert.Equal(2, radio.Starts.Count(id => id == AgpProfile.BatteryStateCharacteristicId));
        Assert.Equal<byte?>(79, (await second.ReadBatteryStateAsync()).LevelPercent);
    }

    private static byte[] Hex(string value) => Convert.FromHexString(value.Replace(" ", ""));

    private sealed class BatteryGatt
    {
        private readonly Dictionary<Guid, ICharacteristic> _characteristics = [];
        private readonly Dictionary<Guid, byte[]> _values = [];
        private DeviceState _state;
        public IAdapter Adapter { get; }
        public IDevice Device { get; }
        public List<Guid> Starts { get; } = [];
        public List<Guid> Stops { get; } = [];
        public List<Guid> Reads { get; } = [];
        public List<byte[]> Writes { get; } = [];
        public int Disconnects { get; private set; }
        public int ReadResult { get; set; }
        public byte[] BatteryValue { get; set; } = Hex(Status80);
        public bool AutoRespond { get; set; } = true;
        public Exception? SubscriptionFailure { get; set; }
        public bool FailBatteryStop { get; set; }

        public BatteryGatt(bool hasBattery = true, bool hasColor = false,
            CharacteristicPropertyType batteryProperties = CharacteristicPropertyType.Read | CharacteristicPropertyType.Notify)
        {
            var service = InterfaceProxy.Create<IService>((name, args) => name switch
            {
                "GetCharacteristicAsync" => Task.FromResult(_characteristics.GetValueOrDefault((Guid)args[0]!)!),
                _ => throw new NotSupportedException(name)
            });
            Device = InterfaceProxy.Create<IDevice>((name, args) => name switch
            {
                "get_Id" => Guid.Empty,
                "get_State" => _state,
                "GetServiceAsync" => Task.FromResult(service),
                _ => throw new NotSupportedException(name)
            });
            Adapter = InterfaceProxy.Create<IAdapter>((name, args) => name switch
            {
                "get_IsScanning" => false,
                "ConnectToDeviceAsync" => Connect(),
                "DisconnectDeviceAsync" => Disconnect(),
                _ => throw new NotSupportedException(name)
            });
            Add(AgpProfile.CapabilitiesCharacteristicId, CharacteristicPropertyType.Read);
            Add(AgpProfile.ControlPointCharacteristicId, CharacteristicPropertyType.Write | CharacteristicPropertyType.Indicate);
            Add(AgpProfile.LightStateCharacteristicId, CharacteristicPropertyType.Read | CharacteristicPropertyType.Notify);
            if (hasColor)
                Add(AgpProfile.ColorStateCharacteristicId, CharacteristicPropertyType.Read | CharacteristicPropertyType.Notify);
            if (hasBattery)
                Add(AgpProfile.BatteryStateCharacteristicId, batteryProperties);
        }

        private Task Connect() { _state = DeviceState.Connected; return Task.CompletedTask; }
        private Task Disconnect() { _state = DeviceState.Disconnected; Disconnects++; return Task.CompletedTask; }

        private void Add(Guid id, CharacteristicPropertyType properties) =>
            _characteristics[id] = InterfaceProxy.Create<ICharacteristic>((name, args) => name switch
            {
                "get_Id" => id,
                "get_Properties" => properties,
                "get_CanRead" => (properties & CharacteristicPropertyType.Read) != 0,
                "get_CanWrite" => (properties & CharacteristicPropertyType.Write) != 0,
                "get_CanUpdate" => (properties & (CharacteristicPropertyType.Notify | CharacteristicPropertyType.Indicate)) != 0,
                "get_Value" => _values.GetValueOrDefault(id),
                "set_WriteType" => null,
                "ReadAsync" => Read(id, (CancellationToken)args[0]!),
                "WriteAsync" => Write((byte[])args[0]!),
                "StartUpdatesAsync" => Start(id),
                "StopUpdatesAsync" => Stop(id),
                _ => throw new NotSupportedException(name)
            });

        private Task<(byte[], int)> Read(Guid id, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Reads.Add(id);
            return Task.FromResult((id == AgpProfile.BatteryStateCharacteristicId ? BatteryValue :
                Hex("01 00 02 00 01 00 00 00 08 00 08 00 00 00 00 00 00"), ReadResult));
        }

        private Task<int> Write(byte[] packet)
        {
            Writes.Add(packet.ToArray());
            var request = AgpCodec.Decode(packet);
            if (AutoRespond)
                Indicate(AgpCodec.Encode(AgpMessageType.Response, request.Opcode, request.TransactionId,
                    Hex("00 50 FF FF FF FF FF FF FF")));
            return Task.FromResult(0);
        }

        private Task Start(Guid id)
        {
            Starts.Add(id);
            return id == AgpProfile.BatteryStateCharacteristicId && SubscriptionFailure is { } failure
                ? Task.FromException(failure) : Task.CompletedTask;
        }

        private Task Stop(Guid id)
        {
            Stops.Add(id);
            return id == AgpProfile.BatteryStateCharacteristicId && FailBatteryStop
                ? Task.FromException(new IOException("Battery CCC stop failed")) : Task.CompletedTask;
        }

        public void NotifyBattery(byte[] packet) => Raise(AgpProfile.BatteryStateCharacteristicId, packet);
        public void Indicate(byte[] packet) => Raise(AgpProfile.ControlPointCharacteristicId, packet);
        private void Raise(Guid id, byte[] packet)
        {
            _values[id] = packet;
            var characteristic = _characteristics[id];
            InterfaceProxy.From(characteristic).Raise("ValueUpdated", characteristic, new CharacteristicUpdatedEventArgs(characteristic));
        }
    }

    private sealed class LegacyConnection : IAgpGattConnection
    {
        public event EventHandler<AgpPacketEventArgs>? ControlPointIndicated { add { } remove { } }
        public event EventHandler<AgpPacketEventArgs>? LightStateNotified { add { } remove { } }
        public string DeviceId => "legacy";
        public Task<ReadOnlyMemory<byte>> ReadCapabilitiesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ReadOnlyMemory<byte>> ReadLightStateAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task WriteControlPointAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
