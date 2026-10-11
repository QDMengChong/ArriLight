using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using ArriOpto.BLE.Agp;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
using Plugin.BLE.Abstractions.Exceptions;
using Xunit;

namespace ArriLight.Tests;

public sealed class PairingTests
{
    [Fact]
    public async Task TokenThenImmediateDisconnectCompletesWithoutReadingLightStateOrAuthenticating()
    {
        await using var fixture = new PairingFixture();
        fixture.Ble.DisconnectAfterToken = true;

        await fixture.Service.BindAsync(fixture.Address, fixture.ReportProgress);

        Assert.Equal("A1B2C3D4", fixture.Repository.GetToken(fixture.Address));
        Assert.Equal(fixture.Ble.Capabilities, fixture.Repository.GetCapabilities(fixture.Address));
        Assert.Equal([AgpOpcode.AuthGetToken], fixture.Ble.Requests);
        Assert.Equal([AgpProfile.CapabilitiesCharacteristicId], fixture.Ble.Reads);
        Assert.Equal([AgpProfile.ControlPointCharacteristicId], fixture.Ble.Subscriptions);
        Assert.Equal([AgpBindingStage.Connecting, AgpBindingStage.ReceivingToken], fixture.Stages);
        Assert.Single(fixture.Ble.ConnectAttempts);
        Assert.False(fixture.Connection.Snapshot.IsConnected);
    }

    [Fact]
    public async Task PersistedCapabilitiesAreUsedWithoutReadingCapabilitiesAgain()
    {
        await using var fixture = new PairingFixture();
        fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);
        Assert.True(fixture.Repository.SetCapabilities(
            fixture.Address, fixture.Ble.Capabilities));

        await fixture.Service.GetOnOffStateAsync(fixture.Address);

