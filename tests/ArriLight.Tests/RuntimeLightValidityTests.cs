using System.Buffers.Binary;
using ArriOpto.BLE.Agp;
using Xunit;

namespace ArriLight.Tests;

public sealed class RuntimeLightValidityTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(10u, 12u, 9u)]
    [InlineData(10u, 12u, 10u)]
    [InlineData(10u, 12u, 11u)]
    [InlineData(uint.MaxValue, 0u, uint.MaxValue)]
    public async Task UnknownToggleCannotRecoverReadyFromHistoryOrBeforeAcceptedRevision(
        uint baselineRevision, uint acceptedRevision, uint oldRevision)
    {
        await using var fixture = RuntimeFixture();
        fixture.Ble.LightStatePacket = StatePacket(fixture.Ble.LightStatePacket, baselineRevision, isOn: true);
        await fixture.Service.StartRuntimeAsync();
        await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
        fixture.Ble.SuppressToggleNotification = true;
        fixture.Ble.OnOffStateRevision = acceptedRevision;
        fixture.Ble.OnLightStateRead = () =>
            fixture.Ble.LightStatePacket = StatePacket(fixture.Ble.LightStatePacket, oldRevision, isOn: true);

        await fixture.Service.ToggleOnOffAsync(fixture.Address).WaitAsync(Deadline);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline));

        var unavailable = fixture.Service.GetRuntimeSnapshot(fixture.Address)!;
        Assert.Equal(DeviceRuntimeOperationStage.Unknown, unavailable.LastOperation!.Stage);
        Assert.Equal(DeviceRuntimeAvailability.Online, unavailable.Availability);
        Assert.Equal(DeviceRuntimeStepStatus.Failed, unavailable.Steps[DeviceRuntimeStep.LightState].Status);
        Assert.Null(fixture.Service.GetCachedDeviceStatus(fixture.Address)!.LightState);

        fixture.Ble.OnLightStateRead = null;
        fixture.Ble.LightStatePacket = StatePacket(fixture.Ble.LightStatePacket, acceptedRevision, isOn: false);
        await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
        var recovered = fixture.Service.GetCachedDeviceStatus(fixture.Address)!.LightState!;
        Assert.Equal(acceptedRevision, recovered.Revision);
        Assert.False(recovered.IsOn);
        Assert.Equal(DeviceRuntimeAvailability.Ready, fixture.Service.GetRuntimeSnapshot(fixture.Address)!.Availability);
        Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.OnOffSet);
        Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.AuthToken);
    }

    [Fact]
    public async Task ExplicitRefreshAllowsTheSamePreviouslyValidRevision()
    {
        await using var fixture = RuntimeFixture();
        fixture.Ble.LightStatePacket = StatePacket(fixture.Ble.LightStatePacket, 10, isOn: true);
        await fixture.Service.StartRuntimeAsync();
        await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
        await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address, refresh: true).WaitAsync(Deadline);

        Assert.Equal(10u, fixture.Service.GetCachedDeviceStatus(fixture.Address)!.LightState!.Revision);
        Assert.Equal(DeviceRuntimeAvailability.Ready, fixture.Service.GetRuntimeSnapshot(fixture.Address)!.Availability);
        Assert.Equal(2, fixture.Ble.Reads.Count(id => id == AgpProfile.LightStateCharacteristicId));
        Assert.Single(fixture.Ble.ConnectAttempts);
    }

    [Fact]
    public async Task FreshConnectionDoesNotInheritAnUnknownCommandsRevisionRestriction()
    {
        await using var fixture = RuntimeFixture();
        fixture.Ble.LightStatePacket = StatePacket(fixture.Ble.LightStatePacket, 10, isOn: true);
        await fixture.Service.StartRuntimeAsync();
        await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);
        fixture.Ble.SuppressToggleNotification = true;
        fixture.Ble.OnOffStateRevision = 12;
        fixture.Ble.OnLightStateRead = () =>
            fixture.Ble.LightStatePacket = StatePacket(fixture.Ble.LightStatePacket, 9, isOn: true);
        await fixture.Service.ToggleOnOffAsync(fixture.Address).WaitAsync(Deadline);

        await fixture.Service.SuspendForBackgroundAsync().WaitAsync(Deadline);
        fixture.Ble.OnLightStateRead = null;
        fixture.Ble.LightStatePacket = StatePacket(fixture.Ble.LightStatePacket, 1, isOn: false);
        await fixture.Service.ResumeAfterBackgroundAsync().WaitAsync(Deadline);
        await fixture.Service.EnsureRuntimeReadyAsync(fixture.Address).WaitAsync(Deadline);

        Assert.Equal(1u, fixture.Service.GetCachedDeviceStatus(fixture.Address)!.LightState!.Revision);
        Assert.Equal(DeviceRuntimeAvailability.Ready, fixture.Service.GetRuntimeSnapshot(fixture.Address)!.Availability);
        Assert.Single(fixture.Ble.Requests, opcode => opcode == AgpOpcode.OnOffSet);
        Assert.Equal(2, fixture.Ble.Requests.Count(opcode => opcode == AgpOpcode.AuthToken));
    }

    private static PairingFixture RuntimeFixture()
    {
        var fixture = new PairingFixture();
        fixture.Ble.Capabilities = AgpCapabilities.OnOff | AgpCapabilities.SecureControl;
        fixture.Repository.SetTokenAndCapabilities(fixture.Address, [1, 2, 3, 4], fixture.Ble.Capabilities);
        return fixture;
    }

    private static byte[] StatePacket(byte[] source, uint revision, bool isOn)
    {
        var payload = AgpCodec.Decode(source).Payload.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(payload, revision);
        payload[4] = isOn ? (byte)1 : (byte)0;
        payload[5] = payload[6] = isOn ? payload[7] : (byte)0;
        return AgpCodec.Encode(AgpMessageType.Status, AgpOpcode.LightStateStatus, 0, payload);
    }
}
