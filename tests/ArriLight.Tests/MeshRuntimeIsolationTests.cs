using ArriOpto.BLE.Agp;
using Xunit;

namespace ArriLight.Tests;

/// <summary>
/// Mesh 设备与 AGP Runtime 的隔离：Mesh 走 MeshDeviceControlService 的按需短连接，
/// 不能被 AGP Runtime 收编（否则每次冷启动都会白白连一次并在找不到 AGP 服务时失败）。
/// </summary>
public sealed class MeshRuntimeIsolationTests
{
    [Fact]
    public async Task StartRuntimeDoesNotConnectMeshDevices()
    {
        await using var fixture = MeshRuntimeFixture();
        var radio = fixture.Ble;

        await fixture.Service.StartRuntimeAsync();

        Assert.Empty(radio.ConnectAttempts);
    }

    [Fact]
    public async Task StartRuntimeStillConnectsAgpDevices()
    {
        await using var fixture = AgpRuntimeFixture();

        await fixture.Service.StartRuntimeAsync();

        // 准备是异步排队的；对照组只需证明 AGP 设备确实会被连接。
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (fixture.Ble.ConnectAttempts.Count == 0 && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(20);

        Assert.NotEmpty(fixture.Ble.ConnectAttempts);
    }

    [Fact]
    public async Task RequestRuntimePreparationSkipsMeshDevices()
    {
        await using var fixture = MeshRuntimeFixture();
        await fixture.Service.StartRuntimeAsync();

        fixture.Service.RequestRuntimePreparation(fixture.Ble.Address);

        Assert.Empty(fixture.Ble.ConnectAttempts);
    }

    [Fact]
    public async Task MeshDeviceIsNotEnrolledInRuntimeSnapshots()
    {
        await using var fixture = MeshRuntimeFixture();
        await fixture.Service.StartRuntimeAsync();

        Assert.Null(fixture.Service.GetRuntimeSnapshot(fixture.Ble.Address));
    }

    private static PairingFixture MeshRuntimeFixture()
    {
        var fixture = new PairingFixture();
        fixture.Repository.SetTokenAndCapabilities(
            fixture.Ble.Address, [1, 2, 3, 4], AgpCapabilities.OnOff);
        // 先按 AGP 加入，再改为 Mesh：模拟已保存设备被识别为 Mesh 的情形。
        fixture.Repository.SetProtocol(fixture.Ble.Address, BluetoothDeviceProtocol.Mesh);
        return fixture;
    }

    private static PairingFixture AgpRuntimeFixture()
    {
        var fixture = new PairingFixture();
        fixture.Repository.SetTokenAndCapabilities(
            fixture.Ble.Address, [1, 2, 3, 4], AgpCapabilities.OnOff);
        return fixture;
    }
}
