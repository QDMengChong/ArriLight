using Plugin.BLE.Abstractions.Contracts;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ArriLight;

// Only platform boundaries are replaced; pairing, transport, connection handling,
// AGP codecs/client and persistence serialization use the production sources.
public sealed class BluetoothDiscoveryService(IDevice device)
{
    private readonly Dictionary<string, IDevice> _devices = new(StringComparer.OrdinalIgnoreCase)
    {
        [device.Id.ToString("D")] = device
    };
    public void RegisterDevice(IDevice other) => _devices[other.Id.ToString("D")] = other;
    public Exception? Failure { get; set; }
    public List<string> InvalidatedAddresses { get; } = [];
    public event EventHandler<DiscoveredBluetoothDevice>? DeviceFound;

    public Task<IDevice> FindDeviceAsync(string address, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Failure is null
            ? Task.FromResult(_devices[address])
            : Task.FromException<IDevice>(Failure);
    }

    public void InvalidateCachedDevice(string address) => InvalidatedAddresses.Add(address);
    public Task ScanAsync(CancellationToken token) => Task.Delay(Timeout.InfiniteTimeSpan, token);
    public int StopCalls { get; private set; }
    public TaskCompletionSource StopStartedForTest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Func<Task>? OnStop { get; set; }
    public Task StopAsync()
    {
        StopCalls++;
        StopStartedForTest.TrySetResult();
        return OnStop?.Invoke() ?? Task.CompletedTask;
    }
    public Task StartOrResetBackgroundScanAsync(TimeSpan duration) => Task.CompletedTask;
    public void Advertise(DiscoveredBluetoothDevice value) => DeviceFound?.Invoke(this, value);
}

public sealed record DiscoveredBluetoothDevice(string Name, string Address, int Rssi, bool IsPairingOpen);
public sealed class BluetoothDiscoveryException(string message) : Exception(message);

public sealed class AppDiagnosticService
{
    public List<(string Area, string Message)> Entries { get; } = [];
    public BleTimingScope BeginBleTiming(
        string stage, string? address = null, string? detail = null)
    {
#if DEBUG
        return new(this, stage, address, detail);
#else
        return BleTimingScope.Disabled;
#endif
    }
    public void Debug(string area, string message) { }
    public void Information(string area, string message)
    {
        lock (Entries) Entries.Add((area, message));
    }
    public void Warning(string area, string message) { }
    public void Error(string area, string message, Exception? exception = null) { }
    public void Trace(string area, string message) { }
}

public static class Preferences
{
    public static MemoryPreferences Default { get; } = new();
}

public sealed class MemoryPreferences
{
    private readonly Dictionary<string, string> _values = [];
    public bool ContainsKey(string key) => _values.ContainsKey(key);
    public string Get(string key, string fallback) => _values.GetValueOrDefault(key, fallback);
    public void Set(string key, string value) => _values[key] = value;
    public void Clear() => _values.Clear();
}
