using System.Collections.Concurrent;
using ArriOpto.BLE.Agp;
using Xunit;

namespace ArriLight.Tests;

public sealed class DeviceGroupTests
{
    private const AgpCapabilities Basic = AgpCapabilities.OnOff | AgpCapabilities.Lightness;

    [Fact]
    public async Task HomeGroupEntrySharesNavigationGuardWithDevicesAndReopensAfterReturning()
    {
        await using var f = new PairingFixture();
        var backend = new GroupBackend();
        var groups = new DeviceGroupService(new DeviceGroupRepository(f.Repository), f.Repository, backend);
        var group = groups.Repository.Create("Home group");
        var navigator = new GroupNavigator();
        await using var mesh = new MeshDeviceControlService(f.Connection, new MeshNetworkManager(), new AppDiagnosticService(), repository: f.Repository);
        var home = new MainPageViewModel(f.Repository, f.Discovery, f.Service, f.Connection, new AppDiagnosticService(),
            new ImmediateDispatcher(), navigator, new GroupInteraction(), mesh, groups);
        await home.OnAppearingAsync();
        Assert.Equal(group.Id, Assert.Single(home.Groups).Id);
        navigator.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = home.OpenGroupCommand.ExecuteAsync(home.Groups[0]);
        await home.OpenControlCommand.ExecuteAsync(home.Devices.First());
        Assert.Single(navigator.Routes);
        navigator.Release.SetResult(); await first;
        await home.OpenGroupCommand.ExecuteAsync(home.Groups[0]);
        Assert.Single(navigator.Routes);
        home.OnDisappearing(); await home.OnAppearingAsync();
        await home.OpenGroupCommand.ExecuteAsync(home.Groups[0]);
        Assert.Equal(2, navigator.Routes.Count);
        Assert.Empty(f.Ble.ConnectAttempts);
        home.OnDisappearing();
    }

    [Fact]
    public void GroupsPersistOrderedUniqueMembersAndDeletionPreservesDevicesAndTokens()
    {
        var f = new GroupFixture();
        f.Devices.SetToken("A", [1, 2, 3, 4]);
        var group = f.Groups.Repository.Create("  Studio  ");
        f.Groups.Repository.SetMembers(group.Id, ["B", "A", "b"]);
        var restored = new DeviceGroupRepository(f.Devices).Get(group.Id)!;
        Assert.Equal("Studio", restored.Name);
        Assert.Equal(["B", "A"], restored.Members);
        ((string[])restored.Members)[0] = "C";
        Assert.Equal("B", f.Groups.Repository.Get(group.Id)!.Members[0]);
        f.Groups.Repository.Delete(group.Id);
        Assert.True(f.Devices.Contains("A"));
        Assert.Equal("01020304", f.Devices.GetToken("A"));
    }

    [Fact]
    public void DeviceDeletionPrunesAllGroupsAndPromotesNextReference()
    {
        var f = new GroupFixture();
        var first = f.Create("A", "B"); var second = f.Create("C", "A");
        f.Devices.Remove("A");
        Assert.Equal(["B"], f.Groups.Repository.Get(first.Id)!.Members);
        Assert.Equal(["C"], f.Groups.Repository.Get(second.Id)!.Members);
        Assert.True(f.Devices.Contains("B"));
        f.Groups.RemoveMember(first.Id, "B");
        Assert.Empty(f.Groups.Repository.Get(first.Id)!.Members);
        Assert.True(f.Devices.Contains("B"));
    }

    [Fact]
    public void AdmissionUsesReferenceControlsAndIgnoresReadOnlyAndSecurityFeatures()
    {
        var f = new GroupFixture();
        f.Backend.Set("A", s => s with { Capabilities = Basic | AgpCapabilities.SecureControl | AgpCapabilities.Battery });
        f.Backend.Set("B", s => s with { Capabilities = AgpCapabilities.OnOff | AgpCapabilities.Lightness16 | AgpCapabilities.Hsl });
        var group = f.Create("A", "B");
        Assert.Equal(2, f.Groups.Repository.Get(group.Id)!.Members.Count);
        f.Backend.Set("C", s => s with { Capabilities = AgpCapabilities.OnOff });
        Assert.Throws<InvalidOperationException>(() => f.Groups.AddMember(group.Id, "C"));
        Assert.Throws<InvalidOperationException>(() => f.Groups.MoveMember(group.Id, "B", -1));
        Assert.Equal("A", f.Groups.Repository.Get(group.Id)!.Members[0]);
    }

