using ArriLight.ViewModels;
using ArriOpto.BLE.Agp;
using Xunit;

namespace ArriLight.Tests;

public sealed class RuntimePartialProfileTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task SuccessfulModeIsAvailableWhenUnrelatedEffectsQueryFails()
    {
        await using var fixture = CreateFixture();
        fixture.Ble.EffectResult = AgpResultCode.InternalError;
        await fixture.Service.StartRuntimeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline));
        var model = CreatePage(fixture);
        try
        {
            await model.EnsureConnectedAsync(showError: false).WaitAsync(Deadline);
            Assert.Equal(DeviceRuntimeAvailability.Online, fixture.Service.GetRuntimeSnapshot(fixture.Address)!.Availability);
            Assert.Equal(DeviceRuntimeStepStatus.Succeeded,
                fixture.Service.GetRuntimeSnapshot(fixture.Address)!.Steps[DeviceRuntimeStep.LocalMode].Status);
            Assert.True(model.IsDeviceOnline);
            Assert.True(model.IsLocalModeSupported);
            Assert.True(model.CanSetLocalMode);
            Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.ModeGet);
            Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.AuthToken);
        }
        finally { model.OnDisappearing(); }
    }

    [Fact]
    public async Task LatePartialDataKeepsLightingAndModeDraftsWithoutRepeatingModeQuery()
    {
        await using var fixture = CreateFixture();
        fixture.Ble.EffectResult = AgpResultCode.InternalError;
        fixture.Ble.LocalMode = new AgpModeConfig(AgpLocalMode.OnAutoOff, OffSeconds: 90);
        fixture.Repository.SetTurnOnDelayEnabled(fixture.Address, true);
        fixture.Repository.SetTurnOnDelaySeconds(fixture.Address, 120);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Ble.BeforeWriteAsync = request =>
        {
            if (request.Opcode != AgpOpcode.ModeGet) return Task.CompletedTask;
            entered.TrySetResult();
            return release.Task;
        };
        DeviceControlPageViewModel? model = null;
        try
        {
            await fixture.Service.StartRuntimeAsync();
            await entered.Task.WaitAsync(Deadline);
            model = CreatePage(fixture);
            var initialization = model.EnsureConnectedAsync(showError: false);
            model.UpdateLightingInput(0.2, 0.3, 0.4, 48);
            model.TurnOnDelaySecondsText = "180";
            model.IsAlwaysOnMode = false;
            model.IsAlwaysOnMode = true;
            release.TrySetResult();
            await initialization.WaitAsync(Deadline);
            Assert.True(model.CanSetLocalMode);
            Assert.Equal(48, model.Brightness);
            Assert.Equal(0.2, model.Hue);
            Assert.Equal(0.3, model.Saturation);
            Assert.Equal(0.4, model.Temperature);
            Assert.True(model.IsAlwaysOnMode);
            Assert.Equal("180", model.TurnOnDelaySecondsText);
            Assert.Equal(180, fixture.Repository.GetTurnOnDelaySeconds(fixture.Address));
            Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.ModeGet);
        }
        finally
        {
            release.TrySetResult();
            model?.OnDisappearing();
        }
    }

    private static PairingFixture CreateFixture()
    {
        var fixture = new PairingFixture();
        var capabilities = AgpCapabilities.OnOff | AgpCapabilities.SecureControl |
            AgpCapabilities.Lightness | AgpCapabilities.LocalMode | AgpCapabilities.Effects |
            AgpCapabilities.RelativeTimer;
        fixture.Ble.Capabilities = capabilities;
        fixture.Repository.SetTokenAndCapabilities(fixture.Address, [1, 2, 3, 4], capabilities);
        return fixture;
    }

    private static DeviceControlPageViewModel CreatePage(PairingFixture fixture)
    {
        var model = new DeviceControlPageViewModel(fixture.Repository, fixture.Discovery, fixture.Connection,
            fixture.Service, new AppDiagnosticService(), new TestUiDispatcher(), new TestAppNavigator(), new TestUserInteraction(), new StubMeshDeviceControlService());
        model.ApplyQueryAttributes(new Dictionary<string, object> { ["address"] = fixture.Address });
        model.OnAppearingAsync().GetAwaiter().GetResult();
        return model;
    }
}
