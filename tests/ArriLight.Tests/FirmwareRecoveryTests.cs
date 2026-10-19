using System.Text;
using ArriOpto.BLE.Ota;
using ArriOpto.BLE.Ota.Protocol;
using ArriLight.ViewModels;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
using Xunit;

namespace ArriLight.Tests;

public sealed class FirmwareRecoveryTests
{
    [Theory]
    [InlineData(FirmwareSourceKind.Local)]
    [InlineData(FirmwareSourceKind.Online)]
    public async Task EverySourceRequiresWarningBeforeAnyUpgradeOperation(FirmwareSourceKind source)
    {
        await using var fixture = new PairingFixture();
        var model = CreateModel(fixture);
        model.SetCandidateForTest(Candidate(source));
        var page = new FirmwareUpdatePage(model);
        page.OnConfirmation = (_, message, _, _) =>
        {
            Assert.Contains(AppResources.Firmware_UpdateConfirmation.Split('\n')[^1].Trim(), message);
            return Task.FromResult(false);
        };
        await model.StartSelectedUpdateCommand.ExecuteAsync(null);
        Assert.Single(page.Alerts);
        Assert.Empty(fixture.Ble.ConnectAttempts);
        Assert.False(model.IsUpgradeLocked);
    }

    [Fact]
    public async Task LoadingLocalFirmwareLeavesNormalAndRecoveryChoicesAvailable()
    {
        await using var fixture = new PairingFixture();
        var model = CreateModel(fixture, new MemoryPicker());
        var page = new FirmwareUpdatePage(model);
        await model.SelectLocalCommand.ExecuteAsync(null);
        Assert.True(model.CanStartLocalUpdate);
        Assert.True(model.CanRecoverUpdate);
        Assert.Empty(page.Alerts);
        Assert.Empty(fixture.Ble.ConnectAttempts);
    }

