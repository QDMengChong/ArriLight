using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ArriOpto.BLE.Ota;
using Plugin.BLE.Abstractions.Contracts;

namespace ArriLight;

public enum FirmwareSourceKind
{
    Online,
    Local
}

public enum FirmwareUpdateStage
{
    Checking,
    Downloading,
    Connecting,
    Transferring,
    Completed
}

public sealed record FirmwareUpdateProgress(
    FirmwareUpdateStage Stage,
    double Percentage,
    string Message,
    OtaState? OtaState = null);

public sealed record FirmwareCheckResult(
    string CurrentVersion,
    FirmwareCandidate? Candidate,
    bool IsUpdateAvailable);

public sealed class FirmwareCandidate
{
    internal FirmwareCandidate(
        FirmwareSourceKind sourceKind,
        OtaProtocol protocol,
        string version,
        string fileName,
        string releaseNotes,
        string? productId,
        Uri? downloadUri,
        string? sha256,
        IFirmwareFile? localFile)
    {
        SourceKind = sourceKind;
        Protocol = protocol;
        Version = version;
        FileName = fileName;
        ReleaseNotes = releaseNotes;
        ProductId = productId;
        DownloadUri = downloadUri;
        Sha256 = sha256;
        LocalFile = localFile;
    }

    public FirmwareSourceKind SourceKind { get; }
    public OtaProtocol Protocol { get; }
    public string Version { get; }
    public string FileName { get; }
    public string ReleaseNotes { get; }
    public string? ProductId { get; }
    public Uri? DownloadUri { get; }
    public string? Sha256 { get; }
    internal IFirmwareFile? LocalFile { get; }

    public string SourceText =>
        SourceKind == FirmwareSourceKind.Online ? AppResources.Firmware_OnlineSource : AppResources.Firmware_LocalSource;

    public string ProtocolText =>
        Protocol == OtaProtocol.Slb ? "SLB" : "Single Bank";
}

public sealed class FirmwareUpdateConfiguration(AppSettingsService? appSettingsService = null)
{
    private AppSettingsService? _appSettings = appSettingsService;

    public string? ListWorkerUrl { get; init; }

    public string? GetWorkerUrl { get; init; }

    public int RequestedMtu { get; init; } = 517;
    public PluginBleOtaTransport? OtaTransport { get; init; }

    // AES Key 不持久化。若产品使用加密 SLB，应由安全配置在运行时注入。
    public string? AesKeyHex { get; set; }

    public async Task<(string ListWorkerUrl, string GetWorkerUrl)> LoadWorkerUrlsAsync(
        CancellationToken cancellationToken = default)
    {
        var listWorkerUrl = ListWorkerUrl;
        var getWorkerUrl = GetWorkerUrl;
        if (string.IsNullOrWhiteSpace(listWorkerUrl) ||
            string.IsNullOrWhiteSpace(getWorkerUrl))
        {
            var saved = await (_appSettings ??= new AppSettingsService()).LoadWorkerUrlsAsync(cancellationToken);
            listWorkerUrl = string.IsNullOrWhiteSpace(listWorkerUrl)
                ? saved.ListWorkerUrl
                : listWorkerUrl;
            getWorkerUrl = string.IsNullOrWhiteSpace(getWorkerUrl)
                ? saved.GetWorkerUrl
                : getWorkerUrl;
        }

        if (string.IsNullOrWhiteSpace(listWorkerUrl) ||
            string.IsNullOrWhiteSpace(getWorkerUrl))
        {
            throw new InvalidOperationException(
                Localization.Format(nameof(AppResources.Firmware_ServiceConfigurationMissing), AppSettingsService.FileName));
        }

        return (listWorkerUrl.Trim(), getWorkerUrl.Trim());
    }
}

