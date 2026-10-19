using CommunityToolkit.Mvvm.ComponentModel;

namespace ArriLight.ViewModels;

public sealed partial class LightDeviceItem : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PowerIcon), nameof(PowerBackgroundColor), nameof(PowerBorderColor))]
    private bool _isOn;
    [ObservableProperty] private string _status;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEnabled), nameof(PowerOpacity), nameof(StatusColor))]
    private bool _isOnline;
    [ObservableProperty] private bool _isLocked;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusColor))]
    private bool _isReady;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStatusBlinking), nameof(StatusColor))]
    private bool _isRuntimeBusy;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEnabled), nameof(PowerOpacity), nameof(IsStatusBlinking), nameof(StatusColor))]
    private bool _isChecking;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEnabled), nameof(IsStatusBlinking), nameof(StatusColor))]
    private bool _isPowerChanging;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEnabled), nameof(IsStatusBlinking), nameof(StatusColor))]
    private bool _hasPendingUserOperation;
    public LightDeviceItem(string name, string address, string status,
        bool isOn = false, bool isDemo = false, bool isOnline = false,
        BluetoothDeviceProtocol protocol = BluetoothDeviceProtocol.Unknown)
    {
        Name = name; Address = address; IsDemo = isDemo;
        Protocol = protocol == BluetoothDeviceProtocol.Unknown
            ? BluetoothDeviceProtocol.Agp
            : protocol;
        _status = status; _isOnline = isOnline; _isOn = isOn;
    }

    public string Name { get; }

    public string Address { get; }

    public bool IsDemo { get; }

    public BluetoothDeviceProtocol Protocol { get; }

    public bool IsMesh => Protocol == BluetoothDeviceProtocol.Mesh;

    public string ProtocolLabel => IsMesh
        ? AppResources.Mesh_Badge
        : AppResources.Gatt_Badge;

    public string ProtocolIconSource => IsMesh
        ? "mesh.svg"
        : "device_light.svg";

    private static readonly PresentationColor OfflineColor = PresentationColor.FromArgb("#E65B65");
    private static readonly PresentationColor OnlineColor = PresentationColor.FromArgb("#E9B949");
    private static readonly PresentationColor ReadyColor = PresentationColor.FromArgb("#35C987");

    public bool IsStatusBlinking => IsRuntimeBusy || IsChecking || IsPowerChanging || HasPendingUserOperation;
    public PresentationColor StatusColor => !IsOnline ? OfflineColor
        : IsReady && !IsStatusBlinking ? ReadyColor : OnlineColor;

    public bool IsEnabled => !IsPowerChanging && !HasPendingUserOperation;
    public double PowerOpacity => 1;
    public string PowerIcon => IsOn ? "power_on.svg" : "power_off.svg";
    public PresentationColor PowerBackgroundColor => IsOn ? PresentationColor.FromArgb("#FFF2C7") : PresentationColor.Transparent;
    public PresentationColor PowerBorderColor => IsOn ? PresentationColor.FromArgb("#FFD66B") : PresentationColor.FromArgb("#56657A");
}
