using ArriOpto.BLE.Agp;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
using Xunit;

namespace ArriLight.Tests;

public sealed class NativeSessionRuntimeTests
{
    [Fact]
    public async Task ConcurrentManualRetrySharesFailureAndCancelingOneWaitDoesNotCancelConnection()
    {
        await using var fixture = new PairingFixture();
        var started = Signal();
        var release = Signal();
        fixture.Ble.OnConnect = async (_, token) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            throw new IOException("offline");
        };
        var first = fixture.Connection.ConnectAsync(fixture.Address);
        await started.Task;
        var joined = fixture.Connection.ConnectAsync(fixture.Address);
        using var cancellation = new CancellationTokenSource();
        var pageWait = fixture.Connection.ConnectAsync(fixture.Address, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pageWait);
        Assert.False(first.IsCompleted);
        release.TrySetResult();
        await Assert.ThrowsAsync<IOException>(() => first);
        await Assert.ThrowsAsync<IOException>(() => joined);
        Assert.Single(fixture.Ble.ConnectAttempts);
    }

    [Fact]
    public async Task DifferentDevicesCanConnectWhileAnotherNativeConnectionIsWaiting()
    {
        await using var fixture = new PairingFixture();
        var peer = fixture.AddDevice();
        var started = Signal();
        var release = Signal();
        fixture.Ble.OnConnect = async (_, token) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        var pending = fixture.Connection.ConnectAsync(fixture.Address);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            await fixture.Connection.ConnectAsync(peer.Address).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(fixture.Connection.IsConnected(peer.Address));
            Assert.False(pending.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await pending;
    }

    [Fact]
    public async Task SameDeviceSharesOneNativeConnectionAttempt()
    {
        await using var fixture = new PairingFixture();
        var started = Signal();
        var release = Signal();
        fixture.Ble.OnConnect = async (_, token) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        var first = fixture.Connection.ConnectAsync(fixture.Address);
        await started.Task;
        var second = fixture.Connection.ConnectAsync(fixture.Address);
        Assert.Single(fixture.Ble.ConnectAttempts);
        release.TrySetResult();
        await Task.WhenAll(first, second);
        Assert.Single(fixture.Ble.ConnectAttempts);
        Assert.Equal(1, fixture.Ble.MaximumConcurrentAttempts);
    }

    [Fact]
    public async Task DisconnectAllIncludesAndCancelsADeviceStillConnecting()
    {
        await using var fixture = new PairingFixture();
        var started = Signal();
        fixture.Ble.OnConnect = async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        var connecting = fixture.Connection.ConnectAsync(fixture.Address);
        await started.Task;
        await fixture.Connection.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting);
        Assert.False(fixture.Connection.IsConnected(fixture.Address));
        Assert.Equal(BluetoothSessionState.Disconnected, fixture.Connection.GetSnapshot(fixture.Address).State);
    }

    [Fact]
    public async Task TargetOtaDoesNotPreventAnotherDevicesConnection()
    {
        await using var fixture = new PairingFixture();
        var peer = fixture.AddDevice();
        var started = Signal();
        var release = Signal();
        var ota = fixture.Connection.RunExclusiveOtaAsync(fixture.Address, async (_, token) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token);
        }, CancellationToken.None);
        await started.Task;
        var targetRead = fixture.Connection.ReadAsync(fixture.Address,
            AgpProfile.ServiceId, AgpProfile.LightStateCharacteristicId);
        try
        {
            await fixture.Connection.ConnectAsync(peer.Address).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(targetRead.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await Task.WhenAll(ota, targetRead);
    }

    [Fact]
    public async Task RepeatedStartUpgradesMinimalTransportWithoutDuplicateSubscriptions()
    {
        await using var fixture = new PairingFixture();
        await using var transport = new AgpGattTransport(fixture.Connection, fixture.Address);
        await transport.StartAsync(subscribeState: false);
        Assert.Equal([AgpProfile.ControlPointCharacteristicId], fixture.Ble.Subscriptions);
        await Task.WhenAll(transport.StartAsync(), transport.EnsureLightStateSubscriptionAsync());
        await transport.StartAsync();
        Assert.Equal([AgpProfile.ControlPointCharacteristicId, AgpProfile.LightStateCharacteristicId],
            fixture.Ble.Subscriptions);
        Assert.Single(fixture.Ble.ConnectAttempts);
    }

    [Fact]
    public async Task MinimalTransportDoesNotRequireOptionalStateCharacteristics()
    {
        var fixture = new TransportBoundary(includeLightState: false);
        await using var transport = new AgpGattTransport(fixture.Connection, fixture.Address);
        await transport.StartAsync(subscribeState: false);
        Assert.Equal([AgpProfile.ControlPointCharacteristicId], fixture.Subscriptions);
        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.EnsureLightStateSubscriptionAsync());
        await transport.StartAsync(subscribeState: false);
        Assert.True(fixture.Connection.IsConnected(fixture.Address));
        await fixture.Connection.DisconnectAsync();
    }

    [Fact]
    public async Task MalformedColorOnlyFailsItsOwnUpgradeAndLeavesRequiredChannelsUsable()
    {
        var fixture = new TransportBoundary(malformedColor: true);
        await using var transport = new AgpGattTransport(fixture.Connection, fixture.Address);
        await transport.StartAsync(subscribeState: false);
        await transport.EnsureLightStateSubscriptionAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.EnsureColorStateSubscriptionAsync());
        await transport.StartAsync(subscribeState: false);
        Assert.Equal([AgpProfile.ControlPointCharacteristicId, AgpProfile.LightStateCharacteristicId],
            fixture.Subscriptions);
        await fixture.Connection.DisconnectAsync();
    }

    [Fact]
    public async Task OldTransportRejectsReadsAfterConnectionGenerationChanges()
    {
        await using var fixture = new PairingFixture();
        await using var transport = new AgpGattTransport(fixture.Connection, fixture.Address);
        await transport.StartAsync(subscribeState: false);
        await fixture.Connection.DisconnectAsync(fixture.Address);
        await fixture.Connection.ConnectAsync(fixture.Address);
        await Assert.ThrowsAsync<IOException>(() => transport.ReadLightStateAsync());
        await Assert.ThrowsAsync<IOException>(() => transport.StartAsync());
        Assert.Empty(fixture.Ble.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedTransportWorkCannotReconnectWhenItsSessionEndsBeforeItsTurn(bool write)
    {
        await using var fixture = new PairingFixture();
        await using var transport = new AgpGattTransport(fixture.Connection, fixture.Address);
        await transport.StartAsync(subscribeState: false);
        var started = Signal();
        var release = Signal();
        var exclusive = fixture.Connection.RunExclusiveOtaAsync(fixture.Address, async (_, token) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token);
        }, CancellationToken.None);
        await started.Task;
        Task queued = write
            ? transport.WriteControlPointAsync(AgpCodec.Encode(AgpMessageType.Request, AgpOpcode.OnOffSet, 1, [2]))
            : transport.ReadLightStateAsync();
        fixture.Ble.SetDisconnectedBeforeEvent();
        release.TrySetResult();
        await exclusive;
        await Assert.ThrowsAsync<IOException>(() => queued);
        Assert.Single(fixture.Ble.ConnectAttempts);
        Assert.Empty(fixture.Ble.Requests);
        Assert.Empty(fixture.Ble.Reads);
    }

    [Fact]
    public async Task ACancelledNativeConnectThatCompletesLateIsReleasedBeforeShutdownFinishes()
    {
        var fixture = new TransportBoundary();
        var started = Signal();
        var release = Signal();
        fixture.OnConnect = async _ =>
        {
            started.TrySetResult();
            await release.Task; // Deliberately models a native adapter ignoring cancellation.
        };
        var connecting = fixture.Connection.ConnectAsync(fixture.Address);
        await started.Task;
        fixture.Connection.SetApplicationSuspended(true);
        var stopping = fixture.Connection.DisconnectAsync();
        release.TrySetResult();
        await stopping.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting);
        Assert.Equal(DeviceState.Disconnected, fixture.Device.State);
        Assert.Equal(1, fixture.DisconnectCount);
        Assert.False(fixture.Connection.IsConnected(fixture.Address));
    }

    [Fact]
    public async Task LateConnectionEventsFromOldDeviceInstanceDoNotCloseReconnectedSession()
    {
        var fixture = new TransportBoundary();
        await fixture.Connection.ConnectAsync(fixture.Address);
        var oldDevice = fixture.Device;
        var oldGeneration = fixture.Connection.GetConnectionGeneration(fixture.Address);
        await fixture.Connection.DisconnectAsync(fixture.Address);
        fixture.ReplaceDeviceInstance();
        await fixture.Connection.ConnectAsync(fixture.Address);
        Assert.Equal(oldDevice.Id, fixture.Device.Id);
        Assert.NotSame(oldDevice, fixture.Device);
        Assert.True(fixture.Connection.GetConnectionGeneration(fixture.Address) > oldGeneration);

        InterfaceProxy.From(fixture.Adapter).Raise("DeviceDisconnected", fixture.Adapter,
            new DeviceEventArgs { Device = oldDevice });
        InterfaceProxy.From(fixture.Adapter).Raise("DeviceConnectionLost", fixture.Adapter,
            new DeviceErrorEventArgs { Device = oldDevice, ErrorMessage = "old connection lost" });
        InterfaceProxy.From(fixture.Adapter).Raise("DeviceConnectionError", fixture.Adapter,
            new DeviceErrorEventArgs { Device = oldDevice, ErrorMessage = "old connection error" });

        Assert.True(fixture.Connection.IsConnected(fixture.Address));
        Assert.Equal(BluetoothSessionState.Connected, fixture.Connection.GetSnapshot(fixture.Address).State);
        await fixture.Connection.DisconnectAsync(fixture.Address);
    }

    private static TaskCompletionSource Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class TransportBoundary
    {
        private DeviceState _state;
        private readonly Func<IDevice> _createDevice;
        private readonly BluetoothDiscoveryService _discovery;
        public IDevice Device { get; private set; }
        public IAdapter Adapter { get; }
        public BluetoothConnectionService Connection { get; }
        public string Address => Device.Id.ToString("D");
        public List<Guid> Subscriptions { get; } = [];
        public Func<CancellationToken, Task>? OnConnect { get; set; }
        public int DisconnectCount { get; private set; }

        public TransportBoundary(bool includeLightState = true, bool malformedColor = false)
        {
            var id = Guid.NewGuid();
            var characteristics = new Dictionary<Guid, ICharacteristic>();
            IService? service = null;
            service = InterfaceProxy.Create<IService>((name, args) => name switch
            {
                "get_Id" => AgpProfile.ServiceId,
                "get_Name" => "AGP",
                "get_IsPrimary" => true,
                "GetCharacteristicsAsync" => Task.FromResult<IReadOnlyList<ICharacteristic>>(characteristics.Values.ToList()),
                "GetCharacteristicAsync" => Task.FromResult(characteristics.GetValueOrDefault((Guid)args[0]!)!),
                _ => throw new NotSupportedException(name)
            });
            Add(AgpProfile.CapabilitiesCharacteristicId, CharacteristicPropertyType.Read);
            Add(AgpProfile.ControlPointCharacteristicId, CharacteristicPropertyType.Write | CharacteristicPropertyType.Indicate);
            if (includeLightState)
                Add(AgpProfile.LightStateCharacteristicId, CharacteristicPropertyType.Read | CharacteristicPropertyType.Notify);
            if (malformedColor)
                Add(AgpProfile.ColorStateCharacteristicId, CharacteristicPropertyType.Read);
            _createDevice = () => InterfaceProxy.Create<IDevice>((name, args) => name switch
            {
                "get_Id" => id,
                "get_Name" => "PW01",
                "get_State" => _state,
                "GetServicesAsync" => Task.FromResult<IReadOnlyList<IService>>([service]),
                "GetServiceAsync" => Task.FromResult(service),
                _ => throw new NotSupportedException(name)
            });
            Device = _createDevice();
            Adapter = InterfaceProxy.Create<IAdapter>((name, args) => name switch
            {
                "ConnectToDeviceAsync" => ConnectAsync((CancellationToken)args[2]!),
                "DisconnectDeviceAsync" => DisconnectAsync(),
                _ => throw new NotSupportedException(name)
            });
            _discovery = new BluetoothDiscoveryService(Device);
            Connection = new BluetoothConnectionService(Adapter, _discovery, new AppDiagnosticService());

            void Add(Guid characteristicId, CharacteristicPropertyType properties)
            {
                characteristics[characteristicId] = InterfaceProxy.Create<ICharacteristic>((name, _) => name switch
                {
                    "get_Id" => characteristicId,
                    "get_Name" => characteristicId.ToString(),
                    "get_Service" => service!,
                    "get_Properties" => properties,
                    "StartUpdatesAsync" => SubscribeAsync(characteristicId),
                    "StopUpdatesAsync" => Task.CompletedTask,
                    _ => throw new NotSupportedException(name)
                });
            }
        }

        public void ReplaceDeviceInstance()
        {
            Device = _createDevice();
            _discovery.RegisterDevice(Device);
        }

        private async Task ConnectAsync(CancellationToken token)
        {
            if (OnConnect is not null) await OnConnect(token);
            _state = DeviceState.Connected;
        }

        private Task DisconnectAsync()
        {
            DisconnectCount++;
            _state = DeviceState.Disconnected;
            return Task.CompletedTask;
        }

        private Task SubscribeAsync(Guid id)
        {
            Subscriptions.Add(id);
            return Task.CompletedTask;
        }
    }
}
