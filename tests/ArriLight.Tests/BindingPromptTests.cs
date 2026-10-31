using ArriOpto.BLE.Agp;
using Plugin.BLE.Abstractions.Exceptions;
using System.Text.Json;
using Xunit;

namespace ArriLight.Tests;

public sealed class BindingPromptTests
{
    [Fact]
    public async Task ReaddingSavedAddressReplacesRecordAndRefreshesBindingTime()
    {
        await using var fixture = new PairingFixture();
        var other = fixture.AddDevice();
        var otherRecord = fixture.Repository.GetByAddress(other.Address);
        Assert.True(fixture.Repository.TryAddOrReplace(new SavedDevice(
            "Old device", fixture.Address, DateTimeOffset.Now.AddDays(-7),
            IsPinned: true, FirmwareVersion: "old-firmware", Token: "01020304",
            TimerSeconds: 900, Capabilities: AgpCapabilities.SecureControl,
            HardwareRevision: "old-hardware", LastOperatedAt: DateTimeOffset.Now.AddDays(-1))));
        var original = fixture.Repository.GetByAddress(fixture.Address);
        fixture.Ble.OnConnect = (_, _) =>
        {
            Assert.Equal(1, fixture.Discovery.StopCalls);
            Assert.Equal(original, fixture.Repository.GetByAddress(fixture.Address));
            return Task.CompletedTask;
        };
        fixture.Ble.DisconnectAfterToken = true;
        var page = CreatePage(fixture);
        var before = DateTimeOffset.Now;

        await page.AddForTestAsync("Replacement", fixture.Address.ToLowerInvariant())
            .WaitAsync(TimeSpan.FromSeconds(1));

        var saved = fixture.Repository.GetByAddress(fixture.Address)!;
        Assert.Equal("Replacement", saved.Name);
        Assert.Equal(fixture.Address, saved.Address);
        Assert.InRange(saved.AddedAt, before, DateTimeOffset.Now);
        Assert.Equal("A1B2C3D4", saved.Token);
        Assert.Equal(fixture.Ble.Capabilities, saved.Capabilities);
        Assert.Equal(BluetoothDeviceProtocol.Agp, saved.Protocol);
        Assert.Equal(SavedDevice.UnknownFirmwareVersion, saved.FirmwareVersion);
        Assert.Null(saved.HardwareRevision);
        Assert.Null(saved.LastOperatedAt);
        Assert.False(saved.IsPinned);
        Assert.Equal(60, saved.TimerSeconds);
        Assert.Single(fixture.Repository.GetAll(), device =>
            device.Address.Equals(fixture.Address, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(otherRecord, fixture.Repository.GetByAddress(other.Address));
        Assert.Equal([AgpOpcode.AuthGetToken], fixture.Ble.Requests);
        Assert.Equal(["已覆盖原有设备"], ToastService.Messages);
        Assert.Empty(page.Alerts);
        Assert.False(page.PromptIsVisible);
        Assert.Equal("//DevicesPage", Assert.Single(Shell.Current.Routes));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledRebindKeepsOriginalRecord(bool cancel)
    {
        await using var fixture = new PairingFixture();
        fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);
        fixture.Repository.SetPinned(fixture.Address, true);
        fixture.Repository.SetTimerSeconds(fixture.Address, 900);
        var original = JsonSerializer.Serialize(fixture.Repository.GetByAddress(fixture.Address));
        if (cancel)
            fixture.Ble.OnConnect = (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token);
        else
            fixture.Ble.ProvisionPayload = [(byte)AgpResultCode.NotAuthorized];
        var page = CreatePage(fixture);

        var adding = page.AddForTestAsync("Replacement", fixture.Address);
        if (cancel)
        {
            Assert.True(page.PromptIsVisible);
            page.CancelForTest();
        }
        await adding.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(original, JsonSerializer.Serialize(fixture.Repository.GetByAddress(fixture.Address)));
        Assert.DoesNotContain("已覆盖原有设备", ToastService.Messages);
        Assert.Empty(Shell.Current.Routes);
        Assert.False(page.PromptIsVisible);
    }

    [Fact]
    public async Task DuplicateMeshAddressContinuesToProvisioningWithoutChangingOriginalRecord()
    {
        await using var fixture = new PairingFixture();
        fixture.Repository.SetProtocol(fixture.Address, BluetoothDeviceProtocol.Mesh);
        var original = fixture.Repository.GetByAddress(fixture.Address);
        var page = CreatePage(fixture);
        var model = new ViewModels.BluetoothScanPageViewModel(
            fixture.Discovery, fixture.Repository, fixture.Service,
            new TestUiDispatcher(), new TestUserInteraction(), new TestAppNavigator());

        await model.AddDeviceSelectionCommand.ExecuteAsync(new DiscoveredBluetoothDevice(
            "Replacement Mesh", fixture.Address, -50, IsPairingOpen: false,
            Protocol: BluetoothDeviceProtocol.Mesh));

        Assert.Equal(original, fixture.Repository.GetByAddress(fixture.Address));
        Assert.Equal(1, fixture.Discovery.StopCalls);
        var route = Assert.Single(Shell.Current.Routes);
        Assert.StartsWith("MeshProvisioningPage?address=", route);
        Assert.Contains("&name=Replacement%20Mesh", route);
        Assert.Empty(page.Alerts);
        Assert.Empty(ToastService.Messages);
    }

    [Fact]
    public async Task SuccessfulAddClosesPromptAndReturnsHomeBeforeCountdownDeadline()
    {
        await using var fixture = new PairingFixture();
        fixture.Ble.DisconnectAfterToken = true;
        fixture.Repository.Remove(fixture.Address);
        var page = CreatePage(fixture);
        var adding = page.AddForTestAsync("PW01", fixture.Address);
        try
        {
            await adding.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.False(page.PromptIsVisible);
            Assert.Null(page.CountdownTask);
            Assert.Equal("A1B2C3D4", fixture.Repository.GetToken(fixture.Address));
            Assert.Contains("添加成功", ToastService.Messages);
            Assert.Equal("//DevicesPage", Assert.Single(Shell.Current.Routes));
        }
        finally
        {
            page.CancelForTest();
            page.ExpireCountdownForTest();
            await adding.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task CancelAfterServiceTokenSourceDisposedStopsCountdownAndClosesPrompt()
    {
        await using var fixture = new PairingFixture();
        var page = CreatePage(fixture);
        using var pageCancellation = new CancellationTokenSource();
        page.SetPageCancellationForTest(pageCancellation);
        using (var serviceCancellation = CancellationTokenSource.CreateLinkedTokenSource(pageCancellation.Token))
            await page.ShowProgressForTestAsync(serviceCancellation.Token);
        var countdown = page.CountdownTask!;
        try
        {
            page.CancelForTest();
            await countdown.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.True(pageCancellation.IsCancellationRequested);
            Assert.False(page.PromptIsVisible);
        }
        finally
        {
            page.ExpireCountdownForTest();
            await countdown.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task CancelDuringConnectionWaitImmediatelyClosesPromptAndRemovesPendingDevice()
    {
        await using var fixture = new PairingFixture();
        fixture.Repository.Remove(fixture.Address);
        fixture.Ble.OnConnect = (_, _) => Task.FromException(
            new DeviceConnectionException(fixture.Ble.Device.Id, "PW01", "busy"));
        var page = CreatePage(fixture);
        var adding = page.AddForTestAsync("PW01", fixture.Address);
        Assert.True(page.PromptIsVisible);

        page.CancelForTest();

        Assert.False(page.PromptIsVisible);
        await adding.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(fixture.Repository.Contains(fixture.Address));
        Assert.DoesNotContain("添加成功", ToastService.Messages);
        Assert.Single(fixture.Ble.ConnectAttempts);
    }

    [Fact]
    public async Task LeavingPageImmediatelyClosesPromptAndStopsPairing()
    {
        await using var fixture = new PairingFixture();
        fixture.Repository.Remove(fixture.Address);
        fixture.Ble.OnConnect = (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token);
        var page = CreatePage(fixture);
        var adding = page.AddForTestAsync("PW01", fixture.Address);
        Assert.True(page.PromptIsVisible);

        page.DisappearForTest();

        Assert.False(page.PromptIsVisible);
        await adding.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(fixture.Repository.Contains(fixture.Address));
    }

    private static BluetoothScanPage CreatePage(PairingFixture fixture)
    {
        ServiceHelper.Set(fixture.Discovery);
        ServiceHelper.Set(fixture.Repository);
        ServiceHelper.Set(fixture.Service);
        ToastService.Messages.Clear();
        Shell.Current.Routes.Clear();
        return new BluetoothScanPage();
    }
}
