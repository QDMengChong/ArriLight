using CommunityToolkit.Mvvm.ComponentModel;

namespace ArriLight.ViewModels;

public sealed partial class LightDeviceItem : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PowerIcon), nameof(PowerBackgroundColor), nameof(PowerBorderColor))]
    private bool _isOn;
    [ObservableProperty] private string _status;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusDetail))]
    private string _statusDetail = string.Empty;
    [ObservableProperty] private PresentationColor _statusColor;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEnabled), nameof(PowerOpacity))]
    private bool _isOnline;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEnabled), nameof(PowerOpacity))]
    private bool _isChecking;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEnabled))]
    private bool _isPowerChanging;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEnabled))]
    private bool _hasPendingUserOperation;
    public LightDeviceItem(string name, string address, string status, PresentationColor statusColor,
        bool isOn = false, bool isDemo = false, bool isOnline = false)
    {
        Name = name; Address = address; IsDemo = isDemo;
        _status = status; _statusColor = statusColor; _isOnline = isOnline; _isOn = isOn;
    }

    public string Name { get; }

    public string Address { get; }

    public bool IsDemo { get; }

    public bool HasStatusDetail => !string.IsNullOrWhiteSpace(StatusDetail);

    public bool IsEnabled => !IsPowerChanging && !HasPendingUserOperation;
    public double PowerOpacity => 1;
    public string PowerIcon => IsOn ? "power_on.svg" : "power_off.svg";
    public PresentationColor PowerBackgroundColor => IsOn ? PresentationColor.FromArgb("#FFF2C7") : PresentationColor.Transparent;
    public PresentationColor PowerBorderColor => IsOn ? PresentationColor.FromArgb("#FFD66B") : PresentationColor.FromArgb("#56657A");
}
