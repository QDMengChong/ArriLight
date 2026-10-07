using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ArriLight.ViewModels;

public partial class DeviceSharePageViewModel : ViewModelBase
{
    private readonly DeviceRepository _repository;
    [ObservableProperty]
    private string _deviceName = AppResources.Device_DefaultName;
    [ObservableProperty]
    private string _deviceAddress = string.Empty;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasShareCode))]
    private string _shareCode = string.Empty;
    [ObservableProperty]
    private string _shareMessage = string.Empty;

    public bool HasShareCode => !string.IsNullOrEmpty(ShareCode);

    public DeviceSharePageViewModel(DeviceRepository repository) { _repository = repository; }

    public override void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("address", out var address) && address is not null)
            DeviceAddress = Uri.UnescapeDataString(address.ToString()!);

        ReloadShareCode();
    }

    public override Task OnAppearingAsync()
    {
        ReloadShareCode();
        return Task.CompletedTask;
    }

    private void ReloadShareCode()
    {
        ShareCode = string.Empty;
        if (_repository.GetByAddress(DeviceAddress) is not { } device)
        {
            ShareMessage = AppResources.Share_RecordMissing;
            return;
        }

        DeviceName = device.DisplayName;
        try
        {
            ShareCode = DeviceShareCode.Encode(device);
            ShareMessage = AppResources.Share_ContentsDescription;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            ErrorMessage = exception.Message;
            ShareMessage = exception.Message;
        }
    }
}
