using ArriOpto.BLE.Agp;
using Xunit;

namespace ArriLight.Tests;

public sealed class DeviceRemovalTests
{
    [Fact]
    public async Task RemovingConnectedDeviceReleasesRadioAndClearsLocalCredentialsAndStatus()
    {
        await using var fixture = new PairingFixture();
        fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);
        await fixture.Service.EnsureAuthorizedAsync(fixture.Address);
        Assert.True(fixture.Connection.Snapshot.IsConnected);
        Assert.NotNull(fixture.Service.GetCachedDeviceStatus(fixture.Address));

        var removed = await fixture.Service.RemoveDeviceAsync(fixture.Address.ToLowerInvariant());

        Assert.True(removed);
        Assert.False(fixture.Connection.Snapshot.IsConnected);
        Assert.Equal(1, fixture.Ble.DisconnectAttempts);
        Assert.Contains(AgpProfile.ControlPointCharacteristicId, fixture.Ble.StoppedSubscriptions);
        Assert.Contains(AgpProfile.LightStateCharacteristicId, fixture.Ble.StoppedSubscriptions);
        Assert.Null(fixture.Repository.GetByAddress(fixture.Address));
        Assert.Null(fixture.Repository.GetTokenBytes(fixture.Address));
        Assert.Null(fixture.Service.GetCachedDeviceStatus(fixture.Address));
        Assert.Contains(fixture.Discovery.InvalidatedAddresses, address =>
            string.Equals(address, fixture.Address, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RemovingDeviceAlsoReleasesPublicConnectionWithoutAgpHandle()
    {
        await using var fixture = new PairingFixture();
        await fixture.Connection.ConnectAsync(fixture.Address);

        Assert.True(await fixture.Service.RemoveDeviceAsync(fixture.Address));

        Assert.False(fixture.Connection.Snapshot.IsConnected);
        Assert.Equal(1, fixture.Ble.DisconnectAttempts);
        Assert.Empty(fixture.Ble.Requests);
        Assert.Null(fixture.Repository.GetByAddress(fixture.Address));
    }

    [Fact]
    public async Task RemovingOtherDevicePreservesCurrentAuthenticatedSessionAndSubscriptions()
    {
        await using var fixture = new PairingFixture();
        var otherAddress = Guid.NewGuid().ToString("D").ToUpperInvariant();
        fixture.Repository.TryAdd("Other PW01", otherAddress);
        await fixture.Service.QueryDeviceStatusAsync(otherAddress);
        fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);
        await fixture.Service.EnsureAuthorizedAsync(fixture.Address);
        var status = fixture.Service.GetCachedDeviceStatus(fixture.Address);
        var disconnects = fixture.Ble.DisconnectAttempts;
        var requests = fixture.Ble.Requests.Count;
        var subscriptions = fixture.Ble.Subscriptions.Count;
        var stoppedSubscriptions = fixture.Ble.StoppedSubscriptions.Count;
        fixture.Discovery.InvalidatedAddresses.Clear();

        Assert.True(await fixture.Service.RemoveDeviceAsync(otherAddress));
        await fixture.Service.EnsureAuthorizedAsync(fixture.Address);

        Assert.True(fixture.Connection.Snapshot.IsConnected);
        Assert.Equal(fixture.Address, fixture.Connection.Snapshot.Address);
        Assert.Equal(disconnects, fixture.Ble.DisconnectAttempts);
        Assert.Equal(requests, fixture.Ble.Requests.Count);
        Assert.Equal(subscriptions, fixture.Ble.Subscriptions.Count);
        Assert.Equal(stoppedSubscriptions, fixture.Ble.StoppedSubscriptions.Count);
        Assert.Same(status, fixture.Service.GetCachedDeviceStatus(fixture.Address));
        Assert.Null(fixture.Service.GetCachedDeviceStatus(otherAddress));
        Assert.NotNull(fixture.Repository.GetByAddress(fixture.Address));
        Assert.Equal([otherAddress], fixture.Discovery.InvalidatedAddresses);
    }

    [Fact]
    public async Task FailedDisconnectPreservesDeviceAndCredentialsForRetry()
    {
        await using var fixture = new PairingFixture();
        fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);
        await fixture.Service.EnsureAuthorizedAsync(fixture.Address);
        var status = fixture.Service.GetCachedDeviceStatus(fixture.Address);
        fixture.Ble.FailDisconnect = true;

        await Assert.ThrowsAsync<IOException>(() => fixture.Service.RemoveDeviceAsync(fixture.Address));

        Assert.True(fixture.Connection.Snapshot.IsConnected);
        Assert.Equal("01020304", fixture.Repository.GetToken(fixture.Address));
        Assert.Same(status, fixture.Service.GetCachedDeviceStatus(fixture.Address));
        fixture.Ble.FailDisconnect = false;
        Assert.True(await fixture.Service.RemoveDeviceAsync(fixture.Address));
        Assert.False(fixture.Connection.Snapshot.IsConnected);
        Assert.Equal(2, fixture.Ble.DisconnectAttempts);
        Assert.Null(fixture.Repository.GetByAddress(fixture.Address));
    }

