using System.Collections.Concurrent;
using ArriOpto.BLE.Agp;
using Plugin.BLE.Abstractions.Contracts;
using Xunit;

namespace ArriLight.Tests;

public sealed class DeviceReconnectQueueTests
{
    [Fact]
    public async Task StartupAndForegroundResumeKeepTheirOriginalConnectionDeadlines()
    {
        var clock = new ManualTimeProvider();
        using var queue = new DeviceReconnectQueue(clock);
        Assert.Equal(TimeSpan.FromMilliseconds(1200), queue.RecoveryConnectionTimeout);
        await using var fixture = new PairingFixture(reconnectQueue: queue);
        Configure(fixture);
        var started = new[] { Signal(), Signal() };
        var releases = new[] { Signal(), Signal() };
        var tokens = new CancellationToken[2];
        fixture.Ble.OnConnect = async (attempt, token) =>
        {
            tokens[attempt - 1] = token;
            started[attempt - 1].TrySetResult();
            await releases[attempt - 1].Task.WaitAsync(token);
        };
        for (var cycle = 0; cycle < 2; cycle++)
        {
            if (cycle == 0) await fixture.Service.StartRuntimeAsync();
            else await fixture.Service.ResumeAfterBackgroundAsync();
            await started[cycle].Task.WaitAsync(TimeSpan.FromSeconds(3));
            clock.Advance(TimeSpan.FromSeconds(2));
            Assert.False(tokens[cycle].IsCancellationRequested);
            releases[cycle].TrySetResult();
            await Until(() => fixture.Service.GetRuntimeSnapshot(fixture.Address)?.Availability == DeviceRuntimeAvailability.Ready);
            if (cycle == 0)
            {
                await fixture.Service.SuspendForBackgroundAsync();
                clock.Advance(TimeSpan.FromSeconds(2));
            }
        }
        Assert.Equal(2, fixture.Ble.ConnectAttempts.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyOfflineRetryAndDisconnectRecoveryUseTheDefault1200Milliseconds(bool disconnect)
    {
        var clock = new ManualTimeProvider();
        using var queue = new DeviceReconnectQueue(clock);
        await using var fixture = new PairingFixture(reconnectQueue: queue);
        Configure(fixture);
        var retryStarted = Signal();
        var canceled = Signal();
        fixture.Ble.OnConnect = async (attempt, token) =>
        {
            if (attempt == 1)
            {
                if (!disconnect) throw new IOException("powered off");
                return;
            }
            retryStarted.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            { canceled.TrySetResult(); throw; }
        };
        await fixture.Service.StartRuntimeAsync();
        if (disconnect)
        {
            await Until(() => fixture.Service.GetRuntimeSnapshot(fixture.Address)?.Availability == DeviceRuntimeAvailability.Ready);
            await fixture.Service.ToggleOnOffAsync(fixture.Address);
            fixture.Ble.RaiseUnexpectedDisconnect();
        }
        else
        {
            await Until(() => fixture.Service.GetRuntimeSnapshot(fixture.Address)?.Error is not null);
            await Task.Delay(20);
        }
        clock.Advance(TimeSpan.FromSeconds(2));
        await retryStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        clock.Advance(TimeSpan.FromMilliseconds(1199));
        Assert.False(canceled.Task.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2, fixture.Ble.ConnectAttempts.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualConnectionJoinsRecoveryWithoutInheritingItsShortDeadline(bool queued)
    {
        var clock = new ManualTimeProvider();
        using var queue = new DeviceReconnectQueue(clock);
        await using var fixture = new PairingFixture(reconnectQueue: queue);
        Configure(fixture);
        await fixture.Service.StartRuntimeAsync();
        await Until(() => fixture.Service.GetRuntimeSnapshot(fixture.Address)?.Availability == DeviceRuntimeAvailability.Ready);
        var recoveryStarted = Signal();
        var release = Signal();
        CancellationToken recoveryToken = default;
        fixture.Ble.OnConnect = async (_, token) =>
        {
            recoveryToken = token;
            recoveryStarted.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        fixture.Ble.RaiseUnexpectedDisconnect();
        await Until(() => fixture.Service.GetRuntimeSnapshot(fixture.Address)?.Steps
            .GetValueOrDefault(DeviceRuntimeStep.Connection)?.Status == DeviceRuntimeStepStatus.Running);
        Task? manual = queued ? fixture.Service.EnsureRuntimeReadyAsync(fixture.Address) : null;
        clock.Advance(TimeSpan.FromSeconds(2));
        await recoveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        manual ??= fixture.Service.EnsureRuntimeReadyAsync(fixture.Address);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(recoveryToken.IsCancellationRequested);
        Assert.Equal(2, fixture.Ble.ConnectAttempts.Count);
        release.TrySetResult();
        await manual.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task OrdinaryConnectionsDoNotReadTheRecoveryTimeoutConfiguration()
    {
        using var settings = new AppSettingsFixture();
        await settings.Store.ReadTextAsync();
        await File.WriteAllTextAsync(settings.Store.ConfigurationPath, "{");
        using var queue = new DeviceReconnectQueue(settings: settings.Store);
        var radio = new FakeBle();
        using (queue.AutomaticPreparation())
            Assert.Same(radio.Device, await queue.ConnectAsync("startup", _ => Task.FromResult(radio.Device), CancellationToken.None));
        Assert.Same(radio.Device, await queue.ConnectAsync("manual", _ => Task.FromResult(radio.Device), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => Automatic(queue, "recovery", _ => Task.FromResult(radio.Device)));
    }

    [Fact]
    public async Task TransientAuthenticationFailureRetriesWithoutRequiringADifferentToken()
    {
        var clock = new ManualTimeProvider();
        using var queue = new DeviceReconnectQueue(clock);
        await using var fixture = new PairingFixture(reconnectQueue: queue);
        Configure(fixture);
        fixture.Ble.AuthenticationResult = AgpResultCode.InternalError;
        await fixture.Service.StartRuntimeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.EnsureRuntimeReadyAsync(fixture.Address));
        fixture.Ble.AuthenticationResult = AgpResultCode.Success;
        clock.Advance(TimeSpan.FromSeconds(2));
        await Until(() => fixture.Service.GetRuntimeSnapshot(fixture.Address)?.Availability == DeviceRuntimeAvailability.Ready);
        Assert.Single(fixture.Ble.ConnectAttempts);
        Assert.Equal(2, fixture.Ble.Requests.Count(opcode => opcode == AgpOpcode.AuthToken));
    }

    [Fact]
    public async Task SlowPrerequisitesAreOutsideThe1200MillisecondConnectionDeadline()
    {
        var clock = new ManualTimeProvider();
        using var queue = new DeviceReconnectQueue(clock) { RecoveryConnectionTimeout = TimeSpan.FromMilliseconds(1200) };
        var preparing = Signal();
        var prepared = Signal();
        var connecting = Signal();
        Task<IDevice> task;
        using (queue.AutomaticPreparation(recovery: true))
            task = queue.ConnectAsync("A", async token =>
            {
                connecting.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("unreachable");
            }, CancellationToken.None, async () => { preparing.TrySetResult(); await prepared.Task; });
        await preparing.Task.WaitAsync(TimeSpan.FromSeconds(3));
        clock.Advance(TimeSpan.FromMinutes(1));
        prepared.TrySetResult();
        await connecting.Task.WaitAsync(TimeSpan.FromSeconds(3));
        clock.Advance(TimeSpan.FromMilliseconds(1199));
        Assert.False(task.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    public async Task FailedDeviceReturnsBehindEveryOtherQueuedAddress(int devices)
    {
        var clock = new ManualTimeProvider();
        using var queue = new DeviceReconnectQueue(clock);
        var starts = new ConcurrentQueue<string>();
        Task<IDevice> Fail(string address) { starts.Enqueue(address); throw new IOException("offline"); }
        var initial = Enumerable.Range(0, devices).Select(index =>
            Automatic(queue, index.ToString(), _ => Fail(index.ToString()))).ToArray();
        await Assert.ThrowsAsync<IOException>(() => initial[0]);
        var retry = Automatic(queue, "0", _ => Fail("0"));
        for (var index = 1; index < devices; index++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Assert.ThrowsAsync<IOException>(() => initial[index].WaitAsync(TimeSpan.FromSeconds(3)));
        }
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<IOException>(() => retry.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(Enumerable.Range(0, devices).Select(index => index.ToString()).Append("0"), starts);
    }

    [Fact]
    public async Task DisabledBluetoothOrMissingPermissionPausesNativeAttemptsUntilAccessReturns()
    {
        var clock = new ManualTimeProvider();
        using var queue = new DeviceReconnectQueue(clock);
        await using var fixture = new PairingFixture(reconnectQueue: queue);
        Configure(fixture);
        fixture.Discovery.Failure = new BluetoothDiscoveryException("no Bluetooth access");
        await fixture.Service.StartRuntimeAsync();
        await Until(() => fixture.Service.GetRuntimeSnapshot(fixture.Address)?.Error is not null);
        clock.Advance(TimeSpan.FromMinutes(1));
        await Task.Delay(30);
        Assert.Empty(fixture.Ble.ConnectAttempts);
        fixture.Discovery.Failure = null;
        clock.Advance(TimeSpan.FromSeconds(2));
        await Until(() => fixture.Service.GetRuntimeSnapshot(fixture.Address)?.Availability == DeviceRuntimeAvailability.Ready);
        Assert.Single(fixture.Ble.ConnectAttempts);
    }

    [Fact]
    public async Task TwoSlotsStaggerStartsAndWaitForCleanupWithoutCatchingUp()
    {
        var clock = new ManualTimeProvider();
        using var queue = new DeviceReconnectQueue(clock) { RecoveryConnectionTimeout = TimeSpan.FromMilliseconds(1200) };
        var radio = new FakeBle();
        var starts = new ConcurrentQueue<(string Address, long Time)>();
        var releases = Enumerable.Range(0, 4).Select(_ => Signal()).ToArray();
        var canceled = Enumerable.Range(0, 4).Select(_ => Signal()).ToArray();
        var tasks = Enumerable.Range(0, 4).Select(index => Automatic(queue, index.ToString(), async token =>
        {
            starts.Enqueue((index.ToString(), clock.GetTimestamp()));
            using var registration = token.Register(() => canceled[index].TrySetResult());
            // Model an adapter that cannot finish cancellation until native cleanup returns.
            await releases[index].Task;
            token.ThrowIfCancellationRequested();
            return radio.Device;
        })).ToArray();
        try
        {
            await Until(() => starts.Count == 1);
            clock.Advance(TimeSpan.FromMilliseconds(999));
            Assert.Single(starts);
            clock.Advance(TimeSpan.FromMilliseconds(1));
            await Until(() => starts.Count == 2);
            clock.Advance(TimeSpan.FromMilliseconds(1200));
            await Task.WhenAll(canceled[0].Task, canceled[1].Task).WaitAsync(TimeSpan.FromSeconds(3));
            clock.Advance(TimeSpan.FromSeconds(8));
            Assert.Equal(2, starts.Count);
            Assert.All(tasks.Take(2), task => Assert.False(task.IsCompleted));

            releases[0].TrySetResult();
            await Until(() => starts.Count == 3);
            releases[1].TrySetResult();
            clock.Advance(TimeSpan.FromMilliseconds(999));
            Assert.Equal(3, starts.Count);
            clock.Advance(TimeSpan.FromMilliseconds(1));
            await Until(() => starts.Count == 4);
            Assert.Equal(new[] { 0L, 1000L, 10200L, 11200L }, starts.Select(start => start.Time / TimeSpan.TicksPerMillisecond));
        }
        finally
        {
            foreach (var release in releases) release.TrySetResult();
            foreach (var task in tasks) try { await task.WaitAsync(TimeSpan.FromSeconds(3)); } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task SingleAddressRetriesEveryTwoSecondsAndSharesPendingAttemptIgnoringCase()
    {
        var clock = new ManualTimeProvider();
        using var queue = new DeviceReconnectQueue(clock);
        var count = 0;
        Task<IDevice> Fail(CancellationToken token) { Interlocked.Increment(ref count); throw new IOException("offline"); }
        await Assert.ThrowsAsync<IOException>(() => Automatic(queue, "DEVICE", Fail));
        var second = Automatic(queue, "device", Fail);
        var joined = Automatic(queue, "DEVICE", _ => throw new InvalidOperationException("duplicate"));
        Assert.Same(second, joined);
        clock.Advance(TimeSpan.FromMilliseconds(1999));
        Assert.Equal(1, count);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await Assert.ThrowsAsync<IOException>(() => second.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task UserAttentionPromotesQueuedAddressAndCanceledQueueWaitDoesNotConnect()
    {
        var clock = new ManualTimeProvider();
        using var queue = new DeviceReconnectQueue(clock);
        var radio = new FakeBle();
        var starts = new ConcurrentQueue<string>();
        Task<IDevice> Connect(string address) { starts.Enqueue(address); return Task.FromResult(radio.Device); }
        await Automatic(queue, "A", _ => Connect("A"));
        using var cancellation = new CancellationTokenSource();
        var canceled = Automatic(queue, "B", _ => Connect("B"), cancellation.Token);
        var ordinary = Automatic(queue, "C", _ => Connect("C"));
        var priority = Automatic(queue, "D", _ => Connect("D"));
        queue.Prioritize("D");
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        clock.Advance(TimeSpan.FromSeconds(1));
        await priority.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(new[] { "A", "D" }, starts);
        clock.Advance(TimeSpan.FromSeconds(1));
        await ordinary.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(new[] { "A", "D", "C" }, starts);
    }

    [Fact]
    public async Task SavedOfflineDeviceContinuesRecoveryAndStopsInBackground()
    {
        var clock = new ManualTimeProvider();
        using var queue = new DeviceReconnectQueue(clock);
        await using var fixture = new PairingFixture(reconnectQueue: queue);
        Configure(fixture);
        var attempts = 0;
        fixture.Ble.OnConnect = (_, _) => Interlocked.Increment(ref attempts) <= 3
            ? Task.FromException(new IOException("device powered off")) : Task.CompletedTask;
        await fixture.Service.StartRuntimeAsync();
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await Until(() => Volatile.Read(ref attempts) == attempt && fixture.Service.GetRuntimeSnapshot(fixture.Address)?.Steps
                .GetValueOrDefault(DeviceRuntimeStep.Connection)?.Status == DeviceRuntimeStepStatus.Failed);
            // Let the failed shared preparation finish before advancing the next tick.
            await Task.Delay(20);
            clock.Advance(TimeSpan.FromSeconds(2));
        }
        await Until(() => fixture.Service.GetRuntimeSnapshot(fixture.Address)?.Availability == DeviceRuntimeAvailability.Ready);
        Assert.Equal(4, attempts);
        Assert.DoesNotContain(AgpOpcode.OnOffSet, fixture.Ble.Requests);
        await fixture.Service.SuspendForBackgroundAsync();
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(4, attempts);
        Assert.Equal(0, fixture.Discovery.ScanCalls);
    }

    [Fact]
    public async Task AuthenticationRejectionWaitsForChangedTokenAndPartialDataRetriesReuseSession()
    {
        var clock = new ManualTimeProvider();
        using var queue = new DeviceReconnectQueue(clock);
        await using var fixture = new PairingFixture(reconnectQueue: queue);
        Configure(fixture, AgpCapabilities.Effects);
        fixture.Ble.AuthenticationResult = AgpResultCode.NotAuthorized;
        await fixture.Service.StartRuntimeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.EnsureRuntimeReadyAsync(fixture.Address));
        clock.Advance(TimeSpan.FromSeconds(20));
        await Task.Delay(30);
        Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.AuthToken);
        fixture.Ble.AuthenticationResult = AgpResultCode.Success;
        fixture.Ble.EffectResult = AgpResultCode.InternalError;
        fixture.Repository.SetToken(fixture.Address, [5, 6, 7, 8]);
        await Until(() => fixture.Service.GetRuntimeSnapshot(fixture.Address)?.Steps.GetValueOrDefault(DeviceRuntimeStep.Effects)?.Status == DeviceRuntimeStepStatus.Failed);
        await Task.Delay(20);
        fixture.Ble.EffectResult = AgpResultCode.Success;
        clock.Advance(TimeSpan.FromSeconds(2));
        await Until(() => fixture.Service.GetRuntimeSnapshot(fixture.Address)?.Availability == DeviceRuntimeAvailability.Ready);
        Assert.Single(fixture.Ble.ConnectAttempts);
        Assert.Equal(2, fixture.Ble.Requests.Count(opcode => opcode == AgpOpcode.AuthToken));
        Assert.Equal(2, fixture.Ble.Requests.Count(opcode => opcode == AgpOpcode.EffectGet));
        Assert.Single(fixture.Ble.Reads, id => id == AgpProfile.LightStateCharacteristicId);
    }

    private static void Configure(PairingFixture fixture, AgpCapabilities additional = 0)
    {
        fixture.Ble.Capabilities = AgpCapabilities.OnOff | AgpCapabilities.SecureControl | additional;
        fixture.Repository.SetTokenAndCapabilities(fixture.Address, [1, 2, 3, 4], fixture.Ble.Capabilities);
    }
    private static Task<IDevice> Automatic(DeviceReconnectQueue queue, string address,
        Func<CancellationToken, Task<IDevice>> connect, CancellationToken token = default)
    { using var scope = queue.AutomaticPreparation(recovery: true); return queue.ConnectAsync(address, connect, token); }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }
}
