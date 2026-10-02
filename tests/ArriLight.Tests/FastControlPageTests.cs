using Xunit;

namespace ArriLight.Tests;

public sealed class FastControlPageTests
{
    [Fact]
    public void PreloadedPageIsConsumedOnceAndTheNextDeviceReceivesAFreshPage()
    {
        ServiceHelper.Set(new AppDiagnosticService());
        var created = new List<Element>();
        var factory = new PreparedControlPageRouteFactory("control", () =>
        {
            var page = new Element();
            created.Add(page);
            return page;
        });
        factory.Prepare();
        factory.Prepare();
        Assert.Single(created);
        Assert.Same(created[0], factory.GetOrCreate());
        var next = factory.GetOrCreate();
        Assert.NotSame(created[0], next);
        Assert.Equal(2, created.Count);
        factory.Prepare();
        Assert.Equal(3, created.Count);
        Assert.Same(created[2], factory.GetOrCreate());
    }

    [Fact]
    public void ColdNavigationCreatesFreshPagesWhenNoPreloadWasReady()
    {
        ServiceHelper.Set(new AppDiagnosticService());
        var factory = new PreparedControlPageRouteFactory("control", () => new Element());
        var first = factory.GetOrCreate();
        Assert.NotSame(first, factory.GetOrCreate());
    }

    [Fact]
    public void FailedPreloadLeavesTheFactoryAvailableForNormalNavigation()
    {
        ServiceHelper.Set(new AppDiagnosticService());
        var calls = 0;
        var factory = new PreparedControlPageRouteFactory("control", () =>
            ++calls == 1 ? throw new InvalidOperationException("warmup failed") : new Element());
        Assert.Throws<InvalidOperationException>(factory.Prepare);
        Assert.NotNull(factory.GetOrCreate());
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ShortAnimationAndConnectionPreparationProceedIndependently()
    {
        await using var fixture = new PairingFixture();
        var control = new DeviceControlPage(fixture.Address);
        control.AppearConnectionForTest(fixture.Discovery);
        var animation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        control.TransitionForTest.Completion = animation;
        control.NavigatedForTest();
        control.Dispatcher.RunNext();
        try
        {
            await control.InitializationStartedForTest.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, fixture.Discovery.StopCalls);
            Assert.Equal(1, control.InitializationCalls);
            Assert.False(animation.Task.IsCompleted);
            Assert.Equal(new uint[] { 120, 120 }, control.TransitionForTest.Durations);
            animation.TrySetResult(false);
            await control.DeferredInitializationForTest!;
            Assert.Equal(1, fixture.Discovery.StopCalls);
            Assert.Equal(1, control.InitializationCalls);
            Assert.Equal(1, control.TransitionForTest.Opacity);
            Assert.Equal(0, control.TransitionForTest.TranslationX);
        }
        finally
        {
            animation.TrySetResult(false);
            control.LeaveConnectionForTest();
        }
    }

    [Fact]
    public async Task LeavingDuringAnimationResetsThePageAndCancelsConcurrentBluetooth()
    {
        await using var fixture = new PairingFixture();
        var control = new DeviceControlPage(fixture.Address);
        CancellationToken initializationToken = default;
        control.OnInitialize = token =>
        {
            initializationToken = token;
            return Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        control.AppearConnectionForTest(fixture.Discovery);
        control.TransitionForTest.Completion =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        control.NavigatedForTest();
        control.Dispatcher.RunNext();
        await control.InitializationStartedForTest.Task.WaitAsync(TimeSpan.FromSeconds(2));
        control.LeaveConnectionForTest();
        await control.DeferredInitializationForTest!.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, fixture.Discovery.StopCalls);
        Assert.Equal(1, control.InitializationCalls);
        Assert.True(initializationToken.IsCancellationRequested);
        Assert.Equal(1, control.TransitionForTest.Opacity);
        Assert.Equal(0, control.TransitionForTest.TranslationX);
    }

    [Fact]
    public async Task HomePreloadRunsAfterDispatchAndDoesNotConnectAnyDevice()
    {
        await using var fixture = new PairingFixture();
        var home = CreateHome(fixture);
        DeviceControlPageRoutes.PreparationCalls = 0;
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        home.Dispatcher.Dispatched += () => posted.TrySetResult();
        try
        {
            await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, DeviceControlPageRoutes.PreparationCalls);
            home.Dispatcher.RunNext();
            Assert.Equal(1, DeviceControlPageRoutes.PreparationCalls);
            Assert.False(DeviceControlPageRoutes.LastPreparationWasDemo);
            Assert.Empty(fixture.Ble.ConnectAttempts);
            Assert.Empty(fixture.Ble.Requests);
        }
        finally { home.DisappearForTest(); }
    }

    [Fact]
    public async Task QueuedHomePreloadIsDiscardedIfTheUserHasAlreadyOpenedControl()
    {
        await using var fixture = new PairingFixture();
        var home = CreateHome(fixture);
        DeviceControlPageRoutes.PreparationCalls = 0;
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        home.Dispatcher.Dispatched += () => posted.TrySetResult();
        try
        {
            await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var device = home.Devices.Single(item => item.Address == fixture.Address);
            await home.OpenControlForTestAsync(device);
            home.Dispatcher.RunNext();
            Assert.Equal(0, DeviceControlPageRoutes.PreparationCalls);
            Assert.False(Shell.Current.AnimationRequests.Last());
            Assert.Empty(fixture.Ble.ConnectAttempts);
        }
        finally { home.DisappearForTest(); }
    }

    private static MainPage CreateHome(PairingFixture fixture)
    {
        ServiceHelper.Set(fixture.Repository);
        ServiceHelper.Set(fixture.Discovery);
        ServiceHelper.Set(fixture.Connection);
        ServiceHelper.Set(fixture.Service);
        ServiceHelper.Set(new AppDiagnosticService());
        Shell.Current.Routes.Clear();
        Shell.Current.AnimationRequests.Clear();
        Shell.Current.OnNavigate = null;
        var home = new MainPage();
        home.AppearForTest();
        return home;
    }
}
