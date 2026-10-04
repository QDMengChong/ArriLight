using ArriOpto.BLE.Agp;
using Xunit;

namespace ArriLight.Tests;

public sealed class ApplicationRestartTests
{
    [Fact]
    public async Task NewWindowAfterBackExitCanQueryAuthenticateAndControlTheSavedDevice()
    {
        await using var fixture = new PairingFixture();
        fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);
        fixture.Ble.Capabilities |= AgpCapabilities.OnOff;
        var app = new App(fixture.Service);
        var first = app.CreateWindowForTest();
        first.CreateNativeWindow();
        await fixture.Service.EnsureAuthorizedAsync(fixture.Address);

        first.Stop();
        first.DestroyNativeWindow();
        Assert.True(fixture.Service.IsApplicationSuspended);
        Assert.Empty(fixture.Connection.GetConnectedAddresses());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Service.EnsureAuthorizedAsync(fixture.Address));

        // Finish() does not destroy the process: App and BLE singletons survive.
        // A newly created native window does not receive a Resumed event.
        var second = app.CreateWindowForTest();
        second.CreateNativeWindow();

        Assert.False(fixture.Service.IsApplicationSuspended);
        Assert.Empty(fixture.Connection.GetConnectedAddresses());
        var status = await fixture.Service.QueryDeviceStatusAsync(fixture.Address);
        Assert.True(status.IsOnline);
        await fixture.Service.EnsureAuthorizedAsync(fixture.Address);
        await fixture.Service.SetOnOffAsync(fixture.Address, false);
        Assert.True(fixture.Connection.IsConnected(fixture.Address));
        Assert.False(fixture.Service.GetCachedDeviceStatus(fixture.Address)!.LightState!.IsOn);
        Assert.Equal(2, fixture.Ble.ConnectAttempts.Count);
        Assert.Equal(2, fixture.Ble.Requests.Count(opcode => opcode == AgpOpcode.AuthToken));
    }

    [Fact]
    public async Task RecreatingNativeWindowOnTheSameMauiWindowAllowsConnectionAgain()
    {
        await using var fixture = new PairingFixture();
        var window = new App(fixture.Service).CreateWindowForTest();
        window.CreateNativeWindow();
        await fixture.Service.QueryDeviceStatusAsync(fixture.Address);
        window.Stop();
        window.DestroyNativeWindow();

        // MAUI can also attach an existing cross-platform Window to a new
        // native window, without calling App.CreateWindow a second time.
        window.CreateNativeWindow();

        Assert.False(fixture.Service.IsApplicationSuspended);
        Assert.True((await fixture.Service.QueryDeviceStatusAsync(fixture.Address)).IsOnline);
        Assert.Equal(2, fixture.Ble.ConnectAttempts.Count);
    }

    [Fact]
    public async Task DestroyedWindowEventsCannotSuspendTheReplacementWindow()
    {
        await using var fixture = new PairingFixture();
        var app = new App(fixture.Service);
        var first = app.CreateWindowForTest();
        first.CreateNativeWindow();
        first.Stop();
        first.DestroyNativeWindow();
        var second = app.CreateWindowForTest();
        second.CreateNativeWindow();
        await fixture.Service.QueryDeviceStatusAsync(fixture.Address);

        first.Stop();
        first.DestroyNativeWindow();

        Assert.False(fixture.Service.IsApplicationSuspended);
        Assert.True(fixture.Connection.IsConnected(fixture.Address));
        Assert.True((await fixture.Service.QueryDeviceStatusAsync(fixture.Address)).IsOnline);
    }

    [Fact]
    public async Task NewHomePageCanRefreshItsOfflineDeviceAfterRepeatedBackExits()
    {
        await using var fixture = new PairingFixture();
        ServiceHelper.Set(fixture.Repository);
        ServiceHelper.Set(fixture.Discovery);
        ServiceHelper.Set(fixture.Connection);
        ServiceHelper.Set(fixture.Service);
        ServiceHelper.Set(new AppDiagnosticService());
        var app = new App(fixture.Service);

        for (var launch = 0; launch < 3; launch++)
        {
            var window = app.CreateWindowForTest();
            window.CreateNativeWindow();
            var page = new MainPage();
            page.AppearForTest();
            try
            {
                var device = Assert.Single(page.Devices, item => item.Address == fixture.Address);
                Assert.False(device.IsOnline);
                await page.QueryForTestAsync(device, TimeSpan.FromSeconds(1));
                Assert.True(device.IsOnline);
                Assert.True(device.IsEnabled);
                Assert.False(device.IsChecking);
            }
            finally { page.DisappearForTest(); }
            window.Stop();
            window.DestroyNativeWindow();
        }

        Assert.Equal(3, fixture.Ble.ConnectAttempts.Count);
        Assert.Equal(3, fixture.Ble.DisconnectAttempts);
    }

    [Fact]
    public async Task NewWindowWaitsForOldDisconnectBeforeAllowingFreshConnections()
    {
        await using var fixture = new PairingFixture();
        await fixture.Service.QueryDeviceStatusAsync(fixture.Address);
        var disconnectStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDisconnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Ble.OnDisconnect = () =>
        {
            disconnectStarted.TrySetResult();
            return releaseDisconnect.Task;
        };
        try
        {
            var closing = fixture.Service.DisconnectForApplicationCloseAsync();
            await disconnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var starting = fixture.Service.StartForNewWindowAsync();
            Assert.False(starting.IsCompleted);
            Assert.True(fixture.Service.IsApplicationSuspended);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                fixture.Service.QueryDeviceStatusAsync(fixture.Address));
            Assert.Single(fixture.Ble.ConnectAttempts);

            releaseDisconnect.TrySetResult();
            await Task.WhenAll(closing, starting).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(fixture.Service.IsApplicationSuspended);
            Assert.Empty(fixture.Connection.GetConnectedAddresses());
            Assert.True((await fixture.Service.QueryDeviceStatusAsync(fixture.Address)).IsOnline);
            Assert.Equal(2, fixture.Ble.ConnectAttempts.Count);
            Assert.Equal(1, fixture.Ble.DisconnectAttempts);
        }
        finally { releaseDisconnect.TrySetResult(); }
    }

    [Fact]
    public async Task WindowStoppedDuringStartupCanResumeAfterOldCleanupCompletes()
    {
        await using var fixture = new PairingFixture();
        await fixture.Service.QueryDeviceStatusAsync(fixture.Address);
        var releaseDisconnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Ble.OnDisconnect = () => releaseDisconnect.Task;
        try
        {
            var closing = fixture.Service.DisconnectForApplicationCloseAsync();
            Assert.Equal(1, fixture.Ble.DisconnectAttempts);
            var starting = fixture.Service.StartForNewWindowAsync();
            var stopping = fixture.Service.SuspendForBackgroundAsync();
            releaseDisconnect.TrySetResult();
            await Task.WhenAll(closing, starting, stopping).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(fixture.Service.IsApplicationSuspended);
            await fixture.Service.ResumeAfterBackgroundAsync();
            Assert.False(fixture.Service.IsApplicationSuspended);
            Assert.Empty(fixture.Connection.GetConnectedAddresses());
            Assert.True((await fixture.Service.QueryDeviceStatusAsync(fixture.Address)).IsOnline);
        }
        finally { releaseDisconnect.TrySetResult(); }
    }

    [Fact]
    public async Task WindowClosedAgainDuringStartupStaysSuspendedUntilAnotherWindowStarts()
    {
        await using var fixture = new PairingFixture();
        await fixture.Service.QueryDeviceStatusAsync(fixture.Address);
        var releaseDisconnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Ble.OnDisconnect = () => releaseDisconnect.Task;
        try
        {
            var firstClose = fixture.Service.DisconnectForApplicationCloseAsync();
            var starting = fixture.Service.StartForNewWindowAsync();
            var secondClose = fixture.Service.DisconnectForApplicationCloseAsync();
            releaseDisconnect.TrySetResult();
            await Task.WhenAll(firstClose, starting, secondClose).WaitAsync(TimeSpan.FromSeconds(5));

            await fixture.Service.ResumeAfterBackgroundAsync();
            Assert.True(fixture.Service.IsApplicationSuspended);
            Assert.Empty(fixture.Connection.GetConnectedAddresses());
            Assert.Single(fixture.Ble.ConnectAttempts);
            await fixture.Service.StartForNewWindowAsync();
            Assert.False(fixture.Service.IsApplicationSuspended);
            Assert.True((await fixture.Service.QueryDeviceStatusAsync(fixture.Address)).IsOnline);
        }
        finally { releaseDisconnect.TrySetResult(); }
    }
}
