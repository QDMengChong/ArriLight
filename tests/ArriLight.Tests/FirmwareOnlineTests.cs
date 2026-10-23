using System.Net;
using ArriOpto.BLE.Ota;
using Xunit;

namespace ArriLight.Tests;

public sealed class FirmwareOnlineTests
{
    [Theory]
    [InlineData("hex16", OtaProtocol.SingleBank)]
    [InlineData("bin", OtaProtocol.Slb)]
    [InlineData("BIN", OtaProtocol.Slb)]
    [InlineData("HEX16", OtaProtocol.SingleBank)]
    public async Task CheckOnlinePassesHardwareAndVersionAndBuildsObjectDownloadUri(
        string extension, OtaProtocol protocol)
    {
        await using var fixture = new PairingFixture();
        var handler = new CapturingHandler(
            "{\"hardwareVersion\":\"PW01-A1\",\"version\":\"1.0.2\",\"fileName\":\"PW01_A1/1.0.2." + extension + "\",\"sha256\":\"aa:bb:cc:dd:ee:ff:00:11:22:33:44:55:66:77:88:99:aa:bb:cc:dd:ee:ff:00:11:22:33:44:55:66:77:88:99\"}");
        using var httpClient = new HttpClient(handler);
        var configuration = new FirmwareUpdateConfiguration
        {
            ListWorkerUrl = "https://list.example?tenant=dev",
            GetWorkerUrl = "https://get.example"
        };
        var service = CreateService(fixture, httpClient, configuration);

        var result = await service.CheckOnlineAsync("PW01-A1", "1.0.1");

        Assert.True(result.IsUpdateAvailable);
        var candidate = Assert.IsType<FirmwareCandidate>(result.Candidate);
        Assert.Equal(FirmwareSourceKind.Online, candidate.SourceKind);
        Assert.Equal(protocol, candidate.Protocol);
        Assert.Equal("1.0.2", candidate.Version);
        Assert.Equal($"1.0.2.{extension}", candidate.FileName);
        Assert.Equal(
            "AABBCCDDEEFF00112233445566778899AABBCCDDEEFF00112233445566778899",
            candidate.Sha256);
        Assert.Equal(
            new Uri($"https://get.example?fileName=PW01_A1%2F1.0.2.{extension}"),
            candidate.DownloadUri);

        Assert.NotNull(handler.RequestUri);
        Assert.Contains("tenant=dev", handler.RequestUri!.Query);
        Assert.Contains("hardwareVersion=PW01-A1", Uri.UnescapeDataString(handler.RequestUri.Query));
        Assert.Contains("currentVersion=1.0.1", Uri.UnescapeDataString(handler.RequestUri.Query));
    }

    [Theory]
    [InlineData("1.1.1", true)]
    [InlineData("1.1.0", false)]
    [InlineData("1.0.9", false)]
    public async Task CheckOnlineReadsSlbVersionFromFileNameAndDoesNotOfferDowngrades(
        string version, bool expectedUpdate)
    {
        await using var fixture = new PairingFixture();
        using var httpClient = new HttpClient(new CapturingHandler(
            "{\"fileName\":\"PW01_A2/" + version + ".bin\"}"));
        var service = CreateService(fixture, httpClient, new FirmwareUpdateConfiguration
        {
            ListWorkerUrl = "https://list.example",
            GetWorkerUrl = "https://get.example"
        });

        var result = await service.CheckOnlineAsync("PW01-A2", "1.1.0");

        Assert.Equal(expectedUpdate, result.IsUpdateAvailable);
        var candidate = Assert.IsType<FirmwareCandidate>(result.Candidate);
        Assert.Equal(OtaProtocol.Slb, candidate.Protocol);
        Assert.Equal(version, candidate.Version);
        Assert.Equal(new Uri($"https://get.example?fileName=PW01_A2%2F{version}.bin"), candidate.DownloadUri);
    }

    [Theory]
    [InlineData("1.1.1.bin.zip")]
    [InlineData("1.1.1.hex16.bak")]
    [InlineData("1.1.1.txt")]
    public async Task CheckOnlineRejectsUnsupportedExtensions(string fileName)
    {
        await using var fixture = new PairingFixture();
        using var httpClient = new HttpClient(new CapturingHandler(
            "{\"fileName\":\"PW01_A2/" + fileName + "\"}"));
        var service = CreateService(fixture, httpClient, new FirmwareUpdateConfiguration
        {
            ListWorkerUrl = "https://list.example",
            GetWorkerUrl = "https://get.example"
        });

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.CheckOnlineAsync("PW01-A2", "1.1.0"));

        Assert.Equal(AppResources.Firmware_InvalidOnlineExtension, exception.Message);
    }

    [Fact]
    public async Task CheckOnlineReturnsNoCandidateWhenWorkerReportsNoNewFile()
    {
        await using var fixture = new PairingFixture();
        var handler = new CapturingHandler(
            "{\"hardwareVersion\":\"PW01-A1\",\"version\":null,\"fileName\":null,\"sha256\":null}");
        using var httpClient = new HttpClient(handler);
        var configuration = new FirmwareUpdateConfiguration
        {
            ListWorkerUrl = "https://list.example",
            GetWorkerUrl = "https://get.example"
        };
        var service = CreateService(fixture, httpClient, configuration);

        var result = await service.CheckOnlineAsync("PW01-A1", "1.0.2");

        Assert.False(result.IsUpdateAvailable);
        Assert.Null(result.Candidate);
    }

    private static FirmwareUpdateService CreateService(
        PairingFixture fixture,
        HttpClient httpClient,
        FirmwareUpdateConfiguration configuration)
    {
        var diagnostics = new AppDiagnosticService();
        return new FirmwareUpdateService(
            httpClient,
            new PluginBleOtaClient(fixture.Ble.Adapter),
            fixture.Connection,
            fixture.Service,
            diagnostics,
            configuration);
    }

    private sealed class CapturingHandler(string json) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            });
        }
    }
}
