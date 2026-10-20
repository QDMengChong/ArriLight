using ArriLight.ViewModels;
using ArriOpto.BLE.Agp;
using Xunit;

namespace ArriLight.Tests;

public sealed class AgpBatteryDisplayTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RuntimeBecomesReadyBeforeBatteryQueryCompletes(bool characteristic)
    {
        await using var fixture = CreateFixture(characteristic);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task PauseBatteryQuery()
        {
            entered.TrySetResult();
            return release.Task;
        }
        fixture.Ble.BeforeReadAsync = id => id == AgpProfile.BatteryStateCharacteristicId
            ? PauseBatteryQuery() : Task.CompletedTask;
        fixture.Ble.BeforeWriteAsync = request => request.Opcode == AgpOpcode.BatteryGet
            ? PauseBatteryQuery() : Task.CompletedTask;
        try
        {
            await fixture.Service.StartRuntimeAsync();
            await entered.Task.WaitAsync(Deadline);
            var snapshot = fixture.Service.GetRuntimeSnapshot(fixture.Address)!;
            Assert.Equal(DeviceRuntimeAvailability.Ready, snapshot.Availability);
            Assert.Equal(DeviceRuntimeStepStatus.Running, snapshot.Steps[DeviceRuntimeStep.BatteryState].Status);
            Assert.Null(fixture.Service.GetCachedDeviceStatus(fixture.Address)!.Profile!.BatteryState);

            release.TrySetResult();
            await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
            Assert.Equal<byte?>(80, fixture.Service.GetCachedDeviceStatus(fixture.Address)!.Profile!.BatteryState!.LevelPercent);
            if (characteristic)
                Assert.Single(fixture.Ble.Reads, id => id == AgpProfile.BatteryStateCharacteristicId);
            else
                Assert.Single(fixture.Ble.Requests, op => op == AgpOpcode.BatteryGet);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(80, "80%")]
    [InlineData(0, "0%")]
    [InlineData(100, "100%")]
    [InlineData(255, null)]
    public async Task SupportedDeviceShowsReadLevelIncludingEmptyAndUnknown(byte level, string? expected)
    {
        await using var fixture = CreateFixture();
        fixture.Ble.BatteryStatePacket = State(level);
        await Prepare(fixture);
        var model = await CreatePage(fixture);
        try
        {
            Assert.True(model.IsBatterySupported);
            Assert.Equal(expected ?? AppResources.Common_Unknown, model.BatteryLevelText);
            Assert.Equal(level == 255 ? null : (byte?)level, model.BatteryLevelPercent);
            Assert.Single(fixture.Ble.Subscriptions, id => id == AgpProfile.BatteryStateCharacteristicId);
            Assert.Single(fixture.Ble.Reads, id => id == AgpProfile.BatteryStateCharacteristicId);
            Assert.DoesNotContain(AgpOpcode.BatteryGet, fixture.Ble.Requests);
        }
        finally { model.OnDisappearing(); }
    }

    [Fact]
    public async Task DiscoveredBatteryCharacteristicEnablesDisplayWithoutTheCapabilityBit()
    {
        await using var fixture = CreateFixture(capability: false);
        await Prepare(fixture);
        var model = await CreatePage(fixture);
        try
        {
            Assert.True(model.IsBatterySupported);
            Assert.Equal("80%", model.BatteryLevelText);
        }
        finally { model.OnDisappearing(); }
    }

    [Fact]
    public async Task UnsupportedDeviceHidesBatteryAndSendsNoBatteryOperations()
    {
        await using var fixture = CreateFixture(characteristic: false, capability: false);
        await Prepare(fixture);
        var model = await CreatePage(fixture);
        try
        {
            Assert.False(model.IsBatterySupported);
            Assert.Null(model.BatteryLevelPercent);
            Assert.DoesNotContain(AgpProfile.BatteryStateCharacteristicId, fixture.Ble.Reads);
            Assert.DoesNotContain(AgpProfile.BatteryStateCharacteristicId, fixture.Ble.Subscriptions);
            Assert.DoesNotContain(AgpOpcode.BatteryGet, fixture.Ble.Requests);
        }
        finally { model.OnDisappearing(); }
    }

    [Fact]
    public async Task GetOnlyFirmwareShowsBatteryAndRefreshQueriesTheCurrentValue()
    {
        await using var fixture = CreateFixture(characteristic: false);
        await Prepare(fixture);
        var model = await CreatePage(fixture);
        try
        {
            Assert.True(model.IsBatterySupported);
            Assert.Equal("80%", model.BatteryLevelText);
            Assert.Single(fixture.Ble.Requests, op => op == AgpOpcode.BatteryGet);
            fixture.Ble.BatteryStatePacket = State(55);
            await model.EnsureConnectedAsync(showError: false, refreshStatus: true).WaitAsync(Deadline);
            Assert.Equal("55%", model.BatteryLevelText);
            Assert.Equal(2, fixture.Ble.Requests.Count(op => op == AgpOpcode.BatteryGet));
            Assert.DoesNotContain(AgpProfile.BatteryStateCharacteristicId, fixture.Ble.Subscriptions);
        }
        finally { model.OnDisappearing(); }
    }

    [Fact]
    public async Task NotificationsUpdateTheVisiblePageAndInvalidPacketsKeepTheLastValidValue()
    {
        await using var fixture = CreateFixture();
        await Prepare(fixture);
        var model = await CreatePage(fixture);
        try
        {
            fixture.Ble.NotifyBatteryState(State(79));
            Assert.Equal("79%", model.BatteryLevelText);
            fixture.Ble.NotifyBatteryState(State(101));
            Assert.Equal("79%", model.BatteryLevelText);
            fixture.Ble.NotifyBatteryState(State(255));
            Assert.Equal(AppResources.Common_Unknown, model.BatteryLevelText);
            fixture.Ble.NotifyBatteryState(State(0));
            Assert.Equal("0%", model.BatteryLevelText);
        }
        finally { model.OnDisappearing(); }
    }

    [Fact]
    public async Task NotificationReceivedDuringReadIsNotOverwrittenByTheOlderReadSnapshot()
    {
        await using var fixture = CreateFixture();
        fixture.Ble.BeforeReadAsync = id =>
        {
            if (id == AgpProfile.BatteryStateCharacteristicId) fixture.Ble.NotifyBatteryState(State(79));
            return Task.CompletedTask;
        };
        await Prepare(fixture);
        var model = await CreatePage(fixture);
        try { Assert.Equal("79%", model.BatteryLevelText); }
        finally { model.OnDisappearing(); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedBatteryReadLeavesTheDeviceReadyAndControlsUsable(bool characteristic)
    {
        await using var fixture = CreateFixture(characteristic);
        fixture.Ble.BatteryReadFailure = new IOException("battery read failed");
        fixture.Ble.BatteryGetResult = AgpResultCode.InternalError;
        await fixture.Service.StartRuntimeAsync();
        await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
        var snapshot = fixture.Service.GetRuntimeSnapshot(fixture.Address)!;
        Assert.Equal(DeviceRuntimeAvailability.Ready, snapshot.Availability);
        Assert.Equal(DeviceRuntimeStepStatus.Failed, snapshot.Steps[DeviceRuntimeStep.BatteryState].Status);
        var model = await CreatePage(fixture);
        try
        {
            Assert.True(model.IsBatterySupported);
            Assert.True(model.IsDeviceOnline);
            Assert.True(model.CanTogglePower);
            Assert.Equal(AppResources.Common_Unknown, model.BatteryLevelText);
            await fixture.Service.SetOnOffAsync(fixture.Address, true).WaitAsync(Deadline);
            Assert.Contains(AgpOpcode.OnOffSet, fixture.Ble.Requests);
        }
        finally { model.OnDisappearing(); }
    }

    [Fact]
    public async Task FailedBatterySubscriptionStillReadsTheSnapshot()
    {
        await using var fixture = CreateFixture();
        fixture.Ble.BatterySubscriptionFailure = new IOException("battery CCC failed");
        await fixture.Service.StartRuntimeAsync();
        await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
        var snapshot = fixture.Service.GetRuntimeSnapshot(fixture.Address)!;
        Assert.Equal(DeviceRuntimeAvailability.Ready, snapshot.Availability);
        Assert.Equal(DeviceRuntimeStepStatus.Failed, snapshot.Steps[DeviceRuntimeStep.BatterySubscription].Status);
        var model = await CreatePage(fixture);
        try
        {
            Assert.True(model.IsDeviceOnline);
            Assert.Equal("80%", model.BatteryLevelText);
        }
        finally { model.OnDisappearing(); }
    }

    [Fact]
    public async Task LeavingThePageStopsUiUpdatesWhileTheRuntimeKeepsTheLatestBattery()
    {
        await using var fixture = CreateFixture();
        await Prepare(fixture);
        var model = await CreatePage(fixture);
        model.OnDisappearing();
        fixture.Ble.NotifyBatteryState(State(70));
        Assert.Equal("80%", model.BatteryLevelText);
        Assert.Equal<byte?>(70, fixture.Service.GetCachedDeviceStatus(fixture.Address)!.Profile!.BatteryState!.LevelPercent);
        await model.OnAppearingAsync();
        try { Assert.Equal("70%", model.BatteryLevelText); }
        finally { model.OnDisappearing(); }
    }

    [Fact]
    public async Task CachedBatterySupportShowsUnknownUntilTheDeviceHasBeenRead()
    {
        await using var fixture = CreateFixture();
        var model = await CreatePage(fixture);
        try
        {
            Assert.True(model.IsBatterySupported);
            Assert.Equal(AppResources.Common_Unknown, model.BatteryLevelText);
            Assert.Empty(fixture.Ble.Reads);
        }
        finally { model.OnDisappearing(); }
    }

    [Fact]
    public async Task ManualInitializationWithoutTheRuntimeAlsoReadsAndSubscribesBattery()
    {
        await using var fixture = CreateFixture();
        var model = await CreatePage(fixture);
        try
        {
            await model.EnsureConnectedAsync(showError: false).WaitAsync(Deadline);
            Assert.True(model.IsBatterySupported);
            Assert.Equal("80%", model.BatteryLevelText);
            fixture.Ble.NotifyBatteryState(State(79));
            Assert.Equal("79%", model.BatteryLevelText);
        }
        finally { model.OnDisappearing(); }
    }

    [Fact]
    public async Task SwitchingDeviceDoesNotCarryOverThePreviousBatteryDisplay()
    {
        await using var fixture = CreateFixture();
        await Prepare(fixture);
        var model = await CreatePage(fixture);
        model.OnDisappearing();
        var peer = fixture.AddDevice();
        model.ApplyQueryAttributes(new Dictionary<string, object> { ["address"] = peer.Address });
        await model.OnAppearingAsync();
        try
        {
            Assert.False(model.IsBatterySupported);
            Assert.Null(model.BatteryLevelPercent);
            Assert.Equal(AppResources.Common_Unknown, model.BatteryLevelText);
        }
        finally { model.OnDisappearing(); }
    }

    [Fact]
    public async Task BackgroundDisconnectShowsUnknownAndReconnectReadsANewSnapshot()
    {
        await using var fixture = CreateFixture();
        await Prepare(fixture);
        var model = await CreatePage(fixture);
        try
        {
            await fixture.Service.SuspendForBackgroundAsync().WaitAsync(Deadline);
            Assert.Equal(AppResources.Common_Unknown, model.BatteryLevelText);
            fixture.Ble.BatteryStatePacket = State(60);
            await fixture.Service.ResumeAfterBackgroundAsync().WaitAsync(Deadline);
            await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
            Assert.Equal("60%", model.BatteryLevelText);
            Assert.Equal(2, fixture.Ble.Subscriptions.Count(id => id == AgpProfile.BatteryStateCharacteristicId));
        }
        finally { model.OnDisappearing(); }
    }

    private static PairingFixture CreateFixture(bool characteristic = true, bool capability = true)
    {
        var fixture = new PairingFixture();
        var capabilities = AgpCapabilities.OnOff | AgpCapabilities.Lightness | AgpCapabilities.SecureControl;
        if (capability) capabilities |= AgpCapabilities.Battery;
        fixture.Ble.Capabilities = capabilities;
        if (characteristic) fixture.Ble.EnableBatteryState();
        fixture.Repository.SetTokenAndCapabilities(fixture.Address, [1, 2, 3, 4], capabilities);
        return fixture;
    }

    private static async Task Prepare(PairingFixture fixture)
    {
        await fixture.Service.StartRuntimeAsync();
        await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
    }

    private static async Task<DeviceControlPageViewModel> CreatePage(PairingFixture fixture)
    {
        var model = new DeviceControlPageViewModel(fixture.Repository, fixture.Discovery, fixture.Connection,
            fixture.Service, new AppDiagnosticService(), new TestUiDispatcher(), new TestAppNavigator(),
            new TestUserInteraction(), new StubMeshDeviceControlService());
        model.ApplyQueryAttributes(new Dictionary<string, object> { ["address"] = fixture.Address });
        await model.OnAppearingAsync();
        return model;
    }

    private static byte[] State(byte level) => AgpCodec.Encode(AgpMessageType.Status, AgpOpcode.BatteryStatus, 0,
        [level, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);
}
