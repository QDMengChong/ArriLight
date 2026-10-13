using System.Reflection;
using ArriLight.ViewModels;
using ArriOpto.BLE.Agp;
using ArriOpto.BLE.Mesh;
using ArriOpto.BLE.Mesh.Crypto;
using ArriOpto.BLE.Mesh.Models;
using Xunit;

namespace ArriLight.Tests;

public sealed class MeshControlTests
{
    [Fact]
    public void CompositionUsesServerModelsAndTheirElementAddresses()
    {
        var composition = new CompositionDataStatus(0, 0, 0, 0, 0, 0,
        [
            new(0, [SigModelIds.GenericOnOffServer, SigModelIds.LightLightnessServer], []),
            new(0, [SigModelIds.LightHslServer], []),
            new(0, [SigModelIds.LightCtlServer, SigModelIds.SceneServer, SigModelIds.TimeServer], [])
        ]);
        var profile = MeshControlProfile.FromComposition(0x20, composition);

        Assert.Equal((ushort)0x20, profile.OnOffAddress);
        Assert.Equal((ushort)0x20, profile.LightnessAddress);
        Assert.Equal((ushort)0x21, profile.HslAddress);
        Assert.Equal((ushort)0x22, profile.CtlAddress);
        Assert.Equal(AgpCapabilities.OnOff | AgpCapabilities.Lightness16 | AgpCapabilities.Hsl | AgpCapabilities.Ctl,
            profile.Capabilities);
        var clients = composition with { Elements = [new(0, [SigModelIds.LightHslClient, SigModelIds.LightCtlClient], [])] };
        Assert.Equal(AgpCapabilities.None, MeshControlProfile.FromComposition(0x20, clients).Capabilities);
    }

    [Fact]
    public void MeshCapabilitiesPersistSeparatelyFromLegacyGattCapabilities()
    {
        Preferences.Default.Clear();
        var repository = new DeviceRepository();
        repository.TryAdd("Mesh", "mesh", BluetoothDeviceProtocol.Mesh);
        var profile = new MeshControlProfile(OnOffAddress: 2, CtlAddress: 4, TemperatureRange: new(2700, 6500));
        repository.SetMeshControlProfile("mesh", profile);

        var reloaded = new DeviceRepository().GetByAddress("mesh")!;
        Assert.Equal(profile, reloaded.MeshProfile);
        Assert.Equal(profile.Capabilities, reloaded.Capabilities);
        Assert.Equal(new AgpCtlRange(2700, 6500), reloaded.MeshProfile!.CtlRange);
    }

    [Theory]
    [InlineData(AgpControlPageKind.Lightness, true, false, false)]
    [InlineData(AgpControlPageKind.Hsl, true, true, false)]
    [InlineData(AgpControlPageKind.Ctl, true, false, true)]
    [InlineData(AgpControlPageKind.HslAndCtl, true, true, true)]
    public async Task CachedMeshCapabilitiesDriveTheSameControlsAsGatt(
        AgpControlPageKind kind, bool brightness, bool color, bool temperature)
    {
        await using var fixture = new PairingFixture();
        fixture.Repository.SetProtocol(fixture.Address, BluetoothDeviceProtocol.Mesh);
        var profile = Profile(kind);
        fixture.Repository.SetMeshControlProfile(fixture.Address, profile);
        await using var mesh = new MeshDeviceControlService(null!, null!, new AppDiagnosticService(), repository: fixture.Repository);
        var model = CreateModel(fixture, mesh);
        await model.OnAppearingAsync();
        try
        {
            mesh.RecordDeviceOffline(fixture.Address);
            Assert.True(model.HasKnownCapabilities);
            Assert.Equal(kind, model.ControlPageKind);
            Assert.Equal(brightness, model.SupportsBrightness);
            Assert.Equal(color, model.SupportsColor);
            Assert.Equal(temperature, model.SupportsTemperature);
            Assert.True(model.SupportsPower);
            Assert.False(model.SupportsEffect);
            Assert.False(model.SupportsTurnOnDelay);
            Assert.False(model.SupportsLocalMode);
            Assert.Equal(profile.Capabilities, model.SupportedCapabilities);
            Assert.Empty(fixture.Ble.ConnectAttempts);
        }
        finally { model.OnDisappearing(); }
    }

