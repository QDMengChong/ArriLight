using ArriOpto.BLE.Agp;
using Xunit;

namespace ArriLight.Tests;

public sealed class ControlNavigationTests
{
    [Fact]
    public async Task NavigationCancelsHomeQueryButDoesNotWaitForItsBleCleanupOrStopScanning()
    {
        await using var fixture = new PairingFixture();
        var home = CreateHome(fixture);
        using var cancellation = new CancellationTokenSource();
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        home.SetHomeRefreshForTest(cleanup.Task, cancellation);
        Shell.Current.OnNavigate = _ =>
        {
            home.DisappearForTest();
            return Task.CompletedTask;
        };
        try
        {
            var device = home.Devices.Single(item => item.Address == fixture.Address);
            await home.OpenControlForTestAsync(device).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(cancellation.IsCancellationRequested);
            Assert.False(cleanup.Task.IsCompleted);
            Assert.Single(Shell.Current.Routes);
            Assert.False(Shell.Current.AnimationRequests.Last());
            Assert.Equal(0, fixture.Discovery.StopCalls);
            Assert.Empty(fixture.Ble.ConnectAttempts);
        }
        finally
        {
            cleanup.TrySetResult();
            Shell.Current.OnNavigate = null;
            home.DisappearForTest();
        }
    }

    [Fact]
    public async Task RepeatedClicksAcrossDevicesOpenOnlyOnePageUntilTheUserReturnsHome()
    {
        await using var fixture = new PairingFixture();
        var other = fixture.AddDevice();
        var home = CreateHome(fixture);
        var navigation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Shell.Current.OnNavigate = _ => navigation.Task;
        var first = home.Devices.Single(item => item.Address == fixture.Address);
        var second = home.Devices.Single(item => item.Address == other.Address);
        var opening = home.OpenControlForTestAsync(first);
        try
        {
            await home.OpenControlForTestAsync(first);
            await home.OpenControlForTestAsync(second);
            Assert.Single(Shell.Current.Routes);
            home.DisappearForTest();
            navigation.TrySetResult();
            await opening;
            await home.OpenControlForTestAsync(first); // A queued stale tap after transition.
            Assert.Single(Shell.Current.Routes);
            home.AppearForTest();
            await home.OpenControlForTestAsync(second);
            Assert.Equal(2, Shell.Current.Routes.Count);
        }
        finally
        {
            navigation.TrySetResult();
            await opening;
            Shell.Current.OnNavigate = null;
            home.DisappearForTest();
        }
    }

    [Fact]
    public async Task NavigationFailureReleasesTheGuardAndAllowsRetry()
    {
        await using var fixture = new PairingFixture();
        var home = CreateHome(fixture);
        Shell.Current.OnNavigate = _ => Task.FromException(new InvalidOperationException("route failed"));
        try
        {
            var device = home.Devices.Single(item => item.Address == fixture.Address);
            await home.OpenControlForTestAsync(device);
            Assert.Contains("打开设备失败，请重试", ToastService.Messages);
            Shell.Current.OnNavigate = null;
            await home.OpenControlForTestAsync(device);
            Assert.Equal(2, Shell.Current.Routes.Count);
        }
        finally
        {
            Shell.Current.OnNavigate = null;
            home.DisappearForTest();
        }
    }