        Assert.DoesNotContain(
            AgpProfile.CapabilitiesCharacteristicId,
            fixture.Ble.Reads);
    }

    [Fact]
    public async Task BusyConnectionRetriesAfter500MillisecondsAndThenPairs()
    {
        await using var fixture = new PairingFixture();
        fixture.Ble.OnConnect = (attempt, _) => attempt == 1
            ? Task.FromException(new DeviceConnectionException(fixture.Ble.Device.Id, "PW01", "busy"))
            : Task.CompletedTask;

        await fixture.Service.BindAsync(fixture.Address, fixture.ReportProgress);

        Assert.Equal(2, fixture.Ble.ConnectAttempts.Count);
        Assert.True(Stopwatch.GetElapsedTime(fixture.Ble.ConnectAttempts[0], fixture.Ble.ConnectAttempts[1])
            >= TimeSpan.FromMilliseconds(480));
        Assert.Contains(AgpBindingStage.WaitingForConnection, fixture.Stages);
        Assert.Equal(AgpBindingStage.ReceivingToken, fixture.Stages[^1]);
        Assert.Equal("A1B2C3D4", fixture.Repository.GetToken(fixture.Address));
    }

    [Fact]
    public async Task UnansweredNativeConnectionIsCancelledBeforeNextAttempt()
    {
        await using var fixture = new PairingFixture();
        var firstCancelled = false;
        fixture.Ble.OnConnect = async (attempt, token) =>
        {
            if (attempt != 1)
                return;
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { firstCancelled = true; throw; }
        };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await fixture.Service.BindAsync(fixture.Address, fixture.ReportProgress, cancellation.Token);

        Assert.True(firstCancelled);
        Assert.Equal(2, fixture.Ble.ConnectAttempts.Count);
        Assert.Equal(1, fixture.Ble.MaximumConcurrentAttempts);
        Assert.Equal("A1B2C3D4", fixture.Repository.GetToken(fixture.Address));
    }

    [Fact]
    public async Task HealthyNativeConnectionLongerThan500MillisecondsIsNotSelfCancelled()
    {
        await using var fixture = new PairingFixture();
        fixture.Ble.OnConnect = (_, token) => Task.Delay(700, token);

        await fixture.Service.BindAsync(fixture.Address, fixture.ReportProgress);

        Assert.Single(fixture.Ble.ConnectAttempts);
        Assert.DoesNotContain(AgpBindingStage.WaitingForConnection, fixture.Stages);
        Assert.Equal("A1B2C3D4", fixture.Repository.GetToken(fixture.Address));
    }

    [Fact]
    public async Task CancelWhileWaitingStopsRetriesAndDoesNotSaveToken()
    {
        await using var fixture = new PairingFixture();
        fixture.Ble.OnConnect = (_, _) => Task.FromException(
            new DeviceConnectionException(fixture.Ble.Device.Id, "PW01", "busy"));
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.BindAsync(
            fixture.Address,
            (stage, token) =>
            {
                fixture.Stages.Add(stage);
                if (stage == AgpBindingStage.WaitingForConnection)
                    cancellation.Cancel();
                return Task.CompletedTask;
            }, cancellation.Token));

        Assert.Single(fixture.Ble.ConnectAttempts);
        Assert.Null(fixture.Repository.GetToken(fixture.Address));
        Assert.Empty(fixture.Ble.Requests);
    }

    [Fact]
    public async Task PairingDeadlineCancellationStopsPendingNativeConnection()
    {
        await using var fixture = new PairingFixture();
        fixture.Ble.OnConnect = (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Service.BindAsync(fixture.Address, fixture.ReportProgress, cancellation.Token));

        Assert.Single(fixture.Ble.ConnectAttempts);
        Assert.Null(fixture.Repository.GetToken(fixture.Address));
    }

    [Fact]
    public async Task ClosedWindowResponseDoesNotReconnectOrSaveToken()
    {
        await using var fixture = new PairingFixture();
        fixture.Ble.ProvisionPayload = [(byte)AgpResultCode.NotAuthorized];
        fixture.Ble.DisconnectAfterToken = true;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.BindAsync(fixture.Address));

        Assert.Contains("配对窗口已关闭", exception.Message);
        Assert.Single(fixture.Ble.ConnectAttempts);
        Assert.Null(fixture.Repository.GetToken(fixture.Address));
    }

    [Fact]
    public async Task InvalidTokenResponseDoesNotPersistPairing()
    {
        await using var fixture = new PairingFixture();
        fixture.Ble.ProvisionPayload = [0, 0xA1, 0xB2];

        await Assert.ThrowsAsync<AgpProtocolException>(() => fixture.Service.BindAsync(fixture.Address));

        Assert.Single(fixture.Ble.ConnectAttempts);
        Assert.Null(fixture.Repository.GetToken(fixture.Address));
        Assert.False(fixture.Connection.Snapshot.IsConnected);
    }

    [Fact]
    public async Task CleanupFailureDoesNotUndoSavedPairing()
    {
        await using var fixture = new PairingFixture();
        fixture.Ble.FailDisconnect = true;

        await fixture.Service.BindAsync(fixture.Address);

        Assert.Equal("A1B2C3D4", fixture.Repository.GetToken(fixture.Address));
    }

    [Fact]
    public async Task CancellationAfterTokenSavedStillCompletesPairing()
    {
        await using var fixture = new PairingFixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Repository.DevicesChanged += (_, _) => cancellation.Cancel();

        await fixture.Service.BindAsync(fixture.Address, cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal("A1B2C3D4", fixture.Repository.GetToken(fixture.Address));
        Assert.False(fixture.Connection.Snapshot.IsConnected);
    }

    [Fact]
    public async Task MissingLocalDeviceCannotCompletePairing()
    {
        await using var fixture = new PairingFixture();
        fixture.Repository.Remove(fixture.Address);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.BindAsync(fixture.Address));

        Assert.Contains("本地设备记录不存在", exception.Message);
        Assert.Null(fixture.Repository.GetToken(fixture.Address));
        Assert.Equal([AgpOpcode.AuthGetToken], fixture.Ble.Requests);
        Assert.False(fixture.Connection.Snapshot.IsConnected);
    }

    [Fact]
    public async Task ControlAfterPairingReconnectsAuthenticatesReadsStateAndSetsTime()
    {
        await using var fixture = new PairingFixture();
        fixture.Ble.DisconnectAfterToken = true;
        await fixture.Service.BindAsync(fixture.Address);

        var isOn = await fixture.Service.GetOnOffStateAsync(fixture.Address);

        Assert.True(isOn);
        Assert.Equal(2, fixture.Ble.ConnectAttempts.Count);
        Assert.Equal([AgpOpcode.AuthGetToken, AgpOpcode.AuthToken, AgpOpcode.TimeSet], fixture.Ble.Requests);
        Assert.Contains(AgpProfile.LightStateCharacteristicId, fixture.Ble.Reads);
        Assert.Contains(AgpProfile.LightStateCharacteristicId, fixture.Ble.Subscriptions);
        Assert.True(fixture.Connection.Snapshot.IsConnected);
    }

    [Fact]
    public async Task DiscoveryPermissionFailureDoesNotEnterBusyRetryLoop()
    {
        await using var fixture = new PairingFixture();
        fixture.Discovery.Failure = new UnauthorizedAccessException("permission denied");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.Service.BindAsync(fixture.Address, fixture.ReportProgress));

        Assert.Empty(fixture.Ble.ConnectAttempts);
        Assert.DoesNotContain(AgpBindingStage.WaitingForConnection, fixture.Stages);
    }
}

