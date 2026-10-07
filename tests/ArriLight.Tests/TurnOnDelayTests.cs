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
        Assert.False(repository.GetTurnOnDelayEnabled(first));
        Assert.Equal(60, repository.GetTurnOnDelaySeconds(first));

        Assert.True(repository.SetTurnOnDelayEnabled(first, true));
        Assert.True(repository.SetTurnOnDelaySeconds(first, 135));
        Assert.True(repository.SetTimerSeconds(first, 900));
        var reloaded = new DeviceRepository();
        Assert.True(reloaded.GetTurnOnDelayEnabled(first));
        Assert.Equal(135, reloaded.GetTurnOnDelaySeconds(first));
        Assert.Equal(900, reloaded.GetTimerSeconds(first));
        Assert.False(reloaded.GetTurnOnDelayEnabled(second));
        Assert.Equal(60, reloaded.GetTurnOnDelaySeconds(second));

        Assert.True(reloaded.SetTurnOnDelayEnabled(first, false));
        Assert.False(new DeviceRepository().GetTurnOnDelayEnabled(first));
        Assert.Equal(135, new DeviceRepository().GetTurnOnDelaySeconds(first));
    }

    [Fact]
    public void LegacyDevicesStartWithDelayUncheckedAnd60Seconds()
    {
        Preferences.Default.Clear();
        Preferences.Default.Set("saved_bluetooth_devices_v1", """
            [{"Name":"旧设备","Address":"AA:BB:CC:DD:EE:01","TimerSeconds":900}]
            """);
        var repository = new DeviceRepository();
        Assert.False(repository.GetTurnOnDelayEnabled("AA:BB:CC:DD:EE:01"));
        Assert.Equal(60, repository.GetTurnOnDelaySeconds("AA:BB:CC:DD:EE:01"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(86401)]
    public void InvalidSecondsDoNotOverwriteTheRememberedDelay(int seconds)
    {
        Preferences.Default.Clear();
        var repository = new DeviceRepository();
        const string address = "02:00:00:00:00:01";
        repository.SetTurnOnDelaySeconds(address, 135);
        Assert.False(repository.SetTurnOnDelaySeconds(address, seconds));
        Assert.Equal(135, new DeviceRepository().GetTurnOnDelaySeconds(address));
        Assert.False(repository.SetTurnOnDelayEnabled("missing", true));
    }

    [Fact]
    public async Task UncheckedDelayPreservesImmediatePowerAndEffectCommands()
    {
        await using var fixture = new PairingFixture();
        fixture.Ble.Capabilities = AgpCapabilities.OnOff | AgpCapabilities.Effects;
        await fixture.Service.SetControlPageOnOffAsync(fixture.Address, true, 0);
        await fixture.Service.SetControlPageEffectAsync(fixture.Address, AgpLightMode.Blink, 1000, 0);
        Assert.Equal([AgpOpcode.OnOffSet, AgpOpcode.EffectSet], fixture.Ble.Requests);
        Assert.False(fixture.Service.HasPendingControlTurnOn(fixture.Address));
    }

    [Fact]
    public async Task CheckedDelayUsesDeviceTimerAndDoesNotPublishPowerOnBeforeExpiry()
    {
        await using var fixture = new PairingFixture();
        fixture.Ble.Capabilities = AgpCapabilities.RelativeTimer;
        fixture.Ble.LightStatePacket = OffLightStatePacket();
        await fixture.Service.SetControlPageOnOffAsync(fixture.Address, true, 135);
        Assert.Equal([AgpOpcode.DelayOnSet], fixture.Ble.Requests);
        Assert.Equal(Convert.FromHexString("87000000"), fixture.Ble.RequestMessages.Single().Payload.ToArray());
        Assert.False(fixture.Service.GetCachedDeviceStatus(fixture.Address)!.LightState!.IsOn);
        Assert.True(fixture.Service.HasPendingControlTurnOn(fixture.Address));
        await fixture.Service.SuspendForBackgroundAsync();
        Assert.True(fixture.Service.HasPendingControlTurnOn(fixture.Address));
    }

    [Fact]
    public async Task PowerOffCancelsNativeCountdownBeforeImmediatelyTurningOff()
    {
        await using var fixture = new PairingFixture();
        fixture.Ble.Capabilities = AgpCapabilities.OnOff | AgpCapabilities.RelativeTimer;
        await fixture.Service.SetControlPageOnOffAsync(fixture.Address, true, 60);
        await fixture.Service.SetControlPageOnOffAsync(fixture.Address, false, 60);
        Assert.Equal([AgpOpcode.DelayOnSet, AgpOpcode.DelayOnSet, AgpOpcode.OnOffSet], fixture.Ble.Requests);
        Assert.Equal(new byte[4], fixture.Ble.RequestMessages[1].Payload.ToArray());
        Assert.Equal((byte)0, fixture.Ble.RequestMessages[2].Payload.Span[0]);
        Assert.False(fixture.Service.HasPendingControlTurnOn(fixture.Address));
    }

    [Theory]
    [InlineData(AgpLightMode.Steady, 0)]
    [InlineData(AgpLightMode.Blink, 1000)]
    [InlineData(AgpLightMode.Breathe, 2500)]
    public async Task EffectsAreSentOnlyWhenTheConfiguredSecondsHaveElapsed(AgpLightMode mode, ushort period)
    {
        var clock = new ManualTimeProvider();
        await using var fixture = new PairingFixture(clock);
        await PrepareEffectsAsync(fixture);
        var effect = fixture.Service.SetControlPageEffectAsync(fixture.Address, mode, period, 60);
        Assert.False(effect.IsCompleted);
        Assert.DoesNotContain(AgpOpcode.EffectSet, fixture.Ble.Requests);
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.False(effect.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        var response = await effect.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(mode, response.Mode);
        Assert.Equal(period, response.PeriodMilliseconds);
        Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.EffectSet);
        Assert.False(fixture.Service.HasPendingControlTurnOn(fixture.Address));
    }

    [Fact]
    public async Task PowerOffCancelsWaitingEffectWithoutHoldingTheBleLock()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = new PairingFixture(clock);
        await PrepareEffectsAsync(fixture);
        var effect = fixture.Service.SetControlPageEffectAsync(fixture.Address, AgpLightMode.Blink, 1000, 60);
        await fixture.Service.SetOnOffAsync(fixture.Address, false).WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => effect);
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.DoesNotContain(AgpOpcode.EffectSet, fixture.Ble.Requests);
        Assert.False(fixture.Service.HasPendingControlTurnOn(fixture.Address));
    }

    [Fact]
    public async Task NewEffectReplacesWaitingEffectAndUsesItsOwnDeadline()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = new PairingFixture(clock);
        await PrepareEffectsAsync(fixture);
        var first = fixture.Service.SetControlPageEffectAsync(fixture.Address, AgpLightMode.Blink, 1000, 60);
        clock.Advance(TimeSpan.FromSeconds(30));
        var second = fixture.Service.SetControlPageEffectAsync(fixture.Address, AgpLightMode.Breathe, 2500, 60);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.False(second.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(AgpLightMode.Breathe, (await second.WaitAsync(TimeSpan.FromSeconds(2))).Mode);
        Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.EffectSet);
    }

    [Fact]
    public async Task BackgroundingCancelsWaitingEffectRatherThanLightingAfterResume()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = new PairingFixture(clock);
        await PrepareEffectsAsync(fixture);
        var effect = fixture.Service.SetControlPageEffectAsync(fixture.Address, AgpLightMode.Blink, 1000, 60);
        await fixture.Service.SuspendForBackgroundAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => effect);
        clock.Advance(TimeSpan.FromSeconds(60));
        await fixture.Service.ResumeAfterBackgroundAsync();
        Assert.DoesNotContain(AgpOpcode.EffectSet, fixture.Ble.Requests);
    }

    [Fact]
    public async Task EffectReplacingNativeTimerCancelsTimerBeforeWaiting()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = new PairingFixture(clock);
        await PrepareEffectsAsync(fixture);
        await fixture.Service.SetControlPageOnOffAsync(fixture.Address, true, 60);
        var effect = fixture.Service.SetControlPageEffectAsync(fixture.Address, AgpLightMode.Blink, 1000, 60);
        // Flush the preflight cancellation through the same serialized client.
        await fixture.Service.EnsureAuthorizedAsync(fixture.Address);
        Assert.Equal(new byte[4], fixture.Ble.RequestMessages.Last().Payload.ToArray());
        clock.Advance(TimeSpan.FromSeconds(60));
        await effect.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal([AgpOpcode.DelayOnSet, AgpOpcode.DelayOnSet, AgpOpcode.EffectSet], fixture.Ble.Requests);
    }

    [Fact]
    public async Task RemovingDeviceCancelsItsWaitingEffect()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = new PairingFixture(clock);
        await PrepareEffectsAsync(fixture);
        var effect = fixture.Service.SetControlPageEffectAsync(fixture.Address, AgpLightMode.Blink, 1000, 60);
        await fixture.Service.RemoveDeviceAsync(fixture.Address);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => effect);
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.DoesNotContain(AgpOpcode.EffectSet, fixture.Ble.Requests);
    }

    [Fact]
    public async Task RejectedNativeTimerDoesNotLeaveAPendingPowerAction()
    {
        await using var fixture = new PairingFixture();
        fixture.Ble.Capabilities = AgpCapabilities.RelativeTimer;
        fixture.Ble.TimerResult = AgpResultCode.NotAuthorized;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.SetControlPageOnOffAsync(fixture.Address, true, 60));
        Assert.False(fixture.Service.HasPendingControlTurnOn(fixture.Address));
    }

    private static async Task PrepareEffectsAsync(PairingFixture fixture)
    {
        fixture.Ble.Capabilities = AgpCapabilities.OnOff | AgpCapabilities.Effects | AgpCapabilities.RelativeTimer;
        fixture.Ble.LightStatePacket = OffLightStatePacket();
        await fixture.Service.EnsureAuthorizedAsync(fixture.Address);
    }

    [Fact]
    public async Task PresetLightSettingsWaitWithTheEffectAndAreAppliedFirst()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = new PairingFixture(clock);
        await PrepareEffectsAsync(fixture);
        var applied = false;
        var effect = fixture.Service.SetControlPageEffectAsync(
            fixture.Address, AgpLightMode.Blink, 1000, 60,
            beforeEffect: token =>
            {
                token.ThrowIfCancellationRequested();
                Assert.DoesNotContain(AgpOpcode.EffectSet, fixture.Ble.Requests);
                applied = true;
                return Task.CompletedTask;
            });
        Assert.False(applied);
        clock.Advance(TimeSpan.FromSeconds(60));
        await effect.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(applied);
        Assert.Contains(AgpOpcode.EffectSet, fixture.Ble.Requests);
    }

    [Fact]
    public async Task CancellingPresetDoesNotApplyItsLightSettings()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = new PairingFixture(clock);
        await PrepareEffectsAsync(fixture);
        var applied = false;
        var effect = fixture.Service.SetControlPageEffectAsync(
            fixture.Address, AgpLightMode.Blink, 1000, 60,
            beforeEffect: _ => { applied = true; return Task.CompletedTask; });
        await fixture.Service.SetOnOffAsync(fixture.Address, false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => effect);
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.False(applied);
        Assert.DoesNotContain(AgpOpcode.EffectSet, fixture.Ble.Requests);
    }

    [Fact]
    public async Task DelayingOneDeviceDoesNotDelayOrCancelAnotherDevice()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = new PairingFixture(clock);
        await PrepareEffectsAsync(fixture);
        var other = fixture.AddDevice();
        other.Capabilities = AgpCapabilities.OnOff;
        var effect = fixture.Service.SetControlPageEffectAsync(fixture.Address, AgpLightMode.Blink, 1000, 60);
        await fixture.Service.SetOnOffAsync(other.Address, true).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(effect.IsCompleted);
        Assert.Equal([AgpOpcode.OnOffSet], other.Requests);
        clock.Advance(TimeSpan.FromSeconds(60));
        await effect.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Contains(AgpOpcode.EffectSet, fixture.Ble.Requests);
    }

    private static byte[] OffLightStatePacket() => AgpCodec.Encode(
        AgpMessageType.Status, AgpOpcode.LightStateStatus, 0,
        [2, 0, 0, 0, 0, 0, 0, 50, 0, 0, 0]);
}