    [Fact]
    public void UnknownCapabilitiesAndNarrowTemperatureRangeCannotBeAdded()
    {
        var f = new GroupFixture();
        f.Backend.Set("A", s => s with { Capabilities = Basic | AgpCapabilities.Ctl, CtlRange = new(2700, 6500) });
        f.Backend.Set("B", s => s with { Capabilities = Basic | AgpCapabilities.Ctl, CtlRange = new(3000, 5000) });
        f.Backend.Set("C", s => s with { Capabilities = null });
        var group = f.Create("A");
        Assert.Throws<InvalidOperationException>(() => f.Groups.AddMember(group.Id, "B"));
        Assert.Throws<InvalidOperationException>(() => f.Groups.AddMember(group.Id, "C"));
        f.Backend.Set("B", s => s with { CtlRange = new(2000, 8000) });
        f.Groups.AddMember(group.Id, "B");
        Assert.Equal(2, f.Groups.Repository.Get(group.Id)!.Members.Count);
    }

    [Theory]
    [InlineData(AgpCapabilities.Hsl, AgpColorModel.Hsl)]
    [InlineData(AgpCapabilities.Ctl, AgpColorModel.Ctl)]
    public async Task ColorModelsExposeBrightnessWithoutRequiringASeparateLightnessCapability(AgpCapabilities capability, AgpColorModel modelKind)
    {
        var f = new GroupFixture();
        f.Backend.Set("A", snapshot => snapshot with { Capabilities = capability, CtlRange = new(2700, 6500) });
        var group = f.Create("A");
        var model = f.Model(group.Id); await model.OnAppearingAsync();
        Assert.True(model.SupportsBrightness);
        model.ApplyLightingCommand.Execute(null);
        await f.Backend.WaitForExecution("A");
        Assert.Equal(modelKind, Assert.Single(f.Backend.Executions).Command.ColorModel);
        model.OnDisappearing();
    }

