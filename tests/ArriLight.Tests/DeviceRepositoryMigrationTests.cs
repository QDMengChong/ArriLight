using System.Text.Json;
using ArriOpto.BLE.Agp;
using Xunit;

namespace ArriLight.Tests;

public sealed class DeviceRepositoryMigrationTests
{
    private const string DevicesKey = "saved_bluetooth_devices_v1";
    private static readonly DateTimeOffset AddedAt = DateTimeOffset.Parse("2026-10-01T00:00:00Z");

    [Fact]
    public void FreshInstallStartsEmptyAndOnlyContainsDevicesExplicitlyAdded()
    {
        Preferences.Default.Clear();
        var repository = new DeviceRepository();
        Assert.Empty(repository.GetAll());
        Assert.Empty(new DeviceRepository().GetAll());

        Assert.True(repository.TryAdd("PW01", "AA:BB:CC:DD:EE:01"));
        Assert.Equal("AA:BB:CC:DD:EE:01", Assert.Single(new DeviceRepository().GetAll()).Address);
    }

    [Fact]
    public void UpgradeRemovesBothBuiltInDemosIncludingRenamedRecordsAndPreservesRealDeviceData()
    {
        Preferences.Default.Clear();
        SavedDevice[] realDevices =
        [
            new("PW01", "AA:BB:CC:DD:EE:01", AddedAt, IsPinned: true,
                FirmwareVersion: "2.3.4", Token: "01020304", TimerSeconds: 135,
                Capabilities: AgpCapabilities.OnOff | AgpCapabilities.SecureControl,
                LastOperatedAt: AddedAt.AddDays(1), Protocol: BluetoothDeviceProtocol.Agp),
            new("客厅主灯", "AA:BB:CC:DD:EE:02", AddedAt, Protocol: BluetoothDeviceProtocol.Mesh)
        ];
        SavedDevice[] saved =
        [
            new("Renamed demo", "02:00:00:00:00:01", AddedAt, IsDemo: true),
            realDevices[0],
            new("卧室氛围灯", "02:00:00:00:00:02", AddedAt, IsDemo: true),
            realDevices[1]
        ];
        Preferences.Default.Set(DevicesKey, JsonSerializer.Serialize(saved));

        Assert.Equal(realDevices, new DeviceRepository().GetAll());
        Assert.Equal(realDevices, JsonSerializer.Deserialize<List<SavedDevice>>(Preferences.Default.Get(DevicesKey, "")));
        Assert.Equal(realDevices, new DeviceRepository().GetAll());
    }

    [Fact]
    public void UpgradeWithOnlyBuiltInDemosStaysEmptyAcrossRepositoryReloads()
    {
        Preferences.Default.Clear();
        SavedDevice[] saved =
        [
            new("Living room main light", "02:00:00:00:00:01", IsDemo: true),
            new("Bedroom ambient light", "02:00:00:00:00:02", IsDemo: true)
        ];
        Preferences.Default.Set(DevicesKey, JsonSerializer.Serialize(saved));

        Assert.Empty(new DeviceRepository().GetAll());
        Assert.Equal("[]", Preferences.Default.Get(DevicesKey, ""));
        Assert.Empty(new DeviceRepository().GetAll());
    }

    [Theory]
    [InlineData("02:00:00:00:00:01")]
    [InlineData("02:00:00:00:00:02")]
    public void RealDeviceAtFormerDemoAddressIsPreserved(string address)
    {
        Preferences.Default.Clear();
        var real = new SavedDevice("Real PW01", address, AddedAt, Token: "01020304");
        Preferences.Default.Set(DevicesKey, JsonSerializer.Serialize(new[] { real }));

        Assert.Equal(real, Assert.Single(new DeviceRepository().GetAll()));
    }

    [Fact]
    public void ExistingGroupsPruneRemovedDemoMembersAndKeepTheirRealMembersAndSettings()
    {
        Preferences.Default.Clear();
        SavedDevice[] saved =
        [
            new("客厅主灯", "02:00:00:00:00:01", AddedAt, IsDemo: true),
            new("卧室氛围灯", "02:00:00:00:00:02", AddedAt, IsDemo: true),
            new("PW01", "AA:BB:CC:DD:EE:01", AddedAt)
        ];
        Preferences.Default.Set(DevicesKey, JsonSerializer.Serialize(saved));
        var group = new SavedDeviceGroup("saved-group", "Lights", saved.Select(device => device.Address).ToArray(),
            AddedAt, TimerSeconds: 135, IsPinned: true);
        Preferences.Default.Set("saved_device_groups_v1", JsonSerializer.Serialize(new[] { group }));

        var groups = new DeviceGroupRepository(new DeviceRepository());
        var expected = group with { Members = new[] { "AA:BB:CC:DD:EE:01" } };
        var reloaded = Assert.Single(groups.GetAll());
        Assert.Equal(expected.Members, reloaded.Members);
        Assert.Equal(expected.Name, reloaded.Name);
        Assert.Equal(expected.AddedAt, reloaded.AddedAt);
        Assert.Equal(expected.TimerSeconds, reloaded.TimerSeconds);
        Assert.True(reloaded.IsPinned);
        var persisted = Assert.Single(JsonSerializer.Deserialize<List<SavedDeviceGroup>>(
            Preferences.Default.Get("saved_device_groups_v1", ""))!);
        Assert.Equal(expected.Members, persisted.Members);
    }
}
