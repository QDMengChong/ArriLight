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
        await fixture.Connection.ConnectAsync(fixture.Address);
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
        Assert.Single(fixture.Ble.ConnectAttempts);
        Assert.False(model.IsUpgradeLocked);
    }

    [Fact]
    public async Task LoadingLocalFirmwareRequiresOtaDetectionBeforeRecovery()
    {
        await using var fixture = new PairingFixture();
        var model = CreateModel(fixture, new MemoryPicker());
        var page = new FirmwareUpdatePage(model);
        await model.SelectLocalCommand.ExecuteAsync(null);
        Assert.False(model.CanStartLocalUpdate);
        Assert.True(model.CanCheckRecovery);
        Assert.False(model.IsRecoverySelectionVisible);
        Assert.Empty(page.Alerts);
        Assert.Empty(fixture.Ble.ConnectAttempts);
    }

    [Fact]
    public async Task ConfirmedUpgradeBlocksCancelAndNavigationThenRequiresDetectionAfterFailure()
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
            fixture.Ble.RaiseUnexpectedDisconnect();
            Assert.True(model.CanCheckRecovery);
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

    [Theory]
    [InlineData(FirmwareSourceKind.Local)]
    [InlineData(FirmwareSourceKind.Online)]
    public async Task OnlineDeviceShowsNormalUpgradeAndOfflineDeviceShowsRecovery(FirmwareSourceKind source)
    {
        await using var fixture = new PairingFixture();
        var model = CreateModel(fixture);
        model.SetCandidateForTest(Candidate(source));
        Assert.True(model.CanShowRecoveryControls);
        Assert.False(model.CanShowOnlineUpdateButton);
        Assert.False(model.CanStartLocalUpdate);
        await fixture.Connection.ConnectAsync(fixture.Address);
        await model.OnAppearingAsync();
        try
        {
            Assert.True(model.IsDeviceOnline);
            Assert.False(model.CanShowRecoveryControls);
            Assert.False(model.CheckRecoveryCommand.CanExecute(null));
            Assert.Equal(source == FirmwareSourceKind.Online, model.CanShowOnlineUpdateButton);
            Assert.Equal(source == FirmwareSourceKind.Local, model.CanStartLocalUpdate);
            fixture.Ble.RaiseUnexpectedDisconnect();
            Assert.False(model.IsDeviceOnline);
            Assert.True(model.CanCheckRecovery);
            Assert.False(model.CanShowOnlineUpdateButton);
            Assert.False(model.CanStartLocalUpdate);
        }
        finally { model.OnDisappearing(); }
    }

    [Fact]
    public async Task SelectingDeviceKeepsDialogOpenAndDoesNotSendAnyCommands()
    {
        await using var fixture = new PairingFixture();
        var radio = new LoaderRadio();
        var other = new LoaderRadio();
        radio.Advertisements = [radio.Device, other.Device];
        var model = CreateModel(fixture, radio: radio);
        model.SetCandidateForTest(Candidate(FirmwareSourceKind.Local));
        var detection = await OpenRecoverySelectionAsync(model);
        Assert.Equal(2, model.RecoveryDevices.Count);
        Assert.False(model.CanConfirmRecoverySelection);
        Assert.False(model.ConfirmRecoverySelectionCommand.CanExecute(null));
        model.ConfirmRecoverySelectionCommand.Execute(null);
        Assert.True(model.IsRecoverySelectionVisible);
        model.SelectedRecoveryDevice = model.RecoveryDevices[0];
        Assert.True(model.IsRecoverySelectionVisible);
        Assert.False(detection.IsCompleted);
        Assert.True(model.CanConfirmRecoverySelection);
        Assert.True(model.SelectedRecoveryDevice.IsSelected);
        model.SelectedRecoveryDevice = model.RecoveryDevices[1];
        Assert.False(model.RecoveryDevices[0].IsSelected);
        Assert.True(model.RecoveryDevices[1].IsSelected);
        Assert.Empty(radio.Commands);
        Assert.Empty(other.Commands);
        Assert.Empty(fixture.Ble.ConnectAttempts);
        model.CancelRecoverySelectionCommand.Execute(null);
        await detection.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(model.IsRecoverySelectionVisible);
        Assert.False(model.IsOperationRunning);
        Assert.Empty(radio.Commands);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeOnlineAndReadyBothHideRecovery(bool partial)
    {
        await using var fixture = new PairingFixture();
        var capabilities = ArriOpto.BLE.Agp.AgpCapabilities.OnOff |
            ArriOpto.BLE.Agp.AgpCapabilities.SecureControl | ArriOpto.BLE.Agp.AgpCapabilities.Effects;
        fixture.Ble.Capabilities = capabilities;
        fixture.Repository.SetTokenAndCapabilities(fixture.Address, [1, 2, 3, 4], capabilities);
        if (partial) fixture.Ble.EffectResult = ArriOpto.BLE.Agp.AgpResultCode.InternalError;
        await fixture.Service.StartRuntimeAsync();
        try { await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (InvalidOperationException) when (partial) { }
        Assert.Equal(partial ? DeviceRuntimeAvailability.Online : DeviceRuntimeAvailability.Ready,
            fixture.Service.GetRuntimeSnapshot(fixture.Address)!.Availability);
        var model = CreateModel(fixture);
        foreach (var source in new[] { FirmwareSourceKind.Local, FirmwareSourceKind.Online })
        {
            model.SetCandidateForTest(Candidate(source));
            Assert.True(model.IsDeviceOnline);
            Assert.False(model.CanShowRecoveryControls);
            Assert.False(model.CanCheckRecovery);
            Assert.Equal(source == FirmwareSourceKind.Online, model.CanShowOnlineUpdateButton);
            Assert.Equal(source == FirmwareSourceKind.Local, model.CanStartLocalUpdate);
        }
    }

    [Fact]
    public async Task SameVersionOnlineCandidateStillAllowsRecoveryWhileOffline()
    {
        await using var fixture = new PairingFixture();
        var model = CreateModel(fixture);
        model.SetCandidateForTest(Candidate(FirmwareSourceKind.Online), canUpgrade: false);
        Assert.True(model.CanCheckRecovery);
        Assert.False(model.CanShowOnlineUpdateButton);
    }

    [Fact]
    public async Task ChangingTargetClosesRecoverySelectionWithoutUpgrading()
    {
        await using var fixture = new PairingFixture();
        var radio = new LoaderRadio();
        radio.Advertisements = [radio.Device];
        var other = fixture.AddDevice();
        var model = CreateModel(fixture, radio: radio);
        model.SetCandidateForTest(Candidate(FirmwareSourceKind.Local));
        var detection = await OpenRecoverySelectionAsync(model);
        model.SelectedRecoveryDevice = Assert.Single(model.RecoveryDevices);
        model.ApplyQueryAttributes(new Dictionary<string, object> { ["address"] = other.Address });
        await detection.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(model.IsRecoverySelectionVisible);
        Assert.False(model.IsOperationRunning);
        Assert.Empty(radio.Commands);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task SameNameWithoutOtaModeOrServiceCannotStartRecovery(bool inOta, bool hasService)
    {
        await using var fixture = new PairingFixture();
        var radio = new LoaderRadio { InOta = inOta, HasService = hasService };
        radio.Advertisements = [radio.Device];
        var model = CreateModel(fixture, radio: radio);
        model.SetCandidateForTest(Candidate(FirmwareSourceKind.Local, new MemoryFile()));
        var detection = await OpenRecoverySelectionAsync(model);
        model.SelectedRecoveryDevice = Assert.Single(model.RecoveryDevices);
        model.ConfirmRecoverySelectionCommand.Execute(null);
        await detection.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(model.IsRecoverySelectionVisible);
        Assert.False(model.IsUpgradeLocked);
        Assert.True(model.CanCheckRecovery);
        if (hasService) Assert.Contains(AppResources.Firmware_RecoveryNotInOta, model.StatusText);
        Assert.Empty(radio.Commands);
        Assert.Empty(radio.PacketSizes);
        Assert.Empty(fixture.Ble.ConnectAttempts);
    }

    [Fact]
    public async Task EmptyScanDoesNotShowSelectionDialog()
    {
        await using var fixture = new PairingFixture();
        var radio = new LoaderRadio();
        var model = CreateModel(fixture, radio: radio);
        model.SetCandidateForTest(Candidate(FirmwareSourceKind.Local));
        await model.CheckRecoveryCommand.ExecuteAsync(null);
        Assert.False(model.IsRecoverySelectionVisible);
        Assert.Contains(AppResources.Firmware_RecoveryNotFound, model.StatusText);
        Assert.True(model.CanCheckRecovery);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangingFirmwareOrLeavingPageClosesSelectionWithoutStartingRecovery(bool leavePage)
    {
        await using var fixture = new PairingFixture();
        var radio = new LoaderRadio();
        radio.Advertisements = [radio.Device];
        var model = CreateModel(fixture, radio: radio);
        model.SetCandidateForTest(Candidate(FirmwareSourceKind.Local));
        var detection = await OpenRecoverySelectionAsync(model);
        model.SelectedRecoveryDevice = Assert.Single(model.RecoveryDevices);
        if (leavePage) model.OnDisappearing();
        else model.SetCandidateForTest(Candidate(FirmwareSourceKind.Local));
        await detection.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(model.IsRecoverySelectionVisible);
        Assert.False(model.IsOperationRunning);
        Assert.Empty(radio.Commands);
    }

    [Fact]
    public async Task BackClosesSelectionInsteadOfStartingRecovery()
    {
        await using var fixture = new PairingFixture();
        var radio = new LoaderRadio();
        radio.Advertisements = [radio.Device];
        var model = CreateModel(fixture, radio: radio);
        var page = new FirmwareUpdatePage(model);
        model.SetCandidateForTest(Candidate(FirmwareSourceKind.Local));
        var detection = await OpenRecoverySelectionAsync(model);
        model.SelectedRecoveryDevice = Assert.Single(model.RecoveryDevices);
        Assert.True(page.BackForOtaTest());
        await detection.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(model.IsRecoverySelectionVisible);
        Assert.Empty(radio.Commands);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryRechecksModeAndNeverFallsBackToAnotherDevice(bool targetDisappears)
    {
        await using var fixture = new PairingFixture();
        var radio = new LoaderRadio();
        var other = new LoaderRadio();
        radio.Advertisements = [radio.Device];
        radio.OnDisconnect = () =>
        {
            if (targetDisappears) radio.Advertisements = [other.Device];
            else radio.InOta = false;
        };
        var model = CreateModel(fixture, radio: radio);
        model.SetCandidateForTest(Candidate(FirmwareSourceKind.Local, new MemoryFile()));
        var detection = await OpenRecoverySelectionAsync(model);
        model.SelectedRecoveryDevice = Assert.Single(model.RecoveryDevices);
        model.ConfirmRecoverySelectionCommand.Execute(null);
        await detection.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(model.IsRecoverySelectionVisible);
        Assert.True(model.CanCheckRecovery);
        Assert.Empty(radio.Commands);
        Assert.Empty(other.Commands);
        Assert.Empty(radio.PacketSizes);
        Assert.Empty(fixture.Ble.ConnectAttempts);
        Assert.Contains(targetDisappears ? AppResources.Firmware_RecoveryTargetUnavailable
            : AppResources.Firmware_RecoveryNotInOta, model.StatusText);
    }

    [Fact]
    public async Task PopupRecoverStartsSelectedDeviceDirectlyWithoutSecondConfirmation()
    {
        await using var fixture = new PairingFixture();
        var radio = new LoaderRadio();
        var other = new LoaderRadio();
        radio.Advertisements = [radio.Device, other.Device];
        var model = CreateModel(fixture, radio: radio);
        var page = new FirmwareUpdatePage(model);
        model.SetCandidateForTest(Candidate(FirmwareSourceKind.Local, new MemoryFile()));
        var detection = await OpenRecoverySelectionAsync(model);
        model.SelectedRecoveryDevice = model.RecoveryDevices.Single(item => item.Identifier == radio.Device.Id.ToString());
        Assert.True(model.IsRecoverySelectionVisible);
        model.ConfirmRecoverySelectionCommand.Execute(null);
        await detection.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(radio.Rebooted);
        Assert.Equal(4000, radio.PacketSizes.Sum());
        Assert.Empty(other.Commands);
        Assert.Empty(page.Alerts);
        Assert.False(model.IsRecoverySelectionVisible);
        Assert.False(model.CanShowRecoveryControls);
        Assert.False(model.IsUpgradeLocked);
        Assert.Empty(fixture.Ble.ConnectAttempts);
    }

    private static async Task<Task> OpenRecoverySelectionAsync(FirmwareUpdatePageViewModel model)
    {
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(model.IsRecoverySelectionVisible) && model.IsRecoverySelectionVisible)
                opened.TrySetResult();
        }
        model.PropertyChanged += Changed;
        var operation = model.CheckRecoveryCommand.ExecuteAsync(null);
        try
        {
            await Task.WhenAny(opened.Task, operation).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(model.IsRecoverySelectionVisible, model.StatusText);
            return operation;
        }
        finally { model.PropertyChanged -= Changed; }
    }

    private static FirmwareUpdatePageViewModel CreateModel(PairingFixture fixture, IFirmwareFilePicker? picker = null,
        LoaderRadio? radio = null)
    {
        var diagnostics = new AppDiagnosticService();
        var configuration = new FirmwareUpdateConfiguration
        {
            OtaTransport = radio is null ? null : new PluginBleOtaTransport
            {
                NormalizeRequestedMtuResult = payload => payload + 3,
                WriteWithoutResponseAsync = (_, _, data, token) => radio.SendData(data, token)
            }
        };
        var service = new FirmwareUpdateService(new HttpClient(), new PluginBleOtaClient(radio?.Adapter ?? fixture.Ble.Adapter),
            fixture.Connection, fixture.Service, diagnostics, configuration);
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
        public bool InOta { get; set; } = true;
        public bool HasService { get; init; } = true;
        public int Disconnects { get; private set; }
        public Action? OnDisconnect { get; set; }
        public List<byte[]> Commands { get; } = [];
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
                "GetCharacteristicAsync" => Task.FromResult(
                    (Guid)args[0]! == OtaConstants.SingleBank.Data && !InOta ? null! : _chars.GetValueOrDefault((Guid)args[0]!)!),
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
                "GetServiceAsync" => Task.FromResult(HasService && (Guid)args[0]! == OtaConstants.SingleBank.Service ? service : null!),
                _ => throw new NotSupportedException(name)
            });
            Adapter = InterfaceProxy.Create<IAdapter>((name, _) => name switch
            {
                "get_IsScanning" => false,
                "DisconnectDeviceAsync" => Disconnect(),
                "ConnectToDeviceAsync" => Connect(),
                "StopScanningForDevicesAsync" => Task.CompletedTask,
                "StartScanningForDevicesAsync" => Scan(),
                _ => throw new NotSupportedException(name)
            });
        }
        private Task Disconnect() { _state = DeviceState.Disconnected; Disconnects++; OnDisconnect?.Invoke(); return Task.CompletedTask; }
        private Task Connect() { _state = DeviceState.Connected; return Task.CompletedTask; }
        private Task Scan()
        {
            foreach (var device in Advertisements)
                InterfaceProxy.From(Adapter).Raise("DeviceDiscovered", Adapter, new DeviceEventArgs { Device = device });
            return Task.CompletedTask;
        }
        private Task<int> SendCommand(Guid characteristic, byte[] command)
        {
            Commands.Add(command.ToArray());
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
