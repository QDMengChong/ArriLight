using Xunit;

namespace ArriLight.Tests;

public sealed class MeshRuntimeStateTests
{
    [Fact]
    public async Task MeshRuntimeOfflineStateUsesTheSharedSnapshotContract()
    {
        await using var service = new MeshDeviceControlService(
            null!,
            null!,
            new AppDiagnosticService());
        DeviceRuntimeChange? change = null;
        service.RuntimeStateChanged += (_, value) => change = value;

        service.RecordDeviceOffline("mesh-device", "测试离线");

        var snapshot = service.GetRuntimeSnapshot("mesh-device");
        Assert.NotNull(snapshot);
        Assert.Equal(BluetoothDeviceProtocol.Mesh, snapshot!.Protocol);
        Assert.Equal(DeviceRuntimeAvailability.Offline, snapshot.Availability);
        Assert.Equal("测试离线", snapshot.Error);
        Assert.Equal(snapshot, change!.Snapshot);
    }
}