    [Fact]
    public async Task CancelledRemovalWaitingForCurrentOperationDoesNotRemoveOrReleaseItsLock()
    {
        await using var fixture = new PairingFixture();
        fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);
        var connecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishConnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Ble.OnConnect = async (_, token) =>
        {
            connecting.SetResult();
            await finishConnect.Task.WaitAsync(token);
        };
        var control = fixture.Service.EnsureAuthorizedAsync(fixture.Address);
        await connecting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var removal = fixture.Service.RemoveDeviceAsync(fixture.Address, cancellation.Token);
        cancellation.Cancel();

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => removal);
            Assert.NotNull(fixture.Repository.GetByAddress(fixture.Address));
            Assert.Equal(0, fixture.Ble.DisconnectAttempts);
        }
        finally
        {
            finishConnect.TrySetResult();
        }
        await control.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(fixture.Connection.Snapshot.IsConnected);
        Assert.True(await fixture.Service.RemoveDeviceAsync(fixture.Address));
        Assert.Equal(1, fixture.Ble.DisconnectAttempts);
    }

    [Fact]
    public async Task RepeatedRemovalDoesNotDisconnectTwice()
    {
        await using var fixture = new PairingFixture();
        fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);
        await fixture.Service.EnsureAuthorizedAsync(fixture.Address);

        Assert.True(await fixture.Service.RemoveDeviceAsync(fixture.Address));
        Assert.False(await fixture.Service.RemoveDeviceAsync(fixture.Address));

        Assert.Equal(1, fixture.Ble.DisconnectAttempts);
    }

    [Fact]
    public async Task LateControlOfRemovedDeviceCannotReconnectOrRestoreOldAuthorization()
    {
        await using var fixture = new PairingFixture();
        fixture.Ble.Capabilities |= AgpCapabilities.OnOff;
        fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);
        await fixture.Service.EnsureAuthorizedAsync(fixture.Address);
        await fixture.Service.RemoveDeviceAsync(fixture.Address);
        var connects = fixture.Ble.ConnectAttempts.Count;
        var disconnects = fixture.Ble.DisconnectAttempts;
        var requests = fixture.Ble.Requests.Count;
        var subscriptions = fixture.Ble.Subscriptions.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.SetOnOffAsync(fixture.Address, true));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.EnsureAuthorizedAsync(fixture.Address));

        Assert.False(fixture.Connection.Snapshot.IsConnected);
        Assert.Equal(connects, fixture.Ble.ConnectAttempts.Count);
        Assert.Equal(disconnects, fixture.Ble.DisconnectAttempts);
        Assert.Equal(requests, fixture.Ble.Requests.Count);
        Assert.Equal(subscriptions, fixture.Ble.Subscriptions.Count);
        Assert.Null(fixture.Repository.GetByAddress(fixture.Address));
        Assert.Null(fixture.Service.GetCachedDeviceStatus(fixture.Address));
    }

    [Fact]
    public async Task LateControlOfRemovedDevicePreservesAnotherCurrentDevice()
    {
        await using var fixture = new PairingFixture();
        var otherAddress = Guid.NewGuid().ToString("D").ToUpperInvariant();
        fixture.Repository.TryAdd("Other PW01", otherAddress);
        fixture.Repository.SetToken(otherAddress, [5, 6, 7, 8]);
        await fixture.Service.RemoveDeviceAsync(fixture.Address);
        await fixture.Service.EnsureAuthorizedAsync(otherAddress);
        var requests = fixture.Ble.Requests.Count;
        var subscriptions = fixture.Ble.Subscriptions.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.EnsureAuthorizedAsync(fixture.Address));
        await fixture.Service.EnsureAuthorizedAsync(otherAddress);

        Assert.True(fixture.Connection.Snapshot.IsConnected);
        Assert.Equal(otherAddress, fixture.Connection.Snapshot.Address);
        Assert.Single(fixture.Ble.ConnectAttempts);
        Assert.Equal(0, fixture.Ble.DisconnectAttempts);
        Assert.Equal(requests, fixture.Ble.Requests.Count);
        Assert.Equal(subscriptions, fixture.Ble.Subscriptions.Count);
        Assert.Empty(fixture.Ble.StoppedSubscriptions);
    }

    [Fact]
    public async Task LatePublicQueryCannotReconnectRemovedDeviceOrInterruptAnotherCurrentDevice()
    {
        await using var fixture = new PairingFixture();
        fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);
        await fixture.Service.EnsureAuthorizedAsync(fixture.Address);
        await fixture.Service.RemoveDeviceAsync(fixture.Address);
        var connects = fixture.Ble.ConnectAttempts.Count;
        var disconnects = fixture.Ble.DisconnectAttempts;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.QueryDeviceStatusAsync(fixture.Address));

        Assert.Equal(connects, fixture.Ble.ConnectAttempts.Count);
        Assert.Equal(disconnects, fixture.Ble.DisconnectAttempts);
        Assert.Null(fixture.Service.GetCachedDeviceStatus(fixture.Address));
        Assert.False(fixture.Connection.Snapshot.IsConnected);

        var otherAddress = Guid.NewGuid().ToString("D").ToUpperInvariant();
        fixture.Repository.TryAdd("Other PW01", otherAddress);
        fixture.Repository.SetToken(otherAddress, [5, 6, 7, 8]);
        await fixture.Service.EnsureAuthorizedAsync(otherAddress);
        connects = fixture.Ble.ConnectAttempts.Count;
        var requests = fixture.Ble.Requests.Count;
        var subscriptions = fixture.Ble.Subscriptions.Count;
        var stoppedSubscriptions = fixture.Ble.StoppedSubscriptions.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.QueryDeviceStatusAsync(fixture.Address));
        await fixture.Service.EnsureAuthorizedAsync(otherAddress);

        Assert.True(fixture.Connection.Snapshot.IsConnected);
        Assert.Equal(otherAddress, fixture.Connection.Snapshot.Address);
        Assert.Equal(connects, fixture.Ble.ConnectAttempts.Count);
        Assert.Equal(disconnects, fixture.Ble.DisconnectAttempts);
        Assert.Equal(requests, fixture.Ble.Requests.Count);
        Assert.Equal(subscriptions, fixture.Ble.Subscriptions.Count);
        Assert.Equal(stoppedSubscriptions, fixture.Ble.StoppedSubscriptions.Count);
        Assert.Null(fixture.Service.GetCachedDeviceStatus(fixture.Address));
    }

    [Fact]
    public async Task RemovedDeviceCanBeAddedAndPairedAgainWithoutReusingOldAuthorization()
    {
        await using var fixture = new PairingFixture();
        fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);
        await fixture.Service.EnsureAuthorizedAsync(fixture.Address);
        await fixture.Service.RemoveDeviceAsync(fixture.Address);
        Assert.True(fixture.Repository.TryAdd("PW01 again", fixture.Address));

        await fixture.Service.BindAsync(fixture.Address);
        await fixture.Service.EnsureAuthorizedAsync(fixture.Address);

        Assert.Equal("A1B2C3D4", fixture.Repository.GetToken(fixture.Address));
        Assert.Equal(3, fixture.Ble.ConnectAttempts.Count);
        Assert.Equal(2, fixture.Ble.DisconnectAttempts);
        Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.AuthGetToken);
        Assert.Equal(2, fixture.Ble.Requests.Count(opcode => opcode == AgpOpcode.AuthToken));
        Assert.Equal(new byte[] { 0xA1, 0xB2, 0xC3, 0xD4 },
            fixture.Ble.RequestMessages.Last(message => message.Opcode == AgpOpcode.AuthToken).Payload.ToArray());
        Assert.True(fixture.Connection.Snapshot.IsConnected);
    }
}
