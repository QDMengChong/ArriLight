using ArriLight.ViewModels;
using ArriOpto.BLE.Agp;
using Xunit;

namespace ArriLight.Tests;

public sealed class DeviceLockDisplayTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicReadShowsLockAlongsideOnlineStatus(bool locked)
    {
        await using var fixture = CreateFixture(locked);
        var home = Home(fixture);
        await home.OnAppearingAsync();
        try
        {
            await fixture.Service.QueryDeviceStatusAsync(fixture.Address);

            var device = Assert.Single(home.Devices, item => item.Address == fixture.Address);
            Assert.True(device.IsOnline);
            Assert.False(device.IsReady);
            Assert.Equal(AppResources.Device_Online, device.Status);
            Assert.Equal(locked, device.IsLocked);
        }
        finally { home.OnDisappearing(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitialRuntimeReadShowsLockOnReadyHomeAndControlPage(bool locked)
    {
        await using var fixture = CreateFixture(locked);
        await Prepare(fixture);
        var home = Home(fixture);
        var control = await Control(fixture);
        try
        {
            var device = Assert.Single(home.Devices, item => item.Address == fixture.Address);
            Assert.True(device.IsReady);
            Assert.Equal(AppResources.Device_Ready, device.Status);
            Assert.Equal(locked, device.IsLocked);
            Assert.True(control.IsDeviceOnline);
            Assert.Equal(locked, control.IsLocked);
        }
        finally { control.OnDisappearing(); }
    }

    [Fact]
    public async Task LockOnlyNotificationsUpdateBothPagesAndIgnoreOlderStates()
    {
        await using var fixture = CreateFixture(false);
        await Prepare(fixture);
        var home = Home(fixture);
        await home.OnAppearingAsync();
        var control = await Control(fixture);
        var rowChanges = new List<string?>();
        var controlChanges = new List<string?>();
        var device = Assert.Single(home.Devices, item => item.Address == fixture.Address);
        device.PropertyChanged += (_, args) => rowChanges.Add(args.PropertyName);
        control.PropertyChanged += (_, args) => controlChanges.Add(args.PropertyName);
        try
        {
            fixture.Ble.NotifyLightState(State(2, true));
            Assert.True(device.IsLocked);
            Assert.True(control.IsLocked);
            Assert.True(device.IsReady);
            Assert.True(control.IsDeviceOnline);
            Assert.Contains(nameof(device.IsLocked), rowChanges);
            Assert.Contains(nameof(control.IsLocked), controlChanges);

            fixture.Ble.NotifyLightState(State(1, false));
            Assert.True(device.IsLocked);
            Assert.True(control.IsLocked);

            fixture.Ble.NotifyLightState(State(3, false));
            Assert.False(device.IsLocked);
            Assert.False(control.IsLocked);
            Assert.False(device.IsOn);
            Assert.False(control.IsMasterPowerOn);
        }
        finally { home.OnDisappearing(); control.OnDisappearing(); }
    }

    [Fact]
    public async Task OfflineRetainsKnownLockAndReconnectReadsUnlockFromANewSession()
    {
        await using var fixture = CreateFixture(true);
        await Prepare(fixture);
        var home = Home(fixture);
        await home.OnAppearingAsync();
        var control = await Control(fixture);
        try
        {
            await fixture.Service.SuspendForBackgroundAsync().WaitAsync(Deadline);
            var device = Assert.Single(home.Devices, item => item.Address == fixture.Address);
            Assert.False(device.IsOnline);
            Assert.Equal(AppResources.Device_Offline, device.Status);
            Assert.True(device.IsLocked);
            Assert.False(control.IsDeviceOnline);
            Assert.True(control.IsLocked);

            var reopenedHome = Home(fixture);
            Assert.True(Assert.Single(reopenedHome.Devices, item => item.Address == fixture.Address).IsLocked);
            control.OnDisappearing();
            await control.OnAppearingAsync();
            Assert.True(control.IsLocked);

            fixture.Ble.LightStatePacket = State(1, false);
            await fixture.Service.ResumeAfterBackgroundAsync().WaitAsync(Deadline);
            await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
            Assert.False(device.IsLocked);
            Assert.False(control.IsLocked);
            Assert.True(device.IsReady);
        }
        finally { home.OnDisappearing(); control.OnDisappearing(); }
    }

    [Fact]
    public async Task SwitchingDeviceClearsThePreviousDevicesLockIndicator()
    {
        await using var fixture = CreateFixture(true);
        await fixture.Service.QueryDeviceStatusAsync(fixture.Address);
        var control = await Control(fixture);
        Assert.True(control.IsLocked);
        control.OnDisappearing();

        var peer = fixture.AddDevice();
        control.ApplyQueryAttributes(new Dictionary<string, object> { ["address"] = peer.Address });
        await control.OnAppearingAsync();
        try { Assert.False(control.IsLocked); }
        finally { control.OnDisappearing(); }
    }

    private static PairingFixture CreateFixture(bool locked)
    {
        var fixture = new PairingFixture();
        fixture.Ble.Capabilities = AgpCapabilities.OnOff | AgpCapabilities.Lightness | AgpCapabilities.SecureControl;
        fixture.Ble.LightStatePacket = State(1, locked);
        fixture.Repository.SetTokenAndCapabilities(fixture.Address, [1, 2, 3, 4], fixture.Ble.Capabilities);
        return fixture;
    }

    private static async Task Prepare(PairingFixture fixture)
    {
        await fixture.Service.StartRuntimeAsync();
        await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
    }

    private static MainPageViewModel Home(PairingFixture fixture) => new(
        fixture.Repository, fixture.Discovery, fixture.Service, fixture.Connection, new AppDiagnosticService(),
        new TestUiDispatcher(), new TestAppNavigator(), new TestUserInteraction(), new StubMeshDeviceControlService());

    private static async Task<DeviceControlPageViewModel> Control(PairingFixture fixture)
    {
        var model = new DeviceControlPageViewModel(fixture.Repository, fixture.Discovery, fixture.Connection,
            fixture.Service, new AppDiagnosticService(), new TestUiDispatcher(), new TestAppNavigator(),
            new TestUserInteraction(), new StubMeshDeviceControlService());
        model.ApplyQueryAttributes(new Dictionary<string, object> { ["address"] = fixture.Address });
        await model.OnAppearingAsync();
        return model;
    }

    private static byte[] State(uint revision, bool locked)
    {
        var packet = Convert.FromHexString("01000200020000000B0100000000000064000000");
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(9, 4), revision);
        packet[19] = locked ? (byte)AgpLightStateFlags.Locked : (byte)0;
        return packet;
    }
}