    [Fact]
    public async Task LegacyMeshRecordDoesNotExposeUnverifiedGattControls()
    {
        await using var fixture = new PairingFixture();
        fixture.Repository.SetProtocol(fixture.Address, BluetoothDeviceProtocol.Mesh);
        fixture.Repository.SetCapabilities(fixture.Address, AgpCapabilities.Hsl | AgpCapabilities.Ctl |
            AgpCapabilities.Effects | AgpCapabilities.RelativeTimer);
        var model = CreateModel(fixture, new StubMeshDeviceControlService());
        await model.OnAppearingAsync();
        try
        {
            Assert.False(model.HasKnownCapabilities);
            Assert.False(model.SupportsBrightness);
            Assert.False(model.SupportsColor);
            Assert.False(model.SupportsTemperature);
            Assert.False(model.SupportsEffect);
            Assert.False(model.SupportsTurnOnDelay);
        }
        finally { model.OnDisappearing(); }
    }

    [Fact]
    public async Task OnOffOnlyMeshDeviceHasNoLightingOrPresetCapabilities()
    {
        await using var fixture = new PairingFixture();
        fixture.Repository.SetProtocol(fixture.Address, BluetoothDeviceProtocol.Mesh);
        fixture.Repository.SetMeshControlProfile(fixture.Address, new(OnOffAddress: 2));
        var model = CreateModel(fixture, new StubMeshDeviceControlService());
        await model.OnAppearingAsync();
        try
        {
            Assert.True(model.HasKnownCapabilities);
            Assert.True(model.SupportsPower);
            Assert.False(model.SupportsBrightness);
            Assert.False(model.SupportsColor);
            Assert.False(model.SupportsTemperature);
        }
        finally { model.OnDisappearing(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ColdMeshInitializationConfirmsCapabilitiesAndPreservesEditsMadeDuringRead(bool editWhileReading)
    {
        await using var fixture = new PairingFixture();
        fixture.Repository.SetProtocol(fixture.Address, BluetoothDeviceProtocol.Mesh);
        var profile = new MeshControlProfile(HslAddress: 2, CtlAddress: 2, TemperatureRange: new(2700, 6500));
        var mesh = new PreparingMeshControl(profile);
        var model = CreateModel(fixture, mesh);
        var capabilityRetries = 0;
        model.CapabilityViewsRetryRequested += () => capabilityRetries++;
        await model.OnAppearingAsync();
        var preparation = model.EnsureConnectedAsync(showError: false);
        await mesh.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            Assert.True(model.HasKnownCapabilities);
            Assert.Equal(AgpControlPageKind.HslAndCtl, model.ControlPageKind);
            Assert.Equal(1, capabilityRetries);
            Assert.False(model.SupportsPower);
            if (editWhileReading) model.UpdateLightingInput(0.8, 0.4, 0.2, 82, AgpColorModel.Ctl);
            mesh.ReadCompleted.SetResult(new(fixture.Address, 2, true, 25, 16384, 32768, 4600, AgpColorModel.Ctl));
            await preparation;
            Assert.Equal(editWhileReading ? 82 : 25, model.Brightness);
            Assert.Equal(editWhileReading ? 0.2 : 0.5, model.Temperature, 4);
            Assert.Equal(editWhileReading ? 0.8 : 0.25, model.Hue, 4);
            Assert.Empty(fixture.Ble.ConnectAttempts);
            Assert.Empty(fixture.Ble.Requests);
        }
        finally
        {
            mesh.ReadCompleted.TrySetCanceled();
            model.OnDisappearing();
            await preparation;
        }
    }

    [Fact]
    public async Task ManualMeshRefreshAppliesConfirmedStateAfterAnEarlierEdit()
    {
        await using var fixture = new PairingFixture();
        fixture.Repository.SetProtocol(fixture.Address, BluetoothDeviceProtocol.Mesh);
        var profile = new MeshControlProfile(CtlAddress: 2, TemperatureRange: new(2700, 6500));
        fixture.Repository.SetMeshControlProfile(fixture.Address, profile);
        var mesh = new PreparingMeshControl(profile);
        var model = CreateModel(fixture, mesh);
        await model.OnAppearingAsync();
        try
        {
            model.UpdateLightingInput(0, 0, 0.2, 82, AgpColorModel.Ctl);
            mesh.ReadCompleted.SetResult(new(fixture.Address, 2, true, 25, TemperatureKelvin: 4600));
            await model.EnsureConnectedAsync(showError: false, refreshStatus: true);
            Assert.Equal(25, model.Brightness);
            Assert.Equal(0.5, model.Temperature);
            Assert.True(mesh.RefreshRequested);
        }
        finally { model.OnDisappearing(); }
    }

    [Fact]
    public async Task TemperaturePresetKeepsItsModelAfterSaveReloadAndApply()
    {
        await using var fixture = new PairingFixture();
        fixture.Repository.SetProtocol(fixture.Address, BluetoothDeviceProtocol.Mesh);
        fixture.Repository.SetMeshControlProfile(fixture.Address, Profile(AgpControlPageKind.HslAndCtl));
        var mesh = new RecordingMeshControl();
        var model = CreateModel(fixture, mesh, new PresetInteraction());
        await model.OnAppearingAsync();
        try
        {
            model.IsDeviceOnline = true;
            model.UpdateLightingInput(0.5, 0.8, 0.7, 42, AgpColorModel.Ctl);
            await model.SavePresetCommand.ExecuteAsync(null);
        }
        finally { model.OnDisappearing(); }

        var saved = Assert.Single(fixture.Repository.GetCustomPresets(fixture.Address));
        Assert.Equal(AgpColorModel.Ctl, saved.ActiveColorModel);
        Assert.Null(saved.EffectMode);
        var reloaded = CreateModel(fixture, mesh);
        await reloaded.OnAppearingAsync();
        try
        {
            reloaded.IsDeviceOnline = true;
            await reloaded.ApplyPresetCommand.ExecuteAsync(reloaded.Presets[0]);
            Assert.Equal("CTL", Assert.Single(mesh.Writes).Model);
            Assert.Equal((byte)42, mesh.Writes[0].Brightness);
            Assert.Empty(fixture.Ble.Requests);
        }
        finally { reloaded.OnDisappearing(); }
    }

    [Theory]
    [InlineData(AgpControlPageKind.Lightness, "Lightness")]
    [InlineData(AgpControlPageKind.Hsl, "HSL")]
    [InlineData(AgpControlPageKind.Ctl, "CTL")]
    public async Task MeshPresetsRouteToTheSupportedProtocolModel(AgpControlPageKind kind, string expected)
    {
        await using var fixture = new PairingFixture();
        fixture.Repository.SetProtocol(fixture.Address, BluetoothDeviceProtocol.Mesh);
        fixture.Repository.SetMeshControlProfile(fixture.Address, Profile(kind));
        var mesh = new RecordingMeshControl();
        var model = CreateModel(fixture, mesh);
        await model.OnAppearingAsync();
        try
        {
            model.IsDeviceOnline = true;
            await model.ApplyPresetCommand.ExecuteAsync(ControlPreset.Create("preset", 0.25, 0.8, 0.5, 63));
            Assert.Equal(expected, Assert.Single(mesh.Writes).Model);
            Assert.Equal((byte)63, mesh.Writes[0].Brightness);
            Assert.Empty(fixture.Ble.Requests);
        }
        finally { model.OnDisappearing(); }
    }

    [Theory]
    [InlineData(false, "Lightness")]
    [InlineData(true, "CTL")]
    public async Task BrightnessSliderPreservesConfirmedColorAndPendingTemperatureEdits(bool editTemperature, string expected)
    {
        await using var fixture = new PairingFixture();
        fixture.Repository.SetProtocol(fixture.Address, BluetoothDeviceProtocol.Mesh);
        fixture.Repository.SetMeshControlProfile(fixture.Address, Profile(AgpControlPageKind.HslAndCtl));
        var mesh = new RecordingMeshControl();
        var model = CreateModel(fixture, mesh);
        await model.OnAppearingAsync();
        try
        {
            model.IsDeviceOnline = true;
            if (editTemperature) model.UpdateLightingInput(0.25, 0.8, 0.5, 75, AgpColorModel.Ctl);
            model.UpdateLightingInput(model.Hue, model.Saturation, model.Temperature, 63);
            var send = (Task)typeof(DeviceControlPageViewModel)
                .GetField("_lightSettingsTask", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(model)!;
            await send.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(expected, Assert.Single(mesh.Writes).Model);
            Assert.Equal((byte)63, mesh.Writes[0].Brightness);
            Assert.Empty(model.ErrorMessage);
        }
        finally { model.OnDisappearing(); }
    }

    [Fact]
    public async Task HslAndCtlWritesUseTheCorrectElementsAndConfirmedStatus()
    {
        await using var fixture = new PairingFixture();
        var networks = new MeshNetworkManager();
        var node = networks.EnsureNode(fixture.Address, networks.DefaultNetwork);
        var profile = new MeshControlProfile(HslAddress: 3, CtlAddress: 4, TemperatureRange: new(2700, 6500));
        fixture.Repository.SetMeshControlProfile(fixture.Address, profile);
        await using var service = new MeshDeviceControlService(fixture.Connection, networks,
            new AppDiagnosticService(), repository: fixture.Repository);
        var link = new ModelLink(networks.RestoreConfigFor(fixture.Address));
        var client = new MeshClient(link);
        client.Restore(link.Config);

        await InvokeCore(service, "SetHslCoreAsync", client, node, fixture.Address, (byte)50, (ushort)16000, (ushort)32000, CancellationToken.None);
        var hsl = Assert.Single(link.Requests);
        Assert.Equal((ushort)3, hsl.Element);
        Assert.Equal(LightHslModel.Set(MeshLightness.ToLightness(50), 16000, 32000, 0), hsl.Pdu);
        Assert.Equal((ushort)16000, service.GetCachedState(fixture.Address)!.Hue);

        await InvokeCore(service, "SetCtlCoreAsync", client, node, fixture.Address, (byte)70, (ushort)20000, CancellationToken.None);
        await InvokeCore(service, "SetBrightnessCoreAsync", client, node, fixture.Address, (byte)40, CancellationToken.None);
        Assert.Equal((ushort)4, link.Requests[1].Element);
        Assert.Equal(LightCtlModel.Set(MeshLightness.ToLightness(70), 6500, 0, 1), link.Requests[1].Pdu);
        Assert.Equal(LightCtlModel.Set(MeshLightness.ToLightness(40), 6500, 0, 2), link.Requests[2].Pdu);
        var state = service.GetCachedState(fixture.Address)!;
        Assert.Equal(AgpColorModel.Ctl, state.ActiveColorModel);
        Assert.Equal((ushort)6500, state.TemperatureKelvin);
        Assert.Equal((ushort)16000, state.Hue);
        Assert.Equal((byte)40, state.LightnessPercent);
    }

    private static Task<bool> InvokeCore(MeshDeviceControlService service, string method, params object[] args) =>
        (Task<bool>)typeof(MeshDeviceControlService).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(service, args)!;

    private static MeshControlProfile Profile(AgpControlPageKind kind) => new(
        OnOffAddress: 2,
        LightnessAddress: kind == AgpControlPageKind.Lightness ? (ushort)2 : null,
        HslAddress: kind is AgpControlPageKind.Hsl or AgpControlPageKind.HslAndCtl ? (ushort)2 : null,
        CtlAddress: kind is AgpControlPageKind.Ctl or AgpControlPageKind.HslAndCtl ? (ushort)2 : null);

    private static DeviceControlPageViewModel CreateModel(PairingFixture fixture, MeshDeviceControlService mesh,
        IUserInteraction? interaction = null)
    {
        var model = new DeviceControlPageViewModel(fixture.Repository, fixture.Discovery, fixture.Connection,
            fixture.Service, new AppDiagnosticService(), new TestUiDispatcher(), new TestAppNavigator(),
            interaction ?? new TestUserInteraction(), mesh);
        model.ApplyQueryAttributes(new Dictionary<string, object> { ["address"] = fixture.Address });
        return model;
    }

    private sealed class RecordingMeshControl() : MeshDeviceControlService(null!, null!, new AppDiagnosticService())
    {
        public List<(string Model, byte Brightness)> Writes { get; } = [];
        public override Task<bool> SetHslAsync(string address, byte brightnessPercent, double hue, double saturation,
            CancellationToken cancellationToken = default)
        { Writes.Add(("HSL", brightnessPercent)); return Task.FromResult(true); }
        public override Task<bool> SetCtlAsync(string address, byte brightnessPercent, ushort temperatureKelvin,
            CancellationToken cancellationToken = default)
        { Writes.Add(("CTL", brightnessPercent)); return Task.FromResult(true); }
        public override Task<bool> SetLightnessAsync(string address, byte brightnessPercent, CancellationToken cancellationToken = default)
        { Writes.Add(("Lightness", brightnessPercent)); return Task.FromResult(true); }
    }

    private sealed class PreparingMeshControl(MeshControlProfile profile) :
        MeshDeviceControlService(null!, null!, new AppDiagnosticService())
    {
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<MeshDeviceState?> ReadCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool RefreshRequested { get; private set; }
        public override Task<MeshControlProfile?> GetControlProfileAsync(string address, bool refresh = false,
            CancellationToken cancellationToken = default)
        { RefreshRequested = refresh; return Task.FromResult<MeshControlProfile?>(profile); }
        public override Task<MeshDeviceState?> GetLightingStateAsync(string address, CancellationToken cancellationToken = default)
        { ReadStarted.TrySetResult(); return ReadCompleted.Task.WaitAsync(cancellationToken); }
    }

    private sealed class PresetInteraction : IUserInteraction
    {
        public void ShowToast(string message) { }
        public void ShowToastForDuration(string message, TimeSpan duration) { }
        public Task AlertAsync(string title, string message, string cancel) => Task.CompletedTask;
        public Task<bool> ConfirmAsync(string title, string message, string accept, string cancel) => Task.FromResult(false);
        public Task<string?> PromptAsync(string title, string message, string accept, string cancel,
            string placeholder = "", string initialValue = "", int maxLength = -1, bool numeric = false) => Task.FromResult<string?>("温暖");
        public Task<string?> ActionSheetAsync(string title, string cancel, params string[] buttons) => Task.FromResult<string?>(null);
        public Task<bool> OpenUriAsync(string uri) => Task.FromResult(true);
    }

    private sealed class ModelLink : IMeshLink
    {
        private uint _sequence;
        private readonly MeshNetworkConfig config;
        public ModelLink(MeshNetworkConfig config) => this.config = config;
        public MeshNetworkConfig Config => config;
        public List<(ushort Element, byte[] Pdu)> Requests { get; } = [];
        public event EventHandler<byte[]>? Received;
        public Task RequestMtuAsync(int mtu, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task WriteAsync(ReadOnlySpan<byte> pdu, CancellationToken cancellationToken = default)
        {
            Assert.True(MeshNetworkLayer.TryDecryptNetworkPdu(pdu[1..], config.EncryptionKey,
                config.PrivacyKey, config.Nid, config.IvIndex, out var network));
            Assert.Equal(0, network.TransportPdu[0] & 0x80);
            var nonce = MeshNonce.CreateApplicationNonce(false, network.SequenceNumber, network.Src, network.Dst, config.IvIndex);
            var access = MeshCrypto.DecryptCcm(config.AppKey, nonce, network.TransportPdu.AsSpan(1), micSize: 4);
            Requests.Add((network.Dst, access));
            var reader = new MeshMessageReader(access);
            byte[] status;
            if (reader.TryConsumeOpcode(MeshOpcode.LightHslSet))
                status = MeshMessageWriter.Create(MeshOpcode.LightHslStatus).AddU16(reader.ReadU16())
                    .AddU16(reader.ReadU16()).AddU16(reader.ReadU16()).ToArray();
            else
            {
                reader = new MeshMessageReader(access);
                Assert.True(reader.TryConsumeOpcode(MeshOpcode.LightCtlSet));
                status = MeshMessageWriter.Create(MeshOpcode.LightCtlStatus).AddU16(reader.ReadU16())
                    .AddU16(reader.ReadU16()).ToArray();
            }
            var seq = ++_sequence;
            byte[] sequence = [(byte)(seq >> 16), (byte)(seq >> 8), (byte)seq];
            var upper = MeshTransportLayer.EncryptUpperTransportPdu(config.AppKey, false, sequence,
                network.Dst, network.Src, config.IvIndex, status);
            var lower = MeshTransportLayer.CreateUnsegmentedLowerTransportPdu(true, config.Aid, upper);
            var response = MeshNetworkLayer.BuildNetworkPdu(config.EncryptionKey, config.PrivacyKey, config.Nid,
                config.IvIndex, 0, config.Ttl, sequence, network.Dst, network.Src, lower);
            Received?.Invoke(this, MeshProxyPdu.Create(MeshProxyPdu.MessageTypeNetwork, response));
            return Task.CompletedTask;
        }
    }
}
