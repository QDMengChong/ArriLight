using Plugin.BLE.Abstractions.Exceptions;
using Xunit;

namespace ArriLight.Tests;

public sealed class BindingPromptTests
{
    [Fact]
    public async Task SuccessfulAddClosesPromptAndStaysOnScanPageBeforeCountdownDeadline()
    {
        await using var fixture = new PairingFixture();
        fixture.Ble.DisconnectAfterToken = true;
        fixture.Repository.Remove(fixture.Address);
        var page = CreatePage(fixture);
        var adding = page.AddForTestAsync("PW01", fixture.Address);
        try
        {
            await adding.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.False(page.PromptIsVisible);
            Assert.Null(page.CountdownTask);
            Assert.Equal("A1B2C3D4", fixture.Repository.GetToken(fixture.Address));
            Assert.Contains("添加成功", ToastService.Messages);
            Assert.Empty(Shell.Current.Routes);
        }
        finally
        {
            page.CancelForTest();
            page.ExpireCountdownForTest();
            await adding.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task CancelAfterServiceTokenSourceDisposedStopsCountdownAndClosesPrompt()
    {
        await using var fixture = new PairingFixture();
        var page = CreatePage(fixture);
        using var pageCancellation = new CancellationTokenSource();
        page.SetPageCancellationForTest(pageCancellation);
        using (var serviceCancellation = CancellationTokenSource.CreateLinkedTokenSource(pageCancellation.Token))
            await page.ShowProgressForTestAsync(serviceCancellation.Token);
        var countdown = page.CountdownTask!;
        try
        {
            page.CancelForTest();
            await countdown.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.True(pageCancellation.IsCancellationRequested);
            Assert.False(page.PromptIsVisible);
        }
        finally
        {
            page.ExpireCountdownForTest();
            await countdown.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task CancelDuringConnectionWaitImmediatelyClosesPromptAndRemovesPendingDevice()
    {
        await using var fixture = new PairingFixture();
        fixture.Repository.Remove(fixture.Address);
        fixture.Ble.OnConnect = (_, _) => Task.FromException(
            new DeviceConnectionException(fixture.Ble.Device.Id, "PW01", "busy"));
        var page = CreatePage(fixture);
        var adding = page.AddForTestAsync("PW01", fixture.Address);
        Assert.True(page.PromptIsVisible);

        page.CancelForTest();

        Assert.False(page.PromptIsVisible);
        await adding.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(fixture.Repository.Contains(fixture.Address));
        Assert.DoesNotContain("添加成功", ToastService.Messages);
        Assert.Single(fixture.Ble.ConnectAttempts);
    }

    [Fact]
    public async Task LeavingPageImmediatelyClosesPromptAndStopsPairing()
    {
        await using var fixture = new PairingFixture();
        fixture.Repository.Remove(fixture.Address);
        fixture.Ble.OnConnect = (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token);
        var page = CreatePage(fixture);
        var adding = page.AddForTestAsync("PW01", fixture.Address);
        Assert.True(page.PromptIsVisible);

        page.DisappearForTest();

        Assert.False(page.PromptIsVisible);
        await adding.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(fixture.Repository.Contains(fixture.Address));
    }

    private static BluetoothScanPage CreatePage(PairingFixture fixture)
    {
        ServiceHelper.Set(fixture.Discovery);
        ServiceHelper.Set(fixture.Repository);
        ServiceHelper.Set(fixture.Service);
        ToastService.Messages.Clear();
        Shell.Current.Routes.Clear();
        return new BluetoothScanPage();
    }
}