internal sealed class PairingFixture : IAsyncDisposable
{
    public FakeBle Ble { get; } = new();
    public DeviceRepository Repository { get; } = new();
    public BluetoothDiscoveryService Discovery { get; }
    public BluetoothConnectionService Connection { get; }
    public AgpLightControlService Service { get; }
    public string Address => Ble.Device.Id.ToString("D").ToUpperInvariant();
    public List<AgpBindingStage> Stages { get; } = [];

    public PairingFixture(TimeProvider? timeProvider = null)
    {
        Preferences.Default.Clear();
        Assert.True(Repository.TryAdd("PW01", Address));
        var diagnostics = new AppDiagnosticService();
        Discovery = new BluetoothDiscoveryService(Ble.Device);
        Connection = new BluetoothConnectionService(Ble.Adapter, Discovery, diagnostics, timeProvider);
        Service = new AgpLightControlService(Connection, diagnostics, Repository, new DeviceWorkScheduler());
    }

    public Task ReportProgress(AgpBindingStage stage, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Stages.Add(stage);
        return Task.CompletedTask;
    }

    public FakeBle AddDevice()
    {
        var peer = Ble.CreatePeer();
        Discovery.RegisterDevice(peer.Device);
        Repository.TryAdd("Other PW01", peer.Address);
        return peer;
    }

    public ValueTask DisposeAsync() => Service.DisposeAsync();
}

