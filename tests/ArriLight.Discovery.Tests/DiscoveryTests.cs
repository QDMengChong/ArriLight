using System.Reflection;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
using Xunit;

namespace ArriLight.Tests;

public sealed class DiscoveryTests
{
    [Theory]
    [InlineData(AdvertisementRecordType.ServiceData128Bit, 1, true)]
    [InlineData(AdvertisementRecordType.ServiceData, 1, true)]
    [InlineData(AdvertisementRecordType.ServiceData128Bit, 2, false)]
    [InlineData(AdvertisementRecordType.ServiceData, 2, false)]
    public async Task Pw01AdvertisementIsDiscoveredWithPairingStatus(
        AdvertisementRecordType type, byte statusByte, bool isPairingOpen)
    {
        var radio = new Radio();
        var found = new List<DiscoveredBluetoothDevice>();
        radio.Discovery.DeviceFound += (_, device) => found.Add(device);
        using var cancellation = new CancellationTokenSource();
        var scan = radio.Discovery.ScanAsync(cancellation.Token);
        try
        {
            await radio.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var data = Convert.FromHexString("D7F906F860FC0F87084433C40124098F0101");
            data[17] = statusByte;
            AdvertisementRecord[] records = [new(type, data)];
            var id = Guid.Parse("16ab155d-2f1b-4578-8235-ab8926433580");
            var device = Proxy.Create<IDevice>((name, _) => name switch
            {
                "get_Id" => id,
                "get_Name" => "PhyLight-12345678",
                "get_Rssi" => -42,
                "get_AdvertisementRecords" => records,
                _ => null
            });

            radio.Advertise(device);

            var discovered = Assert.Single(found);
            Assert.Equal("PhyLight-12345678", discovered.Name);
            Assert.Equal(id.ToString("D").ToUpperInvariant(), discovered.Address);
            Assert.Equal(-42, discovered.Rssi);
            Assert.Equal(BluetoothDeviceProtocol.Agp, discovered.Protocol);
            Assert.Equal(isPairingOpen, discovered.IsPairingOpen);
            Assert.False(discovered.IsMesh);
            Assert.Same(device, radio.Discovery.GetCachedDevice(discovered.Address));
        }
        finally
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan);
        }
    }

    [Fact]
    public async Task PageCancellationStopsNativeScanAndUnsubscribesBeforeRestart()
    {
        var radio = new Radio();
        using var cancellation = new CancellationTokenSource();
        var scan = radio.Discovery.ScanAsync(cancellation.Token);
        await radio.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan);
        Assert.False(radio.IsScanning);
        Assert.Equal(0, radio.AdvertisementHandlers);
        Assert.Equal(1, radio.Stops);

        radio.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var second = new CancellationTokenSource();
        var next = radio.Discovery.ScanAsync(second.Token);
        await radio.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await radio.Discovery.StopAsync();
        await next;
        Assert.Equal(2, radio.Starts);
        Assert.False(radio.IsScanning);
        Assert.Equal(0, radio.AdvertisementHandlers);
    }

    [Theory]
    [InlineData("AA:BB:CC:DD:EE:FF", "00000000-0000-0000-0000-aabbccddeeff")]
    [InlineData("aa-bb-cc-dd-ee-ff", "00000000-0000-0000-0000-aabbccddeeff")]
    [InlineData("16AB155D-2F1B-4578-8235-AB8926433580", "16ab155d-2f1b-4578-8235-ab8926433580")]
    public async Task ColdStartConnectsToSavedAddressWithoutAnyScan(string address, string expectedId)
    {
        var radio = new Radio();
        var found = await radio.Discovery.FindDeviceAsync(address);
        Assert.Same(radio.Device, found);
        Assert.Equal(Guid.Parse(expectedId), Assert.Single(radio.KnownIds));
        Assert.Equal(0, radio.Starts);
        Assert.Same(found, await radio.Discovery.FindDeviceAsync(address));
        Assert.Single(radio.KnownIds);
    }

    [Fact]
    public async Task CancelledOrInvalidRequestsCannotStartScanning()
    {
        var radio = new Radio();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => radio.Discovery.ScanAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => radio.Discovery.FindDeviceAsync("AA:BB:CC:DD:EE:FF", cancellationToken: cancellation.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => radio.Discovery.FindDeviceAsync("invalid"));
        Assert.Equal(0, radio.Starts);
        Assert.Empty(radio.KnownIds);
    }

    private sealed class Radio
    {
        public bool IsScanning;
        public int Starts, Stops, AdvertisementHandlers;
        private event EventHandler<DeviceEventArgs>? DeviceAdvertised;
        public void Advertise(IDevice device) =>
            DeviceAdvertised?.Invoke(this, new DeviceEventArgs { Device = device });
        public List<Guid> KnownIds { get; } = [];
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDevice Device { get; } = Proxy.Create<IDevice>((name, args) => name switch
        {
            "get_Id" => Guid.Parse("00000000-0000-0000-0000-aabbccddeeff"),
            "get_State" => DeviceState.Connected,
            _ => null
        });
        public BluetoothDiscoveryService Discovery { get; }
        public Radio()
        {
            var bluetooth = Proxy.Create<IBluetoothLE>((name, args) => name == "get_State" ? BluetoothState.On : null);
            var adapter = Proxy.Create<IAdapter>(Invoke);
            Discovery = new(bluetooth, adapter, new AppDiagnosticService());
        }
        private object? Invoke(string name, object?[] args)
        {
            switch (name)
            {
                case "get_IsScanning": return IsScanning;
                case "add_DeviceAdvertised":
                    AdvertisementHandlers++;
                    DeviceAdvertised += (EventHandler<DeviceEventArgs>)args[0]!;
                    return null;
                case "remove_DeviceAdvertised":
                    AdvertisementHandlers--;
                    DeviceAdvertised -= (EventHandler<DeviceEventArgs>)args[0]!;
                    return null;
                case "StartScanningForDevicesAsync":
                    Starts++; IsScanning = true; Started.TrySetResult();
                    return Task.Delay(Timeout.InfiniteTimeSpan, (CancellationToken)args[3]!);
                case "StopScanningForDevicesAsync": Stops++; IsScanning = false; return Task.CompletedTask;
                case "ConnectToKnownDeviceAsync": KnownIds.Add((Guid)args[0]!); return Task.FromResult(Device);
                default: return null;
            }
        }
    }

    public class Proxy : DispatchProxy
    {
        private Func<string, object?[], object?> _invoke = null!;
        public static T Create<T>(Func<string, object?[], object?> invoke) where T : class
        {
            var instance = Create<T, Proxy>();
            ((Proxy)(object)instance)._invoke = invoke;
            return instance;
        }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            _invoke(targetMethod!.Name, args ?? []);
    }
}
