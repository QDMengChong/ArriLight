using Plugin.BLE.Abstractions.Contracts;

namespace ArriOpto.BLE.Ota;

/// <summary>Optional native transport hooks; protocol code always uses an ATT MTU.</summary>
public sealed record PluginBleOtaTransport
{
    public Func<int, int>? NormalizeRequestedMtuResult { get; init; }
    public Func<IDevice, ICharacteristic, ReadOnlyMemory<byte>, CancellationToken, Task<int>>?
        WriteWithoutResponseAsync { get; init; }
}