// Deterministic radio boundary: real Plugin.BLE contracts/events with no hardware.
internal sealed class FakeBle
{
    public static readonly Guid DeviceInformationServiceId =
        Guid.Parse("0000180a-0000-1000-8000-00805f9b34fb");
    public static readonly Guid FirmwareRevisionCharacteristicId =
        Guid.Parse("00002a26-0000-1000-8000-00805f9b34fb");
    public static readonly Guid HardwareRevisionCharacteristicId =
        Guid.Parse("00002a27-0000-1000-8000-00805f9b34fb");
    private readonly Dictionary<Guid, FakeBle> _radios;
    private DeviceState _state = DeviceState.Disconnected;
    private readonly Guid _deviceId = Guid.NewGuid();
    private readonly IService _service;
    private readonly IService _deviceInformationService;
    private readonly ICharacteristic _firmwareRevisionCharacteristic;
    private readonly ICharacteristic _hardwareRevisionCharacteristic;
    private readonly Dictionary<Guid, ICharacteristic> _characteristics = [];
    private byte[] _indicatedValue = [];
    private int _concurrentAttempts;
    private uint? _timeSeconds;
    private ushort _timeRevision = 1;
    public IDevice Device { get; }
    public IAdapter Adapter { get; }
    public string Address => Device.Id.ToString("D").ToUpperInvariant();
    public List<long> ConnectAttempts { get; } = [];
    public int MaximumConcurrentAttempts { get; private set; }
    public List<AgpOpcode> Requests { get; } = [];
    public List<AgpMessage> RequestMessages { get; } = [];
    public List<Guid> Reads { get; } = [];
    public List<Guid> Subscriptions { get; } = [];
    public List<Guid> StoppedSubscriptions { get; } = [];
    public int DisconnectAttempts { get; private set; }
    public Func<int, CancellationToken, Task>? OnConnect { get; set; }
    public Func<Task>? OnDisconnect { get; set; }
    public bool DisconnectAfterToken { get; set; }
    public bool FailDisconnect { get; set; }
    public AgpCapabilities Capabilities { get; set; } = AgpCapabilities.SecureControl | AgpCapabilities.RtcTime;
    public AgpResultCode AuthenticationResult { get; set; } = AgpResultCode.Success;
    public AgpResultCode TimerResult { get; set; } = AgpResultCode.Success;
    public AgpResultCode EffectResult { get; set; } = AgpResultCode.Success;
    public AgpResultCode OnOffResult { get; set; } = AgpResultCode.Success;
    public Queue<AgpResultCode> OnOffResults { get; } = [];
    public AgpOpcode? SuppressedResponseOpcode { get; set; }
    public AgpResultCode ModeResult { get; set; } = AgpResultCode.Success;
    public AgpModeConfig LocalMode { get; set; } = new(AgpLocalMode.DelayNone);
    public AgpLightMode CurrentEffectMode { get; set; } = AgpLightMode.Steady;
    public ushort CurrentEffectPeriodMilliseconds { get; set; }
    public string? FirmwareRevision { get; set; }
    public string? HardwareRevision { get; set; }
    public Exception? FirmwareReadFailure { get; set; }
    public int FirmwareReadResult { get; set; }
    public byte ScheduleMaximumSlots { get; set; } = AgpModelCodec.MaximumScheduleSlots;
    public AgpResultCode ScheduleListResult { get; set; } = AgpResultCode.Success;
    public Dictionary<byte, AgpSchedule> Schedules { get; } = [];
    public Dictionary<byte, AgpResultCode> ScheduleReadResults { get; } = [];
    public byte[] LightStatePacket { get; set; } = Convert.FromHexString("01000200020000000B0100000001323232000000");
    public Action? OnLightStateRead { get; set; }
    public Func<Guid, Task>? BeforeReadAsync { get; set; }
    public Func<AgpMessage, Task>? BeforeWriteAsync { get; set; }
    public uint DelayOnSeconds { get; set; }
    public uint DelayOffSeconds { get; set; }
    public bool TimeIsValid { get; set; } = true;
    public AgpResultCode TimeResult { get; set; } = AgpResultCode.Success;
    public bool SuppressToggleNotification { get; set; }
    public uint OnOffStateRevision { get; set; } = 1;
    public Action<AgpMessage>? BeforeControlResponse { get; set; }
    public byte[] ProvisionPayload { get; set; } = [0, 0xA1, 0xB2, 0xC3, 0xD4];

    public FakeBle() : this(null) { }

