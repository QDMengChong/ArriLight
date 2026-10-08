using System.Buffers.Binary;
using ArriLight.ViewModels;
using ArriOpto.BLE.Agp;
using Xunit;

namespace ArriLight.Tests;

public sealed class RuntimeFinalRegressionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task NextScheduledDeviceUsesItsSubmitterContextAfterPreviousDeviceIsCanceled()
    {
        var scheduler = new DeviceWorkScheduler { Limit = 1 };
        var owner = new AsyncLocal<string?>();
        using var firstCancellation = new CancellationTokenSource();
        var firstStarted = Signal();
        var firstRelease = Signal();
        owner.Value = "first-device";
        var first = scheduler.Enqueue("first", 2, async token =>
        {
            Assert.Equal("first-device", owner.Value);
            firstStarted.TrySetResult();
            await firstRelease.Task.WaitAsync(token);
            return owner.Value;
        }, firstCancellation.Token);
        try
        {
            await firstStarted.Task.WaitAsync(Deadline);
            owner.Value = "second-device";
            var second = scheduler.Enqueue("second", 2,
                _ => Task.FromResult(owner.Value), CancellationToken.None);
            owner.Value = "calling-page";

            // The first worker's completion pumps the queued second device.
            // Its canceled preparation context must not become the next owner.
            firstCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            Assert.Equal("second-device", await second.WaitAsync(Deadline));
            Assert.Equal("calling-page", owner.Value);
            await scheduler.DrainAsync().WaitAsync(Deadline);
        }
        finally
        {
            firstCancellation.Cancel();
            firstRelease.TrySetResult();
        }
    }

    [Fact]
    public async Task SuccessfulDelayWriteAfterFailedReadRestoresReadyWithoutKeepingItsOldError()
    {
        await using var fixture = RuntimeFixture(AgpCapabilities.OnOff |
            AgpCapabilities.SecureControl | AgpCapabilities.RelativeTimer);
        fixture.Ble.BeforeWriteAsync = request =>
        {
            fixture.Ble.TimerResult = request.Opcode == AgpOpcode.DelayOffGet
                ? AgpResultCode.InternalError : AgpResultCode.Success;
            return Task.CompletedTask;
        };
        await fixture.Service.StartRuntimeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline));
        var failed = fixture.Service.GetRuntimeSnapshot(fixture.Address)!;
        Assert.Equal(DeviceRuntimeAvailability.Online, failed.Availability);
        Assert.Equal(DeviceRuntimeStepStatus.Failed, failed.Steps[DeviceRuntimeStep.RelativeDelayOff].Status);
        Assert.NotNull(failed.Error);

        fixture.Ble.BeforeWriteAsync = null;
        fixture.Ble.TimerResult = AgpResultCode.Success;
        await fixture.Service.SetTimerAsync(fixture.Address, TimeSpan.FromSeconds(45), turnOn: false)
            .WaitAsync(Deadline);

        // Do not join another preparation before observing the successful write.
        // The write itself completes the last missing piece of runtime data.
        var ready = fixture.Service.GetRuntimeSnapshot(fixture.Address)!;
        Assert.Equal(DeviceRuntimeAvailability.Ready, ready.Availability);
        Assert.Equal(DeviceRuntimeStepStatus.Succeeded, ready.Steps[DeviceRuntimeStep.RelativeDelayOff].Status);
        Assert.Equal(new AgpDelayOffResponse(AgpResultCode.Success, true, 45), ready.RelativeDelayOff);
        Assert.Null(ready.Error);
        Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.DelayOffGet);
        Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.DelayOffSet);
        Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.AuthToken);
    }

    [Fact]
    public async Task OldLightNotificationBeforeToggleAcceptanceCannotScheduleAutomaticOff()
    {
        await using var fixture = RuntimeFixture(AgpCapabilities.OnOff |
            AgpCapabilities.SecureControl | AgpCapabilities.RelativeTimer);
        fixture.Ble.LightStatePacket = StatePacket(fixture.Ble.LightStatePacket, 10, isOn: true);
        await fixture.Service.StartRuntimeAsync();
        await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
        var staleOn = StatePacket(fixture.Ble.LightStatePacket, 9, isOn: true);
        var oldNotificationSent = false;
        fixture.Ble.BeforeControlResponse = request =>
        {
            if (request.Opcode != AgpOpcode.OnOffSet) return;
            oldNotificationSent = true;
            fixture.Ble.NotifyLightState(staleOn);
        };

        // Hardware accepts the toggle and then notifies revision 11, now off.
        // The revision 9 on-state must not finish confirmation or trigger a timer.
        await fixture.Service.ToggleControlPagePowerAsync(fixture.Address, 75).WaitAsync(Deadline);
        Assert.True(oldNotificationSent);
        Assert.DoesNotContain(AgpOpcode.DelayOffSet, fixture.Ble.Requests);
        Assert.Equal(0u, fixture.Ble.DelayOffSeconds);
        var state = fixture.Service.GetCachedDeviceStatus(fixture.Address)!.LightState!;
        Assert.False(state.IsOn);
        Assert.Equal(11u, state.Revision);
        Assert.Equal(DeviceRuntimeOperationStage.Confirmed,
            fixture.Service.GetRuntimeSnapshot(fixture.Address)!.LastOperation!.Stage);
        Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.OnOffSet);
    }

    [Fact]
    public async Task DeviceInformationPageJoinsPreparationAndLeavingCancelsOnlyItsOwnWait()
    {
        await using var fixture = RuntimeFixture(AgpCapabilities.OnOff | AgpCapabilities.SecureControl);
        var readStarted = Signal();
        var readRelease = Signal();
        fixture.Ble.FirmwareRevision = "3.4.5";
        fixture.Ble.BeforeReadAsync = id =>
        {
            if (id != AgpProfile.LightStateCharacteristicId) return Task.CompletedTask;
            readStarted.TrySetResult();
            return readRelease.Task;
        };
        var model = new DeviceMorePageViewModel(fixture.Repository, fixture.Service,
            new TestAppNavigator(), new TestUserInteraction(), new TestUiDispatcher());
        model.ApplyQueryAttributes(new Dictionary<string, object> { ["address"] = fixture.Address });
        try
        {
            await fixture.Service.StartRuntimeAsync();
            await readStarted.Task.WaitAsync(Deadline);
            await model.OnAppearingAsync();
            var visibleWait = model.RuntimePreparationTask;
            Assert.False(visibleWait.IsCompleted);
            var displayedVersion = model.FirmwareVersion;
            Assert.Equal(DeviceRuntimeAvailability.Online,
                fixture.Service.GetRuntimeSnapshot(fixture.Address)!.Availability);

            model.OnDisappearing();
            await visibleWait.WaitAsync(Deadline);
            Assert.Equal(DeviceRuntimeStepStatus.Running,
                fixture.Service.GetRuntimeSnapshot(fixture.Address)!.Steps[DeviceRuntimeStep.LightState].Status);
            Assert.True(fixture.Connection.IsConnected(fixture.Address));
            Assert.Equal(0, fixture.Ble.DisconnectAttempts);
            Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.AuthToken);

            readRelease.TrySetResult();
            await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
            Assert.Equal(DeviceRuntimeAvailability.Ready,
                fixture.Service.GetRuntimeSnapshot(fixture.Address)!.Availability);
            Assert.Equal("3.4.5", fixture.Repository.GetByAddress(fixture.Address)!.FirmwareVersion);
            Assert.Equal(displayedVersion, model.FirmwareVersion);

            await model.OnAppearingAsync();
            await model.RuntimePreparationTask.WaitAsync(Deadline);
            Assert.Equal("3.4.5", model.FirmwareVersion);
            Assert.Equal(string.Empty, model.ErrorMessage);
            Assert.Single(fixture.Ble.ConnectAttempts);
            Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.AuthToken);
            Assert.Single(fixture.Ble.Reads, id => id == AgpProfile.LightStateCharacteristicId);
        }
        finally
        {
            readRelease.TrySetResult();
            model.OnDisappearing();
        }
    }

    [Theory]
    [InlineData(11u)]
    [InlineData(12u)]
    public async Task PreCommandNotificationAtOrBeforeAcceptanceDoesNotConfirmTheToggle(uint responseRevision)
    {
        await using var fixture = RuntimeFixture(AgpCapabilities.OnOff |
            AgpCapabilities.SecureControl | AgpCapabilities.RelativeTimer);
        fixture.Ble.LightStatePacket = StatePacket(fixture.Ble.LightStatePacket, 10, isOn: true);
        await fixture.Service.StartRuntimeAsync();
        await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
        fixture.Ble.SuppressToggleNotification = true;
        fixture.Ble.OnOffStateRevision = responseRevision;
        fixture.Ble.BeforeControlResponse = request =>
        {
            if (request.Opcode != AgpOpcode.OnOffSet) return;
            // A preceding local operation finishes just before this command.
            // Its ON state cannot stand in for the later OFF result.
            fixture.Ble.LightStatePacket = StatePacket(fixture.Ble.LightStatePacket, 11, isOn: true);
            fixture.Ble.NotifyLightState(fixture.Ble.LightStatePacket);
        };

        await fixture.Service.ToggleControlPagePowerAsync(fixture.Address, 75).WaitAsync(Deadline);

        var state = fixture.Service.GetCachedDeviceStatus(fixture.Address)!.LightState!;
        Assert.False(state.IsOn);
        Assert.Equal(12u, state.Revision);
        Assert.Equal(DeviceRuntimeOperationStage.Confirmed,
            fixture.Service.GetRuntimeSnapshot(fixture.Address)!.LastOperation!.Stage);
        Assert.DoesNotContain(AgpOpcode.DelayOffSet, fixture.Ble.Requests);
        Assert.Equal(2, fixture.Ble.Reads.Count(id => id == AgpProfile.LightStateCharacteristicId));
        Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.OnOffSet);
    }

    [Fact]
    public async Task AReadDuringTheTransitionCannotTriggerAutomaticOffBeforeToggleCompletion()
    {
        await using var fixture = RuntimeFixture(AgpCapabilities.OnOff |
            AgpCapabilities.SecureControl | AgpCapabilities.RelativeTimer);
        fixture.Ble.LightStatePacket = StatePacket(fixture.Ble.LightStatePacket, 10, isOn: false);
        await fixture.Service.StartRuntimeAsync();
        await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
        fixture.Ble.SuppressToggleNotification = true;
        fixture.Ble.OnLightStateRead = () =>
        {
            var payload = AgpCodec.Decode(fixture.Ble.LightStatePacket).Payload.ToArray();
            payload[8] = 1; // The hardware still reports a transition in progress.
            fixture.Ble.LightStatePacket = AgpCodec.Encode(AgpMessageType.Status, AgpOpcode.LightStateStatus, 0, payload);
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.ToggleControlPagePowerAsync(fixture.Address, 75).WaitAsync(Deadline));

        Assert.Equal(DeviceRuntimeOperationStage.Unknown,
            fixture.Service.GetRuntimeSnapshot(fixture.Address)!.LastOperation!.Stage);
        Assert.DoesNotContain(AgpOpcode.DelayOffSet, fixture.Ble.Requests);
        Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.OnOffSet);
    }

    private static PairingFixture RuntimeFixture(AgpCapabilities capabilities)
    {
        var fixture = new PairingFixture();
        fixture.Ble.Capabilities = capabilities;
        fixture.Repository.SetTokenAndCapabilities(fixture.Address, [1, 2, 3, 4], capabilities);
        return fixture;
    }

    private static byte[] StatePacket(byte[] source, uint revision, bool isOn)
    {
        var payload = AgpCodec.Decode(source).Payload.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(payload, revision);
        payload[4] = isOn ? (byte)1 : (byte)0;
        payload[5] = payload[6] = isOn ? payload[7] : (byte)0;
        return AgpCodec.Encode(AgpMessageType.Status, AgpOpcode.LightStateStatus, 0, payload);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
