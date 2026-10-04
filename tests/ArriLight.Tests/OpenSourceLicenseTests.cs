using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace ArriLight.Tests;

public sealed class OpenSourceLicenseTests
{
    private static Task<Stream> OpenAsset(string path) =>
        Task.FromResult<Stream>(File.OpenRead(Path.Combine(AppContext.BaseDirectory, "AppAssets", path)));

    [Fact]
    public async Task EveryComponentHasReadableOriginalLicenseAndNoticeFiles()
    {
        var catalog = new OpenSourceLicenseCatalog(OpenAsset);
        var components = await catalog.GetComponentsAsync();
        Assert.NotEmpty(components);
        foreach (var component in components)
        {
            var text = await catalog.GetLicenseTextAsync(component);
            foreach (var document in component.Documents)
            {
                using var stream = await OpenAsset(document.AssetPath);
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer);
                var bytes = buffer.ToArray();
                Assert.True(bytes.Length > 100, component.Id);
                Assert.Contains(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'), text);
                var digest = Convert.ToHexStringLower(SHA256.HashData(bytes))[..24];
                Assert.Equal(digest + ".txt", Path.GetFileName(document.AssetPath));
            }
        }
    }

    [Fact]
    public async Task CatalogIncludesCurrentDirectPackageVersionsAndCompositeLicenses()
    {
        var catalog = new OpenSourceLicenseCatalog(OpenAsset);
        var components = await catalog.GetComponentsAsync();
        var repository = FindRepository();
        var project = XDocument.Load(Path.Combine(repository, "ArriLight/ArriLight.csproj"));
        foreach (var package in project.Descendants("PackageReference"))
        {
            var name = package.Attribute("Include")!.Value;
            var version = package.Attribute("Version")!.Value;
            Assert.Contains(components, c => c.Name == name && c.Version == version && c.IsDirect);
        }

        Assert.Equal("Apache-2.0", components.Single(c => c.Name == "Plugin.BLE").License);
        Assert.Equal("Apache-2.0", components.Single(c => c.Name == "ZXing.Net").License);
        Assert.Equal("Apache-2.0", components.Single(c => c.Name == "Open Sans").License);
        var camera = components.Single(c => c.Name == "Xamarin.AndroidX.Camera.Core");
        Assert.Contains("BSD-3-Clause", camera.License);
        Assert.Contains("THIRD-PARTY SOFTWARE NOTICES", await catalog.GetLicenseTextAsync(camera));
        Assert.Contains(components, c => c.Name == "Xamarin.Android.Glide" && c.License.Contains("BSD-2-Clause"));
    }

    [Fact]
    public async Task RestoredTransitiveDependenciesAreAllListed()
    {
        var catalog = new OpenSourceLicenseCatalog(OpenAsset);
        var components = await catalog.GetComponentsAsync();
        var assetsPath = Path.Combine(FindRepository(), "ArriLight/obj/project.assets.json");
        // This check also catches a stale catalog after an ordinary package upgrade.
        Assert.True(File.Exists(assetsPath), "Restore the mobile project before running license coverage tests.");
        using var assets = JsonDocument.Parse(await File.ReadAllTextAsync(assetsPath));
        foreach (var package in assets.RootElement.GetProperty("libraries").EnumerateObject())
            if (package.Value.GetProperty("type").GetString() == "package")
                Assert.Contains(components, c => c.Id == package.Name);
    }

    [Theory]
    [InlineData("OpenSourceLicenses/texts/../../secret.txt")]
    [InlineData("OtherAssets/license.txt")]
    public async Task InvalidDocumentPathsAreRejected(string path)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            components = new[] { new
            {
                id = "test", name = "test", version = "1.0", license = "MIT",
                projectUrl = "", authors = "", copyright = "", isDirect = false,
                documents = new[] { new { name = "LICENSE", assetPath = path, source = "test" } }
            } }
        });
        var catalog = new OpenSourceLicenseCatalog(_ => Task.FromResult<Stream>(new MemoryStream(data)));
        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.GetComponentsAsync());
    }

    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "ArriLight/ArriLight.csproj")))
                return directory.FullName;
        throw new DirectoryNotFoundException("ArriLight repository was not found.");
    }
}
