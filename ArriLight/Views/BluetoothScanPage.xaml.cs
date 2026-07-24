using System.Collections.ObjectModel;

namespace ArriLight;

public partial class BluetoothScanPage : ContentPage
{
    private readonly BluetoothDiscoveryService _discoveryService;
    private readonly DeviceRepository _repository;
    private CancellationTokenSource? _scanCancellation;
    private bool _isScanning;
    private string _scanStatusText = "准备开始搜索";
    private string _emptyText = "正在等待设备广播…";

    public BluetoothScanPage()
    {
        InitializeComponent();
        _discoveryService = ServiceHelper.GetRequiredService<BluetoothDiscoveryService>();
        _repository = ServiceHelper.GetRequiredService<DeviceRepository>();
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
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (Devices.Any(item =>
                    string.Equals(item.Address, device.Address,
                        StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            Devices.Add(device);
            ScanStatusText = $"正在搜索，已发现 {Devices.Count} 台设备";
        });
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

        await _discoveryService.StopAsync();
        await Shell.Current.GoToAsync("..");
    }

}
