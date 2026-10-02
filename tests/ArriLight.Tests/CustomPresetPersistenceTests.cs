using ArriOpto.BLE.Agp;
using Xunit;

namespace ArriLight.Tests;

public sealed class CustomPresetPersistenceTests
{
    [Fact]
    public void CustomPresetsPersistPerDeviceAndAreRemovedWithTheDevice()
    {
        Preferences.Default.Clear();
        var repository = new DeviceRepository();
        const string firstAddress = "AA:BB:CC:DD:EE:01";
        const string secondAddress = "AA:BB:CC:DD:EE:02";
        Assert.True(repository.TryAdd("第一台", firstAddress));
        Assert.True(repository.TryAdd("第二台", secondAddress));

        var preset = new SavedControlPreset(
            2,
            "夜间",
            0.08,
            0.72,
            0.35,
            46,
            "#FFAA55",
            AgpLightMode.Breathe,
            2_400);
        Assert.True(repository.SetCustomPresets(firstAddress, [preset]));

        var reloaded = new DeviceRepository();
        var saved = Assert.Single(reloaded.GetCustomPresets(firstAddress));
        Assert.Equal(preset, saved);
        Assert.Empty(reloaded.GetCustomPresets(secondAddress));

        Assert.True(reloaded.Remove(firstAddress));
        Assert.Empty(reloaded.GetCustomPresets(firstAddress));
    }
}
