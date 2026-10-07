using System.Collections.ObjectModel;

namespace ArriLight;

public partial class BluetoothScanPage : ContentPage
{
    private static readonly TimeSpan BindingTimeout = AgpLightControlService.BindingTimeout;

    private readonly BluetoothDiscoveryService _discoveryService;
    private readonly DeviceRepository _repository;
    private readonly AgpLightControlService _agpLight;
    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource? _bindingCancellation;
    private DateTimeOffset _bindingDeadline;
    private Task? _bindingCountdownTask;
    private bool _isScanning;
    private bool _isAdding;
    private string _scanStatusText = AppResources.Scan_Ready;
    private string _emptyText = AppResources.Scan_WaitingForAdvertisement;

#if ANDROID
    // 原生 Dialog 的 DimBehind 会覆盖 Shell 顶栏、底部标签栏和页面内容；
    // XAML 页面内的遮罩只能覆盖当前 ContentPage。
    private Android.App.Dialog? _bindingDialog;
    private Android.Widget.TextView? _bindingDialogStatus;
    private Android.Widget.TextView? _bindingDialogCountdown;
#endif

    // 每个设备以“首次发现时的 RSSI”作为固定的排序依据，
    // 后续广播只更新行内强度值，不再改变排序位置。
    private readonly Dictionary<string, int> _firstRssiByAddress =
        new(StringComparer.OrdinalIgnoreCase);

    public BluetoothScanPage()
    {
        InitializeComponent();
        _discoveryService = ServiceHelper.GetRequiredService<BluetoothDiscoveryService>();
        _repository = ServiceHelper.GetRequiredService<DeviceRepository>();
        _agpLight = ServiceHelper.GetRequiredService<AgpLightControlService>();
        BindingContext = this;
    }

    public ObservableCollection<DiscoveredBluetoothDevice> Devices { get; } = [];

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (_isScanning == value)
                return;

