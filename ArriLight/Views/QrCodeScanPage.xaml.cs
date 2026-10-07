using ZXing.Net.Maui;

namespace ArriLight;

public partial class QrCodeScanPage : ContentPage
{
    public QrCodeScanPageViewModel ViewModel { get; }

    private bool _isTorchOn;
    private bool _hasCameraPermission;
    private bool _hasShownCameraPermissionMessage;

    public QrCodeScanPage()
    {
        InitializeComponent();
        ViewModel = ServiceHelper.GetRequiredService<QrCodeScanPageViewModel>();
        BindingContext = ViewModel;
        ViewModel.ScanningChanged += scanning => BarcodeReader.IsDetecting = scanning && _hasCameraPermission;

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
        ViewModel.OnDisappearing();
        BarcodeReader.IsDetecting = false;
        BarcodeReader.IsTorchOn = false;
        base.OnDisappearing();
    }

    private void OnBarcodesDetected(
        object? sender,
        BarcodeDetectionEventArgs eventArgs)
    {
        if (!ViewModel.TryBeginCode())
            return;

        var value = eventArgs.Results.FirstOrDefault()?.Value;
        MainThread.BeginInvokeOnMainThread(async () =>
            await ViewModel.HandleCodeCommand.ExecuteAsync(value));
    }

    private void ResumeScanning() => ViewModel.ResumeScanning();

    private async void OnSelectPhotoClicked(object? sender, EventArgs e)
    {
        if (!ViewModel.TryBeginCode())
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

            await ViewModel.HandleCodeCommand.ExecuteAsync(value);
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
