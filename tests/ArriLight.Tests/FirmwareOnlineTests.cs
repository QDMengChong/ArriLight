using System.Net;
using ArriOpto.BLE.Ota;
using Xunit;

namespace ArriLight.Tests;

public sealed class FirmwareOnlineTests
{
    [Fact]
    public async Task CheckOnlinePassesHardwareAndVersionAndBuildsObjectDownloadUri()
    {
        await using var fixture = new PairingFixture();
        var handler = new CapturingHandler(
            "{\"hardwareVersion\":\"PW01-A1\",\"version\":\"1.0.2\",\"fileName\":\"PW01_A1/PW01_A1_1.0.2.hex16\",\"sha256\":\"aa:bb:cc:dd:ee:ff:00:11:22:33:44:55:66:77:88:99:aa:bb:cc:dd:ee:ff:00:11:22:33:44:55:66:77:88:99\"}");
        using var httpClient = new HttpClient(handler);
        var configuration = new FirmwareUpdateConfiguration
        {
            ListWorkerUrl = "https://list.example/api/firmware?tenant=dev",
            GetWorkerUrl = "https://get.example/api/firmware"
        };
        var service = CreateService(fixture, httpClient, configuration);

        var result = await service.CheckOnlineAsync("PW01-A1", "1.0.1");

        Assert.True(result.IsUpdateAvailable);
        var candidate = Assert.IsType<FirmwareCandidate>(result.Candidate);
        Assert.Equal(FirmwareSourceKind.Online, candidate.SourceKind);
        Assert.Equal(OtaProtocol.SingleBank, candidate.Protocol);
        Assert.Equal("1.0.2", candidate.Version);
        Assert.Equal("PW01_A1_1.0.2.hex16", candidate.FileName);
        Assert.Equal(
            "AABBCCDDEEFF00112233445566778899AABBCCDDEEFF00112233445566778899",
            candidate.Sha256);
        Assert.Equal(
            "https://get.example/api/firmware?fileName=PW01_A1%2FPW01_A1_1.0.2.hex16",
            candidate.DownloadUri?.ToString());

        Assert.NotNull(handler.RequestUri);
        Assert.Contains("tenant=dev", handler.RequestUri!.Query);
        Assert.Contains("hardwareVersion=PW01-A1", Uri.UnescapeDataString(handler.RequestUri.Query));
        Assert.Contains("currentVersion=1.0.1", Uri.UnescapeDataString(handler.RequestUri.Query));
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
            ListWorkerUrl = "https://list.example/api/firmware",
            GetWorkerUrl = "https://get.example/api/firmware"
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