    private FakeBle(FakeBle? parent)
    {
        _service = InterfaceProxy.Create<IService>((name, args) => name switch
        {
            "get_Id" => AgpProfile.ServiceId,
            "get_Name" => "AGP",
            "get_IsPrimary" => true,
            "GetCharacteristicsAsync" => Task.FromResult<IReadOnlyList<ICharacteristic>>(_characteristics.Values.ToList()),
            "GetCharacteristicAsync" => Task.FromResult(_characteristics[(Guid)args[0]!]),
            _ => throw new NotSupportedException(name)
        });
        AddCharacteristic(AgpProfile.CapabilitiesCharacteristicId, CharacteristicPropertyType.Read);
        AddCharacteristic(AgpProfile.ControlPointCharacteristicId, CharacteristicPropertyType.Write | CharacteristicPropertyType.Indicate);
        AddCharacteristic(AgpProfile.LightStateCharacteristicId, CharacteristicPropertyType.Read | CharacteristicPropertyType.Notify);
        _deviceInformationService = InterfaceProxy.Create<IService>((name, args) => name switch
        {
            "get_Id" => DeviceInformationServiceId,
            "get_Name" => "Device Information",
            "get_IsPrimary" => true,
            "GetCharacteristicsAsync" => Task.FromResult<IReadOnlyList<ICharacteristic>>(
                HasFirmwareRevision && HardwareRevision is not null
                    ? [_firmwareRevisionCharacteristic!, _hardwareRevisionCharacteristic!]
                    : HasFirmwareRevision ? [_firmwareRevisionCharacteristic!] : [_hardwareRevisionCharacteristic!]),
            "GetCharacteristicAsync" => Task.FromResult(
                (Guid)args[0]! == FirmwareRevisionCharacteristicId && HasFirmwareRevision ? _firmwareRevisionCharacteristic :
                (Guid)args[0]! == HardwareRevisionCharacteristicId && HardwareRevision is not null ? _hardwareRevisionCharacteristic : null!),
            _ => throw new NotSupportedException(name)
        });
        _firmwareRevisionCharacteristic = InterfaceProxy.Create<ICharacteristic>((name, args) => name switch
        {
            "get_Id" => FirmwareRevisionCharacteristicId,
            "get_Name" => "Firmware Revision",
            "get_Service" => _deviceInformationService,
            "get_Properties" => CharacteristicPropertyType.Read,
            "ReadAsync" => ReadAsync(FirmwareRevisionCharacteristicId, (CancellationToken)args[0]!),
            _ => throw new NotSupportedException(name)
        });
        _hardwareRevisionCharacteristic = InterfaceProxy.Create<ICharacteristic>((name, args) => name switch
        {
            "get_Id" => HardwareRevisionCharacteristicId,
            "get_Name" => "Hardware Revision",
            "get_Service" => _deviceInformationService,
            "get_Properties" => CharacteristicPropertyType.Read,
            "ReadAsync" => ReadAsync(HardwareRevisionCharacteristicId, (CancellationToken)args[0]!),
            _ => throw new NotSupportedException(name)
        });
        Device = InterfaceProxy.Create<IDevice>((name, args) => name switch
        {
            "get_Id" => _deviceId,
            "get_Name" => "PW01",
            "get_State" => _state,
            "GetServicesAsync" => Task.FromResult<IReadOnlyList<IService>>(
                HasFirmwareRevision || HardwareRevision is not null ? [_service, _deviceInformationService] : [_service]),
            "GetServiceAsync" => Task.FromResult((Guid)args[0]! == DeviceInformationServiceId
                ? HasFirmwareRevision || HardwareRevision is not null ? _deviceInformationService : null!
                : _service),
            _ => throw new NotSupportedException(name)
        });
        _radios = parent?._radios ?? [];
        Adapter = parent?.Adapter ?? InterfaceProxy.Create<IAdapter>((name, args) => name switch
        {
            "ConnectToDeviceAsync" => _radios[((IDevice)args[0]!).Id].ConnectAsync((CancellationToken)args[2]!),
            "DisconnectDeviceAsync" => _radios[((IDevice)args[0]!).Id].DisconnectAsync(),
            _ => throw new NotSupportedException(name)
        });
        _radios[_deviceId] = this;
    }

    public FakeBle CreatePeer() => new(this);

    private bool HasFirmwareRevision => FirmwareRevision is not null || FirmwareReadFailure is not null;

    public void NotifyLightState(byte[] packet)
    {
        _indicatedValue = packet;
        var characteristic = _characteristics[AgpProfile.LightStateCharacteristicId];
        InterfaceProxy.From(characteristic).Raise("ValueUpdated", characteristic,
            new CharacteristicUpdatedEventArgs(characteristic));
    }

