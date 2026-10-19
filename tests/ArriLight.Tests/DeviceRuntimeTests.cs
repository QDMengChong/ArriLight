using System.Collections.Concurrent;
using ArriOpto.BLE.Agp;
using Xunit;

namespace ArriLight.Tests;

public sealed class DeviceRuntimeTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(-1)]
    public async Task InitializationConcurrencyDefaultsToFiveAndRejectsInvalidValues(int invalid)
    {
        await using var fixture = RuntimeFixture();
        Assert.Equal(5, fixture.Service.RuntimeConcurrency);
        Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Service.RuntimeConcurrency = invalid);
        fixture.Service.RuntimeConcurrency = 1;
        Assert.Equal(1, fixture.Service.RuntimeConcurrency);
        fixture.Service.RuntimeConcurrency = 5;
        Assert.Equal(5, fixture.Service.RuntimeConcurrency);
    }

    [Fact]
    public async Task CachedCapabilitiesMakeDeviceOnlineBeforeItsLightStateReadCompletes()
    {
        await using var fixture = RuntimeFixture();
        var gate = new AsyncGate();
        fixture.Ble.BeforeReadAsync = id => id == AgpProfile.LightStateCharacteristicId
            ? gate.WaitAsync() : Task.CompletedTask;
        var versions = new ConcurrentQueue<long>();
        fixture.Service.RuntimeStateChanged += (_, change) =>
        {
            if (change.Address.Equals(fixture.Address, StringComparison.OrdinalIgnoreCase))
                versions.Enqueue(change.Snapshot.Version);
        };
        try
        {
            await fixture.Service.StartRuntimeAsync();
            await gate.Entered.Task.WaitAsync(Deadline);
            Assert.Equal(DeviceRuntimeAvailability.Online, fixture.Service.GetRuntimeSnapshot(fixture.Address)!.Availability);
            Assert.Contains(AgpProfile.ControlPointCharacteristicId, fixture.Ble.Subscriptions);
            Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.AuthToken);
            Assert.DoesNotContain(AgpProfile.CapabilitiesCharacteristicId, fixture.Ble.Reads);
            Assert.Equal(DeviceRuntimeStepStatus.Succeeded, fixture.Service.GetRuntimeSnapshot(fixture.Address)!.Steps[DeviceRuntimeStep.Authentication].Status);
            Assert.Equal(DeviceRuntimeStepStatus.Running, fixture.Service.GetRuntimeSnapshot(fixture.Address)!.Steps[DeviceRuntimeStep.LightState].Status);
            gate.Release();
            await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
            Assert.Equal(DeviceRuntimeAvailability.Ready, fixture.Service.GetRuntimeSnapshot(fixture.Address)!.Availability);
            var published = versions.ToArray();
            Assert.True(published.Length >= 3);
            Assert.True(published.Zip(published.Skip(1), (left, right) => right > left).All(value => value));
            Assert.Null(fixture.Repository.GetByAddress(fixture.Address)!.LastOperatedAt);
            Assert.Equal(0, fixture.Discovery.ScanCalls);
            Assert.Equal(0, fixture.Discovery.BackgroundScanCalls);
        }
        finally { gate.Release(); }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public async Task InitializationRespectsOperationBudgetAndAtMostTwoConnectionSlots(int concurrency)
    {
        await using var fixture = RuntimeFixture();
        fixture.Service.RuntimeConcurrency = concurrency;
        var radios = new List<FakeBle> { fixture.Ble };
        for (var index = 1; index < concurrency + 2; index++)
        {
            var radio = fixture.AddDevice();
            Configure(fixture, radio);
            radios.Add(radio);
        }
        var started = new ConcurrentQueue<string>();
        var entered = Completion();
        var release = Completion();
        foreach (var radio in radios)
        {
            radio.OnConnect = async (_, token) =>
            {
                started.Enqueue(radio.Address);
                if (started.Count == Math.Min(concurrency, 2)) entered.TrySetResult();
                await release.Task.WaitAsync(token);
            };
        }
        try
        {
            await fixture.Service.StartRuntimeAsync();
            await entered.Task.WaitAsync(Deadline);
            Assert.Equal(Math.Min(concurrency, 2), started.Count);
            release.TrySetResult();
            await Task.WhenAll(radios.Select(radio => fixture.Service.EnsureRuntimeReadyAsync(radio.Address)))
                .WaitAsync(TimeSpan.FromSeconds(12));
            Assert.Equal(radios.Count, started.Count);
            Assert.All(radios, radio => Assert.Equal(DeviceRuntimeAvailability.Ready,
                fixture.Service.GetRuntimeSnapshot(radio.Address)!.Availability));
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task CancelledPageWaitDoesNotCancelSharedPreparation()
    {
        await using var fixture = RuntimeFixture();
        var gate = new AsyncGate();
        fixture.Ble.OnConnect = (_, token) => gate.WaitAsync(token);
        try
        {
            await fixture.Service.StartRuntimeAsync();
            await gate.Entered.Task.WaitAsync(Deadline);
            using var pageCancellation = new CancellationTokenSource();
            Assert.Equal(DeviceRuntimeStepStatus.Running,
                fixture.Service.GetRuntimeSnapshot(fixture.Address)!.Steps[DeviceRuntimeStep.Connection].Status);
            var pageWait = fixture.Service.EnsureRuntimeReadyAsync(fixture.Address, pageCancellation.Token);
            pageCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pageWait);
            Assert.Equal(0, fixture.Ble.DisconnectAttempts);
            gate.Release();
            await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
            Assert.Equal(DeviceRuntimeAvailability.Ready, fixture.Service.GetRuntimeSnapshot(fixture.Address)!.Availability);
            Assert.Single(fixture.Ble.ConnectAttempts);
        }
        finally { gate.Release(); }
    }

    [Fact]
    public async Task QueuedToggleWaitsForAuthenticationAndPrecedesOrdinaryQueries()
    {
        await using var fixture = RuntimeFixture(AgpCapabilities.OnOff | AgpCapabilities.SecureControl | AgpCapabilities.Effects);
        var gate = new AsyncGate();
        fixture.Ble.BeforeWriteAsync = request => request.Opcode == AgpOpcode.AuthToken
            ? gate.WaitAsync() : Task.CompletedTask;
        try
        {
            await fixture.Service.StartRuntimeAsync();
            await gate.Entered.Task.WaitAsync(Deadline);
            var preparing = fixture.Service.GetRuntimeSnapshot(fixture.Address)!;
            Assert.Equal(DeviceRuntimeAvailability.Offline, preparing.Availability);
            Assert.Equal(DeviceRuntimeStepStatus.Succeeded, preparing.Steps[DeviceRuntimeStep.Connection].Status);
            Assert.Equal(DeviceRuntimeStepStatus.Succeeded, preparing.Steps[DeviceRuntimeStep.ResponseChannel].Status);
            Assert.Equal(DeviceRuntimeStepStatus.Running, preparing.Steps[DeviceRuntimeStep.Authentication].Status);
            var toggle = fixture.Service.ToggleOnOffAsync(fixture.Address);
            Assert.False(toggle.IsCompleted);
            Assert.True(fixture.Service.GetRuntimeSnapshot(fixture.Address)!.HasPendingUserOperation);
            var operation = fixture.Service.GetRuntimeSnapshot(fixture.Address)!.LastOperation!;
            Assert.Equal(DeviceRuntimeOperationStage.Queued, operation.Stage);
            Assert.NotEqual(Guid.Empty, operation.Id);
            Assert.True(operation.Deadline > operation.CreatedAt);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ToggleOnOffAsync(fixture.Address));
            Assert.DoesNotContain(AgpOpcode.OnOffSet, fixture.Ble.Requests);
            Assert.DoesNotContain(AgpProfile.LightStateCharacteristicId, fixture.Ble.Reads);
            gate.Release();
            await toggle.WaitAsync(Deadline);
            await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
            Assert.False(fixture.Service.GetRuntimeSnapshot(fixture.Address)!.HasPendingUserOperation);
            Assert.Equal(operation.Id, fixture.Service.GetRuntimeSnapshot(fixture.Address)!.LastOperation!.Id);
            Assert.Equal(DeviceRuntimeOperationStage.Confirmed, fixture.Service.GetRuntimeSnapshot(fixture.Address)!.LastOperation!.Stage);
            Assert.Equal([AgpOpcode.AuthToken, AgpOpcode.OnOffSet, AgpOpcode.EffectGet], fixture.Ble.Requests);
            Assert.Single(fixture.Ble.ConnectAttempts);
            Assert.Equal(0, fixture.Ble.DisconnectAttempts);
            Assert.Equal(new byte[] { 2 }, fixture.Ble.RequestMessages.Single(request => request.Opcode == AgpOpcode.OnOffSet).Payload.ToArray());
        }
        finally { gate.Release(); }
    }

    [Fact]
    public async Task QueuedToggleFinishesCurrentReadAndPrecedesTheNextQuery()
    {
        await using var fixture = RuntimeFixture(AgpCapabilities.OnOff | AgpCapabilities.SecureControl | AgpCapabilities.Effects);
        var gate = new AsyncGate();
        var firstRead = 0;
        fixture.Ble.BeforeReadAsync = id => id == AgpProfile.LightStateCharacteristicId && Interlocked.Increment(ref firstRead) == 1
            ? gate.WaitAsync() : Task.CompletedTask;
        try
        {
            await fixture.Service.StartRuntimeAsync();
            await gate.Entered.Task.WaitAsync(Deadline);
            var toggle = fixture.Service.ToggleOnOffAsync(fixture.Address);
            Assert.False(toggle.IsCompleted);
            Assert.DoesNotContain(AgpOpcode.OnOffSet, fixture.Ble.Requests);
            Assert.DoesNotContain(AgpOpcode.EffectGet, fixture.Ble.Requests);
            gate.Release();
            await toggle.WaitAsync(Deadline);
            await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
            Assert.Equal([AgpOpcode.AuthToken, AgpOpcode.OnOffSet, AgpOpcode.EffectGet], fixture.Ble.Requests);
            Assert.False(fixture.Service.GetCachedDeviceStatus(fixture.Address)!.LightState!.IsOn);
            Assert.Single(fixture.Ble.ConnectAttempts);
            Assert.Equal(0, fixture.Ble.DisconnectAttempts);
        }
        finally { gate.Release(); }
    }

    [Fact]
    public async Task FailedDataReadKeepsAuthenticatedSessionOnlineAndAllowsToggle()
    {
        await using var fixture = RuntimeFixture();
        fixture.Ble.OnLightStateRead = () => throw new IOException("lamp state unavailable");
        await fixture.Service.StartRuntimeAsync();
        var error = await Record.ExceptionAsync(() => fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline));
        Assert.NotNull(error);
        Assert.IsNotType<TimeoutException>(error);
        Assert.Equal(DeviceRuntimeAvailability.Online, fixture.Service.GetRuntimeSnapshot(fixture.Address)!.Availability);
        Assert.Equal(DeviceRuntimeStepStatus.Failed, fixture.Service.GetRuntimeSnapshot(fixture.Address)!.Steps[DeviceRuntimeStep.LightState].Status);
        Assert.Null(fixture.Service.GetCachedDeviceStatus(fixture.Address)!.LightState);
        Assert.Equal(0, fixture.Ble.DisconnectAttempts);
        await fixture.Service.ToggleOnOffAsync(fixture.Address).WaitAsync(Deadline);
        Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.AuthToken);
        Assert.Single(fixture.Ble.ConnectAttempts);
        Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.OnOffSet);
    }

    [Fact]
    public async Task FailedSupportedEffectQueryNeverAdvertisesReadyOrDisconnects()
    {
        await using var fixture = RuntimeFixture(AgpCapabilities.OnOff | AgpCapabilities.SecureControl | AgpCapabilities.Effects);
        fixture.Ble.EffectResult = AgpResultCode.InternalError;
        await fixture.Service.StartRuntimeAsync();
        var error = await Record.ExceptionAsync(() => fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline));
        Assert.NotNull(error);
        Assert.IsNotType<TimeoutException>(error);
        Assert.Equal(DeviceRuntimeAvailability.Online, fixture.Service.GetRuntimeSnapshot(fixture.Address)!.Availability);
        Assert.Equal(DeviceRuntimeStepStatus.Failed, fixture.Service.GetRuntimeSnapshot(fixture.Address)!.Steps[DeviceRuntimeStep.Effects].Status);
        Assert.Equal(0, fixture.Ble.DisconnectAttempts);
        Assert.True(fixture.Service.GetCachedDeviceStatus(fixture.Address)!.IsOnline);
        Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.AuthToken);
    }

    [Fact]
    public async Task UnsupportedCapabilitiesSkipTheirQueriesAndCanReachReady()
    {
        await using var fixture = RuntimeFixture();
        await fixture.Service.StartRuntimeAsync();
        await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
        Assert.Equal(DeviceRuntimeAvailability.Ready, fixture.Service.GetRuntimeSnapshot(fixture.Address)!.Availability);
        Assert.Equal([AgpOpcode.AuthToken], fixture.Ble.Requests);
        Assert.Single(fixture.Ble.Reads, id => id == AgpProfile.LightStateCharacteristicId);
        var steps = fixture.Service.GetRuntimeSnapshot(fixture.Address)!.Steps;
        Assert.Equal(DeviceRuntimeStepStatus.NotApplicable, steps[DeviceRuntimeStep.LocalMode].Status);
        Assert.Equal(DeviceRuntimeStepStatus.NotApplicable, steps[DeviceRuntimeStep.ColorState].Status);
        Assert.Equal(DeviceRuntimeStepStatus.NotApplicable, steps[DeviceRuntimeStep.Clock].Status);
        Assert.Equal(DeviceRuntimeStepStatus.NotApplicable, steps[DeviceRuntimeStep.Schedules].Status);
    }

    [Fact]
    public async Task MissingCapabilitiesAreReadOnceAndSavedForTheNextConnection()
    {
        await using var fixture = new PairingFixture();
        fixture.Ble.Capabilities = AgpCapabilities.OnOff | AgpCapabilities.SecureControl;
        fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);
        await fixture.Service.StartRuntimeAsync();
        await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
        Assert.Equal(fixture.Ble.Capabilities, fixture.Repository.GetCapabilities(fixture.Address));
        Assert.Single(fixture.Ble.Reads, id => id == AgpProfile.CapabilitiesCharacteristicId);
        await fixture.Service.SuspendForBackgroundAsync().WaitAsync(Deadline);
        await fixture.Service.ResumeAfterBackgroundAsync().WaitAsync(Deadline);
        await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
        Assert.Single(fixture.Ble.Reads, id => id == AgpProfile.CapabilitiesCharacteristicId);
        Assert.Equal(2, fixture.Ble.Requests.Count(opcode => opcode == AgpOpcode.AuthToken));
    }

    [Fact]
    public async Task MissingTokenRemainsOfflineWithoutAutomaticProvisioning()
    {
        await using var fixture = RuntimeFixture();
        fixture.Repository.ClearToken(fixture.Address);
        await fixture.Service.StartRuntimeAsync();
        var error = await Record.ExceptionAsync(() => fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline));
        Assert.NotNull(error);
        Assert.IsNotType<TimeoutException>(error);
        Assert.Equal(DeviceRuntimeAvailability.Offline, fixture.Service.GetRuntimeSnapshot(fixture.Address)!.Availability);
        Assert.Equal(DeviceRuntimeStepStatus.Failed,
            fixture.Service.GetRuntimeSnapshot(fixture.Address)!.Steps[DeviceRuntimeStep.Authentication].Status);
        Assert.DoesNotContain(AgpOpcode.AuthGetToken, fixture.Ble.Requests);
        Assert.DoesNotContain(AgpProfile.CapabilitiesCharacteristicId, fixture.Ble.Reads);
    }

    [Fact]
    public async Task BackgroundInvalidatesConnectingAndQueuedDevicesWithoutWaitingForCleanup()
    {
        await using var fixture = RuntimeFixture();
        fixture.Service.RuntimeConcurrency = 1;
        var queued = fixture.AddDevice();
        Configure(fixture, queued);
        fixture.Repository.SetPinned(fixture.Address, true);
        var gate = new AsyncGate();
        fixture.Ble.OnConnect = (_, token) => gate.WaitAsync(token);
        try
        {
            await fixture.Service.StartRuntimeAsync();
            await gate.Entered.Task.WaitAsync(Deadline);
            var stop = fixture.Service.SuspendForBackgroundAsync();
            Assert.Equal(DeviceRuntimeAvailability.Offline, fixture.Service.GetRuntimeSnapshot(fixture.Address)!.Availability);
            Assert.Equal(DeviceRuntimeAvailability.Offline, fixture.Service.GetRuntimeSnapshot(queued.Address)!.Availability);
            gate.Release();
            await stop.WaitAsync(Deadline);
            Assert.Empty(queued.ConnectAttempts);
            Assert.True(fixture.Service.IsApplicationSuspended);
        }
        finally { gate.Release(); }
    }

    [Fact]
    public async Task DeviceWithoutSecureControlSkipsAuthentication()
    {
        await using var fixture = RuntimeFixture(AgpCapabilities.OnOff);
        fixture.Repository.ClearToken(fixture.Address);
        await fixture.Service.StartRuntimeAsync();
        await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
        Assert.Equal(DeviceRuntimeAvailability.Ready, fixture.Service.GetRuntimeSnapshot(fixture.Address)!.Availability);
        Assert.Equal(DeviceRuntimeStepStatus.NotApplicable,
            fixture.Service.GetRuntimeSnapshot(fixture.Address)!.Steps[DeviceRuntimeStep.Authentication].Status);
        Assert.DoesNotContain(AgpOpcode.AuthToken, fixture.Ble.Requests);
        Assert.DoesNotContain(AgpProfile.CapabilitiesCharacteristicId, fixture.Ble.Reads);
    }

    [Fact]
    public async Task SupportedInformationIsCollectedInDocumentedOrder()
    {
        var capabilities = AgpCapabilities.OnOff | AgpCapabilities.SecureControl | AgpCapabilities.Effects |
            AgpCapabilities.LocalMode | AgpCapabilities.RelativeTimer | AgpCapabilities.RtcTime | AgpCapabilities.Scheduler;
        await using var fixture = RuntimeFixture(capabilities);
        fixture.Ble.FirmwareRevision = "1.2.3";
        fixture.Ble.HardwareRevision = "PW01-R2";
        fixture.Ble.ScheduleMaximumSlots = 2;
        var operations = new ConcurrentQueue<string>();
        fixture.Ble.BeforeReadAsync = id =>
        {
            operations.Enqueue(id == AgpProfile.LightStateCharacteristicId ? "LightState" :
                id == FakeBle.FirmwareRevisionCharacteristicId ? "FirmwareRevision" : "HardwareRevision");
            return Task.CompletedTask;
        };
        fixture.Ble.BeforeWriteAsync = request =>
        {
            operations.Enqueue(request.Opcode.ToString());
            return Task.CompletedTask;
        };
        await fixture.Service.StartRuntimeAsync();
        await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
        var ordered = operations.Where(operation => operation != nameof(AgpOpcode.TimeSet)).ToArray();
        Assert.Equal(new[] { "AuthToken", "LightState", "EffectGet", "ModeGet", "DelayOnGet", "DelayOffGet", "TimeGet",
            "ScheduleListGet", "ScheduleGet", "ScheduleGet", "FirmwareRevision", "HardwareRevision" }, ordered);
        Assert.Equal("1.2.3", fixture.Repository.GetByAddress(fixture.Address)!.FirmwareVersion);
        Assert.Equal("PW01-R2", fixture.Repository.GetByAddress(fixture.Address)!.HardwareRevision);
        Assert.Equal(DeviceRuntimeAvailability.Ready, fixture.Service.GetRuntimeSnapshot(fixture.Address)!.Availability);
        Assert.Null(fixture.Repository.GetByAddress(fixture.Address)!.LastOperatedAt);
    }

    [Fact]
    public async Task SingleInitializationSlotPreparesOtherDevicesBeforeCollectingData()
    {
        await using var fixture = RuntimeFixture();
        fixture.Service.RuntimeConcurrency = 1;
        fixture.Repository.SetPinned(fixture.Address, true);
        var radios = new List<FakeBle> { fixture.Ble, fixture.AddDevice(), fixture.AddDevice() };
        foreach (var radio in radios.Skip(1)) Configure(fixture, radio);
        var connectedBeforeFirstRead = 0;
        var reads = 0;
        foreach (var radio in radios)
        {
            radio.BeforeReadAsync = id =>
            {
                if (id == AgpProfile.LightStateCharacteristicId && Interlocked.Increment(ref reads) == 1)
                    connectedBeforeFirstRead = radios.Count(device => device.ConnectAttempts.Count == 1);
                return Task.CompletedTask;
            };
        }
        await fixture.Service.StartRuntimeAsync();
        await Task.WhenAll(radios.Select(radio => fixture.Service.EnsureRuntimeReadyAsync(radio.Address))).WaitAsync(Deadline);
        Assert.Equal(radios.Count, connectedBeforeFirstRead);
        Assert.Equal(radios.Count, reads);
    }

    [Fact]
    public async Task HomeObservesOnlineReadyAndOfflineAcrossPageNavigation()
    {
        await using var fixture = RuntimeFixture();
        var gate = new AsyncGate();
        fixture.Ble.BeforeReadAsync = id => id == AgpProfile.LightStateCharacteristicId
            ? gate.WaitAsync() : Task.CompletedTask;
        var home = new MainPageViewModel(fixture.Repository, fixture.Discovery, fixture.Service, fixture.Connection,
            new AppDiagnosticService(), new TestUiDispatcher(), new TestAppNavigator(), new TestUserInteraction(), new StubMeshDeviceControlService());
        await home.OnAppearingAsync();
        var row = Assert.Single(home.Devices, device => device.Address == fixture.Address);
        Assert.Equal(AppResources.Device_Offline, row.Status);
        try
        {
            await fixture.Service.StartRuntimeAsync();
            await gate.Entered.Task.WaitAsync(Deadline);
            Assert.Equal(AppResources.Device_Online, row.Status);
            home.OnDisappearing();
            gate.Release();
            await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
            await home.OnAppearingAsync();
            Assert.Same(row, Assert.Single(home.Devices, device => device.Address == fixture.Address));
            Assert.Equal(AppResources.Device_Ready, row.Status);
            await fixture.Service.SuspendForBackgroundAsync().WaitAsync(Deadline);
            Assert.Equal(AppResources.Device_Offline, row.Status);
            Assert.False(row.IsOnline);
            Assert.Single(fixture.Ble.ConnectAttempts);
        }
        finally
        {
            gate.Release();
            home.OnDisappearing();
        }
    }

    [Fact]
    public async Task SettingsExposeTheDefaultAndSaveConcurrencyForTheNextService()
    {
        await using var fixture = RuntimeFixture();
        var settings = new SettingsPageViewModel(new LocalizationService(), new TestApplicationPresentation(),
            new TestUserInteraction(), fixture.Service);
        await settings.OnAppearingAsync();
        Assert.Equal(new[] { "1", "2", "3", "4", "5" }, settings.ConcurrencyOptions);
        Assert.Equal(4, settings.SelectedConcurrencyIndex);
        settings.SelectedConcurrencyIndex = 0;
        Assert.Equal(1, fixture.Service.RuntimeConcurrency);
        await using var restarted = new AgpLightControlService(fixture.Connection, new AppDiagnosticService(), fixture.Repository, new DeviceWorkScheduler());
        Assert.Equal(1, restarted.RuntimeConcurrency);
        settings.SelectedConcurrencyIndex = 4;
        Assert.Equal(5, fixture.Service.RuntimeConcurrency);
    }

    [Fact]
    public async Task BackgroundCancelsUnsentToggleAndResumeDoesNotReplayIt()
    {
        await using var fixture = RuntimeFixture();
        var gate = new AsyncGate();
        var auth = 0;
        fixture.Ble.BeforeWriteAsync = request => request.Opcode == AgpOpcode.AuthToken && Interlocked.Increment(ref auth) == 1
            ? gate.WaitAsync() : Task.CompletedTask;
        try
        {
            await fixture.Service.StartRuntimeAsync();
            await gate.Entered.Task.WaitAsync(Deadline);
            var toggle = fixture.Service.ToggleOnOffAsync(fixture.Address);
            var stop = fixture.Service.SuspendForBackgroundAsync();
            gate.Release();
            await stop.WaitAsync(Deadline);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => toggle);
            Assert.Equal(DeviceRuntimeOperationStage.Canceled, fixture.Service.GetRuntimeSnapshot(fixture.Address)!.LastOperation!.Stage);
            Assert.DoesNotContain(AgpOpcode.OnOffSet, fixture.Ble.Requests);
            Assert.Equal(DeviceRuntimeAvailability.Offline, fixture.Service.GetRuntimeSnapshot(fixture.Address)!.Availability);
            fixture.Ble.BeforeWriteAsync = null;
            await fixture.Service.ResumeAfterBackgroundAsync().WaitAsync(Deadline);
            await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
            Assert.DoesNotContain(AgpOpcode.OnOffSet, fixture.Ble.Requests);
            Assert.Equal(DeviceRuntimeAvailability.Ready, fixture.Service.GetRuntimeSnapshot(fixture.Address)!.Availability);
            Assert.False(fixture.Service.GetRuntimeSnapshot(fixture.Address)!.HasPendingUserOperation);
        }
        finally { gate.Release(); }
    }

    [Fact]
    public async Task SentToggleWithLostResponseIsNeverRepeatedAfterResume()
    {
        await using var fixture = RuntimeFixture();
        await fixture.Service.StartRuntimeAsync();
        await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
        var sent = Completion();
        fixture.Ble.SuppressedResponseOpcode = AgpOpcode.OnOffSet;
        fixture.Ble.BeforeControlResponse = request =>
        {
            if (request.Opcode == AgpOpcode.OnOffSet) sent.TrySetResult();
        };
        var toggle = fixture.Service.ToggleOnOffAsync(fixture.Address);
        await sent.Task.WaitAsync(Deadline);
        await fixture.Service.SuspendForBackgroundAsync().WaitAsync(Deadline);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => toggle);
        Assert.Equal(DeviceRuntimeOperationStage.Unknown, fixture.Service.GetRuntimeSnapshot(fixture.Address)!.LastOperation!.Stage);
        fixture.Ble.SuppressedResponseOpcode = null;
        await fixture.Service.ResumeAfterBackgroundAsync().WaitAsync(Deadline);
        await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
        Assert.Single(fixture.Ble.RequestMessages, request => request.Opcode == AgpOpcode.OnOffSet);
        Assert.False(fixture.Service.GetCachedDeviceStatus(fixture.Address)!.LightState!.IsOn);
        Assert.Equal(DeviceRuntimeAvailability.Ready, fixture.Service.GetRuntimeSnapshot(fixture.Address)!.Availability);
    }

    [Fact]
    public async Task SuccessfulEffectReadDoesNotInventLightStateAfterItsReadFailed()
    {
        await using var fixture = RuntimeFixture(AgpCapabilities.OnOff | AgpCapabilities.SecureControl | AgpCapabilities.Effects);
        fixture.Ble.OnLightStateRead = () => throw new IOException("state unavailable");
        await fixture.Service.StartRuntimeAsync();
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline));
        var snapshot = fixture.Service.GetRuntimeSnapshot(fixture.Address)!;
        Assert.Equal(DeviceRuntimeAvailability.Online, snapshot.Availability);
        Assert.Equal(DeviceRuntimeStepStatus.Succeeded, snapshot.Steps[DeviceRuntimeStep.Effects].Status);
        Assert.Equal(DeviceRuntimeStepStatus.Failed, snapshot.Steps[DeviceRuntimeStep.LightState].Status);
        Assert.Null(fixture.Service.GetCachedDeviceStatus(fixture.Address)!.LightState);
        Assert.Null(fixture.Service.GetCachedDeviceStatus(fixture.Address)!.Profile!.LightState);
    }

    [Fact]
    public async Task RetryCollectsFailedDataWithoutRepeatingSuccessfulStepsOrAuthentication()
    {
        await using var fixture = RuntimeFixture(AgpCapabilities.OnOff | AgpCapabilities.SecureControl |
            AgpCapabilities.Effects | AgpCapabilities.LocalMode);
        fixture.Ble.EffectResult = AgpResultCode.InternalError;
        await fixture.Service.StartRuntimeAsync();
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline));
        fixture.Ble.EffectResult = AgpResultCode.Success;
        await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
        Assert.Equal(DeviceRuntimeAvailability.Ready, fixture.Service.GetRuntimeSnapshot(fixture.Address)!.Availability);
        Assert.Single(fixture.Ble.Reads, id => id == AgpProfile.LightStateCharacteristicId);
        Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.AuthToken);
        Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.ModeGet);
        Assert.Equal(2, fixture.Ble.Requests.Count(opcode => opcode == AgpOpcode.EffectGet));
        Assert.Single(fixture.Ble.ConnectAttempts);
    }

    [Fact]
    public async Task CancelledReadWaitKeepsSharedRequestLinkedToApplicationLifetime()
    {
        await using var fixture = RuntimeFixture(AgpCapabilities.OnOff | AgpCapabilities.SecureControl | AgpCapabilities.LocalMode);
        await fixture.Service.StartRuntimeAsync();
        await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
        var gate = new AsyncGate();
        fixture.Ble.BeforeWriteAsync = request => request.Opcode == AgpOpcode.ModeGet ? gate.WaitAsync() : Task.CompletedTask;
        Task? stopping = null;
        try
        {
            using var pageCancellation = new CancellationTokenSource();
            var reading = fixture.Service.GetLocalModeAsync(fixture.Address, pageCancellation.Token);
            await gate.Entered.Task.WaitAsync(Deadline);
            pageCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
            Assert.Equal(0, fixture.Ble.DisconnectAttempts);
            stopping = fixture.Service.SuspendForBackgroundAsync();
            await stopping.WaitAsync(Deadline);
            Assert.Equal(DeviceRuntimeAvailability.Offline, fixture.Service.GetRuntimeSnapshot(fixture.Address)!.Availability);
        }
        finally
        {
            gate.Release();
            if (stopping is not null) await stopping;
        }
    }

    [Fact]
    public async Task AcceptedToggleWithoutConfirmableStateDowngradesReadyAndRecordsUnknownResult()
    {
        await using var fixture = RuntimeFixture();
        await fixture.Service.StartRuntimeAsync();
        await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
        fixture.Ble.SuppressToggleNotification = true;
        fixture.Ble.OnLightStateRead = () => throw new IOException("confirmation unavailable");
        await fixture.Service.ToggleOnOffAsync(fixture.Address).WaitAsync(Deadline);
        var snapshot = fixture.Service.GetRuntimeSnapshot(fixture.Address)!;
        Assert.Equal(DeviceRuntimeOperationStage.Unknown, snapshot.LastOperation!.Stage);
        Assert.Equal(DeviceRuntimeAvailability.Online, snapshot.Availability);
        Assert.Null(fixture.Service.GetCachedDeviceStatus(fixture.Address)!.LightState);
        Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.OnOffSet);
        Assert.NotNull(fixture.Repository.GetByAddress(fixture.Address)!.LastOperatedAt);
    }

    [Theory]
    [InlineData(9u)]
    [InlineData(10u)]
    public async Task ToggleFallbackCannotConfirmWithAStateAtOrBeforeItsKnownBaseline(uint readRevision)
    {
        await using var fixture = RuntimeFixture(AgpCapabilities.OnOff | AgpCapabilities.SecureControl |
            AgpCapabilities.RelativeTimer);
        var payload = AgpCodec.Decode(fixture.Ble.LightStatePacket).Payload.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(payload, 10);
        fixture.Ble.LightStatePacket = AgpCodec.Encode(AgpMessageType.Status, AgpOpcode.LightStateStatus, 0, payload);
        await fixture.Service.StartRuntimeAsync();
        await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
        Assert.True(fixture.Service.GetCachedDeviceStatus(fixture.Address)!.LightState!.IsOn);
        fixture.Ble.SuppressToggleNotification = true;
        fixture.Ble.OnLightStateRead = () =>
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(payload, readRevision);
            fixture.Ble.LightStatePacket = AgpCodec.Encode(AgpMessageType.Status, AgpOpcode.LightStateStatus, 0, payload);
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.ToggleControlPagePowerAsync(fixture.Address, 75).WaitAsync(Deadline));

        Assert.Equal(DeviceRuntimeOperationStage.Unknown,
            fixture.Service.GetRuntimeSnapshot(fixture.Address)!.LastOperation!.Stage);
        Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.OnOffSet);
        Assert.DoesNotContain(AgpOpcode.DelayOffSet, fixture.Ble.Requests);
        Assert.Equal(0u, fixture.Ble.DelayOffSeconds);
    }

    [Theory]
    [InlineData(AgpResultCode.InvalidValue)]
    [InlineData(AgpResultCode.Busy)]
    [InlineData(AgpResultCode.NotAuthorized)]
    public async Task ExplicitToggleRejectionIsFailedRatherThanAnUnknownOutcome(AgpResultCode result)
    {
        await using var fixture = RuntimeFixture();
        await fixture.Service.StartRuntimeAsync();
        await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
        fixture.Ble.OnOffResult = result;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ToggleOnOffAsync(fixture.Address));
        Assert.Equal(DeviceRuntimeOperationStage.Failed, fixture.Service.GetRuntimeSnapshot(fixture.Address)!.LastOperation!.Stage);
        Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.OnOffSet);
        Assert.Null(fixture.Repository.GetByAddress(fixture.Address)!.LastOperatedAt);
        Assert.True(fixture.Service.GetCachedDeviceStatus(fixture.Address)!.LightState!.IsOn);
        Assert.Equal(0, fixture.Ble.DisconnectAttempts);
    }

    [Fact]
    public async Task ManualRefreshReadsCurrentLightStateWithoutReauthenticating()
    {
        await using var fixture = RuntimeFixture();
        await fixture.Service.StartRuntimeAsync();
        await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
        Assert.True(fixture.Service.GetCachedDeviceStatus(fixture.Address)!.LightState!.IsOn);
        var payload = AgpCodec.Decode(fixture.Ble.LightStatePacket).Payload.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(payload,
            AgpModelCodec.DecodeLightState(AgpCodec.Decode(fixture.Ble.LightStatePacket)).Revision + 1);
        payload[4] = payload[5] = payload[6] = 0;
        fixture.Ble.LightStatePacket = AgpCodec.Encode(AgpMessageType.Status, AgpOpcode.LightStateStatus, 0, payload);
        await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address, refresh: true).WaitAsync(Deadline);
        Assert.False(fixture.Service.GetCachedDeviceStatus(fixture.Address)!.LightState!.IsOn);
        Assert.Equal(2, fixture.Ble.Reads.Count(id => id == AgpProfile.LightStateCharacteristicId));
        Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.AuthToken);
        Assert.Single(fixture.Ble.ConnectAttempts);
        Assert.DoesNotContain(AgpProfile.CapabilitiesCharacteristicId, fixture.Ble.Reads);
    }

    [Fact]
    public async Task FailedConnectionContinuesFairRecoveryWithoutPreventingOtherDevicesPreparing()
    {
        await using var fixture = RuntimeFixture();
        fixture.Service.RuntimeConcurrency = 1;
        fixture.Repository.SetPinned(fixture.Address, true);
        var peer = fixture.AddDevice();
        Configure(fixture, peer);
        var firstAttempt = Completion();
        fixture.Ble.OnConnect = (_, _) =>
        {
            firstAttempt.TrySetResult();
            return Task.FromException(new IOException("device unavailable"));
        };
        await fixture.Service.StartRuntimeAsync();
        await firstAttempt.Task.WaitAsync(Deadline);
        var failed = fixture.Service.EnsureRuntimeReadyAsync(fixture.Address);
        await fixture.Service.EnsureRuntimeReadyAsync(peer.Address).WaitAsync(Deadline);
        var error = await Record.ExceptionAsync(() => failed.WaitAsync(Deadline));
        Assert.NotNull(error);
        Assert.IsNotType<TimeoutException>(error);
        using var retryDeadline = new CancellationTokenSource(Deadline);
        while (fixture.Ble.ConnectAttempts.Count < 2) await Task.Delay(10, retryDeadline.Token);
        Assert.Equal(2, fixture.Ble.ConnectAttempts.Count);
        Assert.Equal(DeviceRuntimeAvailability.Offline, fixture.Service.GetRuntimeSnapshot(fixture.Address)!.Availability);
        Assert.Equal(DeviceRuntimeStepStatus.Failed,
            fixture.Service.GetRuntimeSnapshot(fixture.Address)!.Steps[DeviceRuntimeStep.Connection].Status);
        Assert.Equal(DeviceRuntimeAvailability.Ready, fixture.Service.GetRuntimeSnapshot(peer.Address)!.Availability);
        Assert.Single(peer.ConnectAttempts);
        Assert.DoesNotContain(AgpOpcode.AuthToken, fixture.Ble.Requests);
        Assert.Equal(0, fixture.Discovery.ScanCalls);
    }

    [Fact]
    public async Task UnexpectedForegroundDisconnectAutomaticallyPreparesFreshSessionWithoutReplayingToggle()
    {
        await using var fixture = RuntimeFixture();
        await fixture.Service.StartRuntimeAsync();
        await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
        await fixture.Service.ToggleOnOffAsync(fixture.Address).WaitAsync(Deadline);
        var gate = new AsyncGate();
        fixture.Ble.OnConnect = (_, token) => gate.WaitAsync(token);
        try
        {
            fixture.Ble.RaiseUnexpectedDisconnect();
            await gate.Entered.Task.WaitAsync(Deadline); // No page/API request is needed to start recovery.
            Assert.Equal(DeviceRuntimeAvailability.Offline, fixture.Service.GetRuntimeSnapshot(fixture.Address)!.Availability);
            gate.Release();
            await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
            Assert.Equal(DeviceRuntimeAvailability.Ready, fixture.Service.GetRuntimeSnapshot(fixture.Address)!.Availability);
            Assert.Equal(2, fixture.Ble.ConnectAttempts.Count);
            Assert.Equal(2, fixture.Ble.Requests.Count(opcode => opcode == AgpOpcode.AuthToken));
            Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.OnOffSet);
            Assert.False(fixture.Service.GetCachedDeviceStatus(fixture.Address)!.LightState!.IsOn);
            Assert.DoesNotContain(AgpProfile.CapabilitiesCharacteristicId, fixture.Ble.Reads);
        }
        finally { gate.Release(); }
    }

    [Fact]
    public async Task CompletingFirstMutationCannotConfirmTheNextQueuedMutation()
    {
        await using var fixture = RuntimeFixture();
        await fixture.Service.StartRuntimeAsync();
        await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
        var firstGate = new AsyncGate();
        var secondGate = new AsyncGate();
        var secondStages = new ConcurrentQueue<DeviceRuntimeOperationStage>();
        Guid secondId = default;
        fixture.Service.RuntimeStateChanged += (_, change) =>
        {
            if (change.Snapshot.LastOperation is { } operation && operation.Id == secondId)
                secondStages.Enqueue(operation.Stage);
        };
        fixture.Ble.BeforeWriteAsync = request => request.Opcode == AgpOpcode.OnOffSet
            ? request.Payload.Span[0] == 0 ? firstGate.WaitAsync() : secondGate.WaitAsync()
            : Task.CompletedTask;
        try
        {
            var first = fixture.Service.SetOnOffAsync(fixture.Address, false);
            await firstGate.Entered.Task.WaitAsync(Deadline);
            var firstId = fixture.Service.GetRuntimeSnapshot(fixture.Address)!.LastOperation!.Id;
            var second = fixture.Service.SetOnOffAsync(fixture.Address, true);
            secondId = fixture.Service.GetRuntimeSnapshot(fixture.Address)!.LastOperation!.Id;
            Assert.NotEqual(firstId, secondId);
            Assert.Equal(DeviceRuntimeOperationStage.Queued, fixture.Service.GetRuntimeSnapshot(fixture.Address)!.LastOperation!.Stage);
            firstGate.Release();
            await first.WaitAsync(Deadline);
            await secondGate.Entered.Task.WaitAsync(Deadline);
            Assert.DoesNotContain(DeviceRuntimeOperationStage.Confirmed, secondStages);
            Assert.Equal(secondId, fixture.Service.GetRuntimeSnapshot(fixture.Address)!.LastOperation!.Id);
            Assert.Equal(DeviceRuntimeOperationStage.Sent, fixture.Service.GetRuntimeSnapshot(fixture.Address)!.LastOperation!.Stage);
            secondGate.Release();
            await second.WaitAsync(Deadline);
            Assert.Equal(secondId, fixture.Service.GetRuntimeSnapshot(fixture.Address)!.LastOperation!.Id);
            Assert.Equal(DeviceRuntimeOperationStage.Confirmed, fixture.Service.GetRuntimeSnapshot(fixture.Address)!.LastOperation!.Stage);
            Assert.False(fixture.Service.GetRuntimeSnapshot(fixture.Address)!.HasPendingUserOperation);
            Assert.Equal(2, fixture.Ble.Requests.Count(opcode => opcode == AgpOpcode.OnOffSet));
        }
        finally
        {
            firstGate.Release();
            secondGate.Release();
        }
    }

    [Fact]
    public async Task LateInitialReadCannotOverwriteNewerNotificationFromTheSameConnection()
    {
        await using var fixture = RuntimeFixture();
        var gate = new AsyncGate();
        fixture.Ble.BeforeReadAsync = id => id == AgpProfile.LightStateCharacteristicId ? gate.WaitAsync() : Task.CompletedTask;
        var oldState = AgpModelCodec.DecodeLightState(AgpCodec.Decode(fixture.Ble.LightStatePacket));
        var payload = AgpCodec.Decode(fixture.Ble.LightStatePacket).Payload.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(payload, oldState.Revision + 1);
        payload[4] = payload[5] = payload[6] = 0;
        var notification = AgpCodec.Encode(AgpMessageType.Status, AgpOpcode.LightStateStatus, 0, payload);
        try
        {
            await fixture.Service.StartRuntimeAsync();
            await gate.Entered.Task.WaitAsync(Deadline);
            fixture.Ble.NotifyLightState(notification);
            Assert.False(fixture.Service.GetCachedDeviceStatus(fixture.Address)!.LightState!.IsOn);
            // The delayed native read still returns the old packet, as a value
            // captured before the newer hardware notification can do.
            gate.Release();
            await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
            var current = fixture.Service.GetCachedDeviceStatus(fixture.Address)!.LightState!;
            Assert.False(current.IsOn);
            Assert.Equal(oldState.Revision + 1, current.Revision);
            Assert.Equal(DeviceRuntimeAvailability.Ready, fixture.Service.GetRuntimeSnapshot(fixture.Address)!.Availability);
            Assert.Single(fixture.Ble.Reads, id => id == AgpProfile.LightStateCharacteristicId);
        }
        finally { gate.Release(); }
    }

    [Fact]
    public async Task DelayAndClockValuesRemainHistoricalInBackgroundAndRefreshAfterResume()
    {
        await using var fixture = RuntimeFixture(AgpCapabilities.OnOff | AgpCapabilities.SecureControl |
            AgpCapabilities.RelativeTimer | AgpCapabilities.RtcTime);
        fixture.Ble.DelayOnSeconds = 45;
        fixture.Ble.DelayOffSeconds = 90;
        fixture.Ble.TimeIsValid = false;
        var earliestUtc = checked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await fixture.Service.StartRuntimeAsync();
        await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
        var ready = fixture.Service.GetRuntimeSnapshot(fixture.Address)!;
        Assert.Equal(new AgpDelayOnResponse(AgpResultCode.Success, true, 45), ready.RelativeDelayOn);
        Assert.Equal(new AgpDelayOffResponse(AgpResultCode.Success, true, 90), ready.RelativeDelayOff);
        Assert.True(ready.Clock!.IsValid);
        Assert.InRange(ready.Clock.UtcSeconds, earliestUtc, checked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        Assert.Equal((ushort)2, ready.Clock.TimeRevision); // Snapshot carries the successful TIME_SET result.
        Assert.Equal(DeviceRuntimeStepStatus.Succeeded, ready.Steps[DeviceRuntimeStep.Clock].Status);
        await fixture.Service.SuspendForBackgroundAsync().WaitAsync(Deadline);
        var stopped = fixture.Service.GetRuntimeSnapshot(fixture.Address)!;
        Assert.Equal(DeviceRuntimeAvailability.Offline, stopped.Availability);
        Assert.Equal(ready.RelativeDelayOn, stopped.RelativeDelayOn);
        Assert.Equal(ready.RelativeDelayOff, stopped.RelativeDelayOff);
        Assert.Equal(ready.Clock, stopped.Clock);
        foreach (var step in new[] { DeviceRuntimeStep.RelativeDelayOn, DeviceRuntimeStep.RelativeDelayOff, DeviceRuntimeStep.Clock })
            Assert.Equal(DeviceRuntimeStepStatus.Invalidated, stopped.Steps[step].Status);
        fixture.Ble.DelayOnSeconds = fixture.Ble.DelayOffSeconds = 0;
        fixture.Ble.TimeIsValid = false;
        await fixture.Service.ResumeAfterBackgroundAsync().WaitAsync(Deadline);
        await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
        var resumed = fixture.Service.GetRuntimeSnapshot(fixture.Address)!;
        Assert.Equal(new AgpDelayOnResponse(AgpResultCode.Success, false, 0), resumed.RelativeDelayOn);
        Assert.Equal(new AgpDelayOffResponse(AgpResultCode.Success, false, 0), resumed.RelativeDelayOff);
        Assert.True(resumed.Clock!.IsValid);
        Assert.True(resumed.Clock.TimeRevision > ready.Clock.TimeRevision);
        Assert.Equal(DeviceRuntimeAvailability.Ready, resumed.Availability);
        Assert.Null(fixture.Repository.GetByAddress(fixture.Address)!.LastOperatedAt);
    }

    [Fact]
    public async Task FailedDelayAndClockRefreshPreservesHistoricalValuesWithFailedStepMarkers()
    {
        await using var fixture = RuntimeFixture(AgpCapabilities.OnOff | AgpCapabilities.SecureControl |
            AgpCapabilities.RelativeTimer | AgpCapabilities.RtcTime);
        fixture.Ble.DelayOffSeconds = 60;
        await fixture.Service.StartRuntimeAsync();
        await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
        var previous = fixture.Service.GetRuntimeSnapshot(fixture.Address)!;
        fixture.Ble.TimerResult = fixture.Ble.TimeResult = AgpResultCode.InternalError;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.EnsureRuntimeReadyAsync(fixture.Address, refresh: true));
        var failed = fixture.Service.GetRuntimeSnapshot(fixture.Address)!;
        Assert.Equal(DeviceRuntimeAvailability.Online, failed.Availability);
        Assert.Equal(previous.RelativeDelayOn, failed.RelativeDelayOn);
        Assert.Equal(previous.RelativeDelayOff, failed.RelativeDelayOff);
        Assert.Equal(previous.Clock, failed.Clock);
        foreach (var step in new[] { DeviceRuntimeStep.RelativeDelayOn, DeviceRuntimeStep.RelativeDelayOff, DeviceRuntimeStep.Clock })
            Assert.Equal(DeviceRuntimeStepStatus.Failed, failed.Steps[step].Status);
        Assert.Single(fixture.Ble.ConnectAttempts);
        Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.AuthToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledQueuedWorkCompletesPromptlyEvenIfAttentionChanged(bool attentionChanged)
    {
        var scheduler = new DeviceWorkScheduler { Limit = 1 };
        var gate = new AsyncGate();
        var current = scheduler.Enqueue("active", 2, async token =>
        {
            await gate.WaitAsync(token);
            return true;
        }, CancellationToken.None);
        await gate.Entered.Task.WaitAsync(Deadline);
        var queuedExecuted = false;
        using var queueCancellation = new CancellationTokenSource();
        var queued = scheduler.Enqueue("queued", 4, _ =>
        {
            queuedExecuted = true;
            return Task.FromResult(true);
        }, queueCancellation.Token);
        if (attentionChanged) scheduler.SetAttention("queued", true);
        try
        {
            queueCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(Deadline));
            Assert.False(current.IsCompleted);
            Assert.False(queuedExecuted);
        }
        finally { gate.Release(); }
        await current.WaitAsync(Deadline);
        await scheduler.DrainAsync().WaitAsync(Deadline);
        Assert.False(queuedExecuted);
    }

    [Fact]
    public async Task RepeatedForegroundResumeKeepsInFlightQueryLinkedToTheNextBackgroundStop()
    {
        await using var fixture = RuntimeFixture(AgpCapabilities.OnOff | AgpCapabilities.SecureControl | AgpCapabilities.LocalMode);
        await fixture.Service.StartRuntimeAsync();
        await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
        var gate = new AsyncGate();
        fixture.Ble.BeforeWriteAsync = request => request.Opcode == AgpOpcode.ModeGet ? gate.WaitAsync() : Task.CompletedTask;
        Task? stopping = null;
        try
        {
            var reading = fixture.Service.GetLocalModeAsync(fixture.Address);
            await gate.Entered.Task.WaitAsync(Deadline);
            await fixture.Service.ResumeAfterBackgroundAsync().WaitAsync(Deadline);
            Assert.False(reading.IsCompleted);
            Assert.Single(fixture.Ble.ConnectAttempts);
            stopping = fixture.Service.SuspendForBackgroundAsync();
            await stopping.WaitAsync(Deadline);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
            Assert.Equal(DeviceRuntimeAvailability.Offline, fixture.Service.GetRuntimeSnapshot(fixture.Address)!.Availability);
            Assert.False(fixture.Service.GetCachedDeviceStatus(fixture.Address)!.IsOnline);
            Assert.DoesNotContain(AgpOpcode.OnOffSet, fixture.Ble.Requests);
            Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.AuthToken);
        }
        finally
        {
            gate.Release();
            if (stopping is not null) await stopping;
        }
    }

    [Fact]
    public async Task SuccessfulWritesSynchronizeDelayClockAndEffectCachesWithoutExtraReads()
    {
        await using var fixture = RuntimeFixture(AgpCapabilities.OnOff | AgpCapabilities.SecureControl |
            AgpCapabilities.RelativeTimer | AgpCapabilities.RtcTime | AgpCapabilities.Scheduler | AgpCapabilities.Effects);
        fixture.Ble.ScheduleMaximumSlots = 1;
        await fixture.Service.StartRuntimeAsync();
        await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
        var clockRevision = fixture.Service.GetRuntimeSnapshot(fixture.Address)!.Clock!.TimeRevision;
        await fixture.Service.SetEffectAsync(fixture.Address, AgpLightMode.Blink, 750).WaitAsync(Deadline);
        Assert.Equal((ushort)750, fixture.Service.GetCachedDeviceStatus(fixture.Address)!.Profile!.EffectPeriodMilliseconds);
        Assert.Equal(AgpLightMode.Blink, fixture.Service.GetCachedDeviceStatus(fixture.Address)!.Profile!.EffectMode);
        await fixture.Service.SetTimerAsync(fixture.Address, TimeSpan.FromSeconds(30), turnOn: true).WaitAsync(Deadline);
        Assert.Equal(new AgpDelayOnResponse(AgpResultCode.Success, true, 30), fixture.Service.GetRuntimeSnapshot(fixture.Address)!.RelativeDelayOn);
        await fixture.Service.SetTimerAsync(fixture.Address, TimeSpan.FromSeconds(45), turnOn: false).WaitAsync(Deadline);
        Assert.Equal(new AgpDelayOffResponse(AgpResultCode.Success, true, 45), fixture.Service.GetRuntimeSnapshot(fixture.Address)!.RelativeDelayOff);
        await fixture.Service.SetTimerAsync(fixture.Address, TimeSpan.Zero, turnOn: true).WaitAsync(Deadline);
        await fixture.Service.SetTimerAsync(fixture.Address, TimeSpan.Zero, turnOn: false).WaitAsync(Deadline);
        Assert.Equal(new AgpDelayOnResponse(AgpResultCode.Success, false, 0), fixture.Service.GetRuntimeSnapshot(fixture.Address)!.RelativeDelayOn);
        Assert.Equal(new AgpDelayOffResponse(AgpResultCode.Success, false, 0), fixture.Service.GetRuntimeSnapshot(fixture.Address)!.RelativeDelayOff);
        // Change fake hardware without a notification; Toggle must still use
        // hardware direction and cache the successful one-shot DELAY_OFF response.
        var payload = AgpCodec.Decode(fixture.Ble.LightStatePacket).Payload.ToArray();
        payload[4] = payload[5] = payload[6] = 0;
        fixture.Ble.LightStatePacket = AgpCodec.Encode(AgpMessageType.Status, AgpOpcode.LightStateStatus, 0, payload);
        await fixture.Service.ToggleControlPagePowerAsync(fixture.Address, 75).WaitAsync(Deadline);
        Assert.True(fixture.Service.GetCachedDeviceStatus(fixture.Address)!.LightState!.IsOn);
        Assert.Equal(new AgpDelayOffResponse(AgpResultCode.Success, true, 75), fixture.Service.GetRuntimeSnapshot(fixture.Address)!.RelativeDelayOff);
        var schedule = AgpSchedule.Daily(0, true, AgpScheduleAction.Off, 0, 3600);
        await fixture.Service.SetScheduleAsync(fixture.Address, schedule).WaitAsync(Deadline);
        await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
        var final = fixture.Service.GetRuntimeSnapshot(fixture.Address)!;
        Assert.True(final.Clock!.IsValid);
        Assert.True(final.Clock.TimeRevision > clockRevision);
        Assert.Equal(schedule, fixture.Ble.Schedules[0]);
        Assert.Equal((ushort)750, fixture.Service.GetCachedDeviceStatus(fixture.Address)!.Profile!.EffectPeriodMilliseconds);
        Assert.Equal(DeviceRuntimeAvailability.Ready, final.Availability);
        Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.DelayOnGet);
        Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.DelayOffGet);
        Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.TimeGet);
        Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.EffectGet);
        Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.AuthToken);
        Assert.Single(fixture.Ble.ConnectAttempts);
        Assert.Single(fixture.Ble.Reads, id => id == AgpProfile.LightStateCharacteristicId);
    }

    private static PairingFixture RuntimeFixture(
        AgpCapabilities capabilities = AgpCapabilities.OnOff | AgpCapabilities.SecureControl)
    {
        var fixture = new PairingFixture();
        Configure(fixture, fixture.Ble, capabilities);
        return fixture;
    }

    private static void Configure(PairingFixture fixture, FakeBle radio,
        AgpCapabilities capabilities = AgpCapabilities.OnOff | AgpCapabilities.SecureControl)
    {
        radio.Capabilities = capabilities;
        fixture.Repository.SetTokenAndCapabilities(radio.Address, [1, 2, 3, 4], capabilities);
    }

    private static TaskCompletionSource Completion() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class AsyncGate
    {
        public TaskCompletionSource Entered { get; } = Completion();
        private readonly TaskCompletionSource _release = Completion();

        public async Task WaitAsync(CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }

        public void Release() => _release.TrySetResult();
    }
}
