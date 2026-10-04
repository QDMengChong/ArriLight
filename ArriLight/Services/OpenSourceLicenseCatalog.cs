using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ArriLight;

public sealed record OpenSourceLicenseDocument(string Name, string AssetPath, string Source);

public sealed record OpenSourceComponent(
    string Id, string Name, string Version, string License, string ProjectUrl,
    string Authors, string Copyright, bool IsDirect,
    IReadOnlyList<OpenSourceLicenseDocument> Documents)
{
    public string VersionAndLicense => $"{Version} · {License}";
}

internal sealed record OpenSourceLicenseManifest(int SchemaVersion, IReadOnlyList<OpenSourceComponent> Components);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OpenSourceLicenseManifest))]
internal partial class OpenSourceLicenseJsonContext : JsonSerializerContext;

public sealed class OpenSourceLicenseCatalog
{
    internal const string ManifestAsset = "OpenSourceLicenses/catalog.json";
    private readonly Func<string, Task<Stream>> _openAsset;
    private readonly Lazy<Task<IReadOnlyList<OpenSourceComponent>>> _components;

    public OpenSourceLicenseCatalog() : this(FileSystem.OpenAppPackageFileAsync) { }

    internal OpenSourceLicenseCatalog(Func<string, Task<Stream>> openAsset)
    {
        _openAsset = openAsset;
        _components = new(LoadAsync);
    }

    public Task<IReadOnlyList<OpenSourceComponent>> GetComponentsAsync() => _components.Value;

    public async Task<string> GetLicenseTextAsync(OpenSourceComponent component)
    {
        var text = new StringBuilder();
        foreach (var document in component.Documents)
        {
            using var stream = await _openAsset(document.AssetPath);
            using var reader = new StreamReader(stream);
            if (text.Length > 0)
                text.AppendLine().AppendLine();
            text.AppendLine(document.Name).AppendLine(document.Source).AppendLine();
            // Preserve upstream license and attribution text, including notices
            // that explicitly prohibit translation or localization.
            text.Append(await reader.ReadToEndAsync());
        }
        return text.ToString();
    }

    private async Task<IReadOnlyList<OpenSourceComponent>> LoadAsync()
    {
        using var stream = await _openAsset(ManifestAsset);
        var manifest = await JsonSerializer.DeserializeAsync(
            stream, OpenSourceLicenseJsonContext.Default.OpenSourceLicenseManifest);
        if (manifest is not { SchemaVersion: 1, Components.Count: > 0 })
            throw new InvalidDataException("Invalid open-source license catalog.");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var component in manifest.Components)
        {
            if (string.IsNullOrWhiteSpace(component.Id) || !ids.Add(component.Id) ||
                string.IsNullOrWhiteSpace(component.Name) || string.IsNullOrWhiteSpace(component.Version) ||
                string.IsNullOrWhiteSpace(component.License) || component.Documents is not { Count: > 0 } ||
                component.Documents.Any(d => !Regex.IsMatch(d.AssetPath,
                    @"\AOpenSourceLicenses/texts/[a-f0-9]{24}\.txt\z")))
                throw new InvalidDataException("Invalid component in open-source license catalog.");
        }
        return manifest.Components;
    }
}