public sealed class FirmwareUpdateService(
    HttpClient httpClient,
    PluginBleOtaClient otaClient,
    BluetoothConnectionService connectionService,
    AgpLightControlService agpLightControlService,
    AppDiagnosticService diagnostics,
    FirmwareUpdateConfiguration configuration)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly Regex FirmwareVersionPattern = new(
        @"(?<!\d)v?(\d+(?:\.\d+){1,3})(?!\d)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public async Task<FirmwareCheckResult> CheckOnlineAsync(
        string hardwareVersion,
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(hardwareVersion))
        {
            throw new ArgumentException(
                AppResources.Firmware_HardwareVersionMissing,
                nameof(hardwareVersion));
        }

        var workerUrls = await configuration.LoadWorkerUrlsAsync(cancellationToken);
        var listWorkerUri = ValidateWorkerUri(
            workerUrls.ListWorkerUrl,
            AppResources.Firmware_ListWorkerAddress);
        var getWorkerUri = ValidateWorkerUri(
            workerUrls.GetWorkerUrl,
            AppResources.Firmware_DownloadWorkerAddress);
        var requestUri = AppendQuery(
            listWorkerUri,
            ("hardwareVersion", hardwareVersion.Trim()),
            ("currentVersion", string.IsNullOrWhiteSpace(currentVersion) ? "0.0.0" : currentVersion.Trim()));

        diagnostics.Information(
            "OTA/Online",
            $"开始查询固件 Worker：{requestUri}。");

        using var response = await httpClient.GetAsync(
            requestUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        diagnostics.Debug(
            "OTA/Online",
            $"固件列表 Worker 响应：HTTP {(int)response.StatusCode} {response.ReasonPhrase}。");
        response.EnsureSuccessStatusCode();

        await using var responseStream =
            await response.Content.ReadAsStreamAsync(cancellationToken);
        var result = await JsonSerializer.DeserializeAsync<OnlineFirmwareResponse>(
                responseStream,
                JsonOptions,
                cancellationToken)
            ?? throw new InvalidDataException(AppResources.Firmware_EmptyListResponse);

        if (string.IsNullOrWhiteSpace(result.FileName))
        {
            diagnostics.Information(
                "OTA/Online",
                $"没有找到比 {currentVersion} 更新的 {hardwareVersion} 固件。");
            return new FirmwareCheckResult(currentVersion, null, false);
        }

        var objectKey = result.FileName.Trim();
        var fileName = Path.GetFileName(objectKey);
        // 在线发布支持两种协议的固件；仍由文件格式决定协议，不能互相回退。
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(fileName) || extension is not (".hex16" or ".bin"))
        {
            throw new InvalidDataException(
                AppResources.Firmware_InvalidOnlineExtension);
        }

        var version = string.IsNullOrWhiteSpace(result.Version)
            ? ParseVersionFromFileName(fileName)
            : result.Version.Trim();
        if (string.IsNullOrWhiteSpace(version))
            throw new InvalidDataException(
                AppResources.Firmware_InvalidOnlineVersion);

        var downloadUri = AppendQuery(
            getWorkerUri,
            ("fileName", objectKey));
        var protocol = GetFirmwareProtocol(fileName);
        var candidate = new FirmwareCandidate(
            FirmwareSourceKind.Online,
            protocol,
            version,
            fileName,
            Localization.Format(nameof(AppResources.Firmware_OnlineDescription), hardwareVersion.Trim()),
            null,
            downloadUri,
            NormalizeSha256(result.Sha256),
            null);
        var hasUpdate = CompareVersions(candidate.Version, currentVersion) > 0;

        diagnostics.Information(
            "OTA/Online",
            $"固件 Worker 返回：latest={candidate.Version}，file={candidate.FileName}，protocol={candidate.ProtocolText}，update={hasUpdate}。");
        return new FirmwareCheckResult(currentVersion, candidate, hasUpdate);
    }

    public Task<FirmwareCandidate> LoadLocalAsync(FileResult file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        return LoadLocalAsync(new SelectedFirmwareFile(file), cancellationToken);
    }

    public async Task<FirmwareCandidate> LoadLocalAsync(
        IFirmwareFile file,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        var logicalFileName = GetLocalFirmwareFileName(file.FileName);
        diagnostics.Information(
            "OTA/Local",
            $"开始校验本地固件：selected={file.FileName}，logical={logicalFileName}。");

        await using var stream = await file.OpenReadAsync();
        var firmware = await FirmwareLoader.LoadAsync(
            stream,
            logicalFileName,
            cancellationToken);

        var metadata = firmware.Metadata;
        var version = string.IsNullOrWhiteSpace(metadata.BootVersion)
            ? AppResources.Firmware_LocalFile
            : metadata.BootVersion;
        diagnostics.Information(
            "OTA/Local",
            $"本地 {GetProtocolText(firmware.Protocol)} 固件校验成功：length={firmware.Length}，secure={firmware.IsSecure}，productId={metadata.ProductId ?? "未提供"}，bootVersion={metadata.BootVersion ?? "未提供"}。");

        return new FirmwareCandidate(
            FirmwareSourceKind.Local,
            firmware.Protocol,
            version,
            logicalFileName,
            Localization.Format(nameof(AppResources.Firmware_LocalValidated), GetProtocolText(firmware.Protocol)),
            metadata.ProductId,
            null,
            null,
            file);
    }

    public async Task<IDevice?> DetectRecoveryDeviceAsync(string deviceAddress,
        Func<IReadOnlyList<IDevice>, Task<IDevice?>> selectDevice,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selectDevice);
        IDevice? detected = null;
        await agpLightControlService.RunExclusiveOtaRecoveryAsync(deviceAddress, async token =>
        {
            var devices = await ScanRecoveryDevicesAsync(token);
            var selected = await selectDevice(devices).WaitAsync(token);
            token.ThrowIfCancellationRequested();
            if (selected is null)
                return;
            if (!devices.Any(device => ReferenceEquals(device, selected)))
                throw new InvalidOperationException(AppResources.Firmware_RecoveryTargetUnavailable);
            if (!await otaClient.IsBootloaderAsync(selected, new PluginBleOtaOptions
                {
                    Protocol = new OtaOptions { RequestedMtu = configuration.RequestedMtu },
                    OperationDispatcher = operation => MainThread.InvokeOnMainThreadAsync(operation),
                    Transport = configuration.OtaTransport
                }, token))
                throw new InvalidOperationException(AppResources.Firmware_RecoveryNotInOta);
            token.ThrowIfCancellationRequested();
            detected = selected;
            diagnostics.Information("OTA/Recovery", $"已验证 OTA 模式：id={selected.Id}，name={selected.Name}。");
        }, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return detected;
    }

    private async Task<IReadOnlyList<IDevice>> ScanRecoveryDevicesAsync(CancellationToken token)
    {
        var devices = await otaClient.ScanBootloadersAsync(new PluginBleOtaOptions
        {
            BootloaderScanTimeout = TimeSpan.FromSeconds(5)
        }, token);
        if (devices.Count == 0)
            throw new IOException(AppResources.Firmware_RecoveryNotFound);
        return devices;
    }

    public async Task UpgradeAsync(
        string deviceAddress,
        FirmwareCandidate candidate,
        IProgress<FirmwareUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Func<IReadOnlyList<IDevice>, Task<IDevice?>>? selectRecoveryDevice = null)
    {
        ArgumentNullException.ThrowIfNull(connectionService);
        ArgumentNullException.ThrowIfNull(candidate);
        if (selectRecoveryDevice is not null && candidate.Protocol != OtaProtocol.SingleBank)
            throw new NotSupportedException(AppResources.Firmware_RecoverySingleBankOnly);
        var prepared = await PrepareFirmwareAsync(
            candidate,
            progress,
            cancellationToken);
        var singleBankPayloadAccepted = false;

        try
        {
            progress?.Report(
                new FirmwareUpdateProgress(
                    FirmwareUpdateStage.Connecting,
                    0,
                    Localization.Format(nameof(AppResources.Firmware_PreparingProtocol), candidate.ProtocolText)));

            try
            {
                async Task TransferAsync(IDevice device, CancellationToken token)
                {
                    var otaDiagnostics = diagnostics.CreateOtaDiagnostics();
                    var otaOptions = new PluginBleOtaOptions
                    {
                        Protocol = new OtaOptions
                        {
                            RequestedMtu = configuration.RequestedMtu,
                            AesKeyHex = configuration.AesKeyHex,
                            ValidateProductId = true,
                            Diagnostics = otaDiagnostics
                        },
                        OperationDispatcher = operation =>
                            MainThread.InvokeOnMainThreadAsync(operation),
                        Transport = configuration.OtaTransport,
                        RequireSingleBankOtaMode = selectRecoveryDevice is not null
                    };
                    var otaProgress = new InlineProgress<OtaProgress>(value =>
                    {
                        var percentage = Math.Clamp(value.Percentage, 0, 100);
                        if (candidate.Protocol == OtaProtocol.Slb &&
                            value.State is OtaState.FeedbackProgress or OtaState.VerifyingFirmware)
                            percentage = Math.Min(99.9, percentage);
                        if (candidate.Protocol == OtaProtocol.SingleBank &&
                            value.State == OtaState.UpgradeCompleteWaitingDisconnect &&
                            value.TotalBytes > 0 &&
                            value.BytesTransferred >= value.TotalBytes)
                        {
                            singleBankPayloadAccepted = true;
                        }

                        diagnostics.Debug(
                            "OTA/Progress",
                            $"state={value.State}, progress={percentage:0.0}%, bytes={value.BytesTransferred}/{value.TotalBytes}, message={value.Message}");
                        progress?.Report(
                            new FirmwareUpdateProgress(
                                value.State is OtaState.FeedbackProgress or OtaState.VerifyingFirmware
                                    ? FirmwareUpdateStage.Transferring
                                    : FirmwareUpdateStage.Connecting,
                                percentage,
                                ToUserMessage(value),
                                value.State));
                    });

                    await using var stream = await prepared.OpenReadAsync(token);
                    diagnostics.Information(
                        "OTA",
                        $"开始 {candidate.ProtocolText} 升级：device={deviceAddress}，file={candidate.FileName}，source={candidate.SourceText}。");
                    await otaClient.UpgradeAsync(
                        device,
                        stream,
                        candidate.FileName,
                        otaOptions,
                        otaProgress,
                        token);
                }
                if (selectRecoveryDevice is null)
                {
                    await agpLightControlService.RunExclusiveOtaAsync(deviceAddress, TransferAsync, cancellationToken);
                }
                else
                {
                    await agpLightControlService.RunExclusiveOtaRecoveryAsync(deviceAddress, async token =>
                    {
                        progress?.Report(new(FirmwareUpdateStage.Connecting, 0, AppResources.Firmware_RecoveryScanning));
                        var devices = await ScanRecoveryDevicesAsync(token);
                        var selected = await selectRecoveryDevice(devices);
                        if (selected is null || !devices.Any(device => ReferenceEquals(device, selected)))
                            throw new OperationCanceledException(token);
                        await TransferAsync(selected, token);
                    }, cancellationToken);
                }
            }
            catch (IOException exception)
                when (IsExpectedSingleBankRestartDisconnect(
                    candidate,
                    singleBankPayloadAccepted,
                    exception))
            {
                diagnostics.Warning(
                    "OTA/SingleBank",
                    "设备已确认全部 Single Bank 固件数据；最终重启命令触发 GATT 133 断开，按升级已提交处理。");
            }

            diagnostics.Information(
                "OTA",
                $"{candidate.ProtocolText} OTA 状态机执行完成。");
            progress?.Report(
                new FirmwareUpdateProgress(
                    FirmwareUpdateStage.Completed,
                    100,
                    AppResources.Firmware_UpdateSucceeded));
        }
        finally
        {
            prepared.Dispose();
        }
    }

    private static bool IsExpectedSingleBankRestartDisconnect(
        FirmwareCandidate candidate,
        bool payloadAccepted,
        IOException exception) =>
        candidate.Protocol == OtaProtocol.SingleBank &&
        payloadAccepted &&
        exception.Message.Contains(
            "GATT result 133",
            StringComparison.OrdinalIgnoreCase);

    private async Task<PreparedFirmware> PrepareFirmwareAsync(
        FirmwareCandidate candidate,
        IProgress<FirmwareUpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (candidate.SourceKind == FirmwareSourceKind.Local)
        {
            var file = candidate.LocalFile
                ?? throw new InvalidOperationException(AppResources.Firmware_LocalReferenceExpired);
            return new PreparedFirmware(
                token => file.OpenReadAsync(),
                null);
        }

        var downloadUri = candidate.DownloadUri
            ?? throw new InvalidOperationException(AppResources.Firmware_DownloadAddressMissing);
        var cacheDirectory = Path.Combine(FileSystem.CacheDirectory, "ota");
        Directory.CreateDirectory(cacheDirectory);
        var cachePath = Path.Combine(
            cacheDirectory,
            $"{Guid.NewGuid():N}_{candidate.FileName}");

        diagnostics.Information(
            "OTA/Download",
            $"开始下载固件：{downloadUri}。");
        progress?.Report(
            new FirmwareUpdateProgress(
                FirmwareUpdateStage.Downloading,
                0,
                AppResources.Firmware_Downloading));

        using var response = await httpClient.GetAsync(
            downloadUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var totalLength = response.Content.Headers.ContentLength;
        await using var input =
            await response.Content.ReadAsStreamAsync(cancellationToken);
        await using (var output = new FileStream(
                         cachePath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         81920,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var buffer = new byte[81920];
            long transferred = 0;
            var lastReported = -1;
            while (true)
            {
                var count = await input.ReadAsync(buffer, cancellationToken);
                if (count == 0)
                    break;

                await output.WriteAsync(
                    buffer.AsMemory(0, count),
                    cancellationToken);
                transferred += count;

                if (totalLength is > 0)
                {
                    var percentage = (int)(transferred * 100 / totalLength.Value);
                    if (percentage >= lastReported + 5 || percentage == 100)
                    {
                        lastReported = percentage;
                        progress?.Report(
                            new FirmwareUpdateProgress(
                                FirmwareUpdateStage.Downloading,
                                percentage,
                                Localization.Format(nameof(AppResources.Firmware_DownloadProgress), percentage)));
                        diagnostics.Debug(
                            "OTA/Download",
                            $"下载进度 {percentage}%（{transferred}/{totalLength} bytes）。");
                    }
                }
            }
        }

        await VerifySha256Async(
            cachePath,
            candidate.Sha256,
            cancellationToken);
        diagnostics.Information(
            "OTA/Download",
            $"固件下载完成并通过校验：{cachePath}。");
        return new PreparedFirmware(
            token => Task.FromResult<Stream>(
                new FileStream(
                    cachePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan)),
            cachePath);
    }

    private async Task VerifySha256Async(
        string path,
        string? expectedSha256,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256))
        {
            diagnostics.Warning(
                "OTA/Download",
                "在线 Worker 未提供 SHA-256，已跳过下载完整性校验。正式环境建议强制提供。");
            return;
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken));
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                Localization.Format(nameof(AppResources.Firmware_HashMismatch), expectedSha256, actual));
        }

        diagnostics.Information(
            "OTA/Download",
            $"固件 SHA-256 校验通过：{actual}。");
    }

    private static string ToUserMessage(OtaProgress progress) =>
        progress.State switch
        {
            OtaState.Connecting => AppResources.Firmware_ConnectingOta,
            OtaState.ConnectedDiscovering => AppResources.Firmware_DiscoveringOta,
            OtaState.SingleBankAppService or OtaState.SingleBankOtaService =>
                AppResources.Firmware_SingleBankDiscovered,
            OtaState.SlbService or OtaState.ConfirmedSlb or OtaState.SlbReady =>
                AppResources.Firmware_SlbReady,
            OtaState.ConfirmedSingleBankApp or OtaState.SingleBankAppReady =>
                AppResources.Firmware_SwitchingToOta,
            OtaState.AppModeEndWaitingScan =>
                AppResources.Firmware_WaitingForOtaAdvertisement,
            OtaState.FoundOtaDevice =>
                AppResources.Firmware_ReconnectingSingleBank,
            OtaState.ConfirmedSingleBankOta or OtaState.SingleBankOtaReady =>
                AppResources.Firmware_SingleBankReady,
            OtaState.FeedbackBootVersion => AppResources.Firmware_VersionValidated,
            OtaState.FeedbackProgress =>
                Localization.Format(nameof(AppResources.Firmware_TransferProgress), progress.Percentage),
            OtaState.VerifyingFirmware => AppResources.Firmware_Verifying,
            OtaState.UpgradeCompleteWaitingDisconnect =>
                AppResources.Firmware_WaitingForRestart,
            _ => AppResources.Firmware_OtaProcessing
        };

    private static OtaProtocol GetFirmwareProtocol(string fileName)
    {
        var extension = Path.GetExtension(fileName.Trim()).ToLowerInvariant();
        if (string.Equals(extension, ".bin", StringComparison.OrdinalIgnoreCase))
            return OtaProtocol.Slb;

        if (extension is ".hex" or ".hex4" or ".hex16" or ".hexe16" or ".res")
            return OtaProtocol.SingleBank;

        throw new NotSupportedException(
            AppResources.Firmware_SupportedFormats);
    }

    private static string GetProtocolText(OtaProtocol protocol) =>
        protocol == OtaProtocol.Slb ? "SLB" : "Single Bank";

    private string GetLocalFirmwareFileName(string? selectedFileName)
    {
        var fileName = Path.GetFileName(selectedFileName?.Trim() ?? string.Empty);
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = "selected_firmware";

        if (string.IsNullOrEmpty(Path.GetExtension(fileName)))
        {
            var logicalFileName = $"{fileName}.bin";
            diagnostics.Warning(
                "OTA/Local",
                $"Android 文件提供者未返回扩展名；将文件按 SLB .bin 处理：selected={selectedFileName}，logical={logicalFileName}。"
            );
            return logicalFileName;
        }

        _ = GetFirmwareProtocol(fileName);
        return fileName;
    }

    private static string? NormalizeSha256(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value
            .Replace(":", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Trim();
        if (normalized.Length != 64 ||
            normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException(
                AppResources.Firmware_InvalidHash);
        }

        return normalized.ToUpperInvariant();
    }

    private static Uri ValidateWorkerUri(string value, string description)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException(
                Localization.Format(nameof(AppResources.Firmware_InvalidUrl), description));
        }

        return uri;
    }

    private static Uri AppendQuery(
        Uri baseUri,
        params (string Name, string Value)[] parameters)
    {
        var query = baseUri.Query.TrimStart('?');
        var additions = parameters.Select(parameter =>
            $"{Uri.EscapeDataString(parameter.Name)}={Uri.EscapeDataString(parameter.Value)}");
        var combined = string.Join(
            "&",
            new[] { query, string.Join("&", additions) }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        var builder = new UriBuilder(baseUri)
        {
            Query = combined
        };
        return builder.Uri;
    }

    private static string? ParseVersionFromFileName(string fileName)
    {
        var match = FirmwareVersionPattern.Match(fileName);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static int CompareVersions(string left, string right)
    {
        var leftParts = GetVersionParts(left);
        var rightParts = GetVersionParts(right);
        var count = Math.Max(leftParts.Count, rightParts.Count);
        for (var index = 0; index < count; index++)
        {
            var leftValue = index < leftParts.Count ? leftParts[index] : 0;
            var rightValue = index < rightParts.Count ? rightParts[index] : 0;
            var result = leftValue.CompareTo(rightValue);
            if (result != 0)
                return result;
        }

        return 0;
    }

    private static IReadOnlyList<int> GetVersionParts(string value)
    {
        var normalized = value.Trim().TrimStart('v', 'V');
        var core = normalized.Split('-', '+')[0];
        var parts = new List<int>();
        foreach (var part in core.Split('.'))
        {
            if (!int.TryParse(part, out var number))
                break;
            parts.Add(number);
        }

        return parts.Count > 0 ? parts : [0];
    }

    private sealed class PreparedFirmware(
        Func<CancellationToken, Task<Stream>> openReadAsync,
        string? temporaryPath) : IDisposable
    {
        public Task<Stream> OpenReadAsync(CancellationToken cancellationToken) =>
            openReadAsync(cancellationToken);

        public void Dispose()
        {
            if (temporaryPath is null)
                return;

            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
                // 缓存清理失败不应覆盖 OTA 的真实结果。
            }
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed record OnlineFirmwareResponse
    {
        [JsonPropertyName("fileName")]
        public string? FileName { get; init; }

        [JsonPropertyName("version")]
        public string? Version { get; init; }

        [JsonPropertyName("sha256")]
        public string? Sha256 { get; init; }
    }
}
