using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
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
        Assert.Equal([AgpOpcode.AuthGetToken], fixture.Ble.Requests);
        Assert.Equal([AgpProfile.CapabilitiesCharacteristicId], fixture.Ble.Reads);
        Assert.Equal([AgpProfile.ControlPointCharacteristicId], fixture.Ble.Subscriptions);
        Assert.Equal([AgpBindingStage.Connecting, AgpBindingStage.ReceivingToken], fixture.Stages);
        Assert.Single(fixture.Ble.ConnectAttempts);
        Assert.False(fixture.Connection.Snapshot.IsConnected);
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

    public PairingFixture()
    {
        Preferences.Default.Clear();
        Assert.True(Repository.TryAdd("PW01", Address));
        var diagnostics = new AppDiagnosticService();
        Discovery = new BluetoothDiscoveryService(Ble.Device);
        Connection = new BluetoothConnectionService(Ble.Adapter, Discovery, diagnostics);
        Service = new AgpLightControlService(Connection, diagnostics, Repository);
    }

    public Task ReportProgress(AgpBindingStage stage, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Stages.Add(stage);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => Service.DisposeAsync();
}

// Deterministic radio boundary: real Plugin.BLE contracts/events with no hardware.
internal sealed class FakeBle
{
    private DeviceState _state = DeviceState.Disconnected;
    private readonly Guid _deviceId = Guid.NewGuid();
    private readonly IService _service;
    private readonly Dictionary<Guid, ICharacteristic> _characteristics = [];
    private byte[] _indicatedValue = [];
    private int _concurrentAttempts;
    public IDevice Device { get; }
    public IAdapter Adapter { get; }
    public List<long> ConnectAttempts { get; } = [];
    public int MaximumConcurrentAttempts { get; private set; }
    public List<AgpOpcode> Requests { get; } = [];
    public List<AgpMessage> RequestMessages { get; } = [];
    public List<Guid> Reads { get; } = [];
    public List<Guid> Subscriptions { get; } = [];
    public List<Guid> StoppedSubscriptions { get; } = [];
    public int DisconnectAttempts { get; private set; }
    public Func<int, CancellationToken, Task>? OnConnect { get; set; }
    public bool DisconnectAfterToken { get; set; }
    public bool FailDisconnect { get; set; }
    public AgpCapabilities Capabilities { get; set; } = AgpCapabilities.SecureControl | AgpCapabilities.RtcTime;
    public AgpResultCode AuthenticationResult { get; set; } = AgpResultCode.Success;
    public AgpResultCode TimerResult { get; set; } = AgpResultCode.Success;
    public AgpResultCode EffectResult { get; set; } = AgpResultCode.Success;
    public byte[] LightStatePacket { get; set; } = Convert.FromHexString("01000200020000000B0100000001323232000000");
    public Action? OnLightStateRead { get; set; }
    public byte[] ProvisionPayload { get; set; } = [0, 0xA1, 0xB2, 0xC3, 0xD4];

    public FakeBle()
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
        Device = InterfaceProxy.Create<IDevice>((name, args) => name switch
        {
            "get_Id" => _deviceId,
            "get_Name" => "PW01",
            "get_State" => _state,
            "GetServicesAsync" => Task.FromResult<IReadOnlyList<IService>>([_service]),
            "GetServiceAsync" => Task.FromResult(_service),
            _ => throw new NotSupportedException(name)
        });
        Adapter = InterfaceProxy.Create<IAdapter>((name, args) => name switch
        {
            "ConnectToDeviceAsync" => ConnectAsync((CancellationToken)args[2]!),
            "DisconnectDeviceAsync" => DisconnectAsync(),
            _ => throw new NotSupportedException(name)
        });
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

    private Task DisconnectAsync()
    {
        DisconnectAttempts++;
        if (FailDisconnect)
            return Task.FromException(new IOException("cleanup failed"));
        _state = DeviceState.Disconnected;
        InterfaceProxy.From(Adapter).Raise("DeviceDisconnected", Adapter, new DeviceEventArgs { Device = Device });
        return Task.CompletedTask;
    }

    public void RaiseUnexpectedDisconnect()
    {
        _state = DeviceState.Disconnected;
        InterfaceProxy.From(Adapter).Raise(
            "DeviceConnectionLost",
            Adapter,
            new DeviceErrorEventArgs { Device = Device, ErrorMessage = "link lost" });
    }

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
            "ReadAsync" => ReadAsync(id),
            "StartUpdatesAsync" => SubscribeAsync(id),
            "StopUpdatesAsync" => StopUpdatesAsync(id),
            "WriteAsync" => WriteAsync((byte[])args[0]!),
            _ => throw new NotSupportedException(name)
        });
    }

    private Task<(byte[], int)> ReadAsync(Guid id)
    {
        Reads.Add(id);
        if (id == AgpProfile.CapabilitiesCharacteristicId)
        {
            var payload = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(payload, (ulong)Capabilities);
            return Task.FromResult((AgpCodec.Encode(AgpMessageType.Status, AgpOpcode.CapabilitiesStatus, 0, payload), 0));
        }
        OnLightStateRead?.Invoke();
        return Task.FromResult((LightStatePacket, 0));
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

    private Task<int> WriteAsync(byte[] packet)
    {
        var request = AgpCodec.Decode(packet);
        Requests.Add(request.Opcode);
        RequestMessages.Add(request);
        byte[] payload = request.Opcode switch
        {
            AgpOpcode.AuthGetToken => ProvisionPayload,
            AgpOpcode.AuthToken => [(byte)AuthenticationResult],
            AgpOpcode.OnOffSet => [0, 1, 0, 0, 0],
            AgpOpcode.TimeSet => Convert.FromHexString("000100B955690100"),
            AgpOpcode.DelayOnSet or AgpOpcode.DelayOffSet => TimerResponse(request),
            AgpOpcode.EffectSet or AgpOpcode.EffectGet => EffectResponse(request),
            _ => throw new NotSupportedException(request.Opcode.ToString())
        };
        _indicatedValue = AgpCodec.Encode(AgpMessageType.Response, request.Opcode, request.TransactionId, payload);
        var controlPoint = _characteristics[AgpProfile.ControlPointCharacteristicId];
        InterfaceProxy.From(controlPoint).Raise("ValueUpdated", controlPoint, new CharacteristicUpdatedEventArgs(controlPoint));
        if (DisconnectAfterToken && request.Opcode == AgpOpcode.AuthGetToken)
        {
            _state = DeviceState.Disconnected;
            InterfaceProxy.From(Adapter).Raise("DeviceConnectionLost", Adapter, new DeviceErrorEventArgs { Device = Device });
        }
        return Task.FromResult(0);
    }

    private byte[] TimerResponse(AgpMessage request)
    {
        var seconds = BinaryPrimitives.ReadUInt32LittleEndian(request.Payload.Span);
        var payload = new byte[6];
        payload[0] = (byte)TimerResult;
        payload[1] = seconds == 0 ? (byte)0 : (byte)1;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(2), seconds);
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
        }
        else
        {
            payload[1] = (byte)AgpLightMode.Steady;
        }

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
