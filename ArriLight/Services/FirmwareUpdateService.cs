using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using PhyPlus.Ota.Firmware;
using PhyPlus.Ota.Models;
using PhyPlus.Ota.PluginBle;

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
    FirmwareCandidate Candidate,
    bool IsUpdateAvailable);

public sealed class FirmwareCandidate
{
    internal FirmwareCandidate(
        FirmwareSourceKind sourceKind,
        string version,
        string fileName,
        string releaseNotes,
        string? productId,
        Uri? downloadUri,
        string? sha256,
        FileResult? localFile)
    {
        SourceKind = sourceKind;
        Version = version;
        FileName = fileName;
        ReleaseNotes = releaseNotes;
        ProductId = productId;
        DownloadUri = downloadUri;
        Sha256 = sha256;
        LocalFile = localFile;
    }

    public FirmwareSourceKind SourceKind { get; }
    public string Version { get; }
    public string FileName { get; }
    public string ReleaseNotes { get; }
    public string? ProductId { get; }
    public Uri? DownloadUri { get; }
    public string? Sha256 { get; }
    internal FileResult? LocalFile { get; }

    public string SourceText =>
        SourceKind == FirmwareSourceKind.Online ? "在线固件" : "本地固件";
}

public sealed class FirmwareUpdateConfiguration
{
    private const string ManifestUrlPreferenceKey = "ota_manifest_url_v1";

    public string ManifestUrl
    {
        get => Preferences.Default.Get(ManifestUrlPreferenceKey, string.Empty);
        set => Preferences.Default.Set(
            ManifestUrlPreferenceKey,
            value?.Trim() ?? string.Empty);
    }

    public int RequestedMtu { get; init; } = 517;

    // AES Key 不持久化。若产品使用加密 SLB，应由安全配置在运行时注入。
    public string? AesKeyHex { get; set; }
}