    public void IndicateControlResponse(byte[] packet)
    {
        _indicatedValue = packet;
        var characteristic = _characteristics[AgpProfile.ControlPointCharacteristicId];
        InterfaceProxy.From(characteristic).Raise("ValueUpdated", characteristic,
            new CharacteristicUpdatedEventArgs(characteristic));
    }

    private async Task ConnectAsync(CancellationToken token)
    {
        ConnectAttempts.Add(Stopwatch.GetTimestamp());
        MaximumConcurrentAttempts = Math.Max(MaximumConcurrentAttempts, ++_concurrentAttempts);
        try
        {
            if (OnConnect is not null)
                await OnConnect(ConnectAttempts.Count, token);
            token.ThrowIfCancellationRequested();
            _state = DeviceState.Connected;
        }
        finally { _concurrentAttempts--; }
    }

    private async Task DisconnectAsync()
    {
        DisconnectAttempts++;
        if (OnDisconnect is not null)
            await OnDisconnect();
        if (FailDisconnect)
            throw new IOException("cleanup failed");
        _state = DeviceState.Disconnected;
        InterfaceProxy.From(Adapter).Raise("DeviceDisconnected", Adapter, new DeviceEventArgs { Device = Device });
    }

    public void RaiseUnexpectedDisconnect()
    {
        _state = DeviceState.Disconnected;
        InterfaceProxy.From(Adapter).Raise(
            "DeviceConnectionLost",
            Adapter,
            new DeviceErrorEventArgs { Device = Device, ErrorMessage = "link lost" });
    }

    public void SetDisconnectedBeforeEvent() => _state = DeviceState.Disconnected;

    private void AddCharacteristic(Guid id, CharacteristicPropertyType properties)
    {
        _characteristics[id] = InterfaceProxy.Create<ICharacteristic>((name, args) => name switch
        {
            "get_Id" => id,
            "get_Name" => id.ToString(),
            "get_Service" => _service,
            "get_Properties" => properties,
            "get_Value" => _indicatedValue,
            "set_WriteType" => null,
            "ReadAsync" => ReadAsync(id, (CancellationToken)args[0]!),
            "StartUpdatesAsync" => SubscribeAsync(id),
            "StopUpdatesAsync" => StopUpdatesAsync(id),
            "WriteAsync" => WriteAsync((byte[])args[0]!, (CancellationToken)args[1]!),
            _ => throw new NotSupportedException(name)
        });
    }

    private async Task<(byte[], int)> ReadAsync(Guid id, CancellationToken cancellationToken)
    {
        Reads.Add(id);
        if (BeforeReadAsync is not null)
            await BeforeReadAsync(id).WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (id == FirmwareRevisionCharacteristicId)
        {
            if (FirmwareReadFailure is { } failure)
                throw failure;
            return (Encoding.UTF8.GetBytes(FirmwareRevision ?? string.Empty), FirmwareReadResult);
        }
        if (id == HardwareRevisionCharacteristicId)
            return (Encoding.UTF8.GetBytes(HardwareRevision ?? string.Empty), 0);
        if (id == AgpProfile.CapabilitiesCharacteristicId)
        {
            var payload = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(payload, (ulong)Capabilities);
            return (AgpCodec.Encode(AgpMessageType.Status, AgpOpcode.CapabilitiesStatus, 0, payload), 0);
        }
        OnLightStateRead?.Invoke();
        return (LightStatePacket, 0);
    }

    private Task SubscribeAsync(Guid id)
    {
        Subscriptions.Add(id);
        return Task.CompletedTask;
    }

    private Task StopUpdatesAsync(Guid id)
    {
        StoppedSubscriptions.Add(id);
        return Task.CompletedTask;
    }

