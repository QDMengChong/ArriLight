using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ArriOpto.BLE.Ota;

/// <summary>
/// OTA 固件文件的统一加载入口。
/// </summary>
/// <remarks>
/// 本类型只进行文件分类、读取和结构校验，不执行任何 BLE 操作。文件名和扩展名
/// 大小写不敏感：<c>.bin</c> 归类为 SLB，Intel HEX 相关扩展归类为
/// Single Bank。
/// </remarks>
public static partial class FirmwareLoader
{
    /// <summary>
    /// 异步读取固件并返回对应的强类型镜像。
    /// </summary>
    /// <param name="path">固件文件路径，可为相对路径或绝对路径。</param>
    /// <param name="cancellationToken">取消文件读取和 Intel HEX 解析。</param>
    /// <param name="diagnostics">可选结构化诊断配置。</param>
    /// <returns>
    /// SLB 文件返回 <see cref="SlbFirmwareImage"/>；Single Bank 文件返回
    /// <see cref="SingleBankFirmwareImage"/>。
    /// </returns>
    /// <exception cref="FileNotFoundException">文件不存在。</exception>
    /// <exception cref="InvalidDataException">文件为空、Intel HEX 损坏或没有有效载荷。</exception>
    /// <exception cref="NotSupportedException">扩展名不属于已知 OTA 格式。</exception>
    public static async Task<FirmwareImage> LoadAsync(
        string path,
        CancellationToken cancellationToken = default,
        OtaDiagnostics? diagnostics = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = System.IO.Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Firmware file was not found.", fullPath);
        }

        var fileName = System.IO.Path.GetFileName(fullPath);
        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await LoadCoreAsync(
                stream,
                fullPath,
                fileName,
                diagnostics,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 从调用方提供的可读流加载固件。
    /// </summary>
    /// <param name="stream">从当前位置读取到末尾的固件流。</param>
    /// <param name="fileName">
    /// 带扩展名的逻辑文件名，用于判断协议、加密和资源地址；不要求是真实路径。
    /// </param>
    /// <param name="cancellationToken">取消流读取和 Intel HEX 解析。</param>
    /// <param name="diagnostics">可选结构化诊断配置。</param>
    /// <returns>已解析的强类型固件镜像。</returns>
    /// <remarks>
    /// 本方法不会释放 <paramref name="stream"/>。MAUI 可直接传入
    /// <c>FileResult.OpenReadAsync()</c> 返回的流，并在升级方法返回后由调用方释放。
    /// </remarks>
    /// <exception cref="ArgumentException">流不可读或逻辑文件名无效。</exception>
    /// <exception cref="InvalidDataException">固件为空或格式损坏。</exception>
    /// <exception cref="NotSupportedException">文件扩展名不受支持。</exception>
    public static Task<FirmwareImage> LoadAsync(
        Stream stream,
        string fileName,
        CancellationToken cancellationToken = default,
        OtaDiagnostics? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new ArgumentException("Firmware stream must be readable.", nameof(stream));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        var displayName = System.IO.Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException(
                "Firmware file name must contain a name and extension.",
                nameof(fileName));
        }

        return LoadCoreAsync(
            stream,
            displayName,
            displayName,
            diagnostics,
            cancellationToken);
    }

    private static async Task<FirmwareImage> LoadCoreAsync(
        Stream stream,
        string sourceName,
        string fileName,
        OtaDiagnostics? diagnostics,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        diagnostics?.Report(
            new OtaDiagnosticEvent(
                DateTimeOffset.UtcNow,
                OtaDiagnosticLevel.Information,
                "firmware.load.start",
                "firmware",
                "开始读取并解析 OTA 固件。")
            {
                Properties = new Dictionary<string, string>
                {
                    ["fileName"] = fileName
                }
            });

        var lowerName = fileName.ToLowerInvariant();

        try
        {
            FirmwareImage result;
            // 官方工具以文件名而不是额外的 manifest 区分 SLB 资源和加密镜像。
            if (lowerName.EndsWith(".bin", StringComparison.Ordinal))
            {
                using var dataBuffer = new MemoryStream();
                await stream.CopyToAsync(dataBuffer, cancellationToken).ConfigureAwait(false);
                var data = dataBuffer.ToArray();
                if (data.Length == 0)
                {
                    throw new InvalidDataException("Firmware file is empty.");
                }

                var resourceAddress = ParseSlbResourceAddress(fileName);
                result = new SlbFirmwareImage(
                    sourceName,
                    data,
                    lowerName.EndsWith("hexe16.bin", StringComparison.Ordinal),
                    resourceAddress,
                    FirmwareMetadataReader.Read(data));
            }
            else
            {
                var extension = System.IO.Path.GetExtension(lowerName);
                var supported =
                    extension is ".hex" or ".hex4" or ".hex16" or ".hexe16" or ".res";
                if (!supported)
                {
                    throw new NotSupportedException(
                        $"Unsupported firmware extension '{extension}'.");
                }

                var partitions = await IntelHexParser
                    .ParseAsync(stream, cancellationToken)
                    .ConfigureAwait(false);
                if (partitions.Count == 0 || partitions.All(partition => partition.Length == 0))
                {
                    throw new InvalidDataException("Firmware file contains no payload data.");
                }

                // Product ID marker 可能位于任意分区，因此依次查找第一个完整 metadata。
                var metadata = partitions
                    .Select(partition => FirmwareMetadataReader.Read(partition.Data.Span))
                    .FirstOrDefault(value => value.IsPresent)
                    ?? new FirmwareMetadata(null, null);

                result = new SingleBankFirmwareImage(
                    sourceName,
                    extension == ".res" ? FirmwareKind.Resource : FirmwareKind.IntelHex,
                    extension == ".hexe16",
                    partitions,
                    metadata);
            }

            stopwatch.Stop();
            diagnostics?.Report(
                new OtaDiagnosticEvent(
                    DateTimeOffset.UtcNow,
                    OtaDiagnosticLevel.Information,
                    "firmware.load.complete",
                    "firmware",
                    "OTA 固件读取和解析完成。")
                {
                    Protocol = result.Protocol,
                    Elapsed = stopwatch.Elapsed,
                    Properties = new Dictionary<string, string>
                    {
                        ["fileName"] = fileName,
                        ["kind"] = result.Kind.ToString(),
                        ["isSecure"] = result.IsSecure.ToString(),
                        ["length"] = result.Length.ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                        ["partitionCount"] = result is SingleBankFirmwareImage singleBank
                            ? singleBank.Partitions.Count.ToString(
                                System.Globalization.CultureInfo.InvariantCulture)
                            : "1"
                    }
                });
            return result;
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
                    "firmware.load.failed",
                    "firmware",
                    "OTA 固件读取或解析失败。")
                {
                    Elapsed = stopwatch.Elapsed,
                    ExceptionType = exception.GetType().FullName,
                    ExceptionMessage = exception.Message,
                    Properties = new Dictionary<string, string>
                    {
                        ["fileName"] = fileName
                    }
                });
            throw;
        }
    }

    private static string? ParseSlbResourceAddress(string fileName)
    {
        var match = SlbResourceFileNameRegex().Match(fileName);
        return match.Success ? match.Groups["address"].Value.ToUpperInvariant() : null;
    }

    [GeneratedRegex(
        @"^res_(?<address>[0-9a-fA-F]{8})(?:[_.].*)?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SlbResourceFileNameRegex();
}
