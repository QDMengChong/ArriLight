using System.Net;
using System.Text;
using ArriOpto.BLE.Ota;
using Xunit;

namespace ArriLight.Tests;

internal sealed class AppSettingsFixture : IDisposable
{
    internal const string DefaultText = """
        {
          "FirmwareUpdate": {
            "ListWorkerUrl": "https://list.example/default",
            "GetWorkerUrl": "https://get.example/default"
          }
        }
        """;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ArriLight.Settings.Tests", Guid.NewGuid().ToString("N"));
    public int AssetReads { get; private set; }
    public AppSettingsService Store { get; }
    public AppSettingsFixture() => Store = NewStore();

    public AppSettingsService NewStore() => new(_directory, name =>
    {
        Assert.Equal(AppSettingsService.FileName, name);
        AssetReads++;
        return Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(DefaultText)));
    });

    public static string Text(string listUrl, string getUrl) => System.Text.Json.JsonSerializer.Serialize(new
    {
        FirmwareUpdate = new { ListWorkerUrl = listUrl, GetWorkerUrl = getUrl }
    });

    public void Dispose()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ArriLight.Settings.Tests")) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(_directory);
        Assert.StartsWith(root, target);
        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);
    }
}

public sealed class AppSettingsTests
{
    [Fact]
    public async Task OfflineRecoveryWindowDefaultsTo1200AndCanBeChangedWithoutRestarting()
    {
        using var fixture = new AppSettingsFixture();
        Assert.Equal(TimeSpan.FromMilliseconds(1200), await fixture.Store.LoadReconnectTimeoutAsync());
        var text = AppSettingsFixture.DefaultText.TrimEnd();
        var edited = text[..^1] + ",\"Reconnect\":{\"ConnectionTimeoutMilliseconds\":2400}}";
        await fixture.Store.SaveTextAsync(edited);
        Assert.Equal(TimeSpan.FromMilliseconds(2400), await fixture.Store.LoadReconnectTimeoutAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.SaveTextAsync(edited.Replace("2400", "0")));
        Assert.Equal(TimeSpan.FromMilliseconds(2400), await fixture.Store.LoadReconnectTimeoutAsync());
    }

    [Fact]
    public async Task PackagedFileSeedsWritableTextOnceAndSavedEditsSurviveNewService()
    {
        using var fixture = new AppSettingsFixture();
        Assert.Equal(AppSettingsFixture.DefaultText, await fixture.Store.ReadTextAsync());
        Assert.True(File.Exists(fixture.Store.ConfigurationPath));
        var edited = AppSettingsFixture.Text("https://new.example/list", "https://new.example/get") + "\n";
        await fixture.Store.SaveTextAsync(edited);

        Assert.Equal(edited, await fixture.NewStore().ReadTextAsync());
        Assert.Equal(1, fixture.AssetReads);
        Assert.Equal(edited, await File.ReadAllTextAsync(fixture.Store.ConfigurationPath));
    }

