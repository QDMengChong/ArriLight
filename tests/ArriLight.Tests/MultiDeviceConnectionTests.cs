using ArriOpto.BLE.Agp;
using Xunit;

namespace ArriLight.Tests;

public sealed class MultiDeviceConnectionTests
{
    [Fact]
    public async Task PublicQueriesKeepMultipleConnectionsWithIndependentTenSecondLeases()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = new PairingFixture(clock);
        var other = fixture.AddDevice();
        await fixture.Service.QueryDeviceStatusAsync(fixture.Address);
        clock.Advance(TimeSpan.FromSeconds(4));
        await fixture.Service.QueryDeviceStatusAsync(other.Address);

        Assert.Equal(2, fixture.Connection.GetConnectedAddresses().Count);
        Assert.Empty(fixture.Ble.Requests);
        Assert.Empty(other.Requests);
        using var firstExpired = WatchDisconnect(fixture.Connection, fixture.Address);
        clock.Advance(TimeSpan.FromSeconds(6));
        await firstExpired.Completed;
        Assert.True(fixture.Connection.IsConnected(other.Address));
        Assert.Equal(0, other.DisconnectAttempts);
        using var secondExpired = WatchDisconnect(fixture.Connection, other.Address);
        clock.Advance(TimeSpan.FromSeconds(4));
        await secondExpired.Completed;
        Assert.Empty(fixture.Connection.GetConnectedAddresses());
        Assert.Empty(fixture.Discovery.InvalidatedAddresses);
    }

    [Fact]
    public async Task HomePowerPromotesTargetAndRenewsOtherShortLeasesWithoutDowngradingLongConnections()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = new PairingFixture(clock);
        var second = fixture.AddDevice();
        var third = fixture.AddDevice();
        var longLived = fixture.AddDevice();
        fixture.Ble.Capabilities |= AgpCapabilities.OnOff;
        fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);
        await fixture.Service.QueryDeviceStatusAsync(fixture.Address);
        await fixture.Service.QueryDeviceStatusAsync(second.Address);
        await fixture.Service.QueryDeviceStatusAsync(third.Address);
        fixture.Service.PromoteConnectionToLongLived(longLived.Address);
        await fixture.Connection.ConnectAsync(longLived.Address);
        clock.Advance(TimeSpan.FromSeconds(6));

        fixture.Service.BeginHomeControlOperation(fixture.Address);
        await fixture.Service.SetOnOffAsync(fixture.Address, false);
        clock.Advance(TimeSpan.FromSeconds(4));
        await Task.Delay(30); // Allow an incorrectly retained old expiry to run, if any.
        Assert.Equal(4, fixture.Connection.GetConnectedAddresses().Count);
        Assert.False(fixture.Connection.HasActiveConnectionLeaseFor(fixture.Address));
        Assert.False(fixture.Connection.HasActiveConnectionLeaseFor(longLived.Address));
        Assert.True(fixture.Connection.HasActiveConnectionLeaseFor(second.Address));
        Assert.True(fixture.Connection.HasActiveConnectionLeaseFor(third.Address));

        using var secondExpired = WatchDisconnect(fixture.Connection, second.Address);
        using var thirdExpired = WatchDisconnect(fixture.Connection, third.Address);
        clock.Advance(TimeSpan.FromSeconds(6));
        await Task.WhenAll(secondExpired.Completed, thirdExpired.Completed);
        Assert.True(fixture.Connection.IsConnected(fixture.Address));
        Assert.True(fixture.Connection.IsConnected(longLived.Address));
    }

    [Fact]
    public async Task EnteringControlPageOnlyPromotesTargetAndLeavesOtherDeadlineUnchanged()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = new PairingFixture(clock);
        var other = fixture.AddDevice();
        fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);
        await fixture.Service.QueryDeviceStatusAsync(fixture.Address);
        await fixture.Service.QueryDeviceStatusAsync(other.Address);
        clock.Advance(TimeSpan.FromSeconds(6));
        fixture.Service.PromoteConnectionToLongLived(fixture.Address);
        await fixture.Service.GetControlProfileAsync(fixture.Address, useCachedLightState: true);

        using var expired = WatchDisconnect(fixture.Connection, other.Address);
        clock.Advance(TimeSpan.FromSeconds(4));
        await expired.Completed;
        Assert.True(fixture.Connection.IsConnected(fixture.Address));
        Assert.Single(fixture.Ble.ConnectAttempts);
        Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.AuthToken);
    }

    [Fact]
    public async Task MultipleAuthenticatedClientsKeepTheirOwnSubscriptionsStateAndAuthorization()
    {
        await using var fixture = new PairingFixture();
        var other = fixture.AddDevice();
        fixture.Ble.Capabilities |= AgpCapabilities.OnOff;
        other.Capabilities |= AgpCapabilities.OnOff;
        fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);
        fixture.Repository.SetToken(other.Address, [5, 6, 7, 8]);
        await fixture.Service.EnsureAuthorizedAsync(fixture.Address);
        await fixture.Service.EnsureAuthorizedAsync(other.Address);
        var firstBefore = fixture.Service.GetCachedDeviceStatus(fixture.Address);

        other.NotifyLightState(StatePacket(false));

        Assert.Same(firstBefore, fixture.Service.GetCachedDeviceStatus(fixture.Address));
        Assert.False(fixture.Service.GetCachedDeviceStatus(other.Address)!.LightState!.IsOn);
        await fixture.Service.SetOnOffAsync(fixture.Address, false);
        await fixture.Service.SetOnOffAsync(other.Address, true);
        Assert.False(fixture.Service.GetCachedDeviceStatus(fixture.Address)!.LightState!.IsOn);
        Assert.True(fixture.Service.GetCachedDeviceStatus(other.Address)!.LightState!.IsOn);
        Assert.Equal(2, fixture.Connection.GetConnectedAddresses().Count);
        Assert.Single(fixture.Ble.ConnectAttempts);
        Assert.Single(other.ConnectAttempts);
        Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.AuthToken);
        Assert.Single(other.Requests, opcode => opcode == AgpOpcode.AuthToken);
        Assert.Contains(AgpProfile.LightStateCharacteristicId, fixture.Ble.Subscriptions);
        Assert.Contains(AgpProfile.LightStateCharacteristicId, other.Subscriptions);
        Assert.Empty(fixture.Ble.StoppedSubscriptions);
        Assert.Empty(other.StoppedSubscriptions);
    }

    [Fact]
    public async Task UnexpectedDisconnectAndRemovalAffectOnlyTheirTarget()
    {
        await using var fixture = new PairingFixture();
        var other = fixture.AddDevice();
        fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);
        fixture.Repository.SetToken(other.Address, [5, 6, 7, 8]);
        await fixture.Service.EnsureAuthorizedAsync(fixture.Address);
        await fixture.Service.EnsureAuthorizedAsync(other.Address);
        fixture.Ble.RaiseUnexpectedDisconnect();
        Assert.False(fixture.Connection.IsConnected(fixture.Address));
        Assert.True(fixture.Connection.IsConnected(other.Address));
        await fixture.Service.EnsureAuthorizedAsync(fixture.Address);
        await fixture.Service.RemoveDeviceAsync(fixture.Address);
        await fixture.Service.EnsureAuthorizedAsync(other.Address);
        Assert.True(fixture.Connection.IsConnected(other.Address));
        Assert.Single(other.ConnectAttempts);
        Assert.Single(other.Requests, opcode => opcode == AgpOpcode.AuthToken);
        Assert.Empty(other.StoppedSubscriptions);
        Assert.Equal(0, other.DisconnectAttempts);
        Assert.DoesNotContain(other.Address, fixture.Discovery.InvalidatedAddresses);
    }

    [Fact]
    public async Task PublicQueryOfLongConnectionPreservesItsAuthenticatedClientAndLeaseOwnership()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = new PairingFixture(clock);
        fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);
        await fixture.Service.EnsureAuthorizedAsync(fixture.Address);
        await fixture.Service.QueryDeviceStatusAsync(fixture.Address);
        await fixture.Service.EnsureAuthorizedAsync(fixture.Address);
        clock.Advance(TimeSpan.FromSeconds(11));
        await Task.Delay(30);
        Assert.True(fixture.Connection.IsConnected(fixture.Address));
        Assert.False(fixture.Connection.HasActiveConnectionLeaseFor(fixture.Address));
        Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.AuthToken);
        Assert.Empty(fixture.Ble.StoppedSubscriptions);
    }

    [Fact]
    public async Task ReconnectAfterPublicReadReauthenticatesInsteadOfReusingOldClient()
    {
        await using var fixture = new PairingFixture();
        fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);
        await fixture.Service.EnsureAuthorizedAsync(fixture.Address);
        await fixture.Connection.DisconnectAsync(fixture.Address);
        await fixture.Service.QueryDeviceStatusAsync(fixture.Address);
        await fixture.Service.EnsureAuthorizedAsync(fixture.Address);
        Assert.Equal(2, fixture.Ble.Requests.Count(opcode => opcode == AgpOpcode.AuthToken));
        Assert.Equal(2, fixture.Ble.ConnectAttempts.Count);
    }

    [Fact]
    public async Task OldTransportCannotConsumeNotificationsFromNewConnectionOfSameAddress()
    {
        await using var fixture = new PairingFixture();
        fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);
        await fixture.Service.EnsureAuthorizedAsync(fixture.Address);
        var previous = fixture.Service.GetCachedDeviceStatus(fixture.Address);
        await fixture.Connection.DisconnectAsync(fixture.Address);
        await using var newTransport = new AgpGattTransport(fixture.Connection, fixture.Address);
        await newTransport.StartAsync();
        var notified = false;
        newTransport.LightStateNotified += (_, _) => notified = true;

        fixture.Ble.NotifyLightState(StatePacket(false));

        Assert.True(notified);
        Assert.Same(previous, fixture.Service.GetCachedDeviceStatus(fixture.Address));
        await fixture.Service.EnsureAuthorizedAsync(fixture.Address);
        Assert.Equal(2, fixture.Ble.Requests.Count(opcode => opcode == AgpOpcode.AuthToken));
    }

    [Fact]
    public async Task DisconnectedPlatformStateForcesNewSubscriptionsAndAuthenticationEvenBeforeItsEvent()
    {
        await using var fixture = new PairingFixture();
        fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);
        await fixture.Service.EnsureAuthorizedAsync(fixture.Address);
        fixture.Ble.SetDisconnectedBeforeEvent();

        await fixture.Service.EnsureAuthorizedAsync(fixture.Address);

        Assert.Equal(2, fixture.Ble.ConnectAttempts.Count);
        Assert.Equal(2, fixture.Ble.Requests.Count(opcode => opcode == AgpOpcode.AuthToken));
        Assert.Equal(2, fixture.Ble.Subscriptions.Count(id => id == AgpProfile.ControlPointCharacteristicId));
        Assert.Equal(2, fixture.Ble.Subscriptions.Count(id => id == AgpProfile.LightStateCharacteristicId));
    }

    [Fact]
    public async Task BackgroundResumeRestoresEveryDeviceAndItsLeaseOrControlOwnership()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = new PairingFixture(clock);
        var other = fixture.AddDevice();
        fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);
        await fixture.Service.EnsureAuthorizedAsync(fixture.Address);
        await fixture.Service.QueryDeviceStatusAsync(other.Address);
        await fixture.Service.SuspendForBackgroundAsync();
        Assert.Empty(fixture.Connection.GetConnectedAddresses());
        Assert.Equal(1, fixture.Ble.DisconnectAttempts);
        Assert.Equal(1, other.DisconnectAttempts);
        await fixture.Service.ResumeAfterBackgroundAsync();
        Assert.Equal(2, fixture.Connection.GetConnectedAddresses().Count);
        Assert.False(fixture.Connection.HasActiveConnectionLeaseFor(fixture.Address));
        Assert.True(fixture.Connection.HasActiveConnectionLeaseFor(other.Address));
        Assert.Equal(2, fixture.Ble.Requests.Count(opcode => opcode == AgpOpcode.AuthToken));
        Assert.Empty(other.Requests);
        using var expired = WatchDisconnect(fixture.Connection, other.Address);
        clock.Advance(TimeSpan.FromSeconds(10));
        await expired.Completed;
        Assert.True(fixture.Connection.IsConnected(fixture.Address));
    }

    [Fact]
    public async Task ResumeFailureForOneDeviceDoesNotDisconnectOtherDevicesAndCanBeRetried()
    {
        await using var fixture = new PairingFixture();
        var other = fixture.AddDevice();
        fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);
        fixture.Repository.SetToken(other.Address, [5, 6, 7, 8]);
        await fixture.Service.EnsureAuthorizedAsync(fixture.Address);
        await fixture.Service.EnsureAuthorizedAsync(other.Address);
        await fixture.Service.SuspendForBackgroundAsync();
        fixture.Ble.OnConnect = (_, _) => Task.FromException(new IOException("radio unavailable"));
        await fixture.Service.ResumeAfterBackgroundAsync();
        Assert.False(fixture.Connection.IsConnected(fixture.Address));
        Assert.True(fixture.Connection.IsConnected(other.Address));
        Assert.Equal(1, other.DisconnectAttempts);
        fixture.Ble.OnConnect = null;
        await fixture.Service.ResumeAfterBackgroundAsync();
        Assert.Equal(2, fixture.Connection.GetConnectedAddresses().Count);
        Assert.Equal(2, other.ConnectAttempts.Count);
    }

    [Fact]
    public async Task ClosingApplicationDisconnectsAllDevicesAndClearsEveryResumeTarget()
    {
        await using var fixture = new PairingFixture();
        var other = fixture.AddDevice();
        await fixture.Service.QueryDeviceStatusAsync(fixture.Address);
        await fixture.Service.QueryDeviceStatusAsync(other.Address);
        await fixture.Service.DisconnectForApplicationCloseAsync();
        await fixture.Service.ResumeAfterBackgroundAsync();
        Assert.Empty(fixture.Connection.GetConnectedAddresses());
        Assert.Single(fixture.Ble.ConnectAttempts);
        Assert.Single(other.ConnectAttempts);
        Assert.Equal(1, fixture.Ble.DisconnectAttempts);
        Assert.Equal(1, other.DisconnectAttempts);
    }

    private static byte[] StatePacket(bool isOn) => AgpCodec.Encode(
        AgpMessageType.Status, AgpOpcode.LightStateStatus, 0,
        [1, 0, 0, 0, (byte)(isOn ? 1 : 0), 50, 50, 50, 0, 0, 0]);

    [Fact]
    public async Task ResponseFromOtherDeviceWithIdenticalOpcodeAndTransactionIdCannotCompleteTargetRequest()
    {
        await using var fixture = new PairingFixture();
        var other = fixture.AddDevice();
        fixture.Ble.Capabilities |= AgpCapabilities.OnOff;
        fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);
        fixture.Repository.SetToken(other.Address, [5, 6, 7, 8]);
        await fixture.Service.EnsureAuthorizedAsync(fixture.Address);
        await fixture.Service.EnsureAuthorizedAsync(other.Address);
        fixture.Ble.BeforeControlResponse = request => other.IndicateControlResponse(
            AgpCodec.Encode(AgpMessageType.Response, request.Opcode, request.TransactionId,
                [(byte)AgpResultCode.NotAuthorized, 0, 0, 0, 0]));

        await fixture.Service.SetOnOffAsync(fixture.Address, false);

        Assert.False(fixture.Service.GetCachedDeviceStatus(fixture.Address)!.LightState!.IsOn);
        Assert.True(fixture.Connection.IsConnected(other.Address));
        Assert.DoesNotContain(AgpOpcode.OnOffSet, other.Requests);
    }

    [Fact]
    public async Task QueryFailureDisconnectsOnlyFailingDeviceAndLeavesOtherLeaseIntact()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = new PairingFixture(clock);
        var other = fixture.AddDevice();
        await fixture.Service.QueryDeviceStatusAsync(other.Address);
        fixture.Ble.LightStatePacket = [0];
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Service.QueryDeviceStatusAsync(fixture.Address));
        Assert.False(fixture.Connection.IsConnected(fixture.Address));
        Assert.True(fixture.Connection.IsConnected(other.Address));
        Assert.True(fixture.Connection.HasActiveConnectionLeaseFor(other.Address));
        Assert.Equal(0, other.DisconnectAttempts);
        using var expired = WatchDisconnect(fixture.Connection, other.Address);
        clock.Advance(TimeSpan.FromSeconds(10));
        await expired.Completed;
    }

    [Fact]
    public async Task PairingNewDeviceCleansUpOnlyItsOwnConnection()
    {
        await using var fixture = new PairingFixture();
        var other = fixture.AddDevice();
        fixture.Repository.SetToken(other.Address, [5, 6, 7, 8]);
        await fixture.Service.EnsureAuthorizedAsync(other.Address);
        await fixture.Service.BindAsync(fixture.Address);
        await fixture.Service.EnsureAuthorizedAsync(other.Address);
        Assert.False(fixture.Connection.IsConnected(fixture.Address));
        Assert.True(fixture.Connection.IsConnected(other.Address));
        Assert.Single(other.ConnectAttempts);
        Assert.Single(other.Requests, opcode => opcode == AgpOpcode.AuthToken);
        Assert.Empty(other.StoppedSubscriptions);
        Assert.Equal(0, other.DisconnectAttempts);
    }

    [Fact]
    public async Task FailedBackgroundDisconnectDoesNotPreventOtherDevicesBeingReleased()
    {
        await using var fixture = new PairingFixture();
        var other = fixture.AddDevice();
        await fixture.Service.QueryDeviceStatusAsync(fixture.Address);
        await fixture.Service.QueryDeviceStatusAsync(other.Address);
        fixture.Ble.FailDisconnect = true;
        await Assert.ThrowsAsync<AggregateException>(() => fixture.Service.SuspendForBackgroundAsync());
        Assert.False(fixture.Connection.IsConnected(fixture.Address));
        Assert.Contains(fixture.Address, fixture.Connection.GetConnectedAddresses());
        Assert.False(fixture.Connection.IsConnected(other.Address));
        Assert.Equal(1, other.DisconnectAttempts);
        fixture.Ble.FailDisconnect = false;
    }

    private static DisconnectWatch WatchDisconnect(BluetoothConnectionService connection, string address) =>
        new(connection, address);

    private sealed class DisconnectWatch : IDisposable
    {
        private readonly BluetoothConnectionService _connection;
        private readonly string _address;
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Completed => _completion.Task.WaitAsync(TimeSpan.FromSeconds(3));
        public DisconnectWatch(BluetoothConnectionService connection, string address)
        {
            _connection = connection;
            _address = address;
            connection.StateChanged += OnChanged;
        }
        private void OnChanged(object? sender, BluetoothSessionSnapshot snapshot)
        {
            if (snapshot.State == BluetoothSessionState.Disconnected &&
                string.Equals(snapshot.Address, _address, StringComparison.OrdinalIgnoreCase))
                _completion.TrySetResult();
        }
        public void Dispose() => _connection.StateChanged -= OnChanged;
    }
}
