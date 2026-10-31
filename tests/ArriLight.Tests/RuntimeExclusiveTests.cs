using ArriOpto.BLE.Agp;
using Xunit;

namespace ArriLight.Tests;

public sealed class RuntimeExclusiveTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task OtaOwnsOnlyTargetAndRefreshesItsCapabilitiesAndAuthenticationAfterwards()
    {
        await using var fixture = CreateFixture();
        var peer = fixture.AddDevice();
        Configure(fixture, peer);
        await fixture.Service.StartRuntimeAsync();
        await Task.WhenAll(fixture.Service.EnsureRuntimeReadyAsync(fixture.Address),
            fixture.Service.EnsureRuntimeReadyAsync(peer.Address)).WaitAsync(Deadline);
        var started = Signal();
        var release = Signal();
        var updating = fixture.Service.RunExclusiveOtaAsync(fixture.Address, async (_, token) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token);
        });
        await started.Task.WaitAsync(Deadline);
        try
        {
            Assert.Equal(DeviceRuntimeAvailability.Offline, fixture.Service.GetRuntimeSnapshot(fixture.Address)!.Availability);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ToggleOnOffAsync(fixture.Address));
            await fixture.Service.ToggleOnOffAsync(peer.Address).WaitAsync(Deadline);
            Assert.Single(peer.Requests, opcode => opcode == AgpOpcode.OnOffSet);
            Assert.DoesNotContain(AgpOpcode.OnOffSet, fixture.Ble.Requests);
        }
        finally { release.TrySetResult(); }
        await updating.WaitAsync(Deadline);
        await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
        Assert.Equal(2, fixture.Ble.ConnectAttempts.Count);
        Assert.Equal(2, fixture.Ble.Requests.Count(opcode => opcode == AgpOpcode.AuthToken));
        Assert.Single(fixture.Ble.Reads, id => id == AgpProfile.CapabilitiesCharacteristicId);
        Assert.Equal(fixture.Ble.Capabilities, fixture.Repository.GetCapabilities(fixture.Address));
        Assert.Null(fixture.Repository.GetByAddress(fixture.Address)!.LastOperatedAt);
        Assert.DoesNotContain(AgpProfile.CapabilitiesCharacteristicId, peer.Reads);
    }

    [Fact]
    public async Task RemovingConnectingTargetCancelsPreparationBeforeDeletingItsRecord()
    {
        await using var fixture = CreateFixture();
        var started = Signal();
        fixture.Ble.OnConnect = async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        await fixture.Service.StartRuntimeAsync();
        await started.Task.WaitAsync(Deadline);
        Assert.True(await fixture.Service.RemoveDeviceAsync(fixture.Address).WaitAsync(Deadline));
        Assert.False(fixture.Repository.Contains(fixture.Address));
        Assert.Null(fixture.Service.GetRuntimeSnapshot(fixture.Address));
        Assert.Null(fixture.Service.GetCachedDeviceStatus(fixture.Address));
        Assert.False(fixture.Connection.IsConnected(fixture.Address));
        Assert.Empty(fixture.Ble.Requests);
        Assert.Single(fixture.Ble.ConnectAttempts);
    }

    [Fact]
    public async Task FailedRemovalKeepsRecordAndOfflineErrorForAnExplicitRetry()
    {
        await using var fixture = CreateFixture();
        await fixture.Service.StartRuntimeAsync();
        await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
        fixture.Ble.FailDisconnect = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.RemoveDeviceAsync(fixture.Address));
        Assert.True(fixture.Repository.Contains(fixture.Address));
        var failed = fixture.Service.GetRuntimeSnapshot(fixture.Address)!;
        Assert.Equal(DeviceRuntimeAvailability.Offline, failed.Availability);
        Assert.Contains("cleanup failed", failed.Error);
        Assert.Single(fixture.Ble.ConnectAttempts);
        fixture.Ble.FailDisconnect = false;
        Assert.True(await fixture.Service.RemoveDeviceAsync(fixture.Address).WaitAsync(Deadline));
        Assert.False(fixture.Repository.Contains(fixture.Address));
        Assert.Null(fixture.Service.GetRuntimeSnapshot(fixture.Address));
    }

    [Fact]
    public async Task BackgroundCancelsExclusiveOtaWithoutRestartingItsWorkerOrReplayingCommands()
    {
        await using var fixture = CreateFixture();
        await fixture.Service.StartRuntimeAsync();
        await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
        var started = Signal();
        var updating = fixture.Service.RunExclusiveOtaAsync(fixture.Address, async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        await started.Task.WaitAsync(Deadline);
        await fixture.Service.SuspendForBackgroundAsync().WaitAsync(Deadline);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => updating);
        Assert.Equal(DeviceRuntimeAvailability.Offline, fixture.Service.GetRuntimeSnapshot(fixture.Address)!.Availability);
        Assert.Empty(fixture.Connection.GetConnectedAddresses());
        Assert.Null(fixture.Repository.GetCapabilities(fixture.Address));
        Assert.Single(fixture.Ble.ConnectAttempts);
        await fixture.Service.ResumeAfterBackgroundAsync().WaitAsync(Deadline);
        await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
        Assert.Equal(2, fixture.Ble.ConnectAttempts.Count);
        Assert.Single(fixture.Ble.Reads, id => id == AgpProfile.CapabilitiesCharacteristicId);
        Assert.DoesNotContain(AgpOpcode.OnOffSet, fixture.Ble.Requests);
    }

    [Fact]
    public async Task OtaFailureInvalidatesOldSessionAndOrdinaryPreparationCanRecover()
    {
        await using var fixture = CreateFixture();
        await fixture.Service.StartRuntimeAsync();
        await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.RunExclusiveOtaAsync(fixture.Address,
            (_, _) => Task.FromException(new IOException("transfer failed"))));
        await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
        Assert.Equal(DeviceRuntimeAvailability.Ready, fixture.Service.GetRuntimeSnapshot(fixture.Address)!.Availability);
        Assert.Equal(2, fixture.Ble.Requests.Count(opcode => opcode == AgpOpcode.AuthToken));
        Assert.Single(fixture.Ble.Reads, id => id == AgpProfile.CapabilitiesCharacteristicId);
        Assert.Null(fixture.Repository.GetByAddress(fixture.Address)!.LastOperatedAt);
    }

    [Fact]
    public async Task ToggleQueuedBeforeOtaIsInvalidatedInsteadOfExecutingAfterUpgrade()
    {
        await using var fixture = CreateFixture();
        var readStarted = Signal();
        var readRelease = Signal();
        fixture.Ble.BeforeReadAsync = id =>
        {
            if (id != AgpProfile.LightStateCharacteristicId) return Task.CompletedTask;
            readStarted.TrySetResult();
            return readRelease.Task;
        };
        await fixture.Service.StartRuntimeAsync();
        await readStarted.Task.WaitAsync(Deadline);
        var queuedToggle = fixture.Service.ToggleOnOffAsync(fixture.Address);
        var otaStarted = Signal();
        var otaRelease = Signal();
        var updating = fixture.Service.RunExclusiveOtaAsync(fixture.Address, async (_, token) =>
        {
            otaStarted.TrySetResult();
            await otaRelease.Task.WaitAsync(token);
        });
        try
        {
            await otaStarted.Task.WaitAsync(Deadline);
            fixture.Ble.BeforeReadAsync = null;
        }
        finally { readRelease.TrySetResult(); otaRelease.TrySetResult(); }
        await updating.WaitAsync(Deadline);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queuedToggle);
        await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
        Assert.DoesNotContain(AgpOpcode.OnOffSet, fixture.Ble.Requests);
        Assert.False(fixture.Service.GetRuntimeSnapshot(fixture.Address)!.HasPendingUserOperation);
    }

    [Fact]
    public async Task RebindingUsesTheTargetBarrierAndThenReauthenticatesWithItsNewToken()
    {
        await using var fixture = CreateFixture();
        await fixture.Service.StartRuntimeAsync();
        await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
        var started = Signal();
        var release = Signal();
        var bindingStartedAt = DateTimeOffset.Now;
        var binding = fixture.Service.BindAsync(fixture.Address, async (stage, token) =>
        {
            if (stage != AgpBindingStage.ReceivingToken) return;
            started.TrySetResult();
            await release.Task.WaitAsync(token);
        }, deviceName: "Replacement");
        await started.Task.WaitAsync(Deadline);
        try
        {
            Assert.Equal(DeviceRuntimeAvailability.Offline, fixture.Service.GetRuntimeSnapshot(fixture.Address)!.Availability);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ToggleOnOffAsync(fixture.Address));
        }
        finally { release.TrySetResult(); }
        await binding.WaitAsync(Deadline);
        await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
        Assert.Equal("A1B2C3D4", fixture.Repository.GetToken(fixture.Address));
        var saved = fixture.Repository.GetByAddress(fixture.Address)!;
        Assert.Equal("Replacement", saved.Name);
        Assert.InRange(saved.AddedAt, bindingStartedAt, DateTimeOffset.Now);
        Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.AuthGetToken);
        Assert.Equal(2, fixture.Ble.Requests.Count(opcode => opcode == AgpOpcode.AuthToken));
        Assert.DoesNotContain(AgpOpcode.OnOffSet, fixture.Ble.Requests);
    }

    [Fact]
    public async Task ExplicitRebindClearsAuthorizationBlockEvenWhenDeviceReturnsSameToken()
    {
        await using var fixture = CreateFixture();
        fixture.Repository.SetToken(fixture.Address, [0xA1, 0xB2, 0xC3, 0xD4]);
        fixture.Ble.AuthenticationResult = AgpResultCode.NotAuthorized;
        await fixture.Service.StartRuntimeAsync();
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline));
        Assert.Equal(DeviceRuntimeStepStatus.Failed,
            fixture.Service.GetRuntimeSnapshot(fixture.Address)!.Steps[DeviceRuntimeStep.Authentication].Status);
        fixture.Ble.AuthenticationResult = AgpResultCode.Success;

        await fixture.Service.BindAsync(fixture.Address, bindingProgress: null,
            deviceName: "Replacement").WaitAsync(Deadline);
        await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);

        Assert.Equal("Replacement", fixture.Repository.GetByAddress(fixture.Address)!.Name);
        Assert.Equal(DeviceRuntimeAvailability.Ready, fixture.Service.GetRuntimeSnapshot(fixture.Address)!.Availability);
        Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.AuthGetToken);
        Assert.Equal(2, fixture.Ble.Requests.Count(opcode => opcode == AgpOpcode.AuthToken));
    }

    private static PairingFixture CreateFixture()
    {
        var fixture = new PairingFixture();
        Configure(fixture, fixture.Ble);
        return fixture;
    }

    private static void Configure(PairingFixture fixture, FakeBle radio)
    {
        radio.Capabilities = AgpCapabilities.OnOff | AgpCapabilities.SecureControl;
        fixture.Repository.SetTokenAndCapabilities(radio.Address, [1, 2, 3, 4], radio.Capabilities);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
