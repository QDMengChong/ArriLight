using ArriOpto.BLE.Ota;

namespace ArriLight;

internal static class OtaPlatformTransport
{
    public static PluginBleOtaTransport? Create()
    {
#if IOS
        return new PluginBleOtaTransport
        {
            // Plugin.BLE 3.2.1 Apple returns maximum write payload, not ATT MTU.
            NormalizeRequestedMtuResult = payload => checked(payload + 3),
            WriteWithoutResponseAsync = async (device, characteristic, data, token) =>
            {
                if (device.NativeDevice is not CoreBluetooth.CBPeripheral peripheral)
                    throw new NotSupportedException("Native iOS OTA transport is unavailable.");
                var bytes = data.ToArray();
                while (true)
                {
                    var sent = false;
                    await MainThread.InvokeOnMainThreadAsync(() =>
                    {
                        token.ThrowIfCancellationRequested();
                        if (peripheral.State != CoreBluetooth.CBPeripheralState.Connected)
                            throw new IOException("The iOS OTA peripheral disconnected.");
                        // This is a level signal. The ready event is not a packet acknowledgement.
                        if (!peripheral.CanSendWriteWithoutResponse) return;
                        using var uuid = CoreBluetooth.CBUUID.FromString(characteristic.Id.ToString());
                        var nativeCharacteristic = peripheral.Services?
                            .SelectMany(service => service.Characteristics ?? [])
                            .FirstOrDefault(candidate => candidate.UUID.Equals(uuid))
                            ?? throw new IOException("The discovered native iOS OTA characteristic is unavailable.");
                        using var payload = Foundation.NSData.FromArray(bytes);
                        peripheral.WriteValue(payload, nativeCharacteristic,
                            CoreBluetooth.CBCharacteristicWriteType.WithoutResponse);
                        sent = true;
                    });
                    if (sent) return 0;
                    await Task.Delay(10, token);
                }
            }
        };
#else
        return null;
#endif
    }
}
