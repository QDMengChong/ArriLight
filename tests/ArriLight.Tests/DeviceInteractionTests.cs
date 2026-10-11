using System.Text.Json;
using ArriOpto.BLE.Agp;
using Xunit;

namespace ArriLight.Tests;

public sealed class DeviceInteractionTests
{
    [Theory]
    [InlineData(DeviceSortOrder.LastOperation, "P A C B D")]
    [InlineData(DeviceSortOrder.AddedAt, "A P D B C")]
    [InlineData(DeviceSortOrder.Name, "P A C B D")]
    public async Task SortingAlwaysKeepsPinnedDevicesFirstAndPersists(DeviceSortOrder order, string expected)
    {
        await using var fixture = new PairingFixture();
        var at = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
        SavedDevice[] devices =
        [
            new("Zulu pinned", "A", at.AddDays(3), IsPinned: true, LastOperatedAt: at.AddDays(2)),
            new("Alpha pinned", "P", at.AddDays(1), IsPinned: true, LastOperatedAt: at.AddDays(4)),
            new("Bravo", "B", at.AddDays(4), LastOperatedAt: at.AddDays(1)),
            new("Alpha", "C", at.AddDays(2), LastOperatedAt: at.AddDays(3)),
            new("Charlie", "D", at.AddDays(5))
        ];
        Preferences.Default.Set("saved_bluetooth_devices_v1", JsonSerializer.Serialize(devices));
        var home = Home(fixture);
        Assert.Equal(0, home.SelectedSortIndex);
        home.SelectedSortIndex = (int)order;
        Assert.Equal(expected.Split(' '), home.Devices.Select(device => device.Address));
        Assert.Equal(order, new DeviceRepository().GetSortOrder());
        Assert.Equal((int)order, Home(fixture).SelectedSortIndex);
    }

    [Fact]
    public async Task SuccessfulControlReordersExistingRowsWithoutDroppingTheirBusyGuard()
    {
        await using var fixture = ToggleFixture();
        var other = fixture.AddDevice();
        var home = Home(fixture);
        await home.OnAppearingAsync();
        try
        {
            var device = home.Devices.Single(item => item.Address == fixture.Address);
            device.IsPowerChanging = true;
            fixture.Repository.RecordOperation(fixture.Address);
            Assert.Same(device, home.Devices[0]);
            Assert.True(device.IsPowerChanging);
            Assert.False(device.IsEnabled);
            device.IsPowerChanging = false;
            fixture.Repository.SetPinned(other.Address, true);
            await home.TogglePowerCommand.ExecuteAsync(device);
            Assert.Equal(other.Address, home.Devices[0].Address);
            Assert.NotNull(new DeviceRepository().GetByAddress(fixture.Address)!.LastOperatedAt);
            Assert.Same(device, home.Devices.Single(item => item.Address == fixture.Address));
        }
        finally { home.OnDisappearing(); }
    }

