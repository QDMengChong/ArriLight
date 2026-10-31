using ArriOpto.BLE.Agp;
using Xunit;

namespace ArriLight.Tests;

public sealed class TurnOnDelayTests
{
    [Fact]
    public void DelaySettingsDefaultAndPersistIndependentlyForEachDevice()
    {
        Preferences.Default.Clear();
        var repository = new DeviceRepository();
        const string first = "02:00:00:00:00:01";
        const string second = "02:00:00:00:00:02";
        Assert.True(repository.TryAdd("First device", first));
        Assert.True(repository.TryAdd("Second device", second));
        Assert.False(repository.GetTurnOnDelayEnabled(first));
        Assert.Equal(60, repository.GetTurnOnDelaySeconds(first));
        repository.SetTurnOnDelayEnabled(first, true);
        repository.SetTurnOnDelaySeconds(first, 135);
        repository.SetTimerSeconds(first, 900);
        var reloaded = new DeviceRepository();
        Assert.True(reloaded.GetTurnOnDelayEnabled(first));
        Assert.Equal(135, reloaded.GetTurnOnDelaySeconds(first));
        Assert.Equal(900, reloaded.GetTimerSeconds(first));
        Assert.False(reloaded.GetTurnOnDelayEnabled(second));
        Assert.Equal(60, reloaded.GetTurnOnDelaySeconds(second));
        reloaded.SetTurnOnDelayEnabled(first, false);
        Assert.Equal(135, new DeviceRepository().GetTurnOnDelaySeconds(first));
    }

    [Fact]
    public void ExistingDeviceRecordsKeepCheckboxAndSecondsForAutomaticOff()
    {
        Preferences.Default.Clear();
        Preferences.Default.Set("saved_bluetooth_devices_v1", """
            [{"Name":"旧设备","Address":"AA:BB:CC:DD:EE:01","TurnOnDelayEnabled":true,"TurnOnDelaySeconds":135}]
            """);
        var repository = new DeviceRepository();
        Assert.True(repository.GetTurnOnDelayEnabled("AA:BB:CC:DD:EE:01"));
        Assert.Equal(135, repository.GetTurnOnDelaySeconds("AA:BB:CC:DD:EE:01"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(86401)]
    public async Task InvalidDelayIsRejectedBeforeSending(int seconds)
    {
        await using var fixture = CreateFixture();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            fixture.Service.SetControlPageOnOffAsync(fixture.Address, true, seconds));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            fixture.Service.SetControlPageEffectAsync(fixture.Address, AgpLightMode.Blink, 1000, seconds));
        Assert.Empty(fixture.Ble.Requests);
    }

    [Fact]
    public async Task UncheckedPowerOnSendsOnlyOnOffAndPreservesSelectedEffect()
    {
        await using var fixture = CreateFixture();
        await fixture.Service.SetEffectAsync(fixture.Address, AgpLightMode.Blink, 1000);
        fixture.Ble.Requests.Clear();
        await fixture.Service.SetControlPageOnOffAsync(fixture.Address, true, 0);
        Assert.Equal([AgpOpcode.OnOffSet], Controls(fixture));
        var state = fixture.Service.GetCachedDeviceStatus(fixture.Address)!.LightState!;
        Assert.True(state.IsOn);
        Assert.Equal(AgpLightMode.Blink, state.Mode);
    }

    [Theory]
    [InlineData(1, "01000000")]
    [InlineData(60, "3C000000")]
    [InlineData(86400, "80510100")]
    public async Task CheckedPowerOnImmediatelySendsPowerThenDeviceAutomaticOff(int seconds, string payload)
    {
        await using var fixture = CreateFixture();
        await fixture.Service.SetControlPageOnOffAsync(fixture.Address, true, seconds);
        Assert.Equal([AgpOpcode.OnOffSet, AgpOpcode.DelayOffSet], Controls(fixture));
        Assert.Equal(new byte[] { 1 }, Request(fixture, AgpOpcode.OnOffSet).Payload.ToArray());
        Assert.Equal(Convert.FromHexString(payload), Request(fixture, AgpOpcode.DelayOffSet).Payload.ToArray());
        Assert.True(fixture.Service.GetCachedDeviceStatus(fixture.Address)!.LightState!.IsOn);
        await fixture.Service.SuspendForBackgroundAsync();
        Assert.Equal([AgpOpcode.OnOffSet, AgpOpcode.DelayOffSet], Controls(fixture));
    }