    [Fact]
    public async Task SameFirmwareConfigurationImmediatelyReadsSavedAddresses()
    {
        using var fixture = new AppSettingsFixture();
        var configuration = new FirmwareUpdateConfiguration(fixture.Store);
        Assert.Equal("https://list.example/default", (await configuration.LoadWorkerUrlsAsync()).ListWorkerUrl);
        await fixture.Store.SaveTextAsync(AppSettingsFixture.Text(" https://new.example/list?tenant=a ", "https://new.example/get"));
        var changed = await configuration.LoadWorkerUrlsAsync();
        Assert.Equal("https://new.example/list?tenant=a", changed.ListWorkerUrl);
        Assert.Equal("https://new.example/get", changed.GetWorkerUrl);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"FirmwareUpdate\":null}")]
    [InlineData("{\"FirmwareUpdate\":{\"ListWorkerUrl\":\"\",\"GetWorkerUrl\":\"https://get.example\"}}")]
    [InlineData("{\"FirmwareUpdate\":{\"ListWorkerUrl\":\"https://list.example\"}}")]
    [InlineData("{\"FirmwareUpdate\":{\"ListWorkerUrl\":\"file:///x\",\"GetWorkerUrl\":\"https://get.example\"}}")]
    [InlineData("{\"FirmwareUpdate\":{\"ListWorkerUrl\":\"https://list.example\",\"GetWorkerUrl\":\"relative/path\"}}")]
    [InlineData("{\"FirmwareUpdate\":{\"ListWorkerUrl\":1,\"GetWorkerUrl\":\"https://get.example\"}}")]
    [InlineData("{\"FirmwareUpdate\":{\"ListWorkerUrl\":\"https://list.example\",\"GetWorkerUrl\":\"https://get.example\",\"RequestedMtu\":100}}")]
    public async Task InvalidJsonFieldsOrAddressesDoNotReplaceWorkingFile(string invalid)
    {
        using var fixture = new AppSettingsFixture();
        var original = await fixture.Store.ReadTextAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.SaveTextAsync(invalid));
        Assert.Equal(original, await fixture.Store.ReadTextAsync());
        Assert.Equal("https://list.example/default", (await fixture.Store.LoadWorkerUrlsAsync()).ListWorkerUrl);
    }

    [Fact]
    public async Task CancellationDoesNotReplaceWorkingFileOrLeaveTemporaryFiles()
    {
        using var fixture = new AppSettingsFixture();
        var original = await fixture.Store.ReadTextAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Store.SaveTextAsync(
            AppSettingsFixture.Text("https://new.example/list", "https://new.example/get"), cancellation.Token));
        Assert.Equal(original, await fixture.Store.ReadTextAsync());
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(fixture.Store.ConfigurationPath)!));
    }

    [Fact]
    public async Task EditorCanReadAndRepairMalformedExistingFile()
    {
        using var fixture = new AppSettingsFixture();
        await fixture.Store.ReadTextAsync();
        await File.WriteAllTextAsync(fixture.Store.ConfigurationPath, "{");
        Assert.Equal("{", await fixture.Store.ReadTextAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.LoadWorkerUrlsAsync());
        await fixture.Store.SaveTextAsync(AppSettingsFixture.DefaultText);
        Assert.Equal("https://list.example/default", (await fixture.Store.LoadWorkerUrlsAsync()).ListWorkerUrl);
    }

    [Fact]
    public async Task SupportedCaseInsensitiveFieldsAndHttpAddressesRetainExistingCompatibility()
    {
        using var fixture = new AppSettingsFixture();
        await fixture.Store.SaveTextAsync("""
            {"firmwareupdate":{"listworkerurl":"http://localhost/list","getworkerurl":"https://get.example"}}
            """);
        Assert.Equal("http://localhost/list", (await fixture.Store.LoadWorkerUrlsAsync()).ListWorkerUrl);
    }

    [Fact]
    public async Task OversizedConfigurationIsRejectedWithoutChangingFile()
    {
        using var fixture = new AppSettingsFixture();
        var original = await fixture.Store.ReadTextAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.SaveTextAsync(original + new string(' ', 65_536)));
        Assert.Equal(original, await fixture.Store.ReadTextAsync());
    }

    [Fact]
    public async Task ExistingOnlineServiceUsesNewEndpointOnNextCheckWithoutRestart()
    {
        using var settings = new AppSettingsFixture();
        await using var fixture = new PairingFixture();
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        var service = new FirmwareUpdateService(http, new PluginBleOtaClient(fixture.Ble.Adapter),
            fixture.Connection, fixture.Service, new AppDiagnosticService(), new FirmwareUpdateConfiguration(settings.Store));
        await service.CheckOnlineAsync("PW01-A1", "1.0.0");
        Assert.Equal("list.example", handler.Requests.Single().Host);
        await settings.Store.SaveTextAsync(AppSettingsFixture.Text("https://changed.example/list", "https://changed.example/get"));
        await service.CheckOnlineAsync("PW01-A1", "1.0.0");
        Assert.Equal("changed.example", handler.Requests.Last().Host);
        Assert.Equal("/list", handler.Requests.Last().AbsolutePath);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"fileName\":null}")
            });
        }
    }
}
