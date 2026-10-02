using ArriOpto.BLE.Agp;
using Xunit;

namespace ArriLight.Tests;

public sealed class HomeResumeTests
{
    [Fact]
    public async Task HomePowerConnectionResumesWithAnOnlineEnabledButtonAndFreshPowerStateWithoutAdvertising()
    {
        await using var fixture = new PairingFixture();
        fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);
        fixture.Ble.Capabilities |= AgpCapabilities.OnOff;
        await fixture.Service.QueryDeviceStatusAsync(fixture.Address);
        var page = CreatePage(fixture);
        page.AppearForTest();
        try
        {
            var device = Assert.Single(page.Devices, item => item.Address == fixture.Address);
            await page.TogglePowerForTestAsync(device);
            Assert.True(fixture.Connection.IsLongLived(fixture.Address));
            await fixture.Service.SuspendForBackgroundAsync();
            Assert.False(device.IsOnline);
            Assert.False(device.IsEnabled);

            // The page can be reloaded before the window's resume task finishes.
            page.DisappearForTest();
            page.AppearForTest();
            device = Assert.Single(page.Devices, item => item.Address == fixture.Address);
            Assert.False(device.IsOnline);
            fixture.Ble.LightStatePacket = LightStatePacket(true);
            await fixture.Service.ResumeAfterBackgroundAsync();

            Assert.True(device.IsOnline);
            Assert.True(device.IsEnabled);
            Assert.Equal("在线", device.Status);
            Assert.True(device.IsOn);
            Assert.False(fixture.Connection.HasActiveConnectionLeaseFor(fixture.Address));
            Assert.Equal(2, fixture.Ble.Requests.Count(opcode => opcode == AgpOpcode.AuthToken));
            var connections = fixture.Ble.ConnectAttempts.Count;
            await page.TogglePowerForTestAsync(device);
            Assert.False(device.IsOn);
            Assert.Equal(connections, fixture.Ble.ConnectAttempts.Count);
            Assert.Equal(2, fixture.Ble.Requests.Count(opcode => opcode == AgpOpcode.OnOffSet));
            Assert.Empty(ToastService.Messages);
        }
        finally { page.DisappearForTest(); }
    }

    [Fact]
    public async Task HomeOnlyResumeReadsFreshPublicStateAndUpdatesTheExistingPageWithoutTokenAuthentication()
    {
        await using var fixture = new PairingFixture();
        await fixture.Service.QueryDeviceStatusAsync(fixture.Address);
        var page = CreatePage(fixture);
        page.AppearForTest();
        try
        {
            var device = Assert.Single(page.Devices, item => item.Address == fixture.Address);
            await fixture.Service.SuspendForBackgroundAsync();
            fixture.Ble.LightStatePacket = LightStatePacket(false);
            await fixture.Service.ResumeAfterBackgroundAsync();
            Assert.Same(device, Assert.Single(page.Devices, item => item.Address == fixture.Address));
            Assert.True(device.IsOnline);
            Assert.True(device.IsEnabled);
            Assert.False(device.IsOn);
            Assert.True(fixture.Connection.HasActiveConnectionLeaseFor(fixture.Address));
            Assert.Equal(2, fixture.Ble.Reads.Count(id => id == AgpProfile.LightStateCharacteristicId));
            Assert.Empty(fixture.Ble.Requests);
        }
        finally { page.DisappearForTest(); }
    }

    [Fact]
    public async Task FailedRestoreLeavesOnlyTheFailedDeviceOfflineAndDisabled()
    {
        await using var fixture = new PairingFixture();
        var other = fixture.AddDevice();
        await fixture.Service.QueryDeviceStatusAsync(fixture.Address);
        await fixture.Service.QueryDeviceStatusAsync(other.Address);
        var page = CreatePage(fixture);
        page.AppearForTest();
        try
        {
            await fixture.Service.SuspendForBackgroundAsync();
            fixture.Ble.OnConnect = (_, _) => Task.FromException(new IOException("radio unavailable"));
            await fixture.Service.ResumeAfterBackgroundAsync();
            var failed = page.Devices.Single(device => device.Address == fixture.Address);
            var restored = page.Devices.Single(device => device.Address == other.Address);
            Assert.False(failed.IsOnline);
            Assert.False(failed.IsEnabled);
            Assert.True(restored.IsOnline);
            Assert.True(restored.IsEnabled);
        }
        finally { page.DisappearForTest(); }
    }

    [Fact]
    public async Task QueuedHomeQueryTimeoutCannotDisableAnAlreadyRestoredConnection()
    {
        await using var fixture = new PairingFixture();
        fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);
        await fixture.Service.EnsureAuthorizedAsync(fixture.Address);
        await fixture.Service.SuspendForBackgroundAsync();
        await fixture.Service.ResumeAfterBackgroundAsync();
        var page = CreatePage(fixture);
        var other = fixture.AddDevice();
        fixture.Repository.SetToken(other.Address, [5, 6, 7, 8]);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        other.OnConnect = async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        page.AppearForTest();
        var connecting = fixture.Service.EnsureAuthorizedAsync(other.Address);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var device = page.Devices.Single(item => item.Address == fixture.Address);
            await page.QueryForTestAsync(device, TimeSpan.FromMilliseconds(30));
            Assert.True(fixture.Connection.IsConnected(fixture.Address));
            Assert.True(fixture.Service.GetCachedDeviceStatus(fixture.Address)!.IsOnline);
            Assert.True(device.IsOnline);
            Assert.True(device.IsEnabled);
            Assert.False(device.IsChecking);
            Assert.Equal("在线", device.Status);
        }
        finally
        {
            release.TrySetResult();
            await connecting;
            page.DisappearForTest();
        }
    }

    [Fact]
    public async Task DelayedOfflineUiEventAppliesTheLatestSuccessfulRestoreState()
    {
        await using var fixture = new PairingFixture();
        fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);
        await fixture.Service.EnsureAuthorizedAsync(fixture.Address);
        var page = CreatePage(fixture);
        page.AppearForTest();
        var pending = new Queue<Action>();
        MainThread.PendingActions = pending;
        try
        {
            await fixture.Service.SuspendForBackgroundAsync();
            await fixture.Service.ResumeAfterBackgroundAsync();
            Assert.True(pending.Count >= 2);
            pending.Dequeue()(); // First event reported offline before reconnect.
            var device = Assert.Single(page.Devices, item => item.Address == fixture.Address);
            Assert.True(device.IsOnline);
            Assert.True(device.IsEnabled);
            Assert.Equal("在线", device.Status);
            while (pending.TryDequeue(out var update)) update();
        }
        finally
        {
            MainThread.PendingActions = null;
            page.DisappearForTest();
        }
    }

    [Fact]
    public async Task FailedHomePowerOperationKeepsTheReportedHardwareStateAfterSessionPreparationUpdatesThePage()
    {
        await using var fixture = new PairingFixture();
        fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);
        // No OnOff capability: preparing the client publishes the actual state,
        // then the unsupported command fails before it can change the hardware.
        await fixture.Service.QueryDeviceStatusAsync(fixture.Address);
        var page = CreatePage(fixture);
        page.AppearForTest();
        try
        {
            var device = Assert.Single(page.Devices, item => item.Address == fixture.Address);
            Assert.True(device.IsOn);
            await page.TogglePowerForTestAsync(device);
            Assert.True(device.IsOn);
            Assert.True(device.IsOnline);
            Assert.Contains("发送失败，请重试", ToastService.Messages);
            Assert.DoesNotContain(AgpOpcode.OnOffSet, fixture.Ble.Requests);
        }
        finally { page.DisappearForTest(); }
    }

    [Fact]
    public async Task SuspendedHomeQueryDoesNotLeaveThePageStuckInCheckingState()
    {
        await using var fixture = new PairingFixture();
        await fixture.Service.QueryDeviceStatusAsync(fixture.Address);
        var page = CreatePage(fixture);
        page.AppearForTest();
        try
        {
            await fixture.Service.SuspendForBackgroundAsync();
            var device = Assert.Single(page.Devices, item => item.Address == fixture.Address);
            await page.QueryForTestAsync(device, TimeSpan.FromSeconds(1));
            Assert.False(device.IsChecking);
            Assert.Equal("离线", device.Status);
            await fixture.Service.ResumeAfterBackgroundAsync();
            Assert.True(device.IsOnline);
            Assert.True(device.IsEnabled);
        }
        finally { page.DisappearForTest(); }
    }

    private static MainPage CreatePage(PairingFixture fixture)
    {
        ServiceHelper.Set(fixture.Repository);
        ServiceHelper.Set(fixture.Discovery);
        ServiceHelper.Set(fixture.Connection);
        ServiceHelper.Set(fixture.Service);
        ServiceHelper.Set(new AppDiagnosticService());
        ToastService.Messages.Clear();
        return new MainPage();
    }

    private static byte[] LightStatePacket(bool isOn) => AgpCodec.Encode(
        AgpMessageType.Status, AgpOpcode.LightStateStatus, 0,
        [2, 0, 0, 0, isOn ? (byte)1 : (byte)0, 50, 50, 50, 0, 0, 0]);
}