    [Theory]
    [InlineData(AgpLightMode.Steady, 0, 0, "000000")]
    [InlineData(AgpLightMode.Blink, 1000, 0, "01E803")]
    [InlineData(AgpLightMode.Breathe, 2500, 0, "02C409")]
    [InlineData(AgpLightMode.Steady, 0, 60, "000000")]
    [InlineData(AgpLightMode.Blink, 1000, 60, "01E803")]
    [InlineData(AgpLightMode.Breathe, 2500, 60, "02C409")]
    public async Task EveryEffectIsImmediatelyFollowedByPowerOnAndOptionalAutomaticOff(
        AgpLightMode mode, ushort period, int seconds, string payload)
    {
        await using var fixture = CreateFixture();
        var response = await fixture.Service.SetControlPageEffectAsync(fixture.Address, mode, period, seconds);
        Assert.Equal(mode, response.Mode);
        Assert.Equal(period, response.PeriodMilliseconds);
        Assert.Equal(seconds == 0
            ? new[] { AgpOpcode.EffectSet, AgpOpcode.OnOffSet }
            : [AgpOpcode.EffectSet, AgpOpcode.OnOffSet, AgpOpcode.DelayOffSet], Controls(fixture));
        Assert.Equal(Convert.FromHexString(payload), Request(fixture, AgpOpcode.EffectSet).Payload.ToArray());
        var cached = fixture.Service.GetCachedDeviceStatus(fixture.Address)!;
        Assert.True(cached.LightState!.IsOn);
        Assert.Equal(mode, cached.LightState.Mode);
        Assert.Equal(period, cached.Profile!.EffectPeriodMilliseconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(60)]
    public async Task PowerOffSendsOnlyOffWithoutChangingOrClearingEffect(int seconds)
    {
        await using var fixture = CreateFixture();
        await fixture.Service.SetControlPageEffectAsync(fixture.Address, AgpLightMode.Breathe, 2500, 60);
        fixture.Ble.Requests.Clear();
        await fixture.Service.SetControlPageOnOffAsync(fixture.Address, false, seconds);
        Assert.Equal([AgpOpcode.OnOffSet], Controls(fixture));
        Assert.Equal(new byte[] { 0 }, fixture.Ble.RequestMessages.Last().Payload.ToArray());
        var cached = fixture.Service.GetCachedDeviceStatus(fixture.Address)!;
        Assert.False(cached.LightState!.IsOn);
        Assert.Equal(AgpLightMode.Breathe, cached.LightState.Mode);
    }

    [Fact]
    public async Task EffectFailureStopsBeforePowerOrTimerAndPreservesState()
    {
        await using var fixture = CreateFixture();
        fixture.Ble.EffectResult = AgpResultCode.NotAuthorized;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.SetControlPageEffectAsync(fixture.Address, AgpLightMode.Blink, 1000, 60));
        Assert.Equal([AgpOpcode.EffectSet], Controls(fixture));
        var cached = fixture.Service.GetCachedDeviceStatus(fixture.Address)!;
        Assert.False(cached.LightState!.IsOn);
        Assert.Equal(AgpLightMode.Steady, cached.LightState.Mode);
    }

