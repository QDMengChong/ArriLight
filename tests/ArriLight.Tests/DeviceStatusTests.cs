using ArriOpto.BLE.Agp;
using Xunit;

namespace ArriLight.Tests;

public sealed class DeviceStatusTests
{
    [Fact]
    public async Task HomeQueryCachesCompleteLightStateWithoutAuthenticatingAndReleasesConnection()
    {
        await using var fixture = new PairingFixture();
        var state = await fixture.Service.QueryDeviceStatusAsync(fixture.Address);

        Assert.True(state.IsOnline);
        Assert.True(state.LightState!.IsOn);
        Assert.Equal((byte)50, state.LightState.PresentBrightness);
        Assert.Same(state, fixture.Service.GetCachedDeviceStatus(fixture.Address.ToLowerInvariant()));
        Assert.Null(fixture.Service.GetCachedDeviceStatus("another-device"));
        Assert.Equal([AgpProfile.LightStateCharacteristicId], fixture.Ble.Reads);
        Assert.Empty(fixture.Ble.Requests);
        Assert.Empty(fixture.Ble.Subscriptions);
        Assert.False(fixture.Connection.Snapshot.IsConnected);
    }

    [Fact]
    public async Task EntryReusesHomeLightStateAndStillConnectsAndAuthenticates()
    {
        await using var fixture = new PairingFixture();
        fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);
        var home = await fixture.Service.QueryDeviceStatusAsync(fixture.Address);
        fixture.Ble.LightStatePacket = LightStatePacket(false, 0);

        var profile = await fixture.Service.GetControlProfileAsync(
            fixture.Address, useCachedLightState: true);

        Assert.Equal(home.LightState, profile.LightState);
        Assert.Equal(1, StateReadCount(fixture));
        Assert.Equal(2, fixture.Ble.ConnectAttempts.Count);
        Assert.Contains(AgpOpcode.AuthToken, fixture.Ble.Requests);
        Assert.True(fixture.Connection.Snapshot.IsConnected);
    }

    [Fact]
    public async Task EntryWithoutHomeResultReadsLightStateExactlyOnce()
    {
        await using var fixture = new PairingFixture();
        fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);

        var profile = await fixture.Service.GetControlProfileAsync(
            fixture.Address, useCachedLightState: true);

        Assert.True(profile.LightState!.IsOn);
        Assert.Equal(1, StateReadCount(fixture));
        Assert.Single(fixture.Ble.ConnectAttempts);
        Assert.Contains(AgpOpcode.AuthToken, fixture.Ble.Requests);
    }

    [Fact]
    public async Task RefreshReadsFreshStateOnceWhileReusingAuthenticatedConnection()
    {
        await using var fixture = new PairingFixture();
        fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);
        await fixture.Service.QueryDeviceStatusAsync(fixture.Address);
        await fixture.Service.GetControlProfileAsync(fixture.Address, useCachedLightState: true);
        fixture.Ble.LightStatePacket = LightStatePacket(false, 0);

        var profile = await fixture.Service.GetControlProfileAsync(
            fixture.Address, refreshLightState: true);

        Assert.False(profile.LightState!.IsOn);
        Assert.Equal(2, StateReadCount(fixture));
        Assert.Equal(2, fixture.Ble.ConnectAttempts.Count);
        Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.AuthToken);
        Assert.Equal(profile.LightState, fixture.Service.GetCachedDeviceStatus(fixture.Address)!.LightState);
    }

    [Fact]
    public async Task RefreshAfterDisconnectReauthenticatesAndReadsExactlyOnceEvenWhenCacheIsRequested()
    {
        await using var fixture = new PairingFixture();
        fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);
        await fixture.Service.QueryDeviceStatusAsync(fixture.Address);
        await fixture.Service.GetControlProfileAsync(fixture.Address, useCachedLightState: true);
        await fixture.Connection.DisconnectAsync();
        fixture.Ble.LightStatePacket = LightStatePacket(false, 0);

        var profile = await fixture.Service.GetControlProfileAsync(
            fixture.Address, useCachedLightState: true, refreshLightState: true);

        Assert.False(profile.LightState!.IsOn);
        Assert.Equal(2, StateReadCount(fixture));
        Assert.Equal(3, fixture.Ble.ConnectAttempts.Count);
        Assert.Equal(2, fixture.Ble.Requests.Count(opcode => opcode == AgpOpcode.AuthToken));
    }

    [Fact]
    public async Task CachedPublicStateDoesNotBypassTokenRejection()
    {
        await using var fixture = new PairingFixture();
        fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);
        await fixture.Service.QueryDeviceStatusAsync(fixture.Address);
        fixture.Ble.AuthenticationResult = AgpResultCode.NotAuthorized;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.GetControlProfileAsync(fixture.Address, useCachedLightState: true));

        Assert.Contains("NotAuthorized", error.Message);
        Assert.Contains(AgpOpcode.AuthToken, fixture.Ble.Requests);
        Assert.Equal(1, StateReadCount(fixture));
    }

    [Fact]
    public async Task CancelledHomeQueryDoesNotPublishAPartialResult()
    {
        await using var fixture = new PairingFixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Ble.OnLightStateRead = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Service.QueryDeviceStatusAsync(fixture.Address, cancellation.Token));

        Assert.Null(fixture.Service.GetCachedDeviceStatus(fixture.Address));
        Assert.False(fixture.Connection.Snapshot.IsConnected);
    }

    [Fact]
    public async Task FailedHomeQueryWithOldStateDoesNotReuseItAsOnline()
    {
        await using var fixture = new PairingFixture();
        fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);
        await fixture.Service.QueryDeviceStatusAsync(fixture.Address);
        fixture.Service.RecordDeviceOffline(fixture.Address);
        fixture.Ble.LightStatePacket = LightStatePacket(false, 0);

        var profile = await fixture.Service.GetControlProfileAsync(
            fixture.Address, useCachedLightState: true);

        Assert.False(profile.LightState!.IsOn);
        Assert.Equal(2, StateReadCount(fixture));
    }

    [Fact]
    public async Task SuccessfulPowerCommandUpdatesTheSharedKnownState()
    {
        await using var fixture = new PairingFixture();
        fixture.Ble.Capabilities |= AgpCapabilities.OnOff;
        fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);
        await fixture.Service.QueryDeviceStatusAsync(fixture.Address);
        await fixture.Service.SetOnOffAsync(fixture.Address, false);

        Assert.False(fixture.Service.GetCachedDeviceStatus(fixture.Address)!.LightState!.IsOn);
    }

    private static int StateReadCount(PairingFixture fixture) =>
        fixture.Ble.Reads.Count(id => id == AgpProfile.LightStateCharacteristicId);

    private static byte[] LightStatePacket(bool isOn, byte brightness) => AgpCodec.Encode(
        AgpMessageType.Status, AgpOpcode.LightStateStatus, 0,
        [2, 0, 0, 0, isOn ? (byte)1 : (byte)0, brightness, brightness, 50, 0, 0, 0]);
}
