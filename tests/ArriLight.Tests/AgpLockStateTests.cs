using ArriOpto.BLE.Agp;
using Xunit;

namespace ArriLight.Tests;

public sealed class AgpLockStateTests
{
    private const string Unlocked = "01000200020000000B0100000000000064000000";
    private const string Locked = "01000200020000000B0100000000000064000001";

    [Theory]
    [InlineData(Unlocked, false)]
    [InlineData(Locked, true)]
    public async Task ReadDecodesPw01NormativeLockVectors(string hex, bool locked)
    {
        await using var client = new AgpClient(new StateConnection(Convert.FromHexString(hex)));

        var state = await client.ReadLightStateAsync();

        Assert.Equal(locked, state.IsLocked);
        Assert.Equal(locked ? (byte)AgpLightStateFlags.Locked : (byte)0, state.StateFlags);
        Assert.Equal(1u, state.Revision);
        Assert.False(state.IsOn);
        Assert.Equal(100, state.LastNonzeroBrightness);
        Assert.Equal(AgpLightMode.Steady, state.Mode);
    }

    [Theory]
    [InlineData(0x80, false)]
    [InlineData(0x81, true)]
    [InlineData(0xFF, true)]
    public void PreservesOtherStateBitsWithoutTreatingThemAsLock(int flags, bool locked)
    {
        var packet = Convert.FromHexString(Unlocked);
        packet[19] = (byte)flags;

        var state = AgpModelCodec.DecodeLightState(AgpCodec.Decode(packet));

        Assert.Equal((byte)flags, state.StateFlags);
        Assert.Equal(locked, state.IsLocked);
    }

    [Fact]
    public async Task NotificationsReportLockAndUnlockWithoutChangingTheLightMode()
    {
        var connection = new StateConnection(Convert.FromHexString(Unlocked));
        await using var client = new AgpClient(connection);
        var changes = new List<AgpLightState>();
        client.LightStateChanged += (_, state) => changes.Add(state);

        connection.Notify(Convert.FromHexString(Locked));
        connection.Notify(Convert.FromHexString(Unlocked));

        Assert.Equal(new[] { true, false }, changes.Select(state => state.IsLocked));
        Assert.All(changes, state =>
        {
            Assert.False(state.IsOn);
            Assert.Equal(AgpLightMode.Steady, state.Mode);
        });
    }

    private sealed class StateConnection(byte[] state) : IAgpGattConnection
    {
        public string DeviceId => "PW01-lock-state";
        public event EventHandler<AgpPacketEventArgs>? ControlPointIndicated { add { } remove { } }
        public event EventHandler<AgpPacketEventArgs>? LightStateNotified;
        public Task<ReadOnlyMemory<byte>> ReadLightStateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<ReadOnlyMemory<byte>>(state);
        public Task<ReadOnlyMemory<byte>> ReadCapabilitiesAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task WriteControlPointAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public void Notify(byte[] packet) => LightStateNotified?.Invoke(this, new(packet));
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