    [Fact]
    public async Task PowerFailureRetainsConfirmedEffectAndDoesNotSendTimer()
    {
        await using var fixture = CreateFixture();
        fixture.Ble.OnOffResult = AgpResultCode.InternalError;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.SetControlPageEffectAsync(fixture.Address, AgpLightMode.Blink, 1000, 60));
        Assert.Equal([AgpOpcode.EffectSet, AgpOpcode.OnOffSet], Controls(fixture));
        var cached = fixture.Service.GetCachedDeviceStatus(fixture.Address)!;
        Assert.False(cached.LightState!.IsOn);
        Assert.Equal(AgpLightMode.Blink, cached.LightState.Mode);
        Assert.Equal((ushort)1000, cached.Profile!.EffectPeriodMilliseconds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimerFailureKeepsConfirmedPowerAndEffect(bool withEffect)
    {
        await using var fixture = CreateFixture();
        fixture.Ble.TimerResult = AgpResultCode.InternalError;
        await Assert.ThrowsAsync<InvalidOperationException>(() => withEffect
            ? fixture.Service.SetControlPageEffectAsync(fixture.Address, AgpLightMode.Breathe, 2500, 60)
            : fixture.Service.SetControlPageOnOffAsync(fixture.Address, true, 60));
        var cached = fixture.Service.GetCachedDeviceStatus(fixture.Address)!;
        Assert.True(cached.LightState!.IsOn);
        Assert.Equal(withEffect ? AgpLightMode.Breathe : AgpLightMode.Steady, cached.LightState.Mode);
        Assert.Equal(AgpOpcode.DelayOffSet, Controls(fixture).Last());
    }

    [Fact]
    public async Task BusyRetriesOnlyPowerStepWithNewTransactionId()
    {
        await using var fixture = CreateFixture();
        fixture.Ble.OnOffResults.Enqueue(AgpResultCode.Busy);
        fixture.Ble.OnOffResults.Enqueue(AgpResultCode.Success);
        await fixture.Service.SetControlPageEffectAsync(fixture.Address, AgpLightMode.Blink, 1000, 60);
        Assert.Equal([AgpOpcode.EffectSet, AgpOpcode.OnOffSet, AgpOpcode.OnOffSet, AgpOpcode.DelayOffSet], Controls(fixture));
        var attempts = fixture.Ble.RequestMessages.Where(request => request.Opcode == AgpOpcode.OnOffSet).ToArray();
        Assert.NotEqual(attempts[0].TransactionId, attempts[1].TransactionId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingRequiredCapabilityRejectsSequenceBeforeEffectOrPower(bool missingTimer)
    {
        await using var fixture = CreateFixture();
        fixture.Ble.Capabilities &= ~(missingTimer ? AgpCapabilities.RelativeTimer : AgpCapabilities.OnOff);
        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Service.SetControlPageEffectAsync(
            fixture.Address, AgpLightMode.Blink, 1000, missingTimer ? 60 : 0));
        Assert.Empty(Controls(fixture));
    }

    [Fact]
    public async Task CancellationWhilePowerResponseIsPendingRetainsSuccessfulEffect()
    {
        await using var fixture = CreateFixture();
        fixture.Ble.SuppressedResponseOpcode = AgpOpcode.OnOffSet;
        using var cancellation = new CancellationTokenSource();
        var sending = fixture.Service.SetControlPageEffectAsync(
            fixture.Address, AgpLightMode.Blink, 1000, 60, cancellation.Token);
        Assert.Equal([AgpOpcode.EffectSet, AgpOpcode.OnOffSet], Controls(fixture));
        cancellation.Cancel();
        await Assert.ThrowsAsync<AgpBatchCanceledException>(() => sending);
        var cached = fixture.Service.GetCachedDeviceStatus(fixture.Address)!;
        Assert.Equal(AgpLightMode.Blink, cached.LightState!.Mode);
        Assert.False(cached.LightState.IsOn);
        Assert.DoesNotContain(AgpOpcode.DelayOffSet, fixture.Ble.Requests);
    }

    [Fact]
    public async Task BackgroundCancellationStopsUnconfirmedPowerWithoutLosingEffectSelection()
    {
        await using var fixture = CreateFixture();
        fixture.Ble.SuppressedResponseOpcode = AgpOpcode.OnOffSet;
        var sending = fixture.Service.SetControlPageEffectAsync(fixture.Address, AgpLightMode.Blink, 1000, 60);
        await fixture.Service.SuspendForBackgroundAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<AgpBatchCanceledException>(() => sending);
        Assert.DoesNotContain(AgpOpcode.DelayOffSet, fixture.Ble.Requests);
        Assert.Equal(AgpLightMode.Blink, fixture.Service.GetCachedDeviceStatus(fixture.Address)!.LightState!.Mode);
    }

    [Fact]
    public async Task TransportFailureAfterEffectRetainsProgressWithoutInventingPowerOn()
    {
        await using var fixture = CreateFixture();
        fixture.Ble.BeforeControlResponse = request =>
        {
            if (request.Opcode == AgpOpcode.OnOffSet)
                throw new IOException("radio failed");
        };
        await Assert.ThrowsAsync<AgpBatchException>(() => fixture.Service.SetControlPageEffectAsync(
            fixture.Address, AgpLightMode.Blink, 1000, 60));
        var cached = fixture.Service.GetCachedDeviceStatus(fixture.Address)!;
        Assert.Equal(AgpLightMode.Blink, cached.LightState!.Mode);
        Assert.False(cached.LightState.IsOn);
        Assert.DoesNotContain(AgpOpcode.DelayOffSet, fixture.Ble.Requests);
    }

    private static PairingFixture CreateFixture()
    {
        var fixture = new PairingFixture();
        fixture.Ble.Capabilities = AgpCapabilities.SecureControl | AgpCapabilities.OnOff |
                                   AgpCapabilities.Effects | AgpCapabilities.RelativeTimer;
        fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);
        fixture.Ble.LightStatePacket = AgpCodec.Encode(AgpMessageType.Status, AgpOpcode.LightStateStatus, 0,
            [2, 0, 0, 0, 0, 0, 0, 50, 0, 0, 0]);
        return fixture;
    }

    private static AgpOpcode[] Controls(PairingFixture fixture) => fixture.Ble.Requests
        .Where(opcode => opcode != AgpOpcode.AuthToken).ToArray();

    private static AgpMessage Request(PairingFixture fixture, AgpOpcode opcode) =>
        Assert.Single(fixture.Ble.RequestMessages, request => request.Opcode == opcode);
}
