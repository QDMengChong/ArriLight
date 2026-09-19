using System.Collections.ObjectModel;

namespace ArriLight;

public partial class BluetoothScanPage : ContentPage
{
    private readonly BluetoothDiscoveryService _discoveryService;
    private readonly DeviceRepository _repository;
    private readonly AgpLightControlService _agpLight;
    private CancellationTokenSource? _scanCancellation;
    private bool _isScanning;
    private string _scanStatusText = "准备开始搜索";
    private string _emptyText = "正在等待设备广播…";

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
        _ = _discoveryService.StopAsync();
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
        EmptyText = "正在等待设备广播…";
        ScanStatusText = "正在搜索，请让灯具保持通电";
        IsScanning = true;
        _scanCancellation?.Dispose();
        _scanCancellation = new CancellationTokenSource();

        try
        {
            await _discoveryService.ScanAsync(_scanCancellation.Token);
            ScanStatusText = $"搜索完成，共发现 {Devices.Count} 台设备";
            EmptyText = "没有发现可添加的设备";
            await ScrollToTopAsync();
        }
        catch (OperationCanceledException)
        {
            ScanStatusText = "搜索已停止";
        }
        catch (BluetoothDiscoveryException exception)
        {
            ScanStatusText = "暂时无法搜索设备";
            EmptyText = exception.Message;
            await DisplayAlertAsync("无法搜索", exception.Message, "知道了");
        }
        catch (Exception exception)
        {
            ScanStatusText = "搜索失败";
            EmptyText = "请稍后重新搜索";
            await DisplayAlertAsync(
                "搜索失败",
                $"没有完成蓝牙搜索：{exception.Message}",
                "知道了");
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
            ScanStatusText = $"正在搜索，已发现 {Devices.Count} 台设备";
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
        if (_repository.Contains(address))
        {
            await DisplayAlertAsync(
                "设备已添加",
                "这个蓝牙设备已经在设备列表中。",
                "知道了");
            return;
        }

        var name = await DisplayPromptAsync(
            "命名设备",
            $"设备地址：{address}",
            accept: "保存",
            cancel: "取消",
            placeholder: "例如：客厅主灯",
            initialValue: suggestedName,
            maxLength: 24);

        if (string.IsNullOrWhiteSpace(name))
            return;

        if (!_repository.TryAdd(name, address))
        {
            await DisplayAlertAsync(
                "保存失败",
                "设备名称不能为空，或该设备已经存在。",
                "知道了");
            return;
        }

        try
        {
            await _agpLight.BindAsync(address);
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync(
                "绑定未完成",
                $"设备已保存，但尚未完成物理确认：{exception.Message}",
                "知道了");
        }

        await _discoveryService.StopAsync();
        await Shell.Current.GoToAsync("..");
    }

}