    [Fact]
    public async Task PowerActionControlsTheDeviceWithoutOpeningAControlPage()
    {
        await using var fixture = new PairingFixture();
        fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);
        fixture.Ble.Capabilities |= AgpCapabilities.OnOff;
        await fixture.Service.QueryDeviceStatusAsync(fixture.Address);
        var home = CreateHome(fixture);
        try
        {
            var device = home.Devices.Single(item => item.Address == fixture.Address);
            await home.TogglePowerForTestAsync(device);
            Assert.Empty(Shell.Current.Routes);
            Assert.Contains(AgpOpcode.OnOffSet, fixture.Ble.Requests);
        }
        finally { home.DisappearForTest(); }
    }

    [Fact]
    public async Task ControlInitializationStartsAfterNavigationAndDispatchAndWaitsForScanTeardown()
    {
        await using var fixture = new PairingFixture();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Discovery.OnStop = () => stopped.Task;
        var control = new DeviceControlPage(fixture.Address);
        control.AppearConnectionForTest(fixture.Discovery);
        Assert.Equal(0, fixture.Discovery.StopCalls);
        Assert.Equal(0, control.InitializationCalls);
        control.NavigatedForTest();
        control.NavigatedForTest();
        Assert.Single(control.Dispatcher.PendingActions);
        Assert.Equal(0, fixture.Discovery.StopCalls);
        Assert.False(control.CanRefreshStatus);
        control.Dispatcher.RunNext();
        try
        {
            Assert.Equal(1, fixture.Discovery.StopCalls);
            Assert.Equal(0, control.InitializationCalls);
            stopped.TrySetResult();
            await control.DeferredInitializationForTest!;
            Assert.Equal(1, control.InitializationCalls);
            Assert.True(control.CanRefreshStatus);
        }
        finally
        {
            stopped.TrySetResult();
            control.LeaveConnectionForTest();
        }
    }

    [Fact]
    public async Task LeavingBeforeDispatchPreventsScanAndConnectionWork()
    {
        await using var fixture = new PairingFixture();
        var control = new DeviceControlPage(fixture.Address);
        control.AppearConnectionForTest(fixture.Discovery);
        control.NavigatedForTest();
        control.LeaveConnectionForTest();
        control.Dispatcher.RunNext();
        await control.DeferredInitializationForTest!;
        Assert.Equal(0, fixture.Discovery.StopCalls);
        Assert.Equal(0, control.InitializationCalls);
    }

    [Fact]
    public async Task LeavingWhileScanStopsPreventsLateConnectionInitialization()
    {
        await using var fixture = new PairingFixture();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Discovery.OnStop = () => stopped.Task;
        var control = new DeviceControlPage(fixture.Address);
        control.AppearConnectionForTest(fixture.Discovery);
        control.NavigatedForTest();
        control.Dispatcher.RunNext();
        control.LeaveConnectionForTest();
        stopped.TrySetResult();
        await control.DeferredInitializationForTest!;
        Assert.Equal(0, control.InitializationCalls);
    }

    [Fact]
    public async Task LeavingDuringInitializationCancelsItsAppearanceToken()
    {
        await using var fixture = new PairingFixture();
        var control = new DeviceControlPage(fixture.Address);
        CancellationToken sessionToken = default;
        control.OnInitialize = token =>
        {
            sessionToken = token;
            return Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        control.AppearConnectionForTest(fixture.Discovery);
        control.NavigatedForTest();
        control.Dispatcher.RunNext();
        Assert.Equal(1, control.InitializationCalls);
        control.LeaveConnectionForTest();
        await control.DeferredInitializationForTest!.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(sessionToken.IsCancellationRequested);
    }

    [Fact]
    public async Task DemoNavigationDoesNotScheduleBluetoothWork()
    {
        await using var fixture = new PairingFixture();
        var control = new DeviceControlPage(fixture.Address);
        control.AppearConnectionForTest(fixture.Discovery, isDemo: true);
        control.NavigatedForTest();
        Assert.Single(control.Dispatcher.PendingActions);
        control.Dispatcher.RunNext();
        await control.DeferredInitializationForTest!;
        Assert.Equal(0, fixture.Discovery.StopCalls);
        Assert.Equal(0, control.InitializationCalls);
        control.LeaveConnectionForTest();
    }

    [Fact]
    public async Task ScanTeardownFailureIsShownOnTheOpenedPageAndAllowsRetry()
    {
        await using var fixture = new PairingFixture();
        fixture.Discovery.OnStop = () => Task.FromException(new IOException("scan stop failed"));
        var control = new DeviceControlPage(fixture.Address);
        control.AppearConnectionForTest(fixture.Discovery);
        control.NavigatedForTest();
        control.Dispatcher.RunNext();
        await control.DeferredInitializationForTest!;
        Assert.Equal("连接准备失败，请重试", control.ConnectionStatusText);
        Assert.True(control.CanConnect);
        Assert.True(control.CanRefreshStatus);
        Assert.Equal(0, control.InitializationCalls);
        control.LeaveConnectionForTest();
    }

    private static MainPage CreateHome(PairingFixture fixture)
    {
        ServiceHelper.Set(fixture.Repository);
        ServiceHelper.Set(fixture.Discovery);
        ServiceHelper.Set(fixture.Connection);
        ServiceHelper.Set(fixture.Service);
        ServiceHelper.Set(new AppDiagnosticService());
        Shell.Current.Routes.Clear();
        Shell.Current.OnNavigate = null;
        ToastService.Messages.Clear();
        var home = new MainPage();
        home.AppearForTest();
        return home;
    }
}
