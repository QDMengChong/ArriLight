using ArriOpto.BLE.Mesh;
using ArriOpto.BLE.Mesh.Crypto;
using ArriOpto.BLE.Mesh.Models;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
using Xunit;

namespace ArriLight.Tests;

public sealed class MeshRuntimeConnectionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(6);

    [Fact]
    public async Task MeshStartupAndForegroundResumeDoNotUseTheOfflineRetryDeadline()
    {
        var clock = new ManualTimeProvider();
        using var queue = new DeviceReconnectQueue(clock);
        await using var fixture = new MeshFixture(reconnectQueue: queue);
        var started = new[] { Signal(), Signal() };
        var releases = new[] { Signal(), Signal() };
        var tokens = new CancellationToken[2];
        fixture.Radio.OnConnect = async (attempt, token) =>
        {
            tokens[attempt - 1] = token;
            started[attempt - 1].TrySetResult();
            await releases[attempt - 1].Task.WaitAsync(token);
        };
        for (var cycle = 0; cycle < 2; cycle++)
        {
            if (cycle == 0) await fixture.Mesh.StartRuntimeAsync();
            else await fixture.Mesh.ResumeAfterBackgroundAsync();
            await started[cycle].Task.WaitAsync(Deadline);
            clock.Advance(TimeSpan.FromSeconds(2));
            Assert.False(tokens[cycle].IsCancellationRequested);
            releases[cycle].TrySetResult();
            await fixture.Mesh.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
            if (cycle == 0)
            {
                await fixture.Mesh.SuspendForBackgroundAsync();
                clock.Advance(TimeSpan.FromSeconds(2));
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MeshRecoveryUses1200MillisecondsUnlessAManualConnectionJoins(bool manualJoin)
    {
        var clock = new ManualTimeProvider();
        using var queue = new DeviceReconnectQueue(clock);
        await using var fixture = new MeshFixture(reconnectQueue: queue);
        await fixture.Mesh.StartRuntimeAsync();
        await fixture.Mesh.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
        await fixture.Mesh.SetOnOffAsync(fixture.Address, false);
        var started = Signal();
        var canceled = Signal();
        var release = Signal();
        fixture.Radio.OnConnect = async (_, token) =>
        {
            started.TrySetResult();
            try { await release.Task.WaitAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            { canceled.TrySetResult(); throw; }
        };
        fixture.Radio.RaiseUnexpectedDisconnect();
        clock.Advance(TimeSpan.FromSeconds(2));
        await started.Task.WaitAsync(Deadline);
        var manual = manualJoin ? fixture.Mesh.EnsureRuntimeReadyAsync(fixture.Address) : null;
        clock.Advance(TimeSpan.FromMilliseconds(1200));
        if (manual is not null)
        {
            Assert.False(canceled.Task.IsCompleted);
            release.TrySetResult();
            await manual.WaitAsync(Deadline);
        }
        else await canceled.Task.WaitAsync(Deadline);
        Assert.Equal(2, fixture.Radio.ConnectAttempts.Count);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task ApplicationStartupConnectsSavedOfflineMeshWithoutScanningOrUserAction()
    {
        await using var fixture = new MeshFixture();
        fixture.Repository.SetMeshControlProfile(fixture.Address, new(OnOffAddress: 2, HslAddress: 2, CtlAddress: 2));
        var app = new App(fixture.Agp, fixture.Discovery, fixture.Mesh);
        var window = app.CreateWindowForTest();
        window.CreateNativeWindow();
        await app.CurrentStartupTask.WaitAsync(Deadline);
        await fixture.Mesh.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);

        Assert.Single(fixture.Radio.ConnectAttempts);
        Assert.Equal(0, fixture.Discovery.ScanCalls);
        Assert.Equal(0, fixture.Discovery.BackgroundScanCalls);
        Assert.Empty(fixture.Radio.Requests);
        Assert.Equal(DeviceRuntimeAvailability.Ready, fixture.Mesh.GetRuntimeSnapshot(fixture.Address)!.Availability);
        var profile = fixture.Mesh.GetCachedControlProfile(fixture.Address)!;
        Assert.NotNull(profile.LightnessAddress);
        Assert.Null(profile.HslAddress);
        Assert.Null(profile.CtlAddress);
        Assert.Equal((byte)50, fixture.Mesh.GetCachedState(fixture.Address)!.LightnessPercent);
        Assert.DoesNotContain(fixture.Server.Requests, op => op == MeshOpcode.GenericOnOffSet);
    }

    [Fact]
    public async Task ApplicationResumeReconnectsMeshAutomaticallyAndDoesNotReplayCommands()
    {
        await using var fixture = new MeshFixture();
        var app = new App(fixture.Agp, fixture.Discovery, fixture.Mesh);
        var window = app.CreateWindowForTest();
        window.CreateNativeWindow();
        await app.CurrentStartupTask.WaitAsync(Deadline);
        await fixture.Mesh.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
        await fixture.Mesh.SetOnOffAsync(fixture.Address, false);
        window.Stop();
        await app.CurrentStopTask.WaitAsync(Deadline);
        Assert.Equal(DeviceRuntimeAvailability.Offline, fixture.Mesh.GetRuntimeSnapshot(fixture.Address)!.Availability);
        Assert.False(fixture.Connection.IsConnected(fixture.Address));

        window.Resume();
        await app.CurrentResumeTask.WaitAsync(Deadline);
        await fixture.Mesh.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
        Assert.Equal(2, fixture.Radio.ConnectAttempts.Count);
        Assert.False(fixture.Mesh.GetCachedState(fixture.Address)!.IsOn);
        Assert.Single(fixture.Server.Requests, op => op == MeshOpcode.GenericOnOffSet);
    }

    [Fact]
    public async Task UnexpectedMeshDisconnectPreparesAnotherProxySessionWithoutClickingThePage()
    {
        await using var fixture = new MeshFixture();
        await fixture.Mesh.StartRuntimeAsync();
        await fixture.Mesh.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Mesh.RuntimeStateChanged += (_, change) =>
        {
            if (fixture.Radio.ConnectAttempts.Count == 2 && change.Snapshot?.Availability == DeviceRuntimeAvailability.Ready)
                reconnected.TrySetResult();
        };

        fixture.Radio.RaiseUnexpectedDisconnect();
        await reconnected.Task.WaitAsync(Deadline);
        await fixture.Mesh.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
        Assert.Equal(2, fixture.Radio.ConnectAttempts.Count);
        Assert.Empty(fixture.Radio.Requests);
        Assert.DoesNotContain(fixture.Server.Requests, op => op == MeshOpcode.GenericOnOffSet);
    }

    [Fact]
    public async Task LeavingTheControlPageCancelsOnlyItsWaitForSharedMeshPreparation()
    {
        await using var fixture = new MeshFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Radio.OnConnect = async (_, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); };
        using var page = new CancellationTokenSource();
        await fixture.Mesh.StartRuntimeAsync();
        await entered.Task.WaitAsync(Deadline);
        var wait = fixture.Mesh.EnsureRuntimeReadyAsync(fixture.Address, page.Token);
        page.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        release.TrySetResult();

        await fixture.Mesh.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
        Assert.Single(fixture.Radio.ConnectAttempts);
        Assert.True(fixture.Connection.IsConnected(fixture.Address));
    }

    [Fact]
    public async Task BackgroundCancelsAnInFlightMeshConnectionAndResumeMakesAFreshAttempt()
    {
        await using var fixture = new MeshFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = false;
        fixture.Radio.OnConnect = async (attempt, token) =>
        {
            if (attempt != 1) return;
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { canceled = true; throw; }
        };
        var app = new App(fixture.Agp, fixture.Discovery, fixture.Mesh);
        var window = app.CreateWindowForTest();
        window.CreateNativeWindow();
        await app.CurrentStartupTask.WaitAsync(Deadline);
        await entered.Task.WaitAsync(Deadline);
        window.Stop();
        await app.CurrentStopTask.WaitAsync(Deadline);
        Assert.True(canceled);
        Assert.False(fixture.Connection.IsConnected(fixture.Address));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Mesh.SetOnOffAsync(fixture.Address, true));

        window.Resume();
        await app.CurrentResumeTask.WaitAsync(Deadline);
        await fixture.Mesh.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
        Assert.Equal(2, fixture.Radio.ConnectAttempts.Count);
    }

    [Fact]
    public async Task NewNativeWindowRestartsMeshAfterApplicationClose()
    {
        await using var fixture = new MeshFixture();
        var app = new App(fixture.Agp, fixture.Discovery, fixture.Mesh);
        var window = app.CreateWindowForTest();
        window.CreateNativeWindow();
        await app.CurrentStartupTask.WaitAsync(Deadline);
        await fixture.Mesh.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
        window.DestroyNativeWindow();
        await app.CurrentStopTask.WaitAsync(Deadline);
        window.Resume();
        await app.CurrentResumeTask.WaitAsync(Deadline);
        Assert.False(fixture.Connection.IsConnected(fixture.Address));

        var second = app.CreateWindowForTest();
        second.CreateNativeWindow();
        await app.CurrentStartupTask.WaitAsync(Deadline);
        await fixture.Mesh.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
        Assert.Equal(2, fixture.Radio.ConnectAttempts.Count);
    }

    [Fact]
    public async Task TransientOfflineConnectionRecoversThroughTheApplicationQueue()
    {
        await using var fixture = new MeshFixture();
        fixture.Radio.OnConnect = (attempt, _) => attempt == 1
            ? Task.FromException(new IOException("temporarily unavailable")) : Task.CompletedTask;
        await fixture.Mesh.StartRuntimeAsync();
        using var recoveryDeadline = new CancellationTokenSource(Deadline);
        while (fixture.Mesh.GetRuntimeSnapshot(fixture.Address)?.Availability != DeviceRuntimeAvailability.Ready)
            await Task.Delay(10, recoveryDeadline.Token);
        await fixture.Mesh.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
        Assert.Equal(2, fixture.Radio.ConnectAttempts.Count);
        Assert.True(fixture.Connection.IsConnected(fixture.Address));
    }

    [Fact]
    public async Task SharedDeviceBudgetStillLimitsMeshAutomaticPreparation()
    {
        var work = new DeviceWorkScheduler { Limit = 1 };
        await using var fixture = new MeshFixture(work);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = work.Enqueue("other-gatt-device", 1, async token =>
        { started.TrySetResult(); await release.Task.WaitAsync(token); return true; }, CancellationToken.None);
        await started.Task.WaitAsync(Deadline);
        try
        {
            await fixture.Mesh.StartRuntimeAsync();
            var preparing = fixture.Mesh.EnsureRuntimeReadyAsync(fixture.Address);
            Assert.Empty(fixture.Radio.ConnectAttempts);
            Assert.False(preparing.IsCompleted);
            release.TrySetResult();
            await first.WaitAsync(Deadline);
            await preparing.WaitAsync(Deadline);
            Assert.Single(fixture.Radio.ConnectAttempts);
        }
        finally { release.TrySetResult(); }
    }

    private sealed class MeshFixture : IAsyncDisposable
    {
        private readonly PairingFixture fixture;
        public FakeBle Radio => fixture.Ble;
        public string Address => fixture.Address;
        public DeviceRepository Repository => fixture.Repository;
        public BluetoothDiscoveryService Discovery => fixture.Discovery;
        public BluetoothConnectionService Connection => fixture.Connection;
        public AgpLightControlService Agp => fixture.Service;
        public MeshDeviceControlService Mesh { get; }
        public MeshServer Server { get; }
        public MeshFixture(DeviceWorkScheduler? work = null, DeviceReconnectQueue? reconnectQueue = null)
        {
            fixture = new PairingFixture(reconnectQueue: reconnectQueue);
            Repository.SetProtocol(Address, BluetoothDeviceProtocol.Mesh);
            var networks = new MeshNetworkManager();
            networks.EnsureNode(Address, networks.DefaultNetwork);
            networks.UpdateNodeAfterProvision(Address, networks.DefaultNetwork, new byte[16]);
            Server = new MeshServer(networks.RestoreConfigFor(Address));
            Radio.CustomServices = [Server.Service];
            Radio.NegotiatedMtu = 517;
            Mesh = new(Connection, networks, new AppDiagnosticService(), work, Repository);
        }
        public async ValueTask DisposeAsync() { await Mesh.DisposeAsync(); await fixture.DisposeAsync(); }
    }

    // Real Mesh encryption, codecs, Proxy transport and connection service run against a fake radio.
    private sealed class MeshServer
    {
        private readonly MeshNetworkConfig config;
        private uint sequence;
        private byte[] notification = [];
        private readonly ICharacteristic dataOut;
        private ushort lightness = 32768;
        private bool isOn = true;
        public IService Service { get; }
        public List<ushort> Requests { get; } = [];
        public MeshServer(MeshNetworkConfig config)
        {
            this.config = config;
            ICharacteristic? dataIn = null;
            Service = InterfaceProxy.Create<IService>((name, args) => name switch
            {
                "get_Id" => MeshProfile.ProxyServiceId,
                "get_Name" => "Mesh Proxy",
                "get_IsPrimary" => true,
                "GetCharacteristicsAsync" => Task.FromResult<IReadOnlyList<ICharacteristic>>([dataIn!, dataOut!]),
                "GetCharacteristicAsync" => Task.FromResult((Guid)args[0]! == MeshProfile.ProxyDataInId ? dataIn! : dataOut!),
                _ => throw new NotSupportedException(name)
            });
            dataIn = Characteristic(MeshProfile.ProxyDataInId, CharacteristicPropertyType.WriteWithoutResponse);
            dataOut = Characteristic(MeshProfile.ProxyDataOutId, CharacteristicPropertyType.Notify);
        }
        private ICharacteristic Characteristic(Guid id, CharacteristicPropertyType properties) =>
            InterfaceProxy.Create<ICharacteristic>((name, args) => name switch
            {
                "get_Id" => id,
                "get_Name" => id.ToString(),
                "get_Service" => Service,
                "get_Properties" => properties,
                "get_Value" => notification,
                "set_WriteType" => null,
                "StartUpdatesAsync" or "StopUpdatesAsync" => Task.CompletedTask,
                "WriteAsync" => Write((byte[])args[0]!),
                _ => throw new NotSupportedException(name)
            });
        private Task<int> Write(byte[] pdu)
        {
            if ((pdu[0] & 0x3F) == MeshProxyPdu.MessageTypeProxyConfiguration) return Task.FromResult(0);
            Assert.True(MeshNetworkLayer.TryDecryptNetworkPdu(pdu.AsSpan(1), config.EncryptionKey,
                config.PrivacyKey, config.Nid, config.IvIndex, out var network));
            if (!MeshSegmentedAccessReceiver.IsAccessProxyPdu(pdu, config)) return Task.FromResult(0);
            var appKey = (network.TransportPdu[0] & 0x40) != 0;
            var nonce = appKey ? MeshNonce.CreateApplicationNonce(false, network.SequenceNumber, network.Src, network.Dst, config.IvIndex) :
                MeshNonce.CreateDeviceNonce(false, network.SequenceNumber, network.Src, network.Dst, config.IvIndex);
            var access = MeshCrypto.DecryptCcm(appKey ? config.AppKey : config.DeviceKey, nonce,
                network.TransportPdu.AsSpan(1), micSize: 4);
            var opcode = access[0] < 0x80 ? access[0] : (ushort)((access[0] << 8) | access[1]);
            Requests.Add(opcode);
            byte[] status;
            switch (opcode)
            {
                case MeshOpcode.ConfigCompositionDataGet:
                    status = MeshMessageWriter.Create(MeshOpcode.ConfigCompositionDataStatus).AddU8(0)
                        .AddU16(0x0504).AddU16(0).AddU16(0).AddU16(10).AddU16(0)
                        .AddU16(0).AddU8(2).AddU8(0).AddU16(SigModelIds.GenericOnOffServer).AddU16(SigModelIds.LightLightnessServer).ToArray();
                    break;
                case MeshOpcode.ConfigModelAppBind:
                    status = [.. MeshOpcode.Encode(MeshOpcode.ConfigModelAppStatus), 0, .. access[2..]];
                    break;
                case MeshOpcode.GenericOnOffSet:
                    isOn = access[2] != 0;
                    goto case MeshOpcode.GenericOnOffGet;
                case MeshOpcode.GenericOnOffGet:
                    status = [.. MeshOpcode.Encode(MeshOpcode.GenericOnOffStatus), isOn ? (byte)1 : (byte)0];
                    break;
                case MeshOpcode.LightLightnessGet:
                    status = MeshMessageWriter.Create(MeshOpcode.LightLightnessStatus).AddU16(lightness).ToArray();
                    break;
                default: throw new NotSupportedException($"Mesh opcode 0x{opcode:X4}");
            }
            Reply(status, network.Dst, network.Src, appKey);
            return Task.FromResult(0);
        }
        private void Reply(byte[] access, ushort source, ushort destination, bool appKey)
        {
            var seq = ++sequence;
            byte[] seq0 = [(byte)(seq >> 16), (byte)(seq >> 8), (byte)seq];
            var upper = appKey ? MeshTransportLayer.EncryptUpperTransportPdu(config.AppKey, false, seq0, source, destination, config.IvIndex, access) :
                MeshTransportLayer.EncryptDeviceKeyUpperTransportPdu(config.DeviceKey, false, seq0, source, destination, config.IvIndex, access);
            IReadOnlyList<byte[]> lower = upper.Length <= MeshTransportLayer.MaxUnsegmentedUpperTransportPayload
                ? [MeshTransportLayer.CreateUnsegmentedLowerTransportPdu(appKey, appKey ? config.Aid : (byte)0, upper)] :
                MeshTransportLayer.CreateSegmentedLowerTransportPdus(appKey, appKey ? config.Aid : (byte)0, false, seq0, upper);
            foreach (var part in lower)
            {
                byte[] bytes = [(byte)(seq >> 16), (byte)(seq >> 8), (byte)seq];
                var response = MeshNetworkLayer.BuildNetworkPdu(config.EncryptionKey, config.PrivacyKey, config.Nid,
                    config.IvIndex, 0, config.Ttl, bytes, source, destination, part);
                notification = MeshProxyPdu.Create(MeshProxyPdu.MessageTypeNetwork, response);
                InterfaceProxy.From(dataOut).Raise("ValueUpdated", dataOut, new CharacteristicUpdatedEventArgs(dataOut));
                seq = ++sequence;
            }
        }
    }
}
