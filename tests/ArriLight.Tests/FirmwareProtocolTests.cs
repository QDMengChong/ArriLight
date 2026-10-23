using System.Text;
using ArriOpto.BLE.Ota;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Xunit;

namespace ArriLight.Tests;

public sealed class FirmwareProtocolTests
{
    [Theory]
    [InlineData(OtaGattMode.SingleBankApp, "1.1.1.bin")]
    [InlineData(OtaGattMode.SingleBankOta, "1.1.1.bin")]
    [InlineData(OtaGattMode.Slb, "1.1.1.hex16")]
    public async Task WrongProtocolIsRejectedBeforeAnyUpgradeCommandOrDataWrite(
        OtaGattMode mode, string fileName)
    {
        await using var fixture = new PairingFixture();
        var writes = new List<byte[]>();
        var nativeWrites = new List<byte[]>();
        fixture.Ble.CustomServices = [CreateOtaService(mode, writes)];
        fixture.Ble.NegotiatedMtu = 247;
        var client = new PluginBleOtaClient(fixture.Ble.Adapter);
        // Valid payloads reach protocol matching rather than failing in the file loader.
        var payload = fileName.EndsWith(".bin", StringComparison.Ordinal)
            ? new byte[] { 1, 2, 3, 4 }
            : Encoding.ASCII.GetBytes(":020000041FFFDC\n:0418380001020304A2\n:00000001FF\n");
        await using var stream = new MemoryStream(payload);
        var options = new PluginBleOtaOptions
        {
            Transport = new PluginBleOtaTransport
            {
                WriteWithoutResponseAsync = (_, _, data, _) =>
                {
                    nativeWrites.Add(data.ToArray());
                    return Task.FromResult(0);
                }
            }
        };

        var exception = await Assert.ThrowsAsync<OtaException>(() =>
            client.UpgradeAsync(fixture.Ble.Device, stream, fileName, options));

        Assert.Equal(OtaState.ErrorFileMismatch, exception.State);
        Assert.Empty(writes);
        Assert.Empty(nativeWrites);
        Assert.Single(fixture.Ble.ConnectAttempts);
        Assert.Equal(1, fixture.Ble.DisconnectAttempts);
    }

    private static IService CreateOtaService(OtaGattMode mode, List<byte[]> writes)
    {
        var slb = mode == OtaGattMode.Slb;
        var serviceId = slb ? OtaConstants.Slb.Service : OtaConstants.SingleBank.Service;
        var ids = slb
            ? new[] { OtaConstants.Slb.Command, OtaConstants.Slb.Data, OtaConstants.Slb.Notify }
            : mode == OtaGattMode.SingleBankApp
                ? new[] { OtaConstants.SingleBank.Command, OtaConstants.SingleBank.Notify }
                : new[] { OtaConstants.SingleBank.Command, OtaConstants.SingleBank.Data, OtaConstants.SingleBank.Notify };
        var characteristics = ids.ToDictionary(id => id, id =>
        {
            var writeType = CharacteristicWriteType.WithResponse;
            return InterfaceProxy.Create<ICharacteristic>((name, args) => name switch
            {
                "get_Id" => id,
                "get_CanWrite" or "get_CanUpdate" => true,
                "get_WriteType" => writeType,
                "set_WriteType" => SetWriteType((CharacteristicWriteType)args[0]!),
                "StartUpdatesAsync" or "StopUpdatesAsync" => Task.CompletedTask,
                "WriteAsync" => RecordWrite((byte[])args[0]!),
                _ => throw new NotSupportedException(name)
            });
            object? SetWriteType(CharacteristicWriteType value) { writeType = value; return null; }
        });
        return InterfaceProxy.Create<IService>((name, args) => name switch
        {
            "get_Id" => serviceId,
            "GetCharacteristicAsync" => Task.FromResult(characteristics.GetValueOrDefault((Guid)args[0]!)!),
            _ => throw new NotSupportedException(name)
        });

        Task<int> RecordWrite(byte[] bytes)
        {
            writes.Add(bytes.ToArray());
            return Task.FromResult(0);
        }
    }
}
