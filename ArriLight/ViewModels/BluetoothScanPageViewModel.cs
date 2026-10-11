using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace ArriLight.ViewModels;

public partial class BluetoothScanPageViewModel : ViewModelBase
{
    private static readonly TimeSpan BindingTimeout = AgpLightControlService.BindingTimeout;

    private readonly BluetoothDiscoveryService _discoveryService;
    private readonly DeviceRepository _repository;
    private readonly AgpLightControlService _agpLight;
    private CancellationTokenSource? _scanCancellation;
    private Task? _scanTask;
    private bool _isVisible;
    private CancellationTokenSource? _bindingCancellation;
    private DateTimeOffset _bindingDeadline;
    private Task? _bindingCountdownTask;
    [ObservableProperty] private bool _isScanning;
    private bool _isAdding;
    [ObservableProperty]
    private string _scanStatusText = AppResources.Scan_Ready;
    [ObservableProperty]
    private string _emptyText = AppResources.Scan_WaitingForAdvertisement;

    // 每个设备以“首次发现时的 RSSI”作为固定的排序依据，
    // 后续广播只更新行内强度值，不再改变排序位置。
    private readonly Dictionary<string, int> _firstRssiByAddress =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly IUiDispatcher _dispatcher;
    private readonly IUserInteraction _interaction;
    private readonly IAppNavigator _navigator;
    [ObservableProperty] private bool _isBindingPromptVisible;
    [ObservableProperty] private string _bindingStatus = string.Empty;
    [ObservableProperty] private string _bindingCountdown = string.Empty;
    public event Func<Task>? ScrollToTopRequested;
    public BluetoothScanPageViewModel(BluetoothDiscoveryService discoveryService, DeviceRepository repository,
        AgpLightControlService agpLight, IUiDispatcher dispatcher, IUserInteraction interaction, IAppNavigator navigator)
    { _discoveryService = discoveryService; _repository = repository; _agpLight = agpLight;
      _dispatcher = dispatcher; _interaction = interaction; _navigator = navigator; }

    public ObservableCollection<DiscoveredBluetoothDevice> Devices { get; } = [];

    public override Task OnAppearingAsync()
    {
        _isVisible = true;
        _discoveryService.DeviceFound -= OnDeviceFound;
        _discoveryService.DeviceFound += OnDeviceFound;
        _ = ResumeScanAsync();
        return Task.CompletedTask;
    }

    private async Task ResumeScanAsync()
    {
        if (_scanTask is { IsCompleted: false } previous) await previous;
        if (_isVisible) await StartScanAsync();
    }

    public override void OnDisappearing()
    {
        _isVisible = false;
        _discoveryService.DeviceFound -= OnDeviceFound;
        _scanCancellation?.Cancel();
        CancelBinding();
    }

    [RelayCommand]
    private async Task StartScanAsync()
    {
        if (IsScanning || !_isVisible)
            return;
        _scanTask = RunScanAsync();
        await _scanTask;
    }

    private async Task RunScanAsync()
    {
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
            await _interaction.AlertAsync(AppResources.Scan_Unavailable, exception.Message, AppResources.Common_Ok);
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            ScanStatusText = AppResources.Scan_Failed;
            EmptyText = AppResources.Scan_TryLater;
            await _interaction.AlertAsync(
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
        // AGP 设备必须明确打开配对窗口；Mesh 设备由 SIG Mesh 广播服务 UUID 识别。
        if (!device.IsPairingOpen && !device.IsMesh)
            return;

        _dispatcher.Post(() =>
        {
            if (!_isVisible || !IsScanning) return;
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
    private Task ScrollToTopAsync() => ScrollToTopRequested?.Invoke() ?? Task.CompletedTask;

    [RelayCommand]
    private Task AddDeviceSelectionAsync(DiscoveredBluetoothDevice device) => AddDeviceAsync(device);

    private async Task AddDeviceAsync(DiscoveredBluetoothDevice device)
    {
        if (_isAdding)
            return;

        _isAdding = true;
        try
        {
            _scanCancellation?.Cancel();
            await _discoveryService.StopAsync();
            await AddDeviceCoreAsync(device);
        }
        finally
        {
            _isAdding = false;
        }
    }

    private async Task AddDeviceCoreAsync(DiscoveredBluetoothDevice device)
    {
        if (device.IsMesh)
        {
            if (_repository.Contains(device.Address))
            {
                await _interaction.AlertAsync(
                    AppResources.Device_AlreadyAdded,
                    AppResources.Device_AlreadyInList,
                    AppResources.Common_Ok);
                return;
            }

            var meshName = await _interaction.PromptAsync(
                AppResources.Device_NamePrompt,
                Localization.Format(nameof(AppResources.Device_AddressDisplay), device.Address),
                accept: AppResources.Common_Save,
                cancel: AppResources.Common_Cancel,
                placeholder: AppResources.Device_NameExample,
                initialValue: device.Name,
                maxLength: 24);
            if (string.IsNullOrWhiteSpace(meshName))
                return;

            await _navigator.GoToAsync(
                $"MeshProvisioningPage?address={Uri.EscapeDataString(device.Address)}" +
                $"&name={Uri.EscapeDataString(meshName.Trim())}");
            return;
        }

        var suggestedName = device.Name;
        var address = device.Address;
        if (_repository.Contains(address))
        {
            await _interaction.AlertAsync(
                AppResources.Device_AlreadyAdded,
                AppResources.Device_AlreadyInList,
                AppResources.Common_Ok);
            return;
        }

        var name = await _interaction.PromptAsync(
            AppResources.Device_NamePrompt,
            Localization.Format(nameof(AppResources.Device_AddressDisplay), address),
            accept: AppResources.Common_Save,
            cancel: AppResources.Common_Cancel,
            placeholder: AppResources.Device_NameExample,
            initialValue: suggestedName,
            maxLength: 24);

        if (string.IsNullOrWhiteSpace(name))
            return;

        if (!_repository.TryAdd(
                name,
                address,
                BluetoothDeviceProtocol.Agp))
        {
            await _interaction.AlertAsync(
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
            _interaction.ShowToastForDuration(
                DateTimeOffset.UtcNow >= _bindingDeadline
                    ? AppResources.Pairing_TimedOut
                    : AppResources.Pairing_Cancelled,
                TimeSpan.FromSeconds(1));
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            _interaction.ShowToastForDuration(
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

        _interaction.ShowToastForDuration(
            AppResources.Device_AddSucceeded,
            TimeSpan.FromSeconds(1));

        await _discoveryService.StopAsync();
        await _navigator.GoToAsync("//DevicesPage");
    }

    private async Task UpdateBindingProgressAsync(
        AgpBindingStage stage,
        CancellationToken cancellationToken,
        CancellationToken countdownCancellationToken)
    {
        await _dispatcher.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stage == AgpBindingStage.Connecting)
            {
IsBindingPromptVisible = true;
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

            BindingStatus = text;
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
                await _dispatcher.InvokeAsync(() =>
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
        _dispatcher.InvokeAsync(HideBindingPrompt);

    private void HideBindingPrompt() => IsBindingPromptVisible = false;

    [RelayCommand]
    private void CancelBinding()
    {
        _bindingCancellation?.Cancel();
        // 取消按钮和页面离开均在 UI 线程执行，立即收起原生 Dialog/页面遮罩。
        HideBindingPrompt();
    }

    private void UpdateBindingCountdown(int seconds) => BindingCountdown = Localization.Format(nameof(AppResources.Pairing_TimeRemaining), seconds);

}
