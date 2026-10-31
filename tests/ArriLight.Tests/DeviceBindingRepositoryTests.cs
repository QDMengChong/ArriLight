using ArriOpto.BLE.Agp;
using Xunit;

namespace ArriLight.Tests;

public sealed class DeviceBindingRepositoryTests
{
    [Theory]
    [InlineData(BluetoothDeviceProtocol.Agp)]
    [InlineData(BluetoothDeviceProtocol.Mesh)]
    public void CompletedBindingReplacesAddressCaseInsensitivelyAndPersistsFreshRecord(BluetoothDeviceProtocol protocol)
    {
        Preferences.Default.Clear();
        var repository = new DeviceRepository();
        const string address = "AA:BB:CC:DD:EE:FF";
        Assert.True(repository.TryAdd("Old device", address));
        repository.SetTokenAndCapabilities(address, [1, 2, 3, 4], AgpCapabilities.SecureControl);
        repository.SetPinned(address, true);
        var notifications = 0;
        repository.DevicesChanged += (_, _) => notifications++;
        var bindingTime = DateTimeOffset.Now;

        Assert.True(repository.TryAddOrReplace(new SavedDevice(
            " New device ", " aa:bb:cc:dd:ee:ff ", bindingTime,
            FirmwareVersion: SavedDevice.UnknownFirmwareVersion, Protocol: protocol)));

        var saved = new DeviceRepository().GetByAddress(address)!;
        Assert.Equal("New device", saved.Name);
        Assert.Equal(bindingTime, saved.AddedAt);
        Assert.Equal(protocol, saved.Protocol);
        Assert.Null(saved.Token);
        Assert.Null(saved.Capabilities);
        Assert.False(saved.IsPinned);
        Assert.Single(repository.GetAll(), device => device.Address == address);
        Assert.Equal(1, notifications);
        Assert.False(repository.TryAdd("Duplicate", address));
    }
}
