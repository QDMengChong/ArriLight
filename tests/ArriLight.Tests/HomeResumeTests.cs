using ArriOpto.BLE.Agp;
using Xunit;

namespace ArriLight.Tests;

public sealed class HomeResumeTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task FirstHomePowerClickChangesTheDisplayedStateOnlyOnce(
        bool initiallyOn, bool queuedUiEvents)
    {
        await using var fixture = new PairingFixture();
        fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);
        fixture.Ble.Capabilities |= AgpCapabilities.OnOff;
        fixture.Ble.LightStatePacket = LightStatePacket(initiallyOn);
        await fixture.Service.QueryDeviceStatusAsync(fixture.Address);
        var page = CreatePage(fixture);
        page.AppearForTest();
        var pending = new Queue<Action>();
        try
        {
            var device = Assert.Single(page.Devices, item => item.Address == fixture.Address);
            var displayedStates = new List<bool> { device.IsOn };
            device.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(LightDeviceItem.IsOn))
                    displayedStates.Add(device.IsOn);
            };
            if (queuedUiEvents)
            {
                MainThread.PendingActions = pending;
                fixture.Ble.BeforeControlResponse = request =>
                {
                    // Run the initial-state event while the command is still
                    // waiting for its response, as a real UI dispatcher can.
                    if (request.Opcode == AgpOpcode.OnOffSet)
                        while (pending.TryDequeue(out var update)) update();
                };
            }

            await page.TogglePowerForTestAsync(device);
            while (pending.TryDequeue(out var update)) update();

            Assert.Equal(new[] { initiallyOn, !initiallyOn }, displayedStates);
            Assert.Equal(!initiallyOn, device.IsOn);
            Assert.True(device.IsEnabled);
            Assert.Equal(!initiallyOn,
                fixture.Service.GetCachedDeviceStatus(fixture.Address)!.LightState!.IsOn);
            var command = Assert.Single(fixture.Ble.RequestMessages,
                request => request.Opcode == AgpOpcode.OnOffSet);
            Assert.Equal(!initiallyOn ? (byte)1 : (byte)0, command.Payload.Span[0]);
            Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.AuthToken);
            Assert.Empty(ToastService.Messages);
        }
        finally
        {
            MainThread.PendingActions = null;
            page.DisappearForTest();
        }
    }

    [Fact]
    public async Task PendingPowerClickKeepsItsIntentThroughQueryCleanupAndIgnoresDuplicateClicks()
    {
        await using var fixture = new PairingFixture();
        fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);
        fixture.Ble.Capabilities |= AgpCapabilities.OnOff;
        fixture.Ble.LightStatePacket = LightStatePacket(false);
        await fixture.Service.QueryDeviceStatusAsync(fixture.Address);
        var other = fixture.AddDevice();
        await fixture.Service.QueryDeviceStatusAsync(other.Address);
        var page = CreatePage(fixture);
        page.AppearForTest();
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        page.SetHomeRefreshForTest(cleanup.Task, cancellation);
        Task powerOperation = Task.CompletedTask;
        try
        {
            var device = page.Devices.Single(item => item.Address == fixture.Address);
            var otherDevice = page.Devices.Single(item => item.Address == other.Address);
            var displayedStates = new List<bool> { device.IsOn };
            device.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(LightDeviceItem.IsOn))
                    displayedStates.Add(device.IsOn);
            };
            powerOperation = page.TogglePowerForTestAsync(device);
            Assert.True(device.IsPowerChanging);
            Assert.False(device.IsEnabled);
            Assert.False(powerOperation.IsCompleted);
            await page.TogglePowerForTestAsync(device);

            // The query finishing during cancellation must not reverse the
            // captured click intent. Other devices still receive their updates.
            fixture.Ble.LightStatePacket = LightStatePacket(true);
            await fixture.Service.QueryDeviceStatusAsync(fixture.Address);
            other.LightStatePacket = LightStatePacket(false);
            await fixture.Service.QueryDeviceStatusAsync(other.Address);
            Assert.False(device.IsOn);
            Assert.True(device.IsOnline);
            Assert.False(otherDevice.IsOn);
            Assert.True(otherDevice.IsEnabled);

            cleanup.TrySetResult();
            await powerOperation.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(new[] { false, true }, displayedStates);
            Assert.False(device.IsPowerChanging);
            Assert.True(device.IsEnabled);
            var command = Assert.Single(fixture.Ble.RequestMessages,
                request => request.Opcode == AgpOpcode.OnOffSet);
            Assert.Equal((byte)1, command.Payload.Span[0]);
            Assert.Empty(ToastService.Messages);
        }
        finally
        {
            cleanup.TrySetResult();
            await powerOperation.WaitAsync(TimeSpan.FromSeconds(5));
            page.DisappearForTest();
        }
    }

    [Fact]
    public async Task FailedPowerCommandKeepsFreshHardwareStateWithoutRollingBackThroughAnOldState()
    {
        await using var fixture = new PairingFixture();
        fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);
        fixture.Ble.Capabilities |= AgpCapabilities.OnOff;
        fixture.Ble.LightStatePacket = LightStatePacket(false);
        await fixture.Service.QueryDeviceStatusAsync(fixture.Address);
        var page = CreatePage(fixture);
        page.AppearForTest();
        try
        {
            var device = Assert.Single(page.Devices, item => item.Address == fixture.Address);
            var displayedStates = new List<bool> { device.IsOn };
            device.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(LightDeviceItem.IsOn))
                    displayedStates.Add(device.IsOn);
            };
            fixture.Ble.LightStatePacket = LightStatePacket(true);
            fixture.Ble.BeforeControlResponse = request =>
            {
                if (request.Opcode == AgpOpcode.OnOffSet)
                    throw new IOException("write failed");
            };

            await page.TogglePowerForTestAsync(device);

            Assert.Equal(new[] { false, true }, displayedStates);
            Assert.True(device.IsOn);
            Assert.False(device.IsPowerChanging);
            Assert.True(device.IsEnabled);
            Assert.Contains(AppResources.Control_SendFailed, ToastService.Messages);
        }
        finally { page.DisappearForTest(); }
    }

    [Fact]
    public async Task QueryCleanupFailureReleasesPowerButtonAndReportsFailureWithoutSendingACommand()
    {
        await using var fixture = new PairingFixture();
        fixture.Ble.LightStatePacket = LightStatePacket(false);
        await fixture.Service.QueryDeviceStatusAsync(fixture.Address);
        var page = CreatePage(fixture);
        page.AppearForTest();
        using var cancellation = new CancellationTokenSource();
        page.SetHomeRefreshForTest(Task.FromException(new IOException("cleanup failed")), cancellation);
        try
        {
            var device = Assert.Single(page.Devices, item => item.Address == fixture.Address);

            await page.TogglePowerForTestAsync(device);

            Assert.False(device.IsOn);
            Assert.False(device.IsPowerChanging);
            Assert.True(device.IsEnabled);
            Assert.DoesNotContain(AgpOpcode.OnOffSet, fixture.Ble.Requests);
            Assert.Contains(AppResources.Control_SendFailed, ToastService.Messages);
        }
        finally { page.DisappearForTest(); }
    }

    [Fact]
    public async Task SubsequentPowerClicksAndHardwareNotificationsStillUpdateTheDisplayedState()
    {
        await using var fixture = new PairingFixture();
        fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);
        fixture.Ble.Capabilities |= AgpCapabilities.OnOff;
        fixture.Ble.LightStatePacket = LightStatePacket(false);
        await fixture.Service.QueryDeviceStatusAsync(fixture.Address);
        var page = CreatePage(fixture);
        page.AppearForTest();
        try
        {
            var device = Assert.Single(page.Devices, item => item.Address == fixture.Address);
            var displayedStates = new List<bool> { device.IsOn };
            device.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(LightDeviceItem.IsOn))
                    displayedStates.Add(device.IsOn);
            };

            await page.TogglePowerForTestAsync(device);
            await page.TogglePowerForTestAsync(device);
            fixture.Ble.NotifyLightState(LightStatePacket(true));

            Assert.Equal(new[] { false, true, false, true }, displayedStates);
            Assert.True(device.IsOn);
            Assert.True(device.IsEnabled);
            Assert.Equal(2, fixture.Ble.Requests.Count(opcode => opcode == AgpOpcode.OnOffSet));
            Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.AuthToken);
            Assert.Empty(ToastService.Messages);
        }
        finally { page.DisappearForTest(); }
    }

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
