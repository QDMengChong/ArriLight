using ArriLight.ViewModels;
using ArriOpto.BLE.Agp;
using Xunit;

namespace ArriLight.Tests;

public sealed class DeviceFactoryResetTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(true, false, false, false, true)]
    [InlineData(false, false, false, false, false)]
    [InlineData(true, true, false, false, false)]
    [InlineData(true, false, true, false, false)]
    [InlineData(true, false, false, true, false)]
    public async Task ResetButtonOnlyAppearsForSupportedRealAgpDevices(
        bool supported, bool demo, bool mesh, bool group, bool expected)
    {
        await using var fixture = CreateFixture();
        var saved = fixture.Repository.GetByAddress(fixture.Address)!;
        fixture.Repository.TryAddOrReplace(saved with
        {
            Capabilities = supported ? fixture.Ble.Capabilities : AgpCapabilities.OnOff,
            IsDemo = demo,
            Protocol = mesh ? BluetoothDeviceProtocol.Mesh : BluetoothDeviceProtocol.Agp
        });
        var model = CreateModel(fixture, new Interaction());
        if (group) model.ApplyQueryAttributes(new Dictionary<string, object> { ["groupId"] = "group" });
        await model.OnAppearingAsync();

        Assert.Equal(expected, model.ShowResetButton);
        Assert.Equal(expected, model.ResetCommand.CanExecute(null));
        Assert.Empty(fixture.Ble.Requests);
        model.OnDisappearing();
    }

    [Fact]
    public async Task CancelledConfirmationDoesNotConnectOrChangeDevice()
    {
        await using var fixture = CreateFixture();
        var original = fixture.Repository.GetByAddress(fixture.Address);
        var interaction = new Interaction { Confirmation = () => Task.FromResult(false) };
        var model = CreateModel(fixture, interaction);
        await model.OnAppearingAsync();

        await model.ResetCommand.ExecuteAsync(null);

        Assert.Equal(original, fixture.Repository.GetByAddress(fixture.Address));
        Assert.Empty(fixture.Ble.ConnectAttempts);
        Assert.Empty(fixture.Ble.Requests);
        Assert.Empty(interaction.Alerts);
        Assert.Single(interaction.Confirmations);
        Assert.True(model.UnbindCommand.CanExecute(null));
        model.OnDisappearing();
    }

    [Fact]
    public async Task AcceptedResetAuthenticatesSendsOnceKeepsRecordAndReportsAcceptance()
    {
        await using var fixture = CreateFixture();
        var original = fixture.Repository.GetByAddress(fixture.Address)!;
        var interaction = new Interaction();
        var model = CreateModel(fixture, interaction);
        await model.OnAppearingAsync();

        await model.ResetCommand.ExecuteAsync(null).WaitAsync(Deadline);

        Assert.Equal([AgpOpcode.AuthToken, AgpOpcode.FactoryReset], fixture.Ble.Requests);
        Assert.Equal(Convert.FromHexString("01020304"), fixture.Ble.RequestMessages[0].Payload.ToArray());
        var request = Assert.Single(fixture.Ble.RequestMessages, message => message.Opcode == AgpOpcode.FactoryReset);
        Assert.Empty(request.Payload.ToArray());
        Assert.NotEqual(0, request.TransactionId);
        var saved = fixture.Repository.GetByAddress(fixture.Address)!;
        Assert.Equal(original.Name, saved.Name);
        Assert.Equal(original.AddedAt, saved.AddedAt);
        Assert.Null(saved.Token);
        Assert.False(fixture.Connection.IsConnected(fixture.Address));
        Assert.Null(fixture.Service.GetCachedDeviceStatus(fixture.Address));
        var confirmation = Assert.Single(interaction.Confirmations);
        Assert.Contains(original.Name, confirmation.Message);
        Assert.Contains("旧绑定将失效", confirmation.Message);
        Assert.Equal(AppResources.Device_ResetAccepted, Assert.Single(interaction.Alerts).Title);
        Assert.False(model.ResetCommand.CanExecute(null));
        Assert.True(model.UnbindCommand.CanExecute(null));
        model.OnDisappearing();
    }

    [Fact]
    public async Task ResetAndDeleteAreDisabledDuringOneInFlightReset()
    {
        await using var fixture = CreateFixture();
        var entered = Signal();
        var release = Signal();
        fixture.Ble.BeforeWriteAsync = request =>
        {
            if (request.Opcode != AgpOpcode.FactoryReset) return Task.CompletedTask;
            entered.TrySetResult();
            return release.Task;
        };
        var interaction = new Interaction();
        var model = CreateModel(fixture, interaction);
        await model.OnAppearingAsync();
        var resetting = model.ResetCommand.ExecuteAsync(null);
        try
        {
            await entered.Task.WaitAsync(Deadline);
            Assert.False(model.ResetCommand.CanExecute(null));
            Assert.False(model.UnbindCommand.CanExecute(null));
            Assert.Equal(AppResources.Device_Resetting, model.ResetButtonText);
            await model.ResetCommand.ExecuteAsync(null);
            await model.UnbindCommand.ExecuteAsync(null);
            Assert.Single(interaction.Confirmations);
            Assert.True(fixture.Repository.Contains(fixture.Address));
        }
        finally { release.TrySetResult(); }
        await resetting.WaitAsync(Deadline);
        Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.FactoryReset);
        model.OnDisappearing();
    }

    [Fact]
    public async Task ConfirmationReturningAfterPageLeavesCannotSendReset()
    {
        await using var fixture = CreateFixture();
        var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var interaction = new Interaction { Confirmation = () => confirmation.Task };
        var model = CreateModel(fixture, interaction);
        await model.OnAppearingAsync();
        var resetting = model.ResetCommand.ExecuteAsync(null);

        model.OnDisappearing();
        confirmation.SetResult(true);
        await resetting.WaitAsync(Deadline);

        Assert.Empty(fixture.Ble.Requests);
        Assert.Equal("01020304", fixture.Repository.GetToken(fixture.Address));
    }

    [Theory]
    [InlineData(AgpResultCode.Busy)]
    [InlineData(AgpResultCode.NotAuthorized)]
    [InlineData(AgpResultCode.InternalError)]
    public async Task RejectedResetKeepsBindingAndDoesNotRetry(AgpResultCode result)
    {
        await using var fixture = CreateFixture();
        fixture.Ble.FactoryResetResult = result;
        var original = fixture.Repository.GetByAddress(fixture.Address);
        var interaction = new Interaction();
        var model = CreateModel(fixture, interaction);
        await model.OnAppearingAsync();

        await model.ResetCommand.ExecuteAsync(null).WaitAsync(Deadline);

        Assert.Equal(original, fixture.Repository.GetByAddress(fixture.Address));
        Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.FactoryReset);
        Assert.Equal(AppResources.Device_ResetFailed, Assert.Single(interaction.Alerts).Title);
        Assert.True(model.ResetCommand.CanExecute(null));
        model.OnDisappearing();
    }

    [Fact]
    public async Task FreshLockedStatePreventsResetAfterConfirmation()
    {
        await using var fixture = CreateFixture();
        fixture.Ble.LightStatePacket[^1] = 1;
        var interaction = new Interaction();
        var model = CreateModel(fixture, interaction);
        await model.OnAppearingAsync();

        await model.ResetCommand.ExecuteAsync(null).WaitAsync(Deadline);

        Assert.DoesNotContain(AgpOpcode.FactoryReset, fixture.Ble.Requests);
        Assert.Equal("01020304", fixture.Repository.GetToken(fixture.Address));
        Assert.Equal(AppResources.Device_ResetLocked, Assert.Single(interaction.Alerts).Message);
        model.OnDisappearing();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransportFailureReportsUnconfirmedAndPreservesCredentials(bool timeout)
    {
        await using var fixture = CreateFixture();
        fixture.Ble.BeforeWriteAsync = request => request.Opcode == AgpOpcode.FactoryReset
            ? Task.FromException(timeout ? (Exception)new TimeoutException("timeout") : new IOException("disconnected"))
            : Task.CompletedTask;
        var interaction = new Interaction();
        var model = CreateModel(fixture, interaction);
        await model.OnAppearingAsync();

        await model.ResetCommand.ExecuteAsync(null).WaitAsync(Deadline);

        Assert.Equal("01020304", fixture.Repository.GetToken(fixture.Address));
        Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.FactoryReset);
        Assert.Equal(AppResources.Device_ResetUnconfirmed, Assert.Single(interaction.Alerts).Title);
        Assert.False(fixture.Connection.IsConnected(fixture.Address));
        model.OnDisappearing();
    }

    [Fact]
    public async Task AcceptedResetSurvivesConnectionCleanupFailure()
    {
        await using var fixture = CreateFixture();
        fixture.Ble.FailDisconnect = true;

        await fixture.Service.FactoryResetAsync(fixture.Address).WaitAsync(Deadline);

        Assert.Null(fixture.Repository.GetToken(fixture.Address));
        Assert.True(fixture.Repository.Contains(fixture.Address));
        Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.FactoryReset);
    }

    [Fact]
    public async Task UnsupportedDeviceAndFailedAuthenticationCannotSendReset()
    {
        await using var fixture = CreateFixture();
        fixture.Repository.SetCapabilities(fixture.Address, AgpCapabilities.SecureControl);
        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Service.FactoryResetAsync(fixture.Address));
        Assert.Empty(fixture.Ble.Requests);
        fixture.Repository.SetCapabilities(fixture.Address, fixture.Ble.Capabilities);
        fixture.Ble.AuthenticationResult = AgpResultCode.NotAuthorized;

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => fixture.Service.FactoryResetAsync(fixture.Address));

        Assert.DoesNotContain(AgpOpcode.FactoryReset, fixture.Ble.Requests);
        Assert.Equal("01020304", fixture.Repository.GetToken(fixture.Address));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeResetIsExclusiveAndDoesNotReconnectTargetAfterAcceptanceOrUncertainty(bool unconfirmed)
    {
        await using var fixture = CreateFixture();
        var peer = fixture.AddDevice();
        peer.Capabilities = AgpCapabilities.OnOff | AgpCapabilities.SecureControl;
        fixture.Repository.SetTokenAndCapabilities(peer.Address, [1, 2, 3, 4], peer.Capabilities);
        await fixture.Service.StartRuntimeAsync();
        await Task.WhenAll(fixture.Service.EnsureRuntimeReadyAsync(fixture.Address),
            fixture.Service.EnsureRuntimeReadyAsync(peer.Address)).WaitAsync(Deadline);
        var entered = Signal();
        var release = Signal();
        fixture.Ble.BeforeWriteAsync = async request =>
        {
            if (request.Opcode != AgpOpcode.FactoryReset) return;
            entered.TrySetResult();
            await release.Task;
            if (unconfirmed) throw new IOException("connection lost");
        };
        var resetting = fixture.Service.FactoryResetAsync(fixture.Address);
        try
        {
            await entered.Task.WaitAsync(Deadline);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ToggleOnOffAsync(fixture.Address));
            await fixture.Service.ToggleOnOffAsync(peer.Address).WaitAsync(Deadline);
        }
        finally { release.TrySetResult(); }
        if (unconfirmed)
            await Assert.ThrowsAsync<AgpFactoryResetUnconfirmedException>(() => resetting.WaitAsync(Deadline));
        else
            await resetting.WaitAsync(Deadline);

        var attempts = fixture.Ble.ConnectAttempts.Count;
        fixture.Service.RequestRuntimePreparation(fixture.Address);
        await Assert.ThrowsAsync<AgpAuthenticationRequiredException>(() =>
            fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline));
        Assert.Equal(attempts, fixture.Ble.ConnectAttempts.Count);
        Assert.Equal(DeviceRuntimeAvailability.Offline, fixture.Service.GetRuntimeSnapshot(fixture.Address)!.Availability);
        Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.FactoryReset);
        Assert.Equal(unconfirmed ? "01020304" : null, fixture.Repository.GetToken(fixture.Address));
        Assert.Equal("01020304", fixture.Repository.GetToken(peer.Address));
        Assert.True(fixture.Connection.IsConnected(peer.Address));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedResetCanBeReboundAndControlledAgain(bool runtime)
    {
        await using var fixture = CreateFixture();
        if (runtime)
        {
            await fixture.Service.StartRuntimeAsync();
            await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
        }
        await fixture.Service.FactoryResetAsync(fixture.Address).WaitAsync(Deadline);

        await fixture.Service.BindAsync(fixture.Address, bindingProgress: null,
            deviceName: "PW01 again").WaitAsync(Deadline);
        if (runtime) await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
        await fixture.Service.ToggleOnOffAsync(fixture.Address).WaitAsync(Deadline);

        Assert.Equal("A1B2C3D4", fixture.Repository.GetToken(fixture.Address));
        Assert.Equal("PW01 again", fixture.Repository.GetByAddress(fixture.Address)!.Name);
        Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.FactoryReset);
        Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.OnOffSet);
    }

    [Fact]
    public async Task CancellationAfterDispatchKeepsOutcomeUnconfirmedAndDoesNotRetry()
    {
        await using var fixture = CreateFixture();
        var entered = Signal();
        using var cancellation = new CancellationTokenSource();
        fixture.Ble.BeforeWriteAsync = request =>
        {
            if (request.Opcode != AgpOpcode.FactoryReset) return Task.CompletedTask;
            entered.TrySetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
        };
        var resetting = fixture.Service.FactoryResetAsync(fixture.Address, cancellation.Token);
        await entered.Task.WaitAsync(Deadline);

        cancellation.Cancel();
        await Assert.ThrowsAsync<AgpFactoryResetUnconfirmedException>(() => resetting.WaitAsync(Deadline));

        Assert.Equal("01020304", fixture.Repository.GetToken(fixture.Address));
        Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.FactoryReset);
    }

    private static PairingFixture CreateFixture()
    {
        var fixture = new PairingFixture();
        fixture.Ble.Capabilities = AgpCapabilities.OnOff | AgpCapabilities.SecureControl | AgpCapabilities.FactoryReset;
        fixture.Repository.SetTokenAndCapabilities(fixture.Address, [1, 2, 3, 4], fixture.Ble.Capabilities);
        return fixture;
    }

    private static DeviceMorePageViewModel CreateModel(PairingFixture fixture, Interaction interaction)
    {
        var model = new DeviceMorePageViewModel(fixture.Repository, fixture.Service,
            new TestAppNavigator(), interaction, new TestUiDispatcher());
        model.ApplyQueryAttributes(new Dictionary<string, object> { ["address"] = fixture.Address });
        return model;
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Interaction : IUserInteraction
    {
        public List<(string Title, string Message)> Alerts { get; } = [];
        public List<(string Title, string Message)> Confirmations { get; } = [];
        public Func<Task<bool>> Confirmation { get; init; } = () => Task.FromResult(true);
        public void ShowToast(string message) { }
        public void ShowToastForDuration(string message, TimeSpan duration) { }
        public Task AlertAsync(string title, string message, string cancel)
        { Alerts.Add((title, message)); return Task.CompletedTask; }
        public Task<bool> ConfirmAsync(string title, string message, string accept, string cancel)
        { Confirmations.Add((title, message)); return Confirmation(); }
        public Task<string?> PromptAsync(string title, string message, string accept, string cancel,
            string placeholder = "", string initialValue = "", int maxLength = -1, bool numeric = false) => Task.FromResult<string?>(null);
        public Task<string?> ActionSheetAsync(string title, string cancel, params string[] buttons) => Task.FromResult<string?>(cancel);
        public Task<bool> OpenUriAsync(string uri) => Task.FromResult(true);
    }
}
