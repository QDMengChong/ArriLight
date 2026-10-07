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
    public async Task DeviceInformationLoadsDefaultAndOnlySettingSendsTheSelectedMode()
    {
        await using var fixture = CreateFixture();
        var page = CreatePage(fixture);
        try
        {
            await page.LoadModeForTestAsync();
            Assert.True(page.IsAlwaysOnMode);
            Assert.Equal(AppResources.DeviceMode_AlwaysOn, page.LocalModeLabel);
            Assert.Equal("60", page.LocalModeSecondsText);
            Assert.True(page.CanSetLocalMode);
            Assert.False(page.CanEditLocalModeSeconds);

            page.IsAlwaysOnMode = false;
            Assert.Equal(AppResources.DeviceMode_Delayed, page.LocalModeLabel);
            Assert.True(page.CanEditLocalModeSeconds);
            Assert.DoesNotContain(AgpOpcode.ModeSet, fixture.Ble.Requests);
            await page.SetModeForTestAsync();
            Assert.Equal(Convert.FromHexString("043C000000"), fixture.Ble.RequestMessages.Last().Payload.ToArray());
            Assert.Equal(new(AgpLocalMode.OnAutoOff, OffSeconds: 60), fixture.Ble.LocalMode);
            Assert.Equal(AppResources.DeviceMode_SetSucceeded, ToastService.Messages.Last());

            page.IsAlwaysOnMode = true;
            page.LocalModeSecondsText = "invalid but disabled";
            Assert.False(page.CanEditLocalModeSeconds);
            await page.SetModeForTestAsync();
            Assert.Equal(new byte[] { 0 }, fixture.Ble.RequestMessages.Last().Payload.ToArray());
            Assert.Equal(new(AgpLocalMode.DelayNone), fixture.Ble.LocalMode);
        }
        finally { page.DisappearForTest(); }
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
            page.LocalModeSecondsText = seconds;
            await page.SetModeForTestAsync();
        }
        finally { page.DisappearForTest(); }

        var reopened = CreatePage(fixture);
        try
        {
            await reopened.LoadModeForTestAsync();
            Assert.False(reopened.IsAlwaysOnMode);
            Assert.Equal(seconds, reopened.LocalModeSecondsText);
            Assert.True(reopened.CanEditLocalModeSeconds);
        }
        finally { reopened.DisappearForTest(); }
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
            page.LocalModeSecondsText = text;
            await page.SetModeForTestAsync();
            Assert.DoesNotContain(AgpOpcode.ModeSet, fixture.Ble.Requests);
            Assert.Equal(new(AgpLocalMode.DelayNone), fixture.Ble.LocalMode);
            Assert.Equal(AppResources.DeviceMode_InvalidSeconds, ToastService.Messages.Last());
            Assert.Equal(text, page.LocalModeSecondsText);
        }
        finally { page.DisappearForTest(); }
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
            page.LocalModeSecondsText = "90";
            await page.SetModeForTestAsync();
            Assert.Equal(new(AgpLocalMode.DelayNone), fixture.Ble.LocalMode);
            Assert.False(page.IsAlwaysOnMode);
            Assert.Equal("90", page.LocalModeSecondsText);
            Assert.True(page.CanSetLocalMode);
            Assert.Contains(nameof(AgpResultCode.InternalError), ToastService.Messages.Last());
            Assert.DoesNotContain(AppResources.DeviceMode_SetSucceeded, ToastService.Messages);
        }
        finally { page.DisappearForTest(); }
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
            await page.SetModeForTestAsync();
            Assert.False(page.IsLocalModeSupported);
            Assert.False(page.CanSetLocalMode);
            Assert.DoesNotContain(AgpOpcode.ModeGet, fixture.Ble.Requests);
            Assert.DoesNotContain(AgpOpcode.ModeSet, fixture.Ble.Requests);
        }
        finally { page.DisappearForTest(); }
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
        finally { page.DisappearForTest(); }
    }

    [Fact]
    public async Task LeavingWhileLoadingCancelsConnectionAndDoesNotShowStaleMessages()
    {
        await using var fixture = CreateFixture();
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Ble.OnConnect = (_, token) => blocked.Task.WaitAsync(token);
        var page = CreatePage(fixture);
        var loading = page.LoadModeForTestAsync();
        Assert.True(page.IsLocalModeBusy);
        page.DisappearForTest();
        await loading.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(page.IsLocalModeSupported);
        Assert.False(page.IsLocalModeBusy);
        Assert.Empty(ToastService.Messages);
        Assert.DoesNotContain(AgpOpcode.ModeGet, fixture.Ble.Requests);
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
                Assert.False(page.CanEditLocalModeSeconds);
                Assert.True(page.SetModeForTestAsync().IsCompletedSuccessfully);
            };
            await page.SetModeForTestAsync();
            Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.ModeSet);
            Assert.True(page.CanSetLocalMode);
        }
        finally { page.DisappearForTest(); }
    }

    private static PairingFixture CreateFixture()
    {
        var fixture = new PairingFixture();
        fixture.Ble.Capabilities = AgpCapabilities.SecureControl | AgpCapabilities.LocalMode;
        fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);
        return fixture;
    }

    private static DeviceMorePage CreatePage(PairingFixture fixture)
    {
        ToastService.Messages.Clear();
        ServiceHelper.Set(fixture.Repository);
        ServiceHelper.Set(fixture.Service);
        var page = new DeviceMorePage();
        page.ApplyQueryAttributes(new Dictionary<string, object> { ["address"] = fixture.Address });
        return page;
    }
}