    private async Task<int> WriteAsync(byte[] packet, CancellationToken cancellationToken)
    {
        var request = AgpCodec.Decode(packet);
        Requests.Add(request.Opcode);
        RequestMessages.Add(request);
        if (BeforeWriteAsync is not null)
            await BeforeWriteAsync(request).WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] payload = request.Opcode switch
        {
            AgpOpcode.AuthGetToken => ProvisionPayload,
            AgpOpcode.AuthToken => [(byte)AuthenticationResult],
            AgpOpcode.OnOffSet => [(byte)(OnOffResults.TryDequeue(out var result) ? result : OnOffResult),
                (byte)OnOffStateRevision, (byte)(OnOffStateRevision >> 8),
                (byte)(OnOffStateRevision >> 16), (byte)(OnOffStateRevision >> 24)],
            AgpOpcode.TimeSet or AgpOpcode.TimeGet => TimeResponse(request),
            AgpOpcode.DelayOnSet or AgpOpcode.DelayOffSet or AgpOpcode.DelayOnGet or AgpOpcode.DelayOffGet => TimerResponse(request),
            AgpOpcode.EffectSet or AgpOpcode.EffectGet => EffectResponse(request),
            AgpOpcode.ModeSet or AgpOpcode.ModeGet => ModeResponse(request),
            AgpOpcode.ScheduleListGet => ScheduleListResponse(),
            AgpOpcode.ScheduleGet => ScheduleResponse(request.Payload.Span[0]),
            AgpOpcode.ScheduleSet => SetScheduleResponse(request),
            _ => throw new NotSupportedException(request.Opcode.ToString())
        };
        BeforeControlResponse?.Invoke(request);
        if (request.Opcode != SuppressedResponseOpcode)
            IndicateControlResponse(AgpCodec.Encode(
                AgpMessageType.Response, request.Opcode, request.TransactionId, payload));
        if (request.Opcode == AgpOpcode.OnOffSet && request.Payload.Span[0] == 2 && payload[0] == 0)
        {
            var state = AgpCodec.Decode(LightStatePacket).Payload.ToArray();
            var isOn = state[4] == 0;
            BinaryPrimitives.WriteUInt32LittleEndian(state, BinaryPrimitives.ReadUInt32LittleEndian(state) + 1);
            state[4] = isOn ? (byte)1 : (byte)0;
            state[5] = state[6] = isOn ? state[7] : (byte)0;
            state[9] = (byte)CurrentEffectMode;
            LightStatePacket = AgpCodec.Encode(AgpMessageType.Status, AgpOpcode.LightStateStatus, 0, state);
            if (!SuppressToggleNotification) NotifyLightState(LightStatePacket);
        }
        if (DisconnectAfterToken && request.Opcode == AgpOpcode.AuthGetToken)
        {
            _state = DeviceState.Disconnected;
            InterfaceProxy.From(Adapter).Raise("DeviceConnectionLost", Adapter, new DeviceErrorEventArgs { Device = Device });
        }
        return 0;
    }

    private byte[] ModeResponse(AgpMessage request)
    {
        if (ModeResult != AgpResultCode.Success)
            return [(byte)ModeResult];
        if (request.Opcode == AgpOpcode.ModeSet)
        {
            var echoed = new byte[request.Payload.Length + 1];
            request.Payload.Span.CopyTo(echoed.AsSpan(1));
            LocalMode = AgpModelCodec.DecodeModeResponse(request with
            {
                MessageType = AgpMessageType.Response,
                Payload = echoed
            }).Config!;
        }
        var settings = AgpModelCodec.EncodeModeSet(LocalMode);
        var response = new byte[settings.Length + 1];
        settings.CopyTo(response, 1);
        return response;
    }

    private byte[] TimerResponse(AgpMessage request)
    {
        var seconds = request.Opcode switch
        {
            AgpOpcode.DelayOnGet => DelayOnSeconds,
            AgpOpcode.DelayOffGet => DelayOffSeconds,
            _ => BinaryPrimitives.ReadUInt32LittleEndian(request.Payload.Span)
        };
        if (TimerResult == AgpResultCode.Success)
        {
            if (request.Opcode == AgpOpcode.DelayOnSet) DelayOnSeconds = seconds;
            if (request.Opcode == AgpOpcode.DelayOffSet) DelayOffSeconds = seconds;
        }
        var payload = new byte[6];
        payload[0] = (byte)TimerResult;
        payload[1] = seconds == 0 ? (byte)0 : (byte)1;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(2), seconds);
        return payload;
    }

    private byte[] TimeResponse(AgpMessage request)
    {
        if (request.Opcode == AgpOpcode.TimeSet && TimeResult == AgpResultCode.Success)
        {
            _timeSeconds = BinaryPrimitives.ReadUInt32LittleEndian(request.Payload.Span);
            TimeIsValid = true;
            _timeRevision++;
        }
        var payload = new byte[8];
        payload[0] = (byte)TimeResult;
        payload[1] = TimeIsValid ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(2), _timeSeconds ?? checked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(6), _timeRevision);
        return payload;
    }

    private byte[] EffectResponse(AgpMessage request)
    {
        var payload = new byte[5];
        payload[0] = (byte)EffectResult;
        if (request.Opcode == AgpOpcode.EffectSet)
        {
            payload[1] = request.Payload.Span[0];
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2),
                BinaryPrimitives.ReadUInt16LittleEndian(request.Payload.Span[1..]));
            if (EffectResult == AgpResultCode.Success)
            {
                CurrentEffectMode = (AgpLightMode)payload[1];
                CurrentEffectPeriodMilliseconds = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2));
            }
        }
        else
        {
            payload[1] = (byte)CurrentEffectMode;
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2), CurrentEffectPeriodMilliseconds);
        }

        return payload;
    }

    private byte[] ScheduleListResponse()
    {
        byte enabledMask = 0;
        foreach (var schedule in Schedules.Values.Where(schedule => schedule.IsEnabled))
            enabledMask |= (byte)(1 << schedule.Slot);
        return [(byte)ScheduleListResult, ScheduleMaximumSlots, enabledMask];
    }

    private byte[] ScheduleResponse(byte slot)
    {
        if (ScheduleReadResults.TryGetValue(slot, out var result) && result != AgpResultCode.Success)
            return [(byte)result];

        if (!Schedules.TryGetValue(slot, out var schedule))
            return [0, slot, 0, 0, 0, 0, 0, 0, 0, 0];

        var encoded = AgpModelCodec.EncodeSchedule(schedule);
        var payload = new byte[encoded.Length + 1];
        encoded.CopyTo(payload, 1);
        return payload;
    }

    private byte[] SetScheduleResponse(AgpMessage request)
    {
        byte[] payload = [0, .. request.Payload.ToArray()];
        var schedule = AgpModelCodec.DecodeScheduleResponse(request with
        {
            MessageType = AgpMessageType.Response,
            Payload = payload
        }).Schedule!;
        Schedules[schedule.Slot] = schedule;
        return payload;
    }
}

public class InterfaceProxy : DispatchProxy
{
    private Func<string, object?[], object?> _call = null!;
    private readonly Dictionary<string, Delegate?> _handlers = [];

    public static T Create<T>(Func<string, object?[], object?> call) where T : class
    {
        var instance = Create<T, InterfaceProxy>();
        From(instance)._call = call;
        return instance;
    }

    public static InterfaceProxy From(object instance) => (InterfaceProxy)instance;
    public void Raise(string name, object sender, EventArgs args) =>
        _handlers.GetValueOrDefault(name)?.DynamicInvoke(sender, args);

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        var name = method!.Name;
        args ??= [];
        if (name.StartsWith("add_") || name.StartsWith("remove_"))
        {
            var eventName = name[(name.IndexOf('_') + 1)..];
            var previous = _handlers.GetValueOrDefault(eventName);
            _handlers[eventName] = name.StartsWith("add_")
                ? Delegate.Combine(previous, (Delegate?)args[0])
                : Delegate.Remove(previous, (Delegate?)args[0]);
            return null;
        }
        return _call(name, args);
    }
}
