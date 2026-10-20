using ArriLight.ViewModels;
using ArriOpto.BLE.Agp;
using Xunit;

namespace ArriLight.Tests;

public sealed class HomeBatteryDisplayTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ReadyDeviceHidesBatteryUntilItsBackgroundReadCompletes()
    {
        await using var fixture = CreateFixture();
        var home = Home(fixture);
        await home.OnAppearingAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Ble.BeforeReadAsync = id =>
        {
            if (id != AgpProfile.BatteryStateCharacteristicId) return Task.CompletedTask;
            entered.TrySetResult();
            return release.Task;
        };
        try
        {
            var device = Device(home, fixture);
            Assert.False(device.IsBatteryVisible);
            Assert.Null(device.BatteryLevelPercent);

            await fixture.Service.StartRuntimeAsync();
            await entered.Task.WaitAsync(Deadline);

            Assert.True(device.IsOnline);
            Assert.True(device.IsReady);
            Assert.Equal(AppResources.Device_Ready, device.Status);
            Assert.False(device.IsBatteryVisible);
            Assert.Null(device.BatteryLevelPercent);

            release.TrySetResult();
            await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
            Assert.True(device.IsBatteryVisible);
            Assert.Equal("77%", device.BatteryLevelText);
        }
        finally
        {
            release.TrySetResult();
            home.OnDisappearing();
        }
    }

    [Theory]
    [InlineData(0, true, "0%")]
    [InlineData(77, true, "77%")]
    [InlineData(100, true, "100%")]
    [InlineData(255, false, "")]
    public async Task ReadyHomeDisplaysOnlyKnownBatteryPercentages(byte level, bool visible, string text)
    {
        await using var fixture = CreateFixture(level: level);
        await Prepare(fixture);
        var home = Home(fixture);

        var device = Device(home, fixture);
        Assert.True(device.IsReady);
        Assert.Equal(AppResources.Device_Ready, device.Status);
        Assert.Equal(visible, device.IsBatteryVisible);
        Assert.Equal(text, device.BatteryLevelText);
        Assert.Equal(level <= 100 ? level : (byte?)null, device.BatteryLevelPercent);
        Assert.Single(fixture.Ble.Reads, id => id == AgpProfile.BatteryStateCharacteristicId);
    }

    [Fact]
    public async Task BatteryNotificationsUpdateTheVisibleHomeRowAndHideUnknownValues()
    {
        await using var fixture = CreateFixture();
        await Prepare(fixture);
        var home = Home(fixture);
        await home.OnAppearingAsync();
        var device = Device(home, fixture);
        var changed = new List<string?>();
        device.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        try
        {
            fixture.Ble.NotifyBatteryState(State(35));
            Assert.Equal<byte?>(35, device.BatteryLevelPercent);
            Assert.Equal("35%", device.BatteryLevelText);
            Assert.True(device.IsBatteryVisible);
            Assert.Contains(nameof(LightDeviceItem.BatteryLevelText), changed);
            Assert.Contains(nameof(LightDeviceItem.IsBatteryVisible), changed);

            fixture.Ble.NotifyBatteryState(State(101));
            Assert.Equal("35%", device.BatteryLevelText);

            fixture.Ble.NotifyBatteryState(State(255));
            Assert.Null(device.BatteryLevelPercent);
            Assert.False(device.IsBatteryVisible);
            Assert.Equal(string.Empty, device.BatteryLevelText);

            fixture.Ble.NotifyBatteryState(State(0));
            Assert.True(device.IsBatteryVisible);
            Assert.Equal("0%", device.BatteryLevelText);
        }
        finally { home.OnDisappearing(); }
    }

    [Fact]
    public async Task DisconnectHidesBatteryAndReconnectReadsTheNewSessionValue()
    {
        await using var fixture = CreateFixture();
        await Prepare(fixture);
        var home = Home(fixture);
        await home.OnAppearingAsync();
        try
        {
            var device = Device(home, fixture);
            Assert.True(device.IsBatteryVisible);

            await fixture.Service.SuspendForBackgroundAsync().WaitAsync(Deadline);
            Assert.False(device.IsOnline);
            Assert.False(device.IsBatteryVisible);
            Assert.Null(device.BatteryLevelPercent);
            Assert.False(Device(Home(fixture), fixture).IsBatteryVisible);

            fixture.Ble.BatteryStatePacket = State(60);
            await fixture.Service.ResumeAfterBackgroundAsync().WaitAsync(Deadline);
            await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
            Assert.Same(device, Device(home, fixture));
            Assert.True(device.IsReady);
            Assert.True(device.IsBatteryVisible);
            Assert.Equal("60%", device.BatteryLevelText);
            Assert.Equal(2, fixture.Ble.Reads.Count(id => id == AgpProfile.BatteryStateCharacteristicId));
        }
        finally { home.OnDisappearing(); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OpeningControlReusesTheBatteryAlreadyReadForHome(bool characteristic)
    {
        await using var fixture = CreateFixture(characteristic);
        await Prepare(fixture);
        var home = Home(fixture);
        Assert.Equal("77%", Device(home, fixture).BatteryLevelText);
        var batteryReads = fixture.Ble.Reads.Count(id => id == AgpProfile.BatteryStateCharacteristicId);
        var batteryQueries = fixture.Ble.Requests.Count(op => op == AgpOpcode.BatteryGet);
        Assert.Equal(characteristic ? 1 : 0, batteryReads);
        Assert.Equal(characteristic ? 0 : 1, batteryQueries);
        fixture.Ble.BatteryStatePacket = State(20);

        var control = new DeviceControlPageViewModel(fixture.Repository, fixture.Discovery, fixture.Connection,
            fixture.Service, new AppDiagnosticService(), new TestUiDispatcher(), new TestAppNavigator(),
            new TestUserInteraction(), new StubMeshDeviceControlService());
        control.ApplyQueryAttributes(new Dictionary<string, object> { ["address"] = fixture.Address });
        await control.OnAppearingAsync();
        try
        {
            await control.EnsureConnectedAsync(showError: false).WaitAsync(Deadline);
            Assert.True(control.IsBatterySupported);
            Assert.Equal<byte?>(77, control.BatteryLevelPercent);
            Assert.Equal("77%", control.BatteryLevelText);
            Assert.Equal(batteryReads, fixture.Ble.Reads.Count(id => id == AgpProfile.BatteryStateCharacteristicId));
            Assert.Equal(batteryQueries, fixture.Ble.Requests.Count(op => op == AgpOpcode.BatteryGet));
        }
        finally { control.OnDisappearing(); }
    }

    [Theory]
    [InlineData(false, BluetoothDeviceProtocol.Agp, true, false)]
    [InlineData(false, BluetoothDeviceProtocol.Agp, false, true)]
    [InlineData(true, BluetoothDeviceProtocol.Agp, true, true)]
    [InlineData(false, BluetoothDeviceProtocol.Mesh, true, true)]
    public void BatteryIconIsHiddenForRowsThatAreNotReadyPhysicalAgpDevices(
        bool demo, BluetoothDeviceProtocol protocol, bool online, bool ready)
    {
        var device = new LightDeviceItem("Light", "address", AppResources.Device_Ready,
            isDemo: demo, isOnline: online, protocol: protocol)
        {
            IsReady = ready,
            BatteryLevelPercent = 77
        };

        Assert.False(device.IsBatteryVisible);
    }

    private static PairingFixture CreateFixture(bool characteristic = true, byte level = 77)
    {
        var fixture = new PairingFixture();
        fixture.Ble.Capabilities = AgpCapabilities.OnOff | AgpCapabilities.Lightness |
            AgpCapabilities.SecureControl | AgpCapabilities.Battery;
        fixture.Ble.BatteryStatePacket = State(level);
        if (characteristic) fixture.Ble.EnableBatteryState();
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

    private static LightDeviceItem Device(MainPageViewModel home, PairingFixture fixture) =>
        Assert.Single(home.Devices, item => item.Address == fixture.Address);

    private static byte[] State(byte level) => AgpCodec.Encode(AgpMessageType.Status, AgpOpcode.BatteryStatus, 0,
        [level, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);
}