            _isScanning = value;
            OnPropertyChanged();
        }
    }

    public string ScanStatusText
    {
        get => _scanStatusText;
        private set
        {
            if (_scanStatusText == value)
                return;

            _scanStatusText = value;
            OnPropertyChanged();
        }
    }

    public string EmptyText
    {
        get => _emptyText;
        private set
        {
            if (_emptyText == value)
                return;

            _emptyText = value;
            OnPropertyChanged();
        }
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _discoveryService.DeviceFound += OnDeviceFound;
        _ = StartScanAsync();
    }

    protected override void OnDisappearing()
    {
        _discoveryService.DeviceFound -= OnDeviceFound;
        _scanCancellation?.Cancel();
        CancelBinding();
        // 扫描由 BluetoothDiscoveryService 按服务级窗口管理；离开添加页
        // 只取消本页的等待，不停止可能仍被主页使用的后台扫描。
        base.OnDisappearing();
    }

    private async void OnRefreshClicked(object? sender, EventArgs e) =>
        await StartScanAsync();

    private async Task StartScanAsync()
    {
        if (IsScanning)
            return;

        Devices.Clear();
        _firstRssiByAddress.Clear();
        EmptyText = AppResources.Scan_WaitingForAdvertisement;
        ScanStatusText = AppResources.Scan_KeepPowered;
        IsScanning = true;
        _scanCancellation?.Dispose();
        _scanCancellation = new CancellationTokenSource();

        try
        {
            await _discoveryService.ScanAsync(_scanCancellation.Token);
            ScanStatusText = Localization.Format(nameof(AppResources.Scan_CompletedCount), Devices.Count);
            EmptyText = AppResources.Scan_NoAvailableDevices;
            await ScrollToTopAsync();
        }
        catch (OperationCanceledException)
        {
            ScanStatusText = AppResources.Scan_Stopped;
        }
        catch (BluetoothDiscoveryException exception)
        {
            ScanStatusText = AppResources.Scan_TemporarilyUnavailable;
            EmptyText = exception.Message;
            await DisplayAlertAsync(AppResources.Scan_Unavailable, exception.Message, AppResources.Common_Ok);
        }
        catch (Exception exception)
        {
            ScanStatusText = AppResources.Scan_Failed;
            EmptyText = AppResources.Scan_TryLater;
            await DisplayAlertAsync(
                AppResources.Scan_Failed,
                Localization.Format(nameof(AppResources.Scan_FailedDetail), exception.Message),
                AppResources.Common_Ok);
        }
        finally
        {
            IsScanning = false;
        }
    }

    private void OnDeviceFound(
        object? sender,
        DiscoveredBluetoothDevice device)
    {
        // AgpAdvertisement 按 AGP 128-bit Service Data UUID 解析连接前状态；
        // 仅允许设备端明确打开配对窗口时进入“添加设备”列表。
        if (!device.IsPairingOpen)
            return;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            // 已发现过的设备：保持首次发现时的名称、信号强度和排序完全不变，
            // 防止信号反复跳动导致列表闪烁。
            if (Devices.Any(existing =>
                    string.Equals(
                        existing.Address,
                        device.Address,
                        StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            // 新设备：记录首次发现时的 RSSI 作为固定排序依据，
            // 之后即使信号变化也不更新。
            _firstRssiByAddress[device.Address] = device.Rssi;
            var firstRssi = device.Rssi;

            // 集合始终保持按“首次发现时的 RSSI”降序：越大越靠前。
            var insertAt = 0;
            while (insertAt < Devices.Count)
            {
                var sortKey = _firstRssiByAddress.TryGetValue(
                    Devices[insertAt].Address, out var key)
                    ? key
                    : Devices[insertAt].Rssi;
                if (sortKey <= firstRssi)
                    break;
                insertAt++;
            }

            Devices.Insert(insertAt, device);
            ScanStatusText = Localization.Format(nameof(AppResources.Scan_FoundCount), Devices.Count);
            _ = ScrollToTopAsync();
        });
    }

    // 扫描结束后把列表定位到最顶端。
    private async Task ScrollToTopAsync()
    {
        if (Devices.Count == 0)
            return;

        // 等待列表布局稳定，避免在布局未完成时滚动导致错位。
        await Task.Delay(150);

#if ANDROID
        // MAUI 的 CollectionView.ScrollTo(0, Start) 在 Android 上存在已知 bug
        // （dotnet/maui#27117），会滚动到第二个元素而不是第一个。
        // 这里直接操作平台 RecyclerView，强制滚动到 position 0。
        if (DeviceCollection.Handler?.PlatformView
                is AndroidX.RecyclerView.Widget.RecyclerView recyclerView &&
            recyclerView.GetLayoutManager()
                is AndroidX.RecyclerView.Widget.LinearLayoutManager layoutManager)
        {
            layoutManager.ScrollToPositionWithOffset(0, 0);
            return;
        }
#endif

        DeviceCollection.ScrollTo(
            0, position: ScrollToPosition.Start, animate: false);
    }

    private async void OnDeviceSelected(
        object? sender,
        SelectionChangedEventArgs eventArgs)
    {
        if (eventArgs.CurrentSelection.FirstOrDefault()
            is not DiscoveredBluetoothDevice device)
        {
            return;
        }

        DeviceCollection.SelectedItem = null;
        await AddDeviceAsync(device.Name, device.Address);
    }

    private async Task AddDeviceAsync(string suggestedName, string address)
    {
        if (_isAdding)
            return;

        _isAdding = true;
        _scanCancellation?.Cancel();
        await _discoveryService.StopAsync();

        try
        {
            await AddDeviceCoreAsync(suggestedName, address);
        }
        finally
        {
            _isAdding = false;
        }
    }

    private async Task AddDeviceCoreAsync(string suggestedName, string address)
    {
        if (_repository.Contains(address))
        {
            await DisplayAlertAsync(
                AppResources.Device_AlreadyAdded,
                AppResources.Device_AlreadyInList,
                AppResources.Common_Ok);
            return;
        }

        var name = await DisplayPromptAsync(
            AppResources.Device_NamePrompt,
            Localization.Format(nameof(AppResources.Device_AddressDisplay), address),
            accept: AppResources.Common_Save,
            cancel: AppResources.Common_Cancel,
            placeholder: AppResources.Device_NameExample,
            initialValue: suggestedName,
            maxLength: 24);

        if (string.IsNullOrWhiteSpace(name))
            return;

        if (!_repository.TryAdd(name, address))
        {
            await DisplayAlertAsync(
                AppResources.Common_SaveFailed,
                AppResources.Device_NameRequiredOrDuplicate,
                AppResources.Common_Ok);
            return;
        }

        using var bindingCancellation = new CancellationTokenSource(BindingTimeout);
        _bindingCancellation = bindingCancellation;
        _bindingDeadline = DateTimeOffset.UtcNow + BindingTimeout;
        var bindingSucceeded = false;
        try
        {
            await _agpLight.BindAsync(
                address,
                (stage, cancellationToken) => UpdateBindingProgressAsync(
                    stage, cancellationToken, bindingCancellation.Token),
                bindingCancellation.Token);
            bindingSucceeded = true;
        }
        catch (OperationCanceledException)
            when (bindingCancellation.IsCancellationRequested)
        {
            ToastService.ShowForDuration(
                DateTimeOffset.UtcNow >= _bindingDeadline
                    ? AppResources.Pairing_TimedOut
                    : AppResources.Pairing_Cancelled,
                TimeSpan.FromSeconds(1));
        }
        catch (Exception exception)
        {
            ToastService.ShowForDuration(
                Localization.Format(nameof(AppResources.Device_AddFailedDetail), exception.Message),
                TimeSpan.FromSeconds(1));
        }
        finally
        {
            bindingCancellation.Cancel();
            // 先关闭提示，再等待倒计时任务收尾。
            await HideBindingPromptAsync();
            if (_bindingCountdownTask is not null)
                await _bindingCountdownTask;
            _bindingCountdownTask = null;
            _bindingCancellation = null;
        }

        if (!bindingSucceeded)
        {
            _repository.ClearToken(address);
            _repository.Remove(address);
            return;
        }

        ToastService.ShowForDuration(
            AppResources.Device_AddSucceeded,
            TimeSpan.FromSeconds(1));

        await _discoveryService.StopAsync();
        await Shell.Current.GoToAsync("//DevicesPage");
    }

    private async Task UpdateBindingProgressAsync(
        AgpBindingStage stage,
        CancellationToken cancellationToken,
        CancellationToken countdownCancellationToken)
    {
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stage == AgpBindingStage.Connecting)
            {
#if ANDROID
                ShowBindingDialog();
#else
                BindingPromptOverlay.IsVisible = true;
#endif
                UpdateBindingCountdown((int)BindingTimeout.TotalSeconds);
            }

            var text = stage switch
            {
                AgpBindingStage.Connecting => AppResources.Pairing_Connecting,
                AgpBindingStage.WaitingForConnection =>
                    AppResources.Pairing_Retrying,
                AgpBindingStage.ReceivingToken => AppResources.Pairing_ReadingInformation,
                _ => string.Empty
            };
#if ANDROID
            if (_bindingDialogStatus is not null)
                _bindingDialogStatus.Text = text;
#endif
            BindingPromptStatus.Text = text;
        });

        if (stage == AgpBindingStage.Connecting)
        {
            // 倒计时属于页面，必须使用页面自己的令牌。服务返回时会释放其
            // 内部关联令牌源，此后页面取消已无法传递给那个关联令牌。
            _bindingCountdownTask = RunBindingCountdownAsync(countdownCancellationToken);
        }
    }

    private async Task RunBindingCountdownAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var seconds = Math.Max(0,
                    (int)Math.Ceiling((_bindingDeadline - DateTimeOffset.UtcNow).TotalSeconds));
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    if (!cancellationToken.IsCancellationRequested)
                        UpdateBindingCountdown(seconds);
                });

                if (seconds == 0)
                    break;

                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // 绑定成功、超时或用户离开页面都会结束倒计时。
        }
    }

    private Task HideBindingPromptAsync() =>
        MainThread.InvokeOnMainThreadAsync(HideBindingPrompt);

    private void HideBindingPrompt()
    {
#if ANDROID
        _bindingDialog?.Dismiss();
        _bindingDialog?.Dispose();
        _bindingDialog = null;
        _bindingDialogStatus = null;
        _bindingDialogCountdown = null;
#endif
        BindingPromptOverlay.IsVisible = false;
    }

    private void CancelBinding()
    {
        _bindingCancellation?.Cancel();
        // 取消按钮和页面离开均在 UI 线程执行，立即收起原生 Dialog/页面遮罩。
        HideBindingPrompt();
    }

    private void OnCancelBindingClicked(object? sender, EventArgs e) =>
        CancelBinding();

    private void UpdateBindingCountdown(int seconds)
    {
        var text = Localization.Format(nameof(AppResources.Pairing_TimeRemaining), seconds);
#if ANDROID
        if (_bindingDialogCountdown is not null)
        {
            _bindingDialogCountdown.Text = text;
            return;
        }
#endif
        BindingPromptCountdown.Text = text;
    }

