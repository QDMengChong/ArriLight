using ArriOpto.BLE.Agp;
using Xunit;

namespace ArriLight.Tests;

public sealed class DeviceStatusTests
{
    [Fact]
    public async Task HomeQueryCachesCompleteLightStateWithoutAuthenticatingAndRetainsConnectionBriefly()
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
        Assert.True(fixture.Connection.Snapshot.IsConnected);
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
        Assert.Single(fixture.Ble.ConnectAttempts);
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
        Assert.Single(fixture.Ble.ConnectAttempts);
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
        Assert.Equal(2, fixture.Ble.ConnectAttempts.Count);
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

    [Fact]
    public async Task BackgroundSuspendDisconnectsAndForegroundResumeReauthenticatesThePreviousDevice()
    {
        await using var fixture = new PairingFixture();
        fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);

        await fixture.Service.QueryDeviceStatusAsync(fixture.Address);
        await fixture.Service.GetControlProfileAsync(fixture.Address, useCachedLightState: true);
        await fixture.Service.SuspendForBackgroundAsync();

        Assert.False(fixture.Connection.Snapshot.IsConnected);
        Assert.Equal(1, fixture.Ble.DisconnectAttempts);

        await fixture.Service.ResumeAfterBackgroundAsync();

        Assert.True(fixture.Connection.Snapshot.IsConnected);
        Assert.Equal(2, fixture.Ble.ConnectAttempts.Count);
        Assert.Contains(AgpOpcode.AuthToken, fixture.Ble.Requests);
    }

    [Fact]
    public async Task BackgroundResumeOfHomeOnlyConnectionRestartsItsShortLease()
    {
        await using var fixture = new PairingFixture();

        await fixture.Service.QueryDeviceStatusAsync(fixture.Address);
        await fixture.Service.SuspendForBackgroundAsync();
        await fixture.Service.ResumeAfterBackgroundAsync();

        Assert.True(fixture.Connection.Snapshot.IsConnected);
        Assert.True(fixture.Connection.HasActiveConnectionLease);
        Assert.DoesNotContain(AgpOpcode.AuthToken, fixture.Ble.Requests);
    }

    [Fact]
    public async Task NormalConnectionReleaseKeepsShortLivedDiscoveryLocation()
    {
        await using var fixture = new PairingFixture();

        await fixture.Connection.ConnectAsync(fixture.Address);
        await fixture.Connection.DisconnectAsync();

        Assert.False(fixture.Connection.Snapshot.IsConnected);
        Assert.Empty(fixture.Discovery.InvalidatedAddresses);

        var connectCount = fixture.Ble.ConnectAttempts.Count;
        await fixture.Connection.ConnectAsync(fixture.Address);

        Assert.Equal(connectCount + 1, fixture.Ble.ConnectAttempts.Count);
        Assert.True(fixture.Connection.Snapshot.IsConnected);
    }

    [Fact]
    public async Task ExpiredHomeLeaseKeepsDiscoveryLocationButDropsTheConnection()
    {
        await using var fixture = new PairingFixture();

        await fixture.Connection.ConnectAsync(fixture.Address);
        fixture.Connection.RetainConnectionFor(
            fixture.Address, TimeSpan.FromMilliseconds(25));
        await Task.Delay(150);

        Assert.False(fixture.Connection.Snapshot.IsConnected);
        Assert.Empty(fixture.Discovery.InvalidatedAddresses);
    }

    [Fact]
    public async Task UnexpectedDisconnectInvalidatesDiscoveryLocation()
    {
        await using var fixture = new PairingFixture();

        await fixture.Connection.ConnectAsync(fixture.Address);
        fixture.Ble.RaiseUnexpectedDisconnect();

        Assert.False(fixture.Connection.Snapshot.IsConnected);
        Assert.Contains(fixture.Discovery.InvalidatedAddresses, address =>
            string.Equals(address, fixture.Address, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ClosingApplicationReleasesConnectionWithoutLeavingResumeTarget()
    {
        await using var fixture = new PairingFixture();
        fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);

        await fixture.Service.QueryDeviceStatusAsync(fixture.Address);
        await fixture.Service.DisconnectForApplicationCloseAsync();
        await fixture.Service.ResumeAfterBackgroundAsync();

        Assert.False(fixture.Connection.Snapshot.IsConnected);
        Assert.Equal(1, fixture.Ble.DisconnectAttempts);
        Assert.Single(fixture.Ble.ConnectAttempts);
    }

    [Theory]
    [InlineData(AgpLightMode.Blink, false)]
    [InlineData(AgpLightMode.Breathe, false)]
    [InlineData(AgpLightMode.Blink, true)]
    [InlineData(AgpLightMode.Breathe, true)]
    public async Task SettingDynamicEffectAlonePreservesPowerState(
        AgpLightMode mode, bool isOn)
    {
        await using var fixture = new PairingFixture();
        fixture.Ble.Capabilities |= AgpCapabilities.Effects;
        fixture.Ble.LightStatePacket = LightStatePacket(isOn, isOn ? (byte)50 : (byte)0);
        fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);
        await fixture.Service.QueryDeviceStatusAsync(fixture.Address);

        var response = await fixture.Service.SetEffectAsync(fixture.Address, mode, 1_000);
        var cached = fixture.Service.GetCachedDeviceStatus(fixture.Address)!;

        Assert.Equal(mode, response.Mode);
        Assert.Equal(isOn, cached.LightState!.IsOn);
        Assert.Equal(mode, cached.LightState.Mode);
        Assert.Equal(mode, cached.Profile!.EffectMode);
        Assert.Equal((ushort)1_000, cached.Profile.EffectPeriodMilliseconds);
        Assert.DoesNotContain(AgpOpcode.OnOffSet, fixture.Ble.Requests);
    }

    [Fact]
    public async Task StartingSteadyEffectFromOffDoesNotInventPowerOnState()
    {
        await using var fixture = new PairingFixture();
        fixture.Ble.Capabilities |= AgpCapabilities.Effects;
        fixture.Ble.LightStatePacket = LightStatePacket(false, 0);
        fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);
        await fixture.Service.QueryDeviceStatusAsync(fixture.Address);

        await fixture.Service.SetEffectAsync(
            fixture.Address, AgpLightMode.Steady, periodMilliseconds: 0);

        var cached = fixture.Service.GetCachedDeviceStatus(fixture.Address)!;
        Assert.False(cached.LightState!.IsOn);
        Assert.Equal(AgpLightMode.Steady, cached.LightState.Mode);
        Assert.DoesNotContain(AgpOpcode.OnOffSet, fixture.Ble.Requests);
    }

    [Fact]
    public async Task RejectedEffectDoesNotChangeCachedPowerOrMode()
    {
        await using var fixture = new PairingFixture();
        fixture.Ble.Capabilities |= AgpCapabilities.Effects;
        fixture.Ble.LightStatePacket = LightStatePacket(false, 0);
        fixture.Ble.EffectResult = AgpResultCode.NotAuthorized;
        fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);
        await fixture.Service.QueryDeviceStatusAsync(fixture.Address);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.SetEffectAsync(fixture.Address, AgpLightMode.Blink, 1_000));

        var cached = fixture.Service.GetCachedDeviceStatus(fixture.Address)!;
        Assert.False(cached.LightState!.IsOn);
        Assert.Equal(AgpLightMode.Steady, cached.LightState.Mode);
        Assert.Equal(AgpLightMode.Steady, cached.Profile!.EffectMode);
        Assert.DoesNotContain(AgpOpcode.OnOffSet, fixture.Ble.Requests);
    }

    private static int StateReadCount(PairingFixture fixture) =>
        fixture.Ble.Reads.Count(id => id == AgpProfile.LightStateCharacteristicId);

    private static byte[] LightStatePacket(bool isOn, byte brightness) => AgpCodec.Encode(
        AgpMessageType.Status, AgpOpcode.LightStateStatus, 0,
        [2, 0, 0, 0, isOn ? (byte)1 : (byte)0, brightness, brightness, 50, 0, 0, 0]);
}
