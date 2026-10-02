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
                "相机不可用",
                "没有相机权限，无法实时扫码；你仍然可以从相册选择二维码图片。",
                "知道了");
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
                "二维码无效",
                "二维码不是有效的设备分享码，请使用设备信息页重新生成分享二维码。",
                "重新扫描");
            ResumeScanning();
            return;
        }

        var address = content.Address;
        if (_repository.Contains(address))
        {
            await DisplayAlertAsync(
                "设备已添加",
                "这个蓝牙设备已经在设备列表中。",
                "重新扫描");
            ResumeScanning();
            return;
        }

        var name = await DisplayPromptAsync(
            "命名设备",
            $"设备地址：{address}\n将保存设备名称、地址、绑定授权和能力，并连接设备读取其他资料。",
            accept: "保存",
            cancel: "取消",
            placeholder: "例如：客厅主灯",
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
                "保存失败",
                "设备名称不能为空，或该设备已经存在。",
                "重新扫描");
            ResumeScanning();
            return;
        }

        ToastService.Show("设备已保存，正在读取设备资料…");
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
                "资料暂未读取",
                exception is OperationCanceledException
                    ? "设备已添加，读取资料超时。打开设备控制页后可重新读取。"
                    : $"设备已添加，暂未读到设备资料：{exception.Message}。打开设备控制页后可重试。",
                "知道了");
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
                    Title = "选择设备分享二维码",
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
                    "未识别到二维码",
                    "所选图片中没有识别到二维码，请选择一张更清晰、完整的图片。",
                    "重新选择");
                ResumeScanning();
                return;
            }

            await HandleCodeAsync(value);
        }
        catch (FeatureNotSupportedException)
        {
            await DisplayAlertAsync(
                "无法选择图片",
                "当前设备不支持从相册选择图片。",
                "知道了");
            ResumeScanning();
        }
        catch (PermissionException)
        {
            await DisplayAlertAsync(
                "需要照片权限",
                "请在系统设置中允许访问照片后重试。",
                "知道了");
            ResumeScanning();
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync(
                "识别失败",
                $"没有完成图片识别：{exception.Message}",
                "知道了");
            ResumeScanning();
        }
    }

    private void OnTorchClicked(object? sender, EventArgs e)
    {
        _isTorchOn = !_isTorchOn;
        BarcodeReader.IsTorchOn = _isTorchOn;
        TorchButton.Text = _isTorchOn ? "关闭手电筒" : "打开手电筒";
    }
}
