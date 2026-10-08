using System.Reflection;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Xunit;

namespace ArriLight.Tests;

public sealed class DiscoveryTests
{
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
                case "add_DeviceAdvertised": AdvertisementHandlers++; return null;
                case "remove_DeviceAdvertised": AdvertisementHandlers--; return null;
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
