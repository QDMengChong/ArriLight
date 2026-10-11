using ArriOpto.BLE.Ota;
using Xunit;

namespace ArriLight.Tests;

public sealed class FirmwareUpdateConnectionTests
{
    [Fact]
    public async Task OpeningWithExistingConnectionShowsConnectedWithoutAnotherStateEvent()
    {
        await using var fixture = new PairingFixture();
        fixture.Ble.FirmwareRevision = "2.3.4";
        await fixture.Connection.ConnectAsync(fixture.Address);
        var events = new List<BluetoothSessionSnapshot>();
        fixture.Connection.StateChanged += (_, snapshot) => events.Add(snapshot);
        var page = CreatePage(fixture);
        try
        {
            page.AppearForTest();

            Assert.Equal("设备已连接", page.ConnectionText);
            Assert.Equal("2.3.4", page.CurrentVersion);
            Assert.Single(fixture.Ble.ConnectAttempts);
            Assert.Empty(events);
        }
        finally { page.DisappearForTest(); }
    }

    [Fact]
    public async Task ReappearingSynchronizesDisconnectAndReconnectWhilePageWasHidden()
    {
        await using var fixture = new PairingFixture();
        await fixture.Connection.ConnectAsync(fixture.Address);
        var page = CreatePage(fixture);
        try
        {
            page.AppearForTest();
            page.DisappearForTest();
            await fixture.Connection.DisconnectAsync(fixture.Address);

            page.AppearForTest();
            Assert.Equal(fixture.Connection.GetSnapshot(fixture.Address).Message, page.ConnectionText);
            Assert.False(fixture.Connection.IsConnected(fixture.Address));
            page.DisappearForTest();
            await fixture.Connection.ConnectAsync(fixture.Address);

            page.AppearForTest();
            Assert.Equal("设备已连接", page.ConnectionText);
            Assert.Equal(2, fixture.Ble.ConnectAttempts.Count);
        }
        finally { page.DisappearForTest(); }
    }

    [Fact]
    public async Task NativeConnectionEventsUpdateConnectingConnectedLostAndReconnectedText()
    {
        await using var fixture = new PairingFixture();
        // Complete initial device-info reads before injecting connection loss.
        // Otherwise the page's background initialization may itself reconnect.
        await fixture.Connection.ConnectAsync(fixture.Address);
        var page = CreatePage(fixture);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            page.AppearForTest();
            Assert.Equal("设备已连接", page.ConnectionText);

            fixture.Ble.RaiseUnexpectedDisconnect();
            Assert.Equal("连接意外断开", page.ConnectionText);
            fixture.Ble.OnConnect = (_, token) => release.Task.WaitAsync(token);
            var reconnecting = fixture.Connection.ConnectAsync(fixture.Address);
            Assert.Equal("正在连接设备", page.ConnectionText);
            release.SetResult();
            await reconnecting.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal("设备已连接", page.ConnectionText);
        }
        finally
        {
            release.TrySetResult();
            page.DisappearForTest();
        }
    }

    [Fact]
    public async Task LastActiveDeviceAndOtherDeviceEventsDoNotReplaceTargetStatus()
    {
        await using var fixture = new PairingFixture();
        var other = fixture.AddDevice();
        await fixture.Connection.ConnectAsync(fixture.Address);
        await fixture.Connection.ConnectAsync(other.Address);
        other.RaiseUnexpectedDisconnect();
        var page = CreatePage(fixture);
        try
        {
            page.AppearForTest();
            Assert.Equal("设备已连接", page.ConnectionText);

            await fixture.Connection.ConnectAsync(other.Address);
            other.RaiseUnexpectedDisconnect();
            Assert.Equal("设备已连接", page.ConnectionText);
        }
        finally { page.DisappearForTest(); }
    }

    [Fact]
    public async Task ConnectionFailureReplacesPreparationTextWithReason()
    {
        await using var fixture = new PairingFixture();
        fixture.Ble.OnConnect = (_, _) => Task.FromException(new IOException("radio unavailable"));
        var page = CreatePage(fixture);
        try
        {
            page.AppearForTest();
            Assert.Contains("radio unavailable", page.ConnectionText);
            Assert.Equal(BluetoothSessionState.Error, fixture.Connection.GetSnapshot(fixture.Address).State);
        }
        finally { page.DisappearForTest(); }
    }

    private static FirmwareUpdatePage CreatePage(PairingFixture fixture)
    {
        var diagnostics = new AppDiagnosticService();
        var configuration = new FirmwareUpdateConfiguration();
        ServiceHelper.Set(fixture.Repository);
        ServiceHelper.Set(fixture.Connection);
        ServiceHelper.Set(fixture.Service);
        ServiceHelper.Set(diagnostics);
        ServiceHelper.Set(configuration);
        ServiceHelper.Set(new FirmwareUpdateService(new HttpClient(),
            new PluginBleOtaClient(fixture.Ble.Adapter), fixture.Connection,
            fixture.Service, diagnostics, configuration));
        var page = new FirmwareUpdatePage();
        page.ApplyQueryAttributes(new Dictionary<string, object>
        {
            ["address"] = fixture.Address.ToLowerInvariant()
        });
        return page;
    }
}