    [Fact]
    public async Task DispatchReturnsBeforeConfirmationAndOneFailureDoesNotBlockOtherMembers()
    {
        var f = new GroupFixture(); var group = f.Create("A", "B", "C");
        f.Backend.Work.Limit = 3;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Backend.BeforeExecute = (member, _) => member.Device.Address == "A" ? release.Task : Task.CompletedTask;
        f.Backend.FailAddress = "B";
        var dispatch = f.Groups.Submit(group.Id, new(DeviceGroupCommandKind.Power, IsOn: false));
        Assert.Equal(3, dispatch.TargetCount);
        Assert.False(dispatch.Completion.IsCompleted);
        await f.Backend.WaitForExecution("C");
        await WaitUntil(() => f.Groups.GetLastResult(group.Id, "B")?.Contains("Rejected B") == true);
        release.SetResult();
        await dispatch.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, f.Backend.Executions.Count);
        Assert.Equal(AppResources.Group_DeviceConfirmed, f.Groups.GetLastResult(group.Id, "A"));
    }

    [Fact]
    public async Task TargetsAreCapturedOnceAndOfflineLockedOrLaterConnectedMembersAreSkipped()
    {
        var f = new GroupFixture(); var group = f.Create("A", "B", "C");
        f.Backend.Set("B", s => s with { ConnectionGeneration = null });
        f.Backend.Set("C", s => s with { IsLocked = true });
        var batch = f.Backend.Work.BeginBatch();
        var dispatch = f.Groups.Submit(group.Id, new(DeviceGroupCommandKind.Power));
        Assert.Equal(1, dispatch.TargetCount); Assert.Equal(2, dispatch.SkippedCount);
        f.Backend.Set("B", s => s with { ConnectionGeneration = 2 });
        f.Backend.Set("C", s => s with { IsLocked = false });
        batch.Dispose();
        await dispatch.Completion;
        Assert.Equal("A", Assert.Single(f.Backend.Executions).Address);
    }

    [Fact]
    public async Task RemovedMemberAndReplacedConnectionDoNotExecuteQueuedOperations()
    {
        var f = new GroupFixture(); var group = f.Create("A", "B", "C");
        var batch = f.Backend.Work.BeginBatch();
        var dispatch = f.Groups.Submit(group.Id, new(DeviceGroupCommandKind.Power));
        f.Groups.RemoveMember(group.Id, "B");
        f.Backend.Set("C", s => s with { ConnectionGeneration = 2 });
        batch.Dispose();
        await dispatch.Completion;
        Assert.Equal("A", Assert.Single(f.Backend.Executions).Address);
        Assert.Equal(AppResources.Group_DeviceSkipped, f.Groups.GetLastResult(group.Id, "B"));
        Assert.Equal(AppResources.Group_DeviceSkipped, f.Groups.GetLastResult(group.Id, "C"));
    }

    [Fact]
    public async Task DeletingGroupCancelsUnsentMembershipWithoutDeletingDevices()
    {
        var f = new GroupFixture(); var group = f.Create("A", "B");
        var batch = f.Backend.Work.BeginBatch();
        var dispatch = f.Groups.Submit(group.Id, new(DeviceGroupCommandKind.Power));
        f.Groups.Repository.Delete(group.Id);
        batch.Dispose(); await dispatch.Completion;
        Assert.Empty(f.Backend.Executions);
        Assert.True(f.Devices.Contains("A")); Assert.True(f.Devices.Contains("B"));
    }

    [Fact]
    public async Task RemovingThenReaddingMemberDoesNotReviveItsOldQueuedCommand()
    {
        var f = new GroupFixture(); var group = f.Create("A", "B");
        var batch = f.Backend.Work.BeginBatch();
        var dispatch = f.Groups.Submit(group.Id, new(DeviceGroupCommandKind.Power));
        f.Groups.RemoveMember(group.Id, "B"); f.Groups.AddMember(group.Id, "B");
        batch.Dispose(); await dispatch.Completion;
        Assert.Equal("A", Assert.Single(f.Backend.Executions).Address);
    }

    [Fact]
    public async Task DemoGroupChangesSimulatedMembersWithoutSendingBluetoothCommands()
    {
        await using var f = new PairingFixture();
        var demos = f.Repository.GetAll().Where(d => d.IsDemo).ToArray();
        var real = await RealGroup(f, demos.Select(d => d.Address).ToArray());
        await real.Service.Submit(real.Id, new(DeviceGroupCommandKind.Power, IsOn: false)).Completion;
        Assert.All(real.Service.GetMembers(real.Service.Repository.Get(real.Id)!), m => Assert.False(m.IsOn));
        Assert.Empty(f.Ble.Requests); Assert.Empty(f.Ble.ConnectAttempts);
    }

    [Fact]
    public async Task PendingSliderSettingsKeepNewestValueWithoutDroppingPowerCommands()
    {
        var f = new GroupFixture(); var group = f.Create("A");
        var batch = f.Backend.Work.BeginBatch();
        var first = f.Groups.Submit(group.Id, new(DeviceGroupCommandKind.Lighting, Brightness: 10));
        var power = f.Groups.Submit(group.Id, new(DeviceGroupCommandKind.Power, IsOn: false));
        var last = f.Groups.Submit(group.Id, new(DeviceGroupCommandKind.Lighting, Brightness: 80));
        batch.Dispose(); await Task.WhenAll(first.Completion, power.Completion, last.Completion);
        Assert.Equal(2, f.Backend.Executions.Count);
        Assert.Equal(DeviceGroupCommandKind.Power, f.Backend.Executions[0].Command.Kind);
        Assert.Equal((byte)80, f.Backend.Executions[1].Command.Brightness);
    }

    [Fact]
    public async Task OverlappingGroupsUseTheSameDeviceQueueAndKeepSubmissionOrder()
    {
        var f = new GroupFixture(); var first = f.Create("A", "B"); var second = f.Create("A", "C");
        var batch = f.Backend.Work.BeginBatch();
        var on = f.Groups.Submit(first.Id, new(DeviceGroupCommandKind.Power, IsOn: true));
        var off = f.Groups.Submit(second.Id, new(DeviceGroupCommandKind.Power, IsOn: false));
        batch.Dispose(); await Task.WhenAll(on.Completion, off.Completion);
        Assert.Equal([true, false], f.Backend.Executions.Where(e => e.Address == "A").Select(e => e.Command.IsOn));
    }

    [Fact]
    public void NoTargetsInvalidParametersAndUnsupportedCommandsAreNotReportedAsSubmitted()
    {
        var f = new GroupFixture(); var group = f.Create("A");
        f.Backend.Set("A", s => s with { ConnectionGeneration = null });
        Assert.Throws<InvalidOperationException>(() => f.Groups.Submit(group.Id, new(DeviceGroupCommandKind.Power)));
        Assert.Throws<ArgumentException>(() => f.Groups.Submit(group.Id, new(DeviceGroupCommandKind.Lighting, Brightness: 101)));
        Assert.Throws<InvalidOperationException>(() => f.Groups.Submit(group.Id, new(DeviceGroupCommandKind.Effect)));
        Assert.Empty(f.Backend.Executions);
    }

    [Fact]
    public async Task GroupPageStartsFromReferenceThenIgnoresDeviceNotificationsIncludingFirstDevice()
    {
        var f = new GroupFixture(); var group = f.Create("A", "B");
        f.Backend.Set("A", s => s with { IsOn = false, Brightness = 25 });
        var model = f.Model(group.Id);
        await model.OnAppearingAsync();
        Assert.False(model.IsOn); Assert.Equal(25, model.Brightness);
        model.TurnOnCommand.Execute(null);
        Assert.True(model.IsOn);
        f.Backend.Set("A", s => s with { IsOn = false, Brightness = 70 });
        f.Backend.Notify();
        Assert.True(model.IsOn); Assert.Equal(25, model.Brightness);
        Assert.Contains("2", model.OperationText);
        model.OnDisappearing();
        var reopened = f.Model(group.Id); await reopened.OnAppearingAsync();
        Assert.False(reopened.IsOn); Assert.Equal(70, reopened.Brightness);
        reopened.OnDisappearing();
    }

    [Fact]
    public async Task OfflineReferenceDoesNotDisableConnectedMemberAndUnknownPowerIsNotAssumedOff()
    {
        var f = new GroupFixture(); var group = f.Create("A", "B");
        f.Backend.Set("A", s => s with { ConnectionGeneration = null, IsOn = null, Brightness = null });
        var model = f.Model(group.Id); await model.OnAppearingAsync();
        Assert.Null(model.IsOn); Assert.True(model.CanControl);
        model.TurnOffCommand.Execute(null); Assert.False(model.IsOn);
        await f.Backend.WaitForExecution("B");
        Assert.DoesNotContain(f.Backend.Executions, e => e.Address == "A");
        model.OnDisappearing();
    }

    [Fact]
    public async Task ReorderingReferenceReinitializesPageAndMemberNotificationsOnlyUpdateMemberRows()
    {
        var f = new GroupFixture(); var group = f.Create("A", "B");
        f.Backend.Set("B", s => s with { IsOn = true, Brightness = 55 });
        var model = f.Model(group.Id); await model.OnAppearingAsync();
        var members = new DeviceGroupMembersPageViewModel(f.Groups, new ImmediateDispatcher(), new TestAppNavigator(), new GroupInteraction());
        members.ApplyQueryAttributes(new Dictionary<string, object> { ["groupId"] = group.Id });
        await members.OnAppearingAsync();
        f.Groups.MoveMember(group.Id, "B", -1);
        Assert.Equal(55, model.Brightness); Assert.True(model.IsOn);
        Assert.Equal("B", members.Members[0].Address);
        f.Backend.Set("B", s => s with { Brightness = 66 }); f.Backend.Notify();
        Assert.Contains("66%", members.Members[0].Detail);
        Assert.Equal(55, model.Brightness);
        members.OnDisappearing(); model.OnDisappearing();
    }

    [Fact]
    public async Task GroupSettingsAndPresetsPersistWithoutChangingReferenceDeviceSettings()
    {
        var f = new GroupFixture(); var group = f.Create("A");
        var before = f.Devices.GetByAddress("A");
        var model = f.Model(group.Id); await model.OnAppearingAsync();
        model.AutoOffEnabled = true; model.AutoOffSecondsText = "120"; model.TimerSecondsText = "300";
        model.SaveGroupSettingsCommand.Execute(null);
        await model.SavePresetCommand.ExecuteAsync(null);
        var saved = new DeviceGroupRepository(f.Devices).Get(group.Id)!;
        Assert.True(saved.AutoOffEnabled); Assert.Equal(120, saved.AutoOffSeconds); Assert.Equal(300, saved.TimerSeconds);
        Assert.Single(saved.CustomPresets!);
        Assert.Equal(before, f.Devices.GetByAddress("A"));
        Assert.Empty(f.Backend.Executions);
        model.OnDisappearing();
    }

    [Fact]
    public async Task AgpGroupPowerUsesAbsoluteTargetsAndRetainsPerDeviceState()
    {
        await using var f = new PairingFixture(); var other = f.AddDevice();
        foreach (var radio in new[] { f.Ble, other })
        {
            radio.Capabilities = Basic | AgpCapabilities.SecureControl;
            f.Repository.SetToken(radio.Address, [1, 2, 3, 4]);
            await f.Service.EnsureAuthorizedAsync(radio.Address);
        }
        f.Ble.Requests.Clear(); other.Requests.Clear();
        var group = await RealGroup(f, [f.Address, other.Address]);
        var dispatch = group.Service.Submit(group.Id, new(DeviceGroupCommandKind.Power, IsOn: false));
        await dispatch.Completion;
        Assert.Equal(2, dispatch.TargetCount);
        foreach (var radio in new[] { f.Ble, other })
        {
            var request = Assert.Single(radio.RequestMessages, message => message.Opcode == AgpOpcode.OnOffSet);
            Assert.Equal((byte)0, request.Payload.Span[0]);
            Assert.False(f.Service.GetCachedDeviceStatus(radio.Address)!.LightState!.IsOn);
            Assert.Single(radio.ConnectAttempts);
        }
    }

    [Fact]
    public async Task AgpGroupRejectsExpiredConnectionAndRemovedMembershipWithoutReconnecting()
    {
        await using var f = new PairingFixture();
        f.Ble.Capabilities = Basic | AgpCapabilities.SecureControl;
        f.Repository.SetToken(f.Address, [1, 2, 3, 4]);
        await f.Service.EnsureAuthorizedAsync(f.Address);
        var old = f.Service.GetGroupConnectionGeneration(f.Address)!.Value;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.SetConnectedGroupCommandAsync(f.Address, old,
            new(DeviceGroupCommandKind.Power), () => false));
        f.Ble.RaiseUnexpectedDisconnect();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.SetConnectedGroupCommandAsync(f.Address, old,
            new(DeviceGroupCommandKind.Power), () => true));
        Assert.Single(f.Ble.ConnectAttempts);
        Assert.DoesNotContain(AgpOpcode.OnOffSet, f.Ble.Requests);
        await f.Service.EnsureAuthorizedAsync(f.Address);
        Assert.NotEqual(old, f.Service.GetGroupConnectionGeneration(f.Address));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.SetConnectedGroupCommandAsync(f.Address, old,
            new(DeviceGroupCommandKind.Power), () => true));
        Assert.Equal(2, f.Ble.ConnectAttempts.Count);
        Assert.DoesNotContain(AgpOpcode.OnOffSet, f.Ble.Requests);
    }

    [Fact]
    public async Task AgpGroupEffectKeepsBatchOrderAndAutoOffParameters()
    {
        await using var f = new PairingFixture();
        f.Ble.Capabilities = Basic | AgpCapabilities.Effects | AgpCapabilities.RelativeTimer | AgpCapabilities.SecureControl;
        f.Repository.SetToken(f.Address, [1, 2, 3, 4]); await f.Service.EnsureAuthorizedAsync(f.Address);
        f.Ble.Requests.Clear();
        var generation = f.Service.GetGroupConnectionGeneration(f.Address)!.Value;
        await f.Service.SetConnectedGroupCommandAsync(f.Address, generation,
            new(DeviceGroupCommandKind.Effect, Effect: AgpLightMode.Blink, PeriodMilliseconds: 1000, Seconds: 120), () => true);
        Assert.Equal([AgpOpcode.EffectSet, AgpOpcode.OnOffSet, AgpOpcode.DelayOffSet], f.Ble.Requests);
        var timer = Assert.Single(f.Ble.RequestMessages, m => m.Opcode == AgpOpcode.DelayOffSet);
        Assert.Equal("78000000", Convert.ToHexString(timer.Payload.Span));
    }

    [Fact]
    public async Task RuntimeGroupDispatchSurvivesPageLeaveAndUpdatesOnlyDeviceOperation()
    {
        await using var f = new PairingFixture();
        f.Ble.Capabilities = Basic | AgpCapabilities.SecureControl;
        f.Repository.SetTokenAndCapabilities(f.Address, [1, 2, 3, 4], f.Ble.Capabilities);
        await f.Service.StartRuntimeAsync(); await f.Service.EnsureRuntimeReadyAsync(f.Address);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Ble.BeforeWriteAsync = message =>
        { if (message.Opcode != AgpOpcode.OnOffSet) return Task.CompletedTask; entered.TrySetResult(); return release.Task; };
        var group = await RealGroup(f, [f.Address]);
        var model = new DeviceGroupPageViewModel(group.Service, new ImmediateDispatcher(), new TestAppNavigator(), new GroupInteraction());
        model.ApplyQueryAttributes(new Dictionary<string, object> { ["groupId"] = group.Id });
        await model.OnAppearingAsync(); model.TurnOffCommand.Execute(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(model.IsOn); model.OnDisappearing();
        release.SetResult();
        await WaitUntil(() => f.Service.GetRuntimeSnapshot(f.Address)?.LastOperation?.Stage == DeviceRuntimeOperationStage.Confirmed);
        Assert.False(f.Service.GetCachedDeviceStatus(f.Address)!.LightState!.IsOn);
        Assert.Single(f.Ble.Requests, opcode => opcode == AgpOpcode.OnOffSet);
    }

    private static Task<(DeviceGroupService Service, string Id)> RealGroup(PairingFixture fixture, string[] addresses)
    {
        var mesh = new MeshDeviceControlService(fixture.Connection, new MeshNetworkManager(), new AppDiagnosticService(), repository: fixture.Repository);
        var repository = new DeviceGroupRepository(fixture.Repository);
        var service = new DeviceGroupService(repository, fixture.Repository, new DeviceGroupBackend(fixture.Service, mesh));
        var group = repository.Create("Real group"); foreach (var address in addresses) service.AddMember(group.Id, address);
        return Task.FromResult((service, group.Id));
    }

    private static async Task WaitUntil(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) { timeout.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
    }

    private sealed class GroupFixture
    {
        public DeviceRepository Devices { get; } = new();
        public GroupBackend Backend { get; } = new();
        public DeviceGroupService Groups { get; }
        public GroupFixture()
        {
            Preferences.Default.Clear();
            Backend.Work.Limit = 1;
            foreach (var address in new[] { "A", "B", "C" })
            {
                Devices.TryAdd(address, address);
                Backend.Snapshots[address] = new(Devices.GetByAddress(address)!, Basic, null, 1, IsOn: false, Brightness: 30);
            }
            Groups = new(new DeviceGroupRepository(Devices), Devices, Backend);
        }
        public SavedDeviceGroup Create(params string[] members)
        { var group = Groups.Repository.Create("Group"); foreach (var member in members) Groups.AddMember(group.Id, member); return Groups.Repository.Get(group.Id)!; }
        public DeviceGroupPageViewModel Model(string id)
        {
            var model = new DeviceGroupPageViewModel(Groups, new ImmediateDispatcher(), new TestAppNavigator(), new GroupInteraction());
            model.ApplyQueryAttributes(new Dictionary<string, object> { ["groupId"] = id }); return model;
        }
    }

    private sealed class GroupBackend : IDeviceGroupBackend
    {
        public Dictionary<string, DeviceGroupMemberSnapshot> Snapshots { get; } = new();
        public DeviceWorkScheduler Work { get; } = new();
        public List<(string Address, DeviceGroupCommand Command)> Executions { get; } = [];
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _executed = new();
        public Func<DeviceGroupMemberSnapshot, DeviceGroupCommand, Task>? BeforeExecute { get; set; }
        public string? FailAddress { get; set; }
        public event EventHandler? Changed;
        public DeviceGroupMemberSnapshot Read(SavedDevice device) => Snapshots.TryGetValue(device.Address, out var snapshot)
            ? snapshot with { Device = device } : new(device, null, null, null);
        public void Set(string address, Func<DeviceGroupMemberSnapshot, DeviceGroupMemberSnapshot> update) => Snapshots[address] = update(Snapshots[address]);
        public void Notify() => Changed?.Invoke(this, EventArgs.Empty);
        public Task WaitForExecution(string address) => _executed.GetOrAdd(address, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(TimeSpan.FromSeconds(5));
        public Task ExecuteAsync(DeviceGroupMemberSnapshot member, DeviceGroupCommand command, Func<bool> isStillMember, CancellationToken token) =>
            Work.Enqueue(member.Device.Address, 1, async operationToken =>
            {
                if (!isStillMember() || Snapshots[member.Device.Address].ConnectionGeneration != member.ConnectionGeneration)
                    throw new OperationCanceledException();
                if (BeforeExecute is not null) await BeforeExecute(member, command);
                lock (Executions) Executions.Add((member.Device.Address, command));
                _executed.GetOrAdd(member.Device.Address, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
                if (member.Device.Address == FailAddress) throw new InvalidOperationException("Rejected " + member.Device.Address);
                return true;
            }, token);
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public Task InvokeAsync(Func<Task> action) => action();
        public Task<T> InvokeAsync<T>(Func<T> action) => Task.FromResult(action());
    }
    private sealed class GroupInteraction : IUserInteraction
    {
        public void ShowToast(string message) { }
        public void ShowToastForDuration(string message, TimeSpan duration) { }
        public Task AlertAsync(string title, string message, string cancel) => Task.CompletedTask;
        public Task<bool> ConfirmAsync(string title, string message, string accept, string cancel) => Task.FromResult(true);
        public Task<string?> PromptAsync(string title, string message, string accept, string cancel,
            string placeholder = "", string initialValue = "", int maxLength = -1, bool numeric = false) => Task.FromResult<string?>("Preset");
        public Task<string?> ActionSheetAsync(string title, string cancel, params string[] buttons) => Task.FromResult<string?>(buttons.FirstOrDefault());
        public Task<bool> OpenUriAsync(string uri) => Task.FromResult(true);
    }
    private sealed class GroupNavigator : IAppNavigator
    {
        public List<string> Routes { get; } = [];
        public TaskCompletionSource? Release { get; set; }
        public Task GoToAsync(string route, bool animate = true) { Routes.Add(route); return Release?.Task ?? Task.CompletedTask; }
    }
}