#if ANDROID
    private void ShowBindingDialog()
    {
        if (_bindingDialog?.IsShowing == true)
            return;

        var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
        if (activity is null)
        {
            // 仅在极早期生命周期无法取得 Activity 时回退到页内遮罩。
            BindingPromptOverlay.IsVisible = true;
            return;
        }

        var context = activity;
        var horizontalPadding = ToPixels(context, 28);
        var verticalPadding = ToPixels(context, 22);
        var card = new Android.Widget.LinearLayout(context)
        {
            Orientation = Android.Widget.Orientation.Vertical
        };
        card.SetPadding(horizontalPadding, verticalPadding, horizontalPadding, verticalPadding);
        card.Background = CreateRoundedBackground(
            Android.Graphics.Color.White,
            ToPixels(context, 20));

        var title = CreateDialogText(
            context,
            AppResources.Pairing_InProgress,
            20,
            Android.Graphics.Color.ParseColor("#15213B"));
        title.Typeface = Android.Graphics.Typeface.DefaultBold;
        card.AddView(title, CreateDialogLayoutParams(context, bottomMarginDp: 14));

        _bindingDialogStatus = CreateDialogText(
            context,
            string.Empty,
            14,
            Android.Graphics.Color.ParseColor("#667085"));
        card.AddView(_bindingDialogStatus, CreateDialogLayoutParams(context, bottomMarginDp: 14));

        var message = CreateDialogText(
            context,
            AppResources.Pairing_Instructions,
            14,
            Android.Graphics.Color.ParseColor("#667085"));
        card.AddView(message, CreateDialogLayoutParams(context, bottomMarginDp: 18));

        _bindingDialogCountdown = CreateDialogText(
            context,
            string.Empty,
            16,
            Android.Graphics.Color.ParseColor("#2E5CF2"));
        _bindingDialogCountdown.Typeface = Android.Graphics.Typeface.DefaultBold;
        card.AddView(_bindingDialogCountdown, CreateDialogLayoutParams(context, bottomMarginDp: 20));

        var cancel = new Android.Widget.Button(context)
        {
            Text = AppResources.Pairing_CancelAdding
        };
        cancel.SetTextColor(Android.Graphics.Color.White);
        cancel.TextSize = 16;
        cancel.Background = CreateRoundedBackground(
            Android.Graphics.Color.ParseColor("#2E5CF2"),
            ToPixels(context, 12));
        cancel.Click += OnCancelBindingClicked;
        card.AddView(cancel, new Android.Widget.LinearLayout.LayoutParams(
            Android.Views.ViewGroup.LayoutParams.MatchParent,
            ToPixels(context, 52)));

        var container = new Android.Widget.FrameLayout(context);
        container.AddView(
            card,
            new Android.Widget.FrameLayout.LayoutParams(
                Android.Views.ViewGroup.LayoutParams.MatchParent,
                Android.Views.ViewGroup.LayoutParams.WrapContent)
            {
                LeftMargin = ToPixels(context, 28),
                RightMargin = ToPixels(context, 28),
                Gravity = Android.Views.GravityFlags.Center
            });

        var dialog = new Android.App.Dialog(context);
        dialog.RequestWindowFeature((int)Android.Views.WindowFeatures.NoTitle);
        dialog.SetContentView(container);
        dialog.SetCancelable(false);
        dialog.SetCanceledOnTouchOutside(false);
        dialog.Show();

        if (dialog.Window is { } window)
        {
            window.SetBackgroundDrawable(
                new Android.Graphics.Drawables.ColorDrawable(Android.Graphics.Color.Transparent));
            window.AddFlags(Android.Views.WindowManagerFlags.DimBehind);
            if (window.Attributes is { } attributes)
            {
                attributes.DimAmount = 0.55f;
                window.Attributes = attributes;
            }
            window.SetLayout(
                Android.Views.ViewGroup.LayoutParams.MatchParent,
                Android.Views.ViewGroup.LayoutParams.MatchParent);
        }

        _bindingDialog = dialog;
    }

    private static Android.Widget.LinearLayout.LayoutParams CreateDialogLayoutParams(
        Android.Content.Context context,
        int bottomMarginDp) =>
        new(
            Android.Views.ViewGroup.LayoutParams.MatchParent,
            Android.Views.ViewGroup.LayoutParams.WrapContent)
        {
            BottomMargin = ToPixels(context, bottomMarginDp)
        };

    private static Android.Widget.TextView CreateDialogText(
        Android.Content.Context context,
        string text,
        float sizeSp,
        Android.Graphics.Color color)
    {
        var label = new Android.Widget.TextView(context)
        {
            Text = text,
            Gravity = Android.Views.GravityFlags.Center,
            TextSize = sizeSp
        };
        label.SetTextColor(color);
        return label;
    }

    private static Android.Graphics.Drawables.GradientDrawable CreateRoundedBackground(
        Android.Graphics.Color color,
        int cornerRadiusPixels)
    {
        var drawable = new Android.Graphics.Drawables.GradientDrawable();
        drawable.SetColor(color);
        drawable.SetCornerRadius(cornerRadiusPixels);
        return drawable;
    }

    private static int ToPixels(Android.Content.Context context, int dp) =>
        (int)Math.Round(
            dp * (context.Resources?.DisplayMetrics?.Density ?? 1f));
#endif

}
