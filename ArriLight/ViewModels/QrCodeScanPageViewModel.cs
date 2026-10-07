using CommunityToolkit.Mvvm.Input;

namespace ArriLight.ViewModels;

public partial class QrCodeScanPageViewModel(DeviceRepository repository, AgpLightControlService agpLight,
    IUserInteraction interaction, IAppNavigator navigator) : ViewModelBase
{
    private readonly DeviceRepository _repository = repository;
    private readonly AgpLightControlService _agpLight = agpLight;
    private readonly IUserInteraction _interaction = interaction;
    private readonly IAppNavigator _navigator = navigator;
    private int _isHandlingCode;
    private CancellationTokenSource? _deviceInformationCancellation;
    public event Action<bool>? ScanningChanged;
    public bool TryBeginCode() => Interlocked.CompareExchange(ref _isHandlingCode, 1, 0) == 0;
    public void ResumeScanning()
    {
        Interlocked.Exchange(ref _isHandlingCode, 0);
        ScanningChanged?.Invoke(true);
    }

    public override void OnDisappearing()
    {
        if (_deviceInformationCancellation is { } cancellation)
        {
            cancellation.Cancel();
            _deviceInformationCancellation = null;
        }
        Interlocked.Exchange(ref _isHandlingCode, 0);
    }
    [RelayCommand]
    private async Task HandleCodeAsync(string? value)
    {
        ScanningChanged?.Invoke(false);

        if (!DeviceShareCode.TryParse(value, out var content))
        {
            await _interaction.AlertAsync(
                AppResources.Qr_Invalid,
                AppResources.Qr_InvalidShareCode,
                AppResources.Qr_ScanAgain);
            ResumeScanning();
            return;
        }

        var address = content.Address;
        if (_repository.Contains(address))
        {
            await _interaction.AlertAsync(
                AppResources.Device_AlreadyAdded,
                AppResources.Device_AlreadyInList,
                AppResources.Qr_ScanAgain);
            ResumeScanning();
            return;
        }

        var name = await _interaction.PromptAsync(
            AppResources.Device_NamePrompt,
            Localization.Format(nameof(AppResources.Qr_ImportPrompt), address),
            accept: AppResources.Common_Save,
            cancel: AppResources.Common_Cancel,
            placeholder: AppResources.Device_NameExample,
            maxLength: Math.Max(24, content.Name.Length),
            initialValue: content.Name);

        if (string.IsNullOrWhiteSpace(name))
        {
            ResumeScanning();
            return;
        }

        if (!_repository.TryAddShared(content with { Name = name.Trim() }))
        {
            await _interaction.AlertAsync(
                AppResources.Common_SaveFailed,
                AppResources.Device_NameRequiredOrDuplicate,
                AppResources.Qr_ScanAgain);
            ResumeScanning();
            return;
        }

        _interaction.ShowToast(AppResources.Qr_SavedReadingInformation);
        using var informationCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        _deviceInformationCancellation = informationCancellation;
        try
        {
            await _agpLight.GetControlProfileAsync(
                address, informationCancellation.Token, refreshLightState: true);
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            // Leaving the page preserves the already saved device without another dialog.
            if (!ReferenceEquals(_deviceInformationCancellation, informationCancellation))
                return;

            await _interaction.AlertAsync(
                AppResources.Qr_InformationNotRead,
                exception is OperationCanceledException
                    ? AppResources.Qr_InformationTimedOut
                    : Localization.Format(nameof(AppResources.Qr_InformationReadFailed), exception.Message),
                AppResources.Common_Ok);
        }
        finally
        {
            if (ReferenceEquals(_deviceInformationCancellation, informationCancellation))
                _deviceInformationCancellation = null;
        }

        await _navigator.GoToAsync("..");
    }
}