    [Fact]
    public async Task ConfirmedUpgradeBlocksCancelAndNavigationThenAllowsRecoveryAfterFailure()
    {
        await using var fixture = new PairingFixture();
        fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);
        var file = new BlockingFile();
        var model = CreateModel(fixture);
        model.SetCandidateForTest(Candidate(FirmwareSourceKind.Local, file));
        var page = new FirmwareUpdatePage(model) { OnConfirmation = (_, _, _, _) => Task.FromResult(true) };
        page.AppearForTest();
        try
        {
            var update = model.StartSelectedUpdateCommand.ExecuteAsync(null);
            await file.Opened.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(model.IsUpgradeLocked);
            Assert.False(model.CanCancelOperation);
            Assert.False(model.CancelCommand.CanExecute(null));
            model.CancelCommand.Execute(null);
            Assert.True(page.BackForOtaTest());
            var routeCount = Shell.Current.Routes.Count;
            await Shell.Current.GoToAsync("..blocked-ota");
            Assert.Equal(routeCount, Shell.Current.Routes.Count);
            file.Release.TrySetException(new IOException("simulated transfer failure"));
            await update.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(model.IsUpgradeLocked);
            Assert.True(model.CanRecoverUpdate);
            Assert.Contains(AppResources.Firmware_RecoveryHint, model.StatusText);
            await Shell.Current.GoToAsync("..after-ota");
            Assert.Equal(routeCount + 1, Shell.Current.Routes.Count);
        }
        finally { file.Release.TrySetException(new IOException("test cleanup")); page.DisappearForTest(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryDoesNotRequireOldAppConnectionOrAuthentication(bool runtime)
    {
        await using var fixture = new PairingFixture();
        if (runtime)
        {
            var preparing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Ble.OnConnect = async (_, token) =>
            {
                preparing.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            };
            await fixture.Service.StartRuntimeAsync();
            await preparing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        var attempts = fixture.Ble.ConnectAttempts.Count;
        var entered = false;
        await fixture.Service.RunExclusiveOtaRecoveryAsync(fixture.Address, _ =>
        {
            entered = true;
            return Task.CompletedTask;
        }).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(entered);
        Assert.Equal(attempts, fixture.Ble.ConnectAttempts.Count);
        Assert.Empty(fixture.Ble.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativePayloadLengthAndWriterReachFinalDeviceAcknowledgement(bool rejectCommit)
    {
        var radio = new LoaderRadio { RejectCommit = rejectCommit };
        var transport = new PluginBleOtaTransport
        {
            NormalizeRequestedMtuResult = payload => payload + 3,
            WriteWithoutResponseAsync = (_, _, bytes, token) => radio.SendData(bytes, token)
        };
        await using var connection = await PluginBleConnection.OpenAsync(radio.Adapter, radio.Device,
            transport: transport);
        Assert.Equal(240, connection.Mtu);
        using var file = new MemoryStream(Encoding.ASCII.GetBytes(HexFirmware(4000)));
        var firmware = Assert.IsType<SingleBankFirmwareImage>(await FirmwareLoader.LoadAsync(file, "test.hex16"));
        var session = new SingleBankOtaSession(connection, firmware,
            new OtaOptions { TransferAcknowledgementTimeout = TimeSpan.FromSeconds(1) });
        if (rejectCommit)
        {
            await Assert.ThrowsAsync<OtaException>(() => session.RunOtaModeAsync());
            Assert.False(radio.Rebooted);
        }
        else
        {
            await session.RunOtaModeAsync();
            Assert.True(radio.Rebooted);
        }
        Assert.Equal(237, radio.PacketSizes[0]);
        Assert.Equal(4000, radio.PacketSizes.Sum());
        Assert.Equal(0, radio.PluginDataWrites);
    }

    [Fact]
    public async Task StuckGattWriteHasBoundedFailure()
    {
        var radio = new LoaderRadio { StallCommand = true };
        await using var connection = await PluginBleConnection.OpenAsync(radio.Adapter, radio.Device);
        var exception = await Assert.ThrowsAsync<TimeoutException>(() => connection.WriteCommandAsync(new byte[] { 1, 1, 0 }));
        Assert.Contains("10 seconds", exception.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplicationRecoveryTransfersDirectlyAndNeverReportsUnacknowledgedCommit(bool rejectCommit)
    {
        await using var fixture = new PairingFixture();
        var radio = new LoaderRadio { RejectCommit = rejectCommit };
        radio.Advertisements = [radio.Device];
        var configuration = new FirmwareUpdateConfiguration
        {
            OtaTransport = new PluginBleOtaTransport
            {
                NormalizeRequestedMtuResult = payload => payload + 3,
                WriteWithoutResponseAsync = (_, _, data, token) => radio.SendData(data, token)
            }
        };
        var service = new FirmwareUpdateService(new HttpClient(), new PluginBleOtaClient(radio.Adapter),
            fixture.Connection, fixture.Service, new AppDiagnosticService(), configuration);
        var progress = new CapturedProgress();
        var candidate = Candidate(FirmwareSourceKind.Local, new MemoryFile());
        Task Upgrade() => service.UpgradeAsync(fixture.Address, candidate, progress,
            selectRecoveryDevice: devices => Task.FromResult<IDevice?>(Assert.Single(devices)));
        if (rejectCommit)
        {
            await Assert.ThrowsAsync<OtaException>(Upgrade);
            Assert.DoesNotContain(progress.Values, value => value.Stage == FirmwareUpdateStage.Completed);
        }
        else
        {
            await Upgrade();
            Assert.Contains(progress.Values, value => value.Stage == FirmwareUpdateStage.Completed);
        }
        Assert.Empty(fixture.Ble.ConnectAttempts);
        Assert.Empty(fixture.Ble.Requests);
    }

    [Fact]
    public async Task RecoveryScanCollectsAllLoadersAndDeduplicatesAdvertisements()
    {
        var first = new LoaderRadio();
        var second = new LoaderRadio();
        var client = new PluginBleOtaClient(first.Adapter);
        first.Advertisements = [first.Device, second.Device, first.Device];
        var devices = await client.ScanBootloadersAsync();
        Assert.Equal(2, devices.Count);
        Assert.Contains(first.Device, devices);
        Assert.Contains(second.Device, devices);
    }

    private static FirmwareUpdatePageViewModel CreateModel(PairingFixture fixture, IFirmwareFilePicker? picker = null)
    {
        var diagnostics = new AppDiagnosticService();
        var service = new FirmwareUpdateService(new HttpClient(), new PluginBleOtaClient(fixture.Ble.Adapter),
            fixture.Connection, fixture.Service, diagnostics, new FirmwareUpdateConfiguration());
        var model = new FirmwareUpdatePageViewModel(fixture.Repository, fixture.Connection, service, diagnostics,
            new TestUiDispatcher(), new TestUserInteraction(), picker ?? new TestFirmwareFilePicker(), fixture.Service);
        model.ApplyQueryAttributes(new Dictionary<string, object> { ["address"] = fixture.Address });
        return model;
    }

    private static FirmwareCandidate Candidate(FirmwareSourceKind source, IFirmwareFile? file = null) =>
        new(source, OtaProtocol.SingleBank, "1.2.3", "test.hex16", "", null, null, null, file);

    private sealed class BlockingFile : IFirmwareFile
    {
        public string FileName => "test.hex16";
        public TaskCompletionSource Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<Stream> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<Stream> OpenReadAsync() { Opened.TrySetResult(); return Release.Task; }
    }

    private sealed class MemoryFile : IFirmwareFile
    {
        public string FileName => "test.hex16";
        public Task<Stream> OpenReadAsync() => Task.FromResult<Stream>(
            new MemoryStream(Encoding.ASCII.GetBytes(HexFirmware(4000))));
    }

    private sealed class MemoryPicker : IFirmwareFilePicker
    {
        public Task<IFirmwareFile?> PickAsync(string title) => Task.FromResult<IFirmwareFile?>(new MemoryFile());
    }

    private sealed class CapturedProgress : IProgress<FirmwareUpdateProgress>
    {
        public List<FirmwareUpdateProgress> Values { get; } = [];
        public void Report(FirmwareUpdateProgress value) => Values.Add(value);
    }

    private static string HexFirmware(int length)
    {
        var lines = new List<string> { ":020000041FFFDC" };
        for (var offset = 0; offset < length; offset += 16)
        {
            var record = new byte[20];
            record[0] = 16;
            var address = 0x1838 + offset;
            record[1] = (byte)(address >> 8); record[2] = (byte)address;
            for (var i = 4; i < 20; i++) record[i] = (byte)i;
            lines.Add(":" + Convert.ToHexString(record) + unchecked((byte)-record.Sum(value => value)).ToString("X2"));
        }
        return string.Join('\n', lines) + "\n:00000001FF\n";
    }

    private sealed class LoaderRadio
    {
        private readonly Dictionary<Guid, ICharacteristic> _chars = [];
        private byte[] _notification = [];
        private DeviceState _state = DeviceState.Connected;
        private int _received;
        public bool RejectCommit { get; init; }
        public bool StallCommand { get; init; }
        public bool Rebooted { get; private set; }
        public int PluginDataWrites { get; private set; }
        public List<int> PacketSizes { get; } = [];
        public IReadOnlyList<IDevice> Advertisements { get; set; } = [];
        public IDevice Device { get; }
        public IAdapter Adapter { get; }
        public LoaderRadio()
        {
            var id = Guid.NewGuid();
            var service = InterfaceProxy.Create<IService>((name, args) => name switch
            {
                "GetCharacteristicAsync" => Task.FromResult(_chars.GetValueOrDefault((Guid)args[0]!)!),
                _ => throw new NotSupportedException(name)
            });
            foreach (var characteristicId in new[] { OtaConstants.SingleBank.Command, OtaConstants.SingleBank.Data, OtaConstants.SingleBank.Notify })
            {
                var writeType = CharacteristicWriteType.WithResponse;
                _chars[characteristicId] = InterfaceProxy.Create<ICharacteristic>((name, args) => name switch
                {
                    "get_Id" => characteristicId, "get_CanWrite" or "get_CanUpdate" => true,
                    "get_WriteType" => writeType, "set_WriteType" => SetWriteType((CharacteristicWriteType)args[0]!),
                    "get_Value" => _notification,
                    "StartUpdatesAsync" or "StopUpdatesAsync" => Task.CompletedTask,
                    "WriteAsync" => SendCommand(characteristicId, (byte[])args[0]!),
                    _ => throw new NotSupportedException(name)
                });
                object? SetWriteType(CharacteristicWriteType value) { writeType = value; return null; }
            }
            Device = InterfaceProxy.Create<IDevice>((name, args) => name switch
            {
                "get_Id" => id, "get_Name" => "PPlusOTA", "get_State" => _state,
                "RequestMtuAsync" => Task.FromResult(237),
                "GetServiceAsync" => Task.FromResult((Guid)args[0]! == OtaConstants.SingleBank.Service ? service : null!),
                _ => throw new NotSupportedException(name)
            });
            Adapter = InterfaceProxy.Create<IAdapter>((name, _) => name switch
            {
                "get_IsScanning" => false,
                "DisconnectDeviceAsync" or "StopScanningForDevicesAsync" => Task.CompletedTask,
                "StartScanningForDevicesAsync" => Scan(),
                _ => throw new NotSupportedException(name)
            });
        }
        private Task Scan()
        {
            foreach (var device in Advertisements)
                InterfaceProxy.From(Adapter).Raise("DeviceDiscovered", Adapter, new DeviceEventArgs { Device = device });
            return Task.CompletedTask;
        }
        private Task<int> SendCommand(Guid characteristic, byte[] command)
        {
            if (characteristic == OtaConstants.SingleBank.Data) { PluginDataWrites++; throw new InvalidOperationException("Plugin data writer must be bypassed."); }
            if (StallCommand) return new TaskCompletionSource<int>().Task;
            switch (command[0])
            {
                case 1: Notify([0, 0x81]); break;
                case 2: Notify([0, 0x84]); break;
                case 4:
                    Rebooted = true;
                    _state = DeviceState.Disconnected;
                    throw new Exception("iOS reset-time native write failure");
            }
            return Task.FromResult(0);
        }
        public Task<int> SendData(ReadOnlyMemory<byte> data, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            PacketSizes.Add(data.Length); _received += data.Length;
            if (_received == 4000) Notify(RejectCommit ? [0x05, 0xFF] : [0, 0x83]);
            else if (_received == 16 * 237) Notify([0, 0x87]);
            return Task.FromResult(0);
        }
        private void Notify(byte[] value)
        {
            _notification = value;
            var characteristic = _chars[OtaConstants.SingleBank.Notify];
            InterfaceProxy.From(characteristic).Raise("ValueUpdated", characteristic, new CharacteristicUpdatedEventArgs(characteristic));
        }
    }
}