public sealed class FirmwareUpdateService(
    HttpClient httpClient,
    PluginBleOtaClient otaClient,
    BluetoothConnectionService connectionService,
    AppDiagnosticService diagnostics,
    FirmwareUpdateConfiguration configuration)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<FirmwareCheckResult> CheckOnlineAsync(
        string currentVersion,
        string manifestUrl,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(manifestUrl, UriKind.Absolute, out var manifestUri) ||
            manifestUri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("在线固件清单地址必须是有效的 HTTP/HTTPS URL。");
        }

        configuration.ManifestUrl = manifestUri.ToString();
        diagnostics.Information(
            "OTA/Online",
            $"开始请求固件清单：{manifestUri}，当前版本={currentVersion}。");

        using var response = await httpClient.GetAsync(
            manifestUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        diagnostics.Debug(
            "OTA/Online",
            $"固件清单响应：HTTP {(int)response.StatusCode} {response.ReasonPhrase}。");
        response.EnsureSuccessStatusCode();

        await using var responseStream =
            await response.Content.ReadAsStreamAsync(cancellationToken);
        var manifest = await JsonSerializer.DeserializeAsync<FirmwareManifest>(
                responseStream,
                JsonOptions,
                cancellationToken)
            ?? throw new InvalidDataException("在线固件清单为空。");

        manifest.Validate();
        var downloadUri = Uri.TryCreate(
            manifestUri,
            manifest.DownloadUrl,
            out var resolvedDownloadUri)
            ? resolvedDownloadUri
            : throw new InvalidDataException("固件下载地址无效。");

        var fileName = string.IsNullOrWhiteSpace(manifest.FileName)
            ? Path.GetFileName(downloadUri.LocalPath)
            : Path.GetFileName(manifest.FileName);
        EnsureSlbFileName(fileName);

        var candidate = new FirmwareCandidate(
            FirmwareSourceKind.Online,
            manifest.Version.Trim(),
            fileName,
            manifest.ReleaseNotes?.Trim() ?? "暂无版本说明",
            manifest.ProductId?.Trim(),
            downloadUri,
            NormalizeSha256(manifest.Sha256),
            null);
        var hasUpdate = CompareVersions(candidate.Version, currentVersion) > 0;

        diagnostics.Information(
            "OTA/Online",
            $"固件清单解析完成：latest={candidate.Version}，file={candidate.FileName}，update={hasUpdate}。");
        return new FirmwareCheckResult(currentVersion, candidate, hasUpdate);
    }

    public async Task<FirmwareCandidate> LoadLocalAsync(
        FileResult file,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        EnsureSlbFileName(file.FileName);
        diagnostics.Information(
            "OTA/Local",
            $"开始校验本地固件：{file.FileName}。");

        await using var stream = await file.OpenReadAsync();
        var firmware = await FirmwareLoader.LoadAsync(
            stream,
            file.FileName,
            cancellationToken);
        if (firmware.Protocol != OtaProtocol.Slb)
        {
            throw new NotSupportedException(
                "当前 APP 的升级流程仅开放 SLB 固件，请选择 .bin 文件。");
        }

        var metadata = firmware.Metadata;
        var version = string.IsNullOrWhiteSpace(metadata.BootVersion)
            ? "本地文件"
            : metadata.BootVersion;
        diagnostics.Information(
            "OTA/Local",
            $"本地 SLB 固件校验成功：length={firmware.Length}，secure={firmware.IsSecure}，productId={metadata.ProductId ?? "未提供"}，bootVersion={metadata.BootVersion ?? "未提供"}。");

        return new FirmwareCandidate(
            FirmwareSourceKind.Local,
            version,
            Path.GetFileName(file.FileName),
            "已加载并通过格式校验的本地 SLB 固件。",
            metadata.ProductId,
            null,
            null,
            file);
    }

    public async Task UpgradeAsync(
        string deviceAddress,
        FirmwareCandidate candidate,
        IProgress<FirmwareUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var prepared = await PrepareFirmwareAsync(
            candidate,
            progress,
            cancellationToken);

        try
        {
            progress?.Report(
                new FirmwareUpdateProgress(
                    FirmwareUpdateStage.Connecting,
                    0,
                    "正在连接设备并准备 SLB OTA。"));

            await connectionService.RunExclusiveOtaAsync(
                deviceAddress,
                async (device, token) =>
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
                            MainThread.InvokeOnMainThreadAsync(operation)
                    };
                    var otaProgress = new Progress<OtaProgress>(value =>
                    {
                        var percentage = Math.Clamp(value.Percentage, 0, 100);
                        diagnostics.Debug(
                            "OTA/Progress",
                            $"state={value.State}, progress={percentage:0.0}%, bytes={value.BytesTransferred}/{value.TotalBytes}, message={value.Message}");
                        progress?.Report(
                            new FirmwareUpdateProgress(
                                value.State == OtaState.FeedbackProgress
                                    ? FirmwareUpdateStage.Transferring
                                    : FirmwareUpdateStage.Connecting,
                                percentage,
                                ToUserMessage(value),
                                value.State));
                    });

                    await using var stream = await prepared.OpenReadAsync(token);
                    diagnostics.Information(
                        "OTA",
                        $"开始 SLB 升级：device={deviceAddress}，file={candidate.FileName}，source={candidate.SourceText}。");
                    await otaClient.UpgradeAsync(
                        device,
                        stream,
                        candidate.FileName,
                        otaOptions,
                        otaProgress,
                        token);
                },
                cancellationToken);

            diagnostics.Information("OTA", "SLB OTA 状态机执行完成。");
            progress?.Report(
                new FirmwareUpdateProgress(
                    FirmwareUpdateStage.Completed,
                    100,
                    "固件校验通过，设备正在重启。"));
        }
        finally
        {
            prepared.Dispose();
        }
    }

    private async Task<PreparedFirmware> PrepareFirmwareAsync(
        FirmwareCandidate candidate,
        IProgress<FirmwareUpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (candidate.SourceKind == FirmwareSourceKind.Local)
        {
            var file = candidate.LocalFile
                ?? throw new InvalidOperationException("本地固件文件引用已失效。");
            return new PreparedFirmware(
                token => file.OpenReadAsync(),
                null);
        }

        var downloadUri = candidate.DownloadUri
            ?? throw new InvalidOperationException("在线固件缺少下载地址。");
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
                "正在下载固件…"));

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
                                $"正在下载固件… {percentage}%"));
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
                "在线清单未提供 SHA-256，已跳过下载完整性校验。正式环境建议强制提供。");
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
                $"固件 SHA-256 校验失败，expected={expectedSha256}，actual={actual}。");
        }

        diagnostics.Information(
            "OTA/Download",
            $"固件 SHA-256 校验通过：{actual}。");
    }

    private static string ToUserMessage(OtaProgress progress) =>
        progress.State switch
        {
            OtaState.Connecting => "正在连接 OTA 设备…",
            OtaState.ConnectedDiscovering => "已连接，正在发现 OTA 服务…",
            OtaState.SlbService or OtaState.ConfirmedSlb or OtaState.SlbReady =>
                "SLB OTA 服务已准备完成。",
            OtaState.FeedbackBootVersion => "已读取设备版本并校验固件。",
            OtaState.FeedbackProgress =>
                $"正在传输固件… {progress.Percentage:0.0}%",
            OtaState.UpgradeCompleteWaitingDisconnect =>
                "设备已确认固件，等待重启。",
            _ => progress.Message
        };

    private static void EnsureSlbFileName(string fileName)
    {
        if (!string.Equals(
                Path.GetExtension(fileName),
                ".bin",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                "当前 APP 主要使用 SLB OTA，请选择扩展名为 .bin 的固件。");
        }
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
                "在线固件清单中的 SHA-256 必须是 64 个十六进制字符。");
        }

        return normalized.ToUpperInvariant();
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

    private sealed record FirmwareManifest
    {
        [JsonPropertyName("productId")]
        public string? ProductId { get; init; }

        [JsonPropertyName("version")]
        public string Version { get; init; } = string.Empty;

        [JsonPropertyName("fileName")]
        public string? FileName { get; init; }

        [JsonPropertyName("downloadUrl")]
        public string DownloadUrl { get; init; } = string.Empty;

        [JsonPropertyName("sha256")]
        public string? Sha256 { get; init; }

        [JsonPropertyName("releaseNotes")]
        public string? ReleaseNotes { get; init; }

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(Version))
                throw new InvalidDataException("在线固件清单缺少 version。");
            if (string.IsNullOrWhiteSpace(DownloadUrl))
                throw new InvalidDataException("在线固件清单缺少 downloadUrl。");
        }
    }
}
