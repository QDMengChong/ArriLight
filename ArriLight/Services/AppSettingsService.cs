using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArriLight;

internal sealed record AppSettingsDocument(FirmwareUpdateEndpoints? FirmwareUpdate, ReconnectSettings? Reconnect = null);
internal sealed record FirmwareUpdateEndpoints(string? ListWorkerUrl, string? GetWorkerUrl);
internal sealed record ReconnectSettings(int ConnectionTimeoutMilliseconds = 1200);

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(AppSettingsDocument))]
internal partial class AppSettingsJsonContext : JsonSerializerContext;

/// <summary>The packaged JSON seeds a writable file once; saved changes are read on each use.</summary>
public sealed class AppSettingsService
{
    public const string FileName = "appsettings.json";
    private const int MaximumBytes = 64 * 1024;
    private readonly Func<string, Task<Stream>> _openAsset;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    public AppSettingsService() : this(FileSystem.AppDataDirectory, FileSystem.OpenAppPackageFileAsync) { }

    internal AppSettingsService(string directory, Func<string, Task<Stream>> openAsset)
    {
        ConfigurationPath = Path.Combine(directory, FileName);
        _openAsset = openAsset;
    }

    public string ConfigurationPath { get; }

    public async Task<string> ReadTextAsync(CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(ConfigurationPath))
            {
                await using var stream = await _openAsset(FileName);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var defaults = await reader.ReadToEndAsync(cancellationToken);
                Validate(defaults);
                await WriteFileAsync(defaults, cancellationToken);
                return defaults;
            }

            if (new FileInfo(ConfigurationPath).Length > MaximumBytes)
                throw new InvalidDataException(AppResources.Configuration_TooLarge);
            // Return the original text so the editor can also repair an invalid file.
            return await File.ReadAllTextAsync(ConfigurationPath, Encoding.UTF8, cancellationToken);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task SaveTextAsync(string text, CancellationToken cancellationToken = default)
    {
        Validate(text);
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            await WriteFileAsync(text, cancellationToken);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task<(string ListWorkerUrl, string GetWorkerUrl)> LoadWorkerUrlsAsync(
        CancellationToken cancellationToken = default)
    {
        var document = Validate(await ReadTextAsync(cancellationToken));
        return (document.FirmwareUpdate!.ListWorkerUrl!.Trim(), document.FirmwareUpdate.GetWorkerUrl!.Trim());
    }

    public async Task<TimeSpan> LoadReconnectTimeoutAsync(CancellationToken cancellationToken = default) =>
        TimeSpan.FromMilliseconds(Validate(await ReadTextAsync(cancellationToken)).Reconnect?.ConnectionTimeoutMilliseconds ?? 1200);

    private static AppSettingsDocument Validate(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Encoding.UTF8.GetByteCount(text) > MaximumBytes)
            throw new InvalidDataException(AppResources.Configuration_TooLarge);

        AppSettingsDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(text, AppSettingsJsonContext.Default.AppSettingsDocument);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                Localization.Format(nameof(AppResources.Configuration_InvalidJson), exception.Message), exception);
        }

        ValidateUrl(document?.FirmwareUpdate?.ListWorkerUrl, "FirmwareUpdate.ListWorkerUrl");
        ValidateUrl(document?.FirmwareUpdate?.GetWorkerUrl, "FirmwareUpdate.GetWorkerUrl");
        if (document?.Reconnect?.ConnectionTimeoutMilliseconds is < 100 or > 30000)
            throw new InvalidDataException("Reconnect.ConnectionTimeoutMilliseconds must be between 100 and 30000.");
        return document!;
    }

    private static void ValidateUrl(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException(Localization.Format(nameof(AppResources.Configuration_MissingSetting), name));
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidDataException(Localization.Format(nameof(AppResources.Configuration_InvalidUrl), name));
    }

    private async Task WriteFileAsync(string text, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(ConfigurationPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $"appsettings.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, text, new UTF8Encoding(false), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, ConfigurationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}