    [Fact]
    public async Task HomeWithoutHardwareStateStillSendsToggleAndNeverStartsScanning()
    {
        await using var fixture = ToggleFixture();
        fixture.Ble.OnLightStateRead = () => throw new IOException("state unavailable");
        var home = Home(fixture);
        await home.OnAppearingAsync();
        try
        {
            var device = home.Devices.Single(item => item.Address == fixture.Address);
            Assert.False(device.IsOnline);
            Assert.True(device.IsEnabled);
            await home.TogglePowerCommand.ExecuteAsync(device);
            var toggle = Assert.Single(fixture.Ble.RequestMessages, message => message.Opcode == AgpOpcode.OnOffSet);
            Assert.Equal(new byte[] { 2 }, toggle.Payload.ToArray());
            Assert.DoesNotContain(AgpProfile.LightStateCharacteristicId, fixture.Ble.Reads);
            Assert.False(device.IsOn); // Fake hardware started ON despite the unknown/off display.
            Assert.True(device.IsOnline);
            Assert.Equal(0, fixture.Discovery.ScanCalls);
            Assert.Equal(0, fixture.Discovery.BackgroundScanCalls);
        }
        finally { home.OnDisappearing(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ControlToggleSchedulesAutomaticOffOnlyWhenHardwareTurnsOn(bool initiallyOn)
    {
        await using var fixture = ToggleFixture();
        var packet = fixture.Ble.LightStatePacket.ToArray();
        packet[13] = initiallyOn ? (byte)1 : (byte)0;
        fixture.Ble.LightStatePacket = packet;
        fixture.Ble.OnLightStateRead = () => throw new IOException("initial reads forbidden");
        await fixture.Service.ToggleControlPagePowerAsync(fixture.Address, 60);
        Assert.Equal(new byte[] { 2 }, Assert.Single(fixture.Ble.RequestMessages,
            request => request.Opcode == AgpOpcode.OnOffSet).Payload.ToArray());
        Assert.Equal(!initiallyOn, fixture.Service.GetCachedDeviceStatus(fixture.Address)!.LightState!.IsOn);
        Assert.Equal(initiallyOn ? 0 : 1, fixture.Ble.Requests.Count(opcode => opcode == AgpOpcode.DelayOffSet));
    }

    [Fact]
    public async Task AcceptedToggleWithUnavailableResultIsNotRetriedOrInventedAsAnOffState()
    {
        await using var fixture = ToggleFixture();
        fixture.Ble.SuppressToggleNotification = true;
        fixture.Ble.OnLightStateRead = () => throw new IOException("read unavailable");
        await fixture.Service.ToggleOnOffAsync(fixture.Address);
        Assert.Single(fixture.Ble.RequestMessages, request => request.Opcode == AgpOpcode.OnOffSet);
        Assert.Null(fixture.Service.GetCachedDeviceStatus(fixture.Address)!.LightState);
        Assert.NotNull(fixture.Repository.GetByAddress(fixture.Address)!.LastOperatedAt);
    }

    [Fact]
    public async Task LeavingAddPageCancelsScanAndReturningStartsAFreshPageOwnedScan()
    {
        await using var fixture = new PairingFixture();
        var scan = new BluetoothScanPageViewModel(fixture.Discovery, fixture.Repository, fixture.Service,
            new TestUiDispatcher(), new TestUserInteraction(), new TestAppNavigator());
        await scan.OnAppearingAsync();
        var first = fixture.Discovery.ScanToken;
        Assert.Equal(1, fixture.Discovery.ScanCalls);
        scan.OnDisappearing();
        Assert.True(first.IsCancellationRequested);
        await scan.ScanForTest.WaitAsync(TimeSpan.FromSeconds(2));
        await scan.OnAppearingAsync();
        try
        {
            Assert.Equal(2, fixture.Discovery.ScanCalls);
            Assert.False(fixture.Discovery.ScanToken.IsCancellationRequested);
            Assert.Equal(0, fixture.Discovery.BackgroundScanCalls);
        }
        finally { scan.OnDisappearing(); }
    }

    [Fact]
    public async Task ControlPagePowerButtonCanConnectWithoutKnownOnlineOrLightState()
    {
        await using var fixture = ToggleFixture();
        fixture.Ble.OnLightStateRead = () => throw new IOException("no initial state");
        var model = new DeviceControlPageViewModel(fixture.Repository, fixture.Discovery,
            fixture.Connection, fixture.Service, new AppDiagnosticService(), new TestUiDispatcher(),
            new TestAppNavigator(), new TestUserInteraction(), new StubMeshDeviceControlService());
        model.ApplyQueryAttributes(new Dictionary<string, object> { ["address"] = fixture.Address, ["name"] = "PW01" });
        await model.OnAppearingAsync();
        try
        {
            Assert.False(model.IsDeviceOnline);
            Assert.True(model.CanTogglePower);
            await model.MasterPowerCommand.ExecuteAsync(null);
            Assert.Equal(new byte[] { 2 }, Assert.Single(fixture.Ble.RequestMessages,
                request => request.Opcode == AgpOpcode.OnOffSet).Payload.ToArray());
            Assert.False(model.IsMasterPowerOn);
            Assert.True(model.CanTogglePower);
        }
        finally { model.OnDisappearing(); }
    }

    [Fact]
    public async Task ApplicationBackgroundAlsoStopsAddPageScanning()
    {
        await using var fixture = new PairingFixture();
        var app = new App(fixture.Service, fixture.Discovery);
        var window = app.CreateWindowForTest();
        window.Stop();
        await fixture.Discovery.StopStartedForTest.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, fixture.Discovery.StopCalls);
    }

    private static PairingFixture ToggleFixture()
    {
        var fixture = new PairingFixture();
        fixture.Ble.Capabilities = AgpCapabilities.OnOff | AgpCapabilities.RelativeTimer | AgpCapabilities.SecureControl;
        fixture.Repository.SetTokenAndCapabilities(fixture.Address, [1, 2, 3, 4], fixture.Ble.Capabilities);
        return fixture;
    }

    private static MainPageViewModel Home(PairingFixture fixture) => new(
        fixture.Repository, fixture.Discovery, fixture.Service, fixture.Connection, new AppDiagnosticService(),
        new TestUiDispatcher(), new TestAppNavigator(), new TestUserInteraction(), new StubMeshDeviceControlService());
}
