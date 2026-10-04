using ZXing.Net.Maui;

namespace ArriLight;

public partial class QrCodeScanPage : ContentPage
{
    private readonly DeviceRepository _repository;
    private readonly AgpLightControlService _agpLight;
    private int _isHandlingCode;
    private bool _isTorchOn;
    private bool _hasCameraPermission;
    private bool _hasShownCameraPermissionMessage;
    private CancellationTokenSource? _deviceInformationCancellation;

    public QrCodeScanPage()
    {
        InitializeComponent();
        _repository = ServiceHelper.GetRequiredService<DeviceRepository>();
        _agpLight = ServiceHelper.GetRequiredService<AgpLightControlService>();
        BarcodeReader.Options = new BarcodeReaderOptions
        {
            Formats = BarcodeFormats.TwoDimensional,
            AutoRotate = true,
            Multiple = false
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        var status = await Permissions.RequestAsync<Permissions.Camera>();
        _hasCameraPermission = status == PermissionStatus.Granted;
        TorchButton.IsEnabled = _hasCameraPermission;
        BarcodeReader.IsDetecting = _hasCameraPermission;

        if (!_hasCameraPermission && !_hasShownCameraPermissionMessage)
        {
            _hasShownCameraPermissionMessage = true;
            await DisplayAlertAsync(
                AppResources.Qr_CameraUnavailable,
                AppResources.Qr_CameraPermissionMissing,
                AppResources.Common_Ok);
        }
    }

    protected override void OnDisappearing()
    {
        if (_deviceInformationCancellation is { } cancellation)
        {
            cancellation.Cancel();
            _deviceInformationCancellation = null;
            Interlocked.Exchange(ref _isHandlingCode, 0);
        }
        BarcodeReader.IsDetecting = false;
        BarcodeReader.IsTorchOn = false;
        base.OnDisappearing();
    }

    private void OnBarcodesDetected(
        object? sender,
        BarcodeDetectionEventArgs eventArgs)
    {
        if (Interlocked.CompareExchange(ref _isHandlingCode, 1, 0) != 0)
            return;

        var value = eventArgs.Results.FirstOrDefault()?.Value;
        MainThread.BeginInvokeOnMainThread(async () =>
            await HandleCodeAsync(value));
    }

    private async Task HandleCodeAsync(string? value)
    {
        BarcodeReader.IsDetecting = false;

        if (!DeviceShareCode.TryParse(value, out var content))
        {
            await DisplayAlertAsync(
                AppResources.Qr_Invalid,
                AppResources.Qr_InvalidShareCode,
                AppResources.Qr_ScanAgain);
            ResumeScanning();
            return;
        }

        var address = content.Address;
        if (_repository.Contains(address))
        {
            await DisplayAlertAsync(
                AppResources.Device_AlreadyAdded,
                AppResources.Device_AlreadyInList,
                AppResources.Qr_ScanAgain);
            ResumeScanning();
            return;
        }

        var name = await DisplayPromptAsync(
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
            await DisplayAlertAsync(
                AppResources.Common_SaveFailed,
                AppResources.Device_NameRequiredOrDuplicate,
                AppResources.Qr_ScanAgain);
            ResumeScanning();
            return;
        }

        ToastService.Show(AppResources.Qr_SavedReadingInformation);
        using var informationCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        _deviceInformationCancellation = informationCancellation;
        try
        {
            await _agpLight.GetControlProfileAsync(
                address, informationCancellation.Token, refreshLightState: true);
        }
        catch (Exception exception)
        {
            // Leaving the page preserves the already saved device without another dialog.
            if (!ReferenceEquals(_deviceInformationCancellation, informationCancellation))
                return;

            await DisplayAlertAsync(
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

        await Shell.Current.GoToAsync("..");
    }

    private void ResumeScanning()
    {
        Interlocked.Exchange(ref _isHandlingCode, 0);
        BarcodeReader.IsDetecting = _hasCameraPermission;
    }

    private async void OnSelectPhotoClicked(object? sender, EventArgs e)
    {
        if (Interlocked.CompareExchange(ref _isHandlingCode, 1, 0) != 0)
            return;

        BarcodeReader.IsDetecting = false;

        try
        {
            var photos = await MediaPicker.Default.PickPhotosAsync(
                new MediaPickerOptions
                {
                    Title = AppResources.Qr_SelectShareImage,
                    SelectionLimit = 1
                });
            var photo = photos.FirstOrDefault();

            if (photo is null)
            {
                ResumeScanning();
                return;
            }

            BarcodeReader.IsDetecting = false;
            await using var stream = await photo.OpenReadAsync();
            var results = await ZXing.Net.Maui.BarcodeReader.DecodeAsync(
                stream,
                new BarcodeReaderOptions
                {
                    Formats = BarcodeFormats.TwoDimensional,
                    AutoRotate = true,
                    TryHarder = true,
                    Multiple = false
                });

            var value = results?.FirstOrDefault()?.Value;
            if (string.IsNullOrWhiteSpace(value))
            {
                await DisplayAlertAsync(
                    AppResources.Qr_NotFound,
                    AppResources.Qr_ImageNotReadable,
                    AppResources.Qr_SelectAgain);
                ResumeScanning();
                return;
            }

            await HandleCodeAsync(value);
        }
        catch (FeatureNotSupportedException)
        {
            await DisplayAlertAsync(
                AppResources.Qr_ImageSelectionFailed,
                AppResources.Qr_ImageSelectionUnsupported,
                AppResources.Common_Ok);
            ResumeScanning();
        }
        catch (PermissionException)
        {
            await DisplayAlertAsync(
                AppResources.Qr_PhotoPermissionRequired,
                AppResources.Qr_AllowPhotos,
                AppResources.Common_Ok);
            ResumeScanning();
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync(
                AppResources.Qr_DecodeFailed,
                Localization.Format(nameof(AppResources.Qr_DecodeFailedDetail), exception.Message),
                AppResources.Common_Ok);
            ResumeScanning();
        }
    }

    private void OnTorchClicked(object? sender, EventArgs e)
    {
        _isTorchOn = !_isTorchOn;
        BarcodeReader.IsTorchOn = _isTorchOn;
        TorchButton.Text = _isTorchOn ? AppResources.Qr_TorchOff : AppResources.Qr_TorchOn;
    }
}
