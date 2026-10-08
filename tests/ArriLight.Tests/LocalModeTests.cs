using ArriOpto.BLE.Agp;
using Xunit;

namespace ArriLight.Tests;

public sealed class LocalModeTests
{
    [Theory]
    [InlineData(AgpLocalMode.OnAutoOff, 0u, 60u, "043C000000")]
    [InlineData(AgpLocalMode.OffAutoOn, 120u, 0u, "0578000000")]
    public async Task ModeServiceAuthenticatesAndSendsExactAutomaticModePayload(
        AgpLocalMode mode, uint on, uint off, string payload)
    {
        await using var fixture = CreateFixture();
        var configuration = new AgpModeConfig(mode, on, off);
        Assert.Equal(configuration, await fixture.Service.SetLocalModeAsync(fixture.Address, configuration));
        Assert.Equal([AgpOpcode.AuthToken, AgpOpcode.ModeSet], fixture.Ble.Requests);
        var request = fixture.Ble.RequestMessages.Last();
        Assert.Equal(Convert.FromHexString(payload), request.Payload.ToArray());
        Assert.Equal(configuration, await fixture.Service.GetLocalModeAsync(fixture.Address));
        Assert.Empty(fixture.Ble.RequestMessages.Last().Payload.ToArray());
    }

    [Fact]
    public async Task MissingCapabilityNeverSendsModeRequests()
    {
        await using var fixture = CreateFixture();
        fixture.Ble.Capabilities = AgpCapabilities.SecureControl;
        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Service.SetLocalModeAsync(
            fixture.Address, new(AgpLocalMode.DelayNone)));
        Assert.DoesNotContain(AgpOpcode.ModeSet, fixture.Ble.Requests);
    }

    [Fact]
    public async Task InvalidConfigurationIsRejectedBeforeConnecting()
    {
        await using var fixture = CreateFixture();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => fixture.Service.SetLocalModeAsync(
            fixture.Address, new(AgpLocalMode.OnAutoOff, OffSeconds: 2_592_001)));
        Assert.Empty(fixture.Ble.ConnectAttempts);
        Assert.Empty(fixture.Ble.Requests);
    }

    [Fact]
    public async Task ControlPageLoadsDefaultAndOnlySettingSendsTheSelectedMode()
    {
        await using var fixture = CreateFixture();
        var page = CreatePage(fixture);
        try
        {
            await page.LoadModeForTestAsync();
            Assert.True(page.IsAlwaysOnMode);
            Assert.Equal(AppResources.DeviceMode_AlwaysOn, page.LocalModeLabel);
            Assert.Equal("60", page.TurnOnDelaySecondsText);
            Assert.True(page.CanSetLocalMode);
            Assert.False(page.CanEditDelaySeconds);

            page.IsAlwaysOnMode = false;
            Assert.Equal(AppResources.DeviceMode_Delayed, page.LocalModeLabel);
            Assert.True(page.CanEditDelaySeconds);
            Assert.DoesNotContain(AgpOpcode.ModeSet, fixture.Ble.Requests);
            await page.SetLocalModeCommand.ExecuteAsync(null);
            Assert.Equal(Convert.FromHexString("043C000000"), fixture.Ble.RequestMessages.Last().Payload.ToArray());
            Assert.Equal(new(AgpLocalMode.OnAutoOff, OffSeconds: 60), fixture.Ble.LocalMode);
            Assert.Equal(AppResources.DeviceMode_SetSucceeded, ToastService.Messages.Last());

            page.IsAlwaysOnMode = true;
            page.TurnOnDelaySecondsText = "invalid but disabled";
            Assert.False(page.CanEditDelaySeconds);
            await page.SetLocalModeCommand.ExecuteAsync(null);
            Assert.Equal(new byte[] { 0 }, fixture.Ble.RequestMessages.Last().Payload.ToArray());
            Assert.Equal(new(AgpLocalMode.DelayNone), fixture.Ble.LocalMode);
        }
        finally { page.OnDisappearing(); }
    }

    [Theory]
    [InlineData("1")]
    [InlineData("2592000")]
    public async Task ReopeningReadsDeviceStoredModeAndFullRangeSeconds(string seconds)
    {
        await using var fixture = CreateFixture();
        var page = CreatePage(fixture);
        try
        {
            await page.LoadModeForTestAsync();
            page.IsAlwaysOnMode = false;
            page.TurnOnDelaySecondsText = seconds;
            await page.SetLocalModeCommand.ExecuteAsync(null);
        }
        finally { page.OnDisappearing(); }

        var reopened = CreatePage(fixture);
        try
        {
            await reopened.LoadModeForTestAsync();
            Assert.False(reopened.IsAlwaysOnMode);
            Assert.Equal(seconds, reopened.TurnOnDelaySecondsText);
            Assert.True(reopened.CanEditDelaySeconds);
        }
        finally { reopened.OnDisappearing(); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("2592001")]
    [InlineData("4294967296")]
    public async Task InvalidSecondsDoNotSendOrChangeDeviceConfiguration(string text)
    {
        await using var fixture = CreateFixture();
        var page = CreatePage(fixture);
        try
        {
            await page.LoadModeForTestAsync();
            page.IsAlwaysOnMode = false;
            page.TurnOnDelaySecondsText = text;
            await page.SetLocalModeCommand.ExecuteAsync(null);
            Assert.DoesNotContain(AgpOpcode.ModeSet, fixture.Ble.Requests);
            Assert.Equal(new(AgpLocalMode.DelayNone), fixture.Ble.LocalMode);
            Assert.Equal(AppResources.DeviceMode_InvalidSeconds, ToastService.Messages.Last());
            Assert.Equal(text, page.TurnOnDelaySecondsText);
        }
        finally { page.OnDisappearing(); }
    }

    [Fact]
    public async Task FailedDeviceSaveRetainsDraftAndOriginalDeviceConfiguration()
    {
        await using var fixture = CreateFixture();
        var page = CreatePage(fixture);
        try
        {
            await page.LoadModeForTestAsync();
            fixture.Ble.ModeResult = AgpResultCode.InternalError;
            page.IsAlwaysOnMode = false;
            page.TurnOnDelaySecondsText = "90";
            await page.SetLocalModeCommand.ExecuteAsync(null);
            Assert.Equal(new(AgpLocalMode.DelayNone), fixture.Ble.LocalMode);
            Assert.False(page.IsAlwaysOnMode);
            Assert.Equal("90", page.TurnOnDelaySecondsText);
            Assert.True(page.CanSetLocalMode);
            Assert.Contains(nameof(AgpResultCode.InternalError), ToastService.Messages.Last());
            Assert.DoesNotContain(AppResources.DeviceMode_SetSucceeded, ToastService.Messages);
        }
        finally { page.OnDisappearing(); }
    }

    [Fact]
    public async Task UnsupportedDeviceHasNoEditableModeCardOrModeRequest()
    {
        await using var fixture = CreateFixture();
        fixture.Ble.Capabilities = AgpCapabilities.SecureControl;
        var page = CreatePage(fixture);
        try
        {
            await page.LoadModeForTestAsync();
            await page.SetLocalModeCommand.ExecuteAsync(null);
            Assert.False(page.IsLocalModeSupported);
            Assert.False(page.CanSetLocalMode);
            Assert.DoesNotContain(AgpOpcode.ModeGet, fixture.Ble.Requests);
            Assert.DoesNotContain(AgpOpcode.ModeSet, fixture.Ble.Requests);
        }
        finally { page.OnDisappearing(); }
    }

    [Theory]
    [InlineData(AgpLocalMode.OffAutoOn, 120u, 0u)]
    [InlineData(AgpLocalMode.DelayBoth, 60u, 120u)]
    public async Task OtherDeviceModesAreReportedAndNeverChangedOnLoad(AgpLocalMode mode, uint on, uint off)
    {
        await using var fixture = CreateFixture();
        var original = new AgpModeConfig(mode, on, off);
        fixture.Ble.LocalMode = original;
        var page = CreatePage(fixture);
        try
        {
            await page.LoadModeForTestAsync();
            Assert.Equal(AppResources.DeviceMode_OtherMode, page.LocalModeNotice);
            Assert.Equal(original, fixture.Ble.LocalMode);
            Assert.DoesNotContain(AgpOpcode.ModeSet, fixture.Ble.Requests);
        }
        finally { page.OnDisappearing(); }
    }

    [Fact]
    public async Task LeavingWhileLoadingCancelsModeReadAndDoesNotShowStaleMessages()
    {
        await using var fixture = CreateFixture();
        fixture.Ble.SuppressedResponseOpcode = AgpOpcode.ModeGet;
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Ble.BeforeControlResponse = request =>
        {
            if (request.Opcode == AgpOpcode.ModeGet) reading.TrySetResult();
        };
        var page = CreatePage(fixture);
        var loading = page.LoadModeForTestAsync();
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(page.IsLocalModeBusy);
        page.OnDisappearing();
        await loading.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(page.IsLocalModeSupported);
        Assert.False(page.IsLocalModeBusy);
        Assert.Empty(ToastService.Messages);
        Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.ModeGet);
    }

    [Fact]
    public async Task SettingDisablesControlsAndIgnoresDuplicateTapsUntilResponse()
    {
        await using var fixture = CreateFixture();
        var page = CreatePage(fixture);
        try
        {
            await page.LoadModeForTestAsync();
            page.IsAlwaysOnMode = false;
            fixture.Ble.BeforeControlResponse = request =>
            {
                if (request.Opcode != AgpOpcode.ModeSet)
                    return;
                Assert.False(page.CanSetLocalMode);
                Assert.False(page.CanEditDelaySeconds);
                Assert.True(page.SetLocalModeCommand.ExecuteAsync(null).IsCompletedSuccessfully);
            };
            await page.SetLocalModeCommand.ExecuteAsync(null);
            Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.ModeSet);
            Assert.True(page.CanSetLocalMode);
        }
        finally { page.OnDisappearing(); }
    }

    [Fact]
    public async Task SharedSecondsFeedBothActionsWithoutCouplingTheCheckboxAndModeSwitch()
    {
        await using var fixture = CreateFixture();
        fixture.Ble.Capabilities |= AgpCapabilities.RelativeTimer | AgpCapabilities.OnOff;
        var model = CreatePage(fixture);
        try
        {
            await model.LoadModeForTestAsync();
            Assert.True(model.SupportsTurnOnDelay);
            Assert.True(model.SupportsLocalMode);
            Assert.False(model.CanEditDelaySeconds);
            var notifications = new List<string?>();
            model.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
            model.IsTurnOnDelayEnabled = true;
            Assert.True(model.CanEditDelaySeconds); // Always-on mode does not disable the first row.
            Assert.Contains(nameof(model.CanEditDelaySeconds), notifications);
            model.IsAlwaysOnMode = false;
            model.TurnOnDelaySecondsText = "135";
            fixture.Ble.Requests.Clear();
            fixture.Ble.RequestMessages.Clear();

            await model.SetLocalModeCommand.ExecuteAsync(null);
            Assert.Equal([AgpOpcode.ModeSet], fixture.Ble.Requests);
            Assert.Equal(Convert.FromHexString("0487000000"), fixture.Ble.RequestMessages.Last().Payload.ToArray());
            Assert.Equal(135, fixture.Repository.GetTurnOnDelaySeconds(fixture.Address));

            model.IsMasterPowerOn = false;
            // The power button now toggles the device, independent of its display.
            fixture.Ble.LightStatePacket[13] = 0;
            await model.MasterPowerCommand.ExecuteAsync(null);
            Assert.Equal([AgpOpcode.ModeSet, AgpOpcode.OnOffSet, AgpOpcode.DelayOffSet], fixture.Ble.Requests);
            Assert.Equal(Convert.FromHexString("87000000"), fixture.Ble.RequestMessages.Last().Payload.ToArray());

            model.IsTurnOnDelayEnabled = false;
            Assert.True(model.CanEditDelaySeconds); // Hardware delayed mode can still use the shared input.
            model.TurnOnDelaySecondsText = "240";
            await model.SetLocalModeCommand.ExecuteAsync(null);
            Assert.Equal(new(AgpLocalMode.OnAutoOff, OffSeconds: 240), fixture.Ble.LocalMode);
            Assert.False(fixture.Repository.GetTurnOnDelayEnabled(fixture.Address));
            Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.DelayOffSet);

            notifications.Clear();
            model.IsAlwaysOnMode = true;
            Assert.False(model.CanEditDelaySeconds);
            Assert.Contains(nameof(model.CanEditDelaySeconds), notifications);
            Assert.Contains(nameof(model.LocalModeLabel), notifications);
            model.IsAlwaysOnMode = false;
            Assert.True(model.CanEditDelaySeconds);
            Assert.Equal(new(AgpLocalMode.OnAutoOff, OffSeconds: 240), fixture.Ble.LocalMode);
        }
        finally { model.OnDisappearing(); }
    }

    [Fact]
    public async Task SharedInputPreservesTheFullModeRangeAndTheOriginalOneShotLimit()
    {
        await using var fixture = CreateFixture();
        fixture.Ble.Capabilities |= AgpCapabilities.RelativeTimer | AgpCapabilities.OnOff;
        var model = CreatePage(fixture);
        try
        {
            await model.LoadModeForTestAsync();
            model.IsAlwaysOnMode = false;
            model.IsTurnOnDelayEnabled = true;
            model.TurnOnDelaySecondsText = "2592000";
            await model.SetLocalModeCommand.ExecuteAsync(null);
            Assert.Equal(new(AgpLocalMode.OnAutoOff, OffSeconds: 2_592_000), fixture.Ble.LocalMode);
            model.IsMasterPowerOn = false;
            await model.MasterPowerCommand.ExecuteAsync(null);
            Assert.DoesNotContain(AgpOpcode.OnOffSet, fixture.Ble.Requests);
            Assert.DoesNotContain(AgpOpcode.DelayOffSet, fixture.Ble.Requests);
            Assert.Equal(AppResources.Timer_InvalidSeconds, ToastService.Messages.Last());
        }
        finally { model.OnDisappearing(); }
    }

    [Fact]
    public async Task PendingModeReadDoesNotBlockControlInitializationOrOverwriteNewSeconds()
    {
        await using var fixture = CreateFixture();
        fixture.Ble.Capabilities |= AgpCapabilities.RelativeTimer;
        fixture.Ble.LocalMode = new(AgpLocalMode.OnAutoOff, OffSeconds: 90);
        fixture.Ble.SuppressedResponseOpcode = AgpOpcode.ModeGet;
        fixture.Repository.SetTurnOnDelaySeconds(fixture.Address, 120);
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Ble.BeforeControlResponse = request =>
        {
            if (request.Opcode == AgpOpcode.ModeGet) reading.TrySetResult();
        };
        var model = CreatePage(fixture);
        try
        {
            await model.EnsureConnectedAsync(showError: false).WaitAsync(TimeSpan.FromSeconds(3));
            await reading.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(model.IsDeviceOnline);
            Assert.False(model.IsInitializingConnection);
            Assert.True(model.CanRefreshStatus);
            Assert.False(model.LocalModeLoadForTest.IsCompleted);
            Assert.Equal("120", model.TurnOnDelaySecondsText);

            model.TurnOnDelaySecondsText = "180";
            var request = fixture.Ble.RequestMessages.Last(item => item.Opcode == AgpOpcode.ModeGet);
            fixture.Ble.IndicateControlResponse(AgpCodec.Encode(AgpMessageType.Response,
                AgpOpcode.ModeGet, request.TransactionId, [0, 4, 90, 0, 0, 0]));
            await model.LocalModeLoadForTest.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(model.IsAlwaysOnMode);
            Assert.Equal("180", model.TurnOnDelaySecondsText);
            Assert.Equal(180, fixture.Repository.GetTurnOnDelaySeconds(fixture.Address));
            Assert.DoesNotContain(AgpOpcode.ModeSet, fixture.Ble.Requests);
        }
        finally { model.OnDisappearing(); }
    }

    [Fact]
    public async Task LeavingWhileSettingPreservesSubmittedMutationAndDoesNotShowStaleSuccess()
    {
        await using var fixture = CreateFixture();
        var model = CreatePage(fixture);
        await model.LoadModeForTestAsync();
        fixture.Ble.SuppressedResponseOpcode = AgpOpcode.ModeSet;
        var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Ble.BeforeControlResponse = request =>
        {
            if (request.Opcode == AgpOpcode.ModeSet) writing.TrySetResult();
        };
        model.IsAlwaysOnMode = false;
        var setting = model.SetLocalModeCommand.ExecuteAsync(null);
        await writing.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(model.IsLocalModeBusy);
        Assert.False(model.CanEditDelaySeconds);
        model.OnDisappearing();
        var request = fixture.Ble.RequestMessages.Last(item => item.Opcode == AgpOpcode.ModeSet);
        Assert.False(setting.IsCompleted);
        fixture.Ble.IndicateControlResponse(AgpCodec.Encode(AgpMessageType.Response,
            AgpOpcode.ModeSet, request.TransactionId, [0, .. request.Payload.ToArray()]));
        await setting.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(model.IsLocalModeBusy);
        Assert.False(model.CanSetLocalMode);
        Assert.Empty(ToastService.Messages);
        Assert.Equal(0, fixture.Ble.DisconnectAttempts);
    }

    [Fact]
    public async Task ModeReadFailureKeepsTheOneShotDelayAvailableAndRefreshCanRetry()
    {
        await using var fixture = CreateFixture();
        fixture.Ble.Capabilities |= AgpCapabilities.RelativeTimer;
        fixture.Ble.ModeResult = AgpResultCode.InternalError;
        fixture.Repository.SetTurnOnDelayEnabled(fixture.Address, true);
        var model = CreatePage(fixture);
        try
        {
            await model.LoadModeForTestAsync();
            Assert.True(model.IsDeviceOnline);
            Assert.False(model.IsLocalModeSupported);
            Assert.True(model.CanEditDelaySeconds);
            fixture.Ble.ModeResult = AgpResultCode.Success;
            await model.RefreshCommand.ExecuteAsync(null);
            await model.LocalModeLoadForTest;
            Assert.True(model.IsLocalModeSupported);
            Assert.True(model.CanSetLocalMode);
            Assert.Equal(2, fixture.Ble.Requests.Count(opcode => opcode == AgpOpcode.ModeGet));
        }
        finally { model.OnDisappearing(); }
    }

    [Fact]
    public async Task DeviceInformationNoLongerReadsOrWritesTheMovedModeConfiguration()
    {
        await using var fixture = CreateFixture();
        var model = new DeviceMorePageViewModel(fixture.Repository, fixture.Service,
            new TestAppNavigator(), new TestUserInteraction());
        model.ApplyQueryAttributes(new Dictionary<string, object> { ["address"] = fixture.Address });
        await model.OnAppearingAsync();
        Assert.Empty(fixture.Ble.Requests);
        Assert.Empty(fixture.Ble.ConnectAttempts);
        Assert.Equal(fixture.Repository.GetByAddress(fixture.Address)!.DisplayName, model.DeviceName);
    }

    private static PairingFixture CreateFixture()
    {
        var fixture = new PairingFixture();
        fixture.Ble.Capabilities = AgpCapabilities.SecureControl | AgpCapabilities.LocalMode;
        fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);
        return fixture;
    }

    private static DeviceControlPageViewModel CreatePage(PairingFixture fixture)
    {
        ToastService.Messages.Clear();
        var page = new DeviceControlPageViewModel(fixture.Repository, fixture.Discovery, fixture.Connection,
            fixture.Service, new AppDiagnosticService(), new TestUiDispatcher(), new TestAppNavigator(), new TestUserInteraction());
        page.ApplyQueryAttributes(new Dictionary<string, object> { ["address"] = fixture.Address });
        page.OnAppearingAsync().GetAwaiter().GetResult();
        return page;
    }
}
