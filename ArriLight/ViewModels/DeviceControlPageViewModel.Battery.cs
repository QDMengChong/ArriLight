using CommunityToolkit.Mvvm.ComponentModel;

namespace ArriLight.ViewModels;

public partial class DeviceControlPageViewModel
{
    [ObservableProperty] private bool _isBatterySupported;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(BatteryLevelText))] private byte? _batteryLevelPercent;

    public string BatteryLevelText => IsDeviceOnline && BatteryLevelPercent is { } percent
        ? $"{percent}%" : AppResources.Common_Unknown;

    private void ApplyBatteryProfile(AgpDeviceControlProfile profile)
    {
        IsBatterySupported = !IsDemo && !IsMeshDevice && profile.SupportsBattery;
        BatteryLevelPercent = IsBatterySupported && IsDeviceOnline ? profile.BatteryState?.LevelPercent : null;
    }
}
