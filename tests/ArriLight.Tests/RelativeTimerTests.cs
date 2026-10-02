using ArriOpto.BLE.Agp;
using Xunit;

namespace ArriLight.Tests;

public sealed class RelativeTimerTests
{
    [Fact]
    public void TimerSecondsArePersistedPerSavedDevice()
    {
        Preferences.Default.Clear();
        var repository = new DeviceRepository();
        Assert.Equal(60, repository.GetTimerSeconds("02:00:00:00:00:01"));

        Assert.True(repository.SetTimerSeconds("02:00:00:00:00:01", 135));
        var reloaded = new DeviceRepository();

        Assert.Equal(135, reloaded.GetTimerSeconds("02:00:00:00:00:01"));
        Assert.Equal(60, reloaded.GetTimerSeconds("02:00:00:00:00:02"));
        Assert.Equal(60, reloaded.GetTimerSeconds("missing"));
    }

    [Fact]
    public void LegacyDeviceRecordsDefaultTo60SecondsAndNoCustomPresets()
    {
        Preferences.Default.Clear();
        Preferences.Default.Set("saved_bluetooth_devices_v1", """
            [{"Name":"旧设备","Address":"AA:BB:CC:DD:EE:01"}]
            """);
        var repository = new DeviceRepository();

        Assert.Equal(60, repository.GetTimerSeconds("AA:BB:CC:DD:EE:01"));
        Assert.Empty(repository.GetCustomPresets("AA:BB:CC:DD:EE:01"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(86401)]
    public void InvalidTimerSecondsDoNotOverwriteTheRememberedValue(int seconds)
    {
        Preferences.Default.Clear();
        var repository = new DeviceRepository();
        const string address = "02:00:00:00:00:01";
        Assert.True(repository.SetTimerSeconds(address, 135));

        Assert.False(repository.SetTimerSeconds(address, seconds));

        Assert.Equal(135, new DeviceRepository().GetTimerSeconds(address));
    }

    [Theory]
    [InlineData(true, AgpOpcode.DelayOnSet)]
    [InlineData(false, AgpOpcode.DelayOffSet)]
    public async Task BothTimerDirectionsRequireOnlyRelativeTimerAndSendSeconds(
        bool turnOn, AgpOpcode expectedOpcode)
    {
        await using var fixture = new PairingFixture();
        fixture.Ble.Capabilities = AgpCapabilities.SecureControl | AgpCapabilities.RelativeTimer;
        fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);

        await fixture.Service.SetTimerAsync(fixture.Address, TimeSpan.FromSeconds(60), turnOn);

        Assert.Equal([AgpOpcode.AuthToken, expectedOpcode], fixture.Ble.Requests);
        var request = Assert.Single(fixture.Ble.RequestMessages, request => request.Opcode == expectedOpcode);
        Assert.Equal(Convert.FromHexString("3C000000"), request.Payload.ToArray());
        Assert.False(fixture.Ble.Capabilities.HasFlag(AgpCapabilities.RtcTime));
        Assert.False(fixture.Ble.Capabilities.HasFlag(AgpCapabilities.Scheduler));
    }

    [Theory]
    [InlineData(true, AgpOpcode.DelayOnSet)]
    [InlineData(false, AgpOpcode.DelayOffSet)]
    public async Task ZeroSecondsSendsCancellationInSelectedDirection(bool turnOn, AgpOpcode expectedOpcode)
    {
        await using var fixture = new PairingFixture();
        fixture.Ble.Capabilities = AgpCapabilities.RelativeTimer;

        await fixture.Service.SetTimerAsync(fixture.Address, TimeSpan.Zero, turnOn);

        Assert.Equal([expectedOpcode], fixture.Ble.Requests);
        Assert.Equal(new byte[4], Assert.Single(fixture.Ble.RequestMessages).Payload.ToArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RtcAndSchedulerDoNotSubstituteForRelativeTimer(bool turnOn)
    {
        await using var fixture = new PairingFixture();
        fixture.Ble.Capabilities = AgpCapabilities.RtcTime | AgpCapabilities.Scheduler;

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            fixture.Service.SetTimerAsync(fixture.Address, TimeSpan.FromSeconds(60), turnOn));

        Assert.DoesNotContain(AgpOpcode.DelayOnSet, fixture.Ble.Requests);
        Assert.DoesNotContain(AgpOpcode.DelayOffSet, fixture.Ble.Requests);
        Assert.DoesNotContain(AgpOpcode.ScheduleSet, fixture.Ble.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FractionalSecondsRoundUpForBothDirections(bool turnOn)
    {
        await using var fixture = new PairingFixture();
        fixture.Ble.Capabilities = AgpCapabilities.RelativeTimer;

        await fixture.Service.SetTimerAsync(fixture.Address, TimeSpan.FromMilliseconds(1500), turnOn);

        Assert.Equal(Convert.FromHexString("02000000"), Assert.Single(fixture.Ble.RequestMessages).Payload.ToArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ThirtyDayMaximumUsesAllFourPayloadBytes(bool turnOn)
    {
        await using var fixture = new PairingFixture();
        fixture.Ble.Capabilities = AgpCapabilities.RelativeTimer;

        await fixture.Service.SetTimerAsync(fixture.Address, TimeSpan.FromDays(30), turnOn);

        Assert.Equal(Convert.FromHexString("008D2700"), Assert.Single(fixture.Ble.RequestMessages).Payload.ToArray());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2_592_001)]
    public async Task InvalidDelayIsRejectedBeforeConnecting(int seconds)
    {
        await using var fixture = new PairingFixture();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            fixture.Service.SetTimerAsync(fixture.Address, TimeSpan.FromSeconds(seconds), turnOn: true));

        Assert.Empty(fixture.Ble.ConnectAttempts);
        Assert.Empty(fixture.Ble.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TimerBusinessFailureIsNotReportedAsSuccessfulGattWrite(bool turnOn)
    {
        await using var fixture = new PairingFixture();
        fixture.Ble.Capabilities = AgpCapabilities.RelativeTimer;
        fixture.Ble.TimerResult = AgpResultCode.NotAuthorized;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.SetTimerAsync(fixture.Address, TimeSpan.FromSeconds(60), turnOn));

        Assert.Contains("NotAuthorized", error.Message);
    }
}
