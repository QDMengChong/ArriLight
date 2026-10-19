using System.Diagnostics;
using ArriOpto.BLE.Ota.Protocol;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;

namespace ArriOpto.BLE.Ota;

/// <summary>
/// 面向 MAUI 应用的一次 OTA 升级用例入口。
/// </summary>
/// <remarks>
/// 调用方传入首次扫描选中的 <see cref="IDevice"/>。本类型负责加载固件、打开
/// OTA GATT 连接、选择协议状态机，以及处理 Single Bank APP→OTA 的重新扫描。
/// 它不负责平台权限、首次设备列表扫描或 UI 生命周期。
/// </remarks>
public sealed class PluginBleOtaClient
{
    private readonly IAdapter _adapter;

    /// <summary>使用应用共享的 Plugin.BLE adapter 创建客户端。</summary>
    public PluginBleOtaClient(IAdapter adapter)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
    }

    /// <summary>
    /// 将指定固件升级到目标设备。
    /// </summary>
    /// <param name="device">当前扫描得到的设备实例。</param>
    /// <param name="firmwarePath">SLB 或 Single Bank 固件路径。</param>
    /// <param name="options">协议、MTU、重新扫描和线程调度设置。</param>
    /// <param name="progress">可选进度接收器。</param>
    /// <param name="cancellationToken">取消文件、连接、扫描和协议等待。</param>
    /// <remarks>
    /// 同一 <see cref="IAdapter"/> 上不要并发执行扫描或其他 GATT 操作。若设备从
    /// APP 重启到 OTA，本方法会自动重新扫描并获取新的设备/特征对象。
    /// </remarks>
    public async Task UpgradeAsync(
        IDevice device,
        string firmwarePath,
        PluginBleOtaOptions? options = null,
        IProgress<OtaProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        options ??= new PluginBleOtaOptions();
        options.Validate();

        var firmware = await FirmwareLoader.LoadAsync(
                firmwarePath,
                cancellationToken,
                options.Protocol.Diagnostics)
            .ConfigureAwait(false);
        await UpgradeFirmwareAsync(device, firmware, options, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 从调用方提供的流读取固件并升级目标设备。
    /// </summary>
    /// <param name="device">当前扫描得到的设备实例。</param>
    /// <param name="firmwareStream">从当前位置读取到末尾的固件流。</param>
    /// <param name="fileName">用于识别格式的原始文件名，必须包含扩展名。</param>
    /// <param name="options">协议、诊断、重新扫描和线程调度设置。</param>
    /// <param name="progress">可选进度接收器。</param>
    /// <param name="cancellationToken">取消读取、连接、扫描和协议等待。</param>
    /// <remarks>
    /// 本方法不会释放 <paramref name="firmwareStream"/>。MAUI 调用方应使用
    /// <c>await using</c> 管理 <c>FileResult.OpenReadAsync()</c> 返回的流。
    /// </remarks>
    public async Task UpgradeAsync(
        IDevice device,
        Stream firmwareStream,
        string fileName,
        PluginBleOtaOptions? options = null,
        IProgress<OtaProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(firmwareStream);
        options ??= new PluginBleOtaOptions();
        options.Validate();

        var firmware = await FirmwareLoader.LoadAsync(
                firmwareStream,
                fileName,
                cancellationToken,
                options.Protocol.Diagnostics)
            .ConfigureAwait(false);
        await UpgradeFirmwareAsync(device, firmware, options, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task UpgradeFirmwareAsync(
        IDevice device,
        FirmwareImage firmware,
        PluginBleOtaOptions options,
        IProgress<OtaProgress>? progress,
        CancellationToken cancellationToken)
    {
        var diagnostics = options.Protocol.Diagnostics;
        var stopwatch = Stopwatch.StartNew();
        diagnostics?.Report(
            new OtaDiagnosticEvent(
                DateTimeOffset.UtcNow,
                OtaDiagnosticLevel.Information,
                "ota.upgrade.start",
                "ota",
                "开始执行 OTA 升级。")
            {
                DeviceId = device.Id.ToString(),
                Protocol = firmware.Protocol,
                Properties = new Dictionary<string, string>
                {
                    ["fileName"] = Path.GetFileName(firmware.Path),
                    ["kind"] = firmware.Kind.ToString(),
                    ["isSecure"] = firmware.IsSecure.ToString(),
                    ["length"] = firmware.Length.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)
                }
            });

        try
        {
            await ExecuteFirmwareAsync(
                    device,
                    firmware,
                    options,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
            stopwatch.Stop();
            diagnostics?.Report(
                new OtaDiagnosticEvent(
                    DateTimeOffset.UtcNow,
                    OtaDiagnosticLevel.Information,
                    "ota.upgrade.complete",
                    "ota",
                    "OTA 升级状态机执行完成。")
                {
                    DeviceId = device.Id.ToString(),
                    Protocol = firmware.Protocol,
                    State = OtaState.UpgradeCompleteWaitingDisconnect,
                    Elapsed = stopwatch.Elapsed
                });
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            diagnostics?.Report(
                new OtaDiagnosticEvent(
                    DateTimeOffset.UtcNow,
                    exception is OperationCanceledException
                        ? OtaDiagnosticLevel.Warning
                        : OtaDiagnosticLevel.Error,
                    "ota.upgrade.failed",
                    "ota",
                    "OTA 升级失败。")
                {
                    DeviceId = device.Id.ToString(),
                    Protocol = firmware.Protocol,
                    State = exception is OtaException otaException
                        ? otaException.State
                        : null,
                    Elapsed = stopwatch.Elapsed,
                    ExceptionType = exception.GetType().FullName,
                    ExceptionMessage = exception.Message
                });
            throw;
        }
    }

    private async Task ExecuteFirmwareAsync(
        IDevice device,
        FirmwareImage firmware,
        PluginBleOtaOptions options,
        IProgress<OtaProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(OtaProgress.Status(OtaState.Connecting, "Connecting to OTA device."));

        // 文件类型是协议选择的唯一来源，不能只根据设备当前暴露的服务猜测固件。
        if (firmware is SlbFirmwareImage slb)
        {
            await using var connection = await PluginBleConnection.OpenAsync(
                    _adapter,
                    device,
                    options.Protocol.RequestedMtu,
                    options.OperationDispatcher,
                    cancellationToken,
                    options.Protocol.Diagnostics, options.Transport)
                .ConfigureAwait(false);
            var session = new SlbOtaSession(connection, slb, options.Protocol, progress);
            await session.RunAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var singleBank = (SingleBankFirmwareImage)firmware;
        var otaDevice = device;
        await using (var initialConnection = await PluginBleConnection.OpenAsync(
                         _adapter,
                         device,
                         options.Protocol.RequestedMtu,
                         options.OperationDispatcher,
                         cancellationToken,
                         options.Protocol.Diagnostics, options.Transport)
                     .ConfigureAwait(false))
        {
            if (options.RequireSingleBankOtaMode && initialConnection.Mode != OtaGattMode.SingleBankOta)
            {
                throw new OtaException(OtaState.ErrorServiceState,
                    "Recovery requires a device already in Single Bank OTA mode.");
            }
            if (initialConnection.Mode == OtaGattMode.SingleBankApp)
            {
                var appSession = new SingleBankOtaSession(
                    initialConnection,
                    singleBank,
                    options.Protocol,
                    progress);
                var transition = await appSession.SwitchAppToOtaAsync(cancellationToken)
                    .ConfigureAwait(false);
                // 重启会使旧服务和 characteristic 全部失效，必须断开并重新扫描。
                await initialConnection.DisconnectAsync(cancellationToken).ConfigureAwait(false);
                otaDevice = await FindBootloaderAsync(transition, options, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (initialConnection.Mode == OtaGattMode.SingleBankOta)
            {
                var directSession = new SingleBankOtaSession(
                    initialConnection,
                    singleBank,
                    options.Protocol,
                    progress);
                await directSession.RunOtaModeAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            else
            {
                throw new OtaException(
                    OtaState.ErrorFileMismatch,
                    $"Single Bank firmware cannot be transferred through a {initialConnection.Mode} connection.");
            }
        }

        progress?.Report(
            OtaProgress.Status(
                OtaState.FoundOtaDevice,
                $"Found Single Bank OTA device {otaDevice.Name ?? otaDevice.Id.ToString()}."));
        await using var otaConnection = await PluginBleConnection.OpenAsync(
                _adapter,
                otaDevice,
                options.Protocol.RequestedMtu,
                options.OperationDispatcher,
                cancellationToken,
                options.Protocol.Diagnostics, options.Transport)
            .ConfigureAwait(false);
        var otaSession = new SingleBankOtaSession(
            otaConnection,
            singleBank,
            options.Protocol,
            progress);
        await otaSession.RunOtaModeAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>验证所选设备的 GATT 模式；不发送切换、擦除或固件写入命令。</summary>
    public async Task<bool> IsBootloaderAsync(IDevice device,
        PluginBleOtaOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new PluginBleOtaOptions();
        options.Validate();
        await using var connection = await PluginBleConnection.OpenAsync(_adapter, device,
            options.Protocol.RequestedMtu, options.OperationDispatcher, cancellationToken,
            options.Protocol.Diagnostics, options.Transport).ConfigureAwait(false);
        return connection.Mode == OtaGattMode.SingleBankOta;
    }

    /// <summary>Scan recovery loaders without connecting to the old application identity.</summary>
    public async Task<IReadOnlyList<IDevice>> ScanBootloadersAsync(
        PluginBleOtaOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new PluginBleOtaOptions();
        options.Validate();
        if (_adapter.IsScanning)
            await _adapter.StopScanningForDevicesAsync().ConfigureAwait(false);
        var found = new System.Collections.Concurrent.ConcurrentDictionary<Guid, IDevice>();
        bool IsLoader(IDevice device) => device.Name is not null && options.BootloaderNames.Contains(device.Name);
        void Discovered(object? sender, DeviceEventArgs args)
        {
            if (IsLoader(args.Device)) found[args.Device.Id] = args.Device;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.BootloaderScanTimeout);
        _adapter.DeviceDiscovered += Discovered;
        _adapter.DeviceAdvertised += Discovered;
        try
        {
            // No service filter: this loader advertises its name/manufacturer data only.
            await _adapter.StartScanningForDevicesAsync(new ScanFilterOptions(), IsLoader, false, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        finally
        {
            _adapter.DeviceDiscovered -= Discovered;
            _adapter.DeviceAdvertised -= Discovered;
            if (_adapter.IsScanning)
                await _adapter.StopScanningForDevicesAsync().ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return found.Values.OrderBy(device => device.Id).ToArray();
    }

    private async Task<IDevice> FindBootloaderAsync(
        SingleBankAppTransition transition,
        PluginBleOtaOptions options,
        CancellationToken cancellationToken)
    {
        var diagnostics = options.Protocol.Diagnostics;
        var scanStopwatch = Stopwatch.StartNew();
        diagnostics?.Report(
            new OtaDiagnosticEvent(
                DateTimeOffset.UtcNow,
                OtaDiagnosticLevel.Information,
                "bootloader.scan.start",
                "scan",
                "开始扫描 Single Bank OTA bootloader。")
            {
                Protocol = OtaProtocol.SingleBank,
                State = OtaState.AppModeEndWaitingScan,
                Properties = new Dictionary<string, string>
                {
                    ["timeoutSeconds"] = options.BootloaderScanTimeout.TotalSeconds.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    ["deviceProductId"] = transition.DeviceProductId ?? string.Empty,
                    ["deviceBootVersion"] = transition.DeviceBootVersion ?? string.Empty,
                    ["addressIncrementByte"] = transition.AddressIncrementByte.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)
                }
            });

        if (_adapter.IsScanning)
        {
            await _adapter.StopScanningForDevicesAsync().ConfigureAwait(false);
        }

        // DeviceDiscovered 事件与扫描 Task 存在竞争，因此使用异步 continuation 的
        // TCS 汇合两条完成路径，避免在 BLE 回调线程继续执行连接逻辑。
        var completion = new TaskCompletionSource<IDevice>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        bool IsMatch(IDevice candidate) =>
            options.BootloaderMatcher?.Invoke(candidate, transition)
            ?? (candidate.Name is not null && options.BootloaderNames.Contains(candidate.Name));

        void OnDeviceDiscovered(object? sender, DeviceEventArgs args)
        {
            if (IsMatch(args.Device))
            {
                completion.TrySetResult(args.Device);
            }
        }

        _adapter.DeviceDiscovered += OnDeviceDiscovered;
        using var scanSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        scanSource.CancelAfter(options.BootloaderScanTimeout);

        try
        {
            var scanTask = _adapter.StartScanningForDevicesAsync(
                new ScanFilterOptions(),
                IsMatch,
                false,
                scanSource.Token);
            var completed = await Task.WhenAny(completion.Task, scanTask).ConfigureAwait(false);
            if (completed == completion.Task)
            {
                await _adapter.StopScanningForDevicesAsync().ConfigureAwait(false);
                var foundDevice = await completion.Task.ConfigureAwait(false);
                scanStopwatch.Stop();
                diagnostics?.Report(
                    new OtaDiagnosticEvent(
                        DateTimeOffset.UtcNow,
                        OtaDiagnosticLevel.Information,
                        "bootloader.scan.found",
                        "scan",
                        "找到匹配的 Single Bank OTA bootloader。")
                    {
                        DeviceId = foundDevice.Id.ToString(),
                        Protocol = OtaProtocol.SingleBank,
                        State = OtaState.FoundOtaDevice,
                        Elapsed = scanStopwatch.Elapsed,
                        Properties = new Dictionary<string, string>
                        {
                            ["deviceName"] = foundDevice.Name ?? string.Empty
                        }
                    });
                return foundDevice;
            }

            await scanTask.ConfigureAwait(false);
            scanStopwatch.Stop();
            diagnostics?.Report(
                new OtaDiagnosticEvent(
                    DateTimeOffset.UtcNow,
                    OtaDiagnosticLevel.Error,
                    "bootloader.scan.not-found",
                    "scan",
                    "扫描结束但没有找到匹配的 OTA bootloader。")
                {
                    Protocol = OtaProtocol.SingleBank,
                    State = OtaState.ErrorCannotConnect,
                    Elapsed = scanStopwatch.Elapsed
                });
            throw new OtaException(
                OtaState.ErrorCannotConnect,
                "Single Bank bootloader device was not found before the scan ended.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            scanStopwatch.Stop();
            diagnostics?.Report(
                new OtaDiagnosticEvent(
                    DateTimeOffset.UtcNow,
                    OtaDiagnosticLevel.Error,
                    "bootloader.scan.timeout",
                    "scan",
                    "扫描 OTA bootloader 超时。")
                {
                    Protocol = OtaProtocol.SingleBank,
                    State = OtaState.ErrorCannotConnect,
                    Elapsed = scanStopwatch.Elapsed
                });
            throw new OtaException(
                OtaState.ErrorCannotConnect,
                $"Single Bank bootloader device was not found within {options.BootloaderScanTimeout.TotalSeconds:0.###} seconds.");
        }
        finally
        {
            _adapter.DeviceDiscovered -= OnDeviceDiscovered;
            if (_adapter.IsScanning)
            {
                await _adapter.StopScanningForDevicesAsync().ConfigureAwait(false);
            }
        }
    }
}
