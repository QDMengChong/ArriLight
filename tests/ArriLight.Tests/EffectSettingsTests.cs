using ArriOpto.BLE.Agp;
using Xunit;

namespace ArriLight.Tests;

public sealed class EffectSettingsTests
{
    public EffectSettingsTests() => Preferences.Default.Clear();

    [Theory]
    [InlineData(AgpLightMode.Blink, "闪烁参数")]
    [InlineData(AgpLightMode.Breathe, "呼吸灯参数")]
    public async Task ApplyUpdatesActiveEffectOnlyAfterConfirmationWithoutChangingSelection(
        AgpLightMode mode, string title)
    {
        var page = new DeviceControlPage { ActiveEffect = mode };
        var opening = page.OpenForTestAsync(mode);
        Assert.True(page.DialogIsVisible);
        Assert.Equal(title, page.DialogTitle);
        page.ChangePeriodForTest(1_460);
        Assert.Empty(page.SentEffects);

        page.ApplyForTest();
        Assert.False(page.DialogIsVisible);
        await opening.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal([(mode, (ushort)1_500)], page.SentEffects);
        Assert.Equal((ushort)1_500, page.PeriodForTest(mode));
        Assert.Equal(mode, page.ActiveEffect);
        Assert.False(page.DialogIsOpen);
    }

    [Fact]
    public async Task CancelClosesImmediatelyAndAllowsReopeningWithoutChangingPeriod()
    {
        var page = new DeviceControlPage();
        var opening = page.OpenForTestAsync(AgpLightMode.Blink);
        page.ChangePeriodForTest(2_000);
        page.CancelForTest();
        Assert.False(page.DialogIsVisible);
        await opening.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Empty(page.SentEffects);
        Assert.Equal((ushort)1_000, page.PeriodForTest(AgpLightMode.Blink));

        var reopening = page.OpenForTestAsync(AgpLightMode.Breathe);
        Assert.True(page.DialogIsVisible);
        Assert.Equal("呼吸灯参数", page.DialogTitle);
        page.CancelForTest();
        await reopening.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData("back")]
    [InlineData("disconnect")]
    [InlineData("leave")]
    public async Task LifecycleCancellationClosesDialogAndCompletesWaitingTask(string reason)
    {
        var page = new DeviceControlPage();
        var opening = page.OpenForTestAsync(AgpLightMode.Blink);
        if (reason == "back")
            Assert.True(page.BackForTest());
        else if (reason == "disconnect")
            page.DisconnectForTest();
        else
            page.LeaveForTest();

        Assert.False(page.DialogIsVisible);
        await opening.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(page.DialogIsOpen);
        Assert.Empty(page.SentEffects);
        Assert.False(page.BackForTest());
    }

    [Fact]
    public async Task RepeatedOpenKeepsOneDialogAndItsOriginalMode()
    {
        var page = new DeviceControlPage();
        var opening = page.OpenForTestAsync(AgpLightMode.Blink);
        await page.OpenForTestAsync(AgpLightMode.Breathe).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal("闪烁参数", page.DialogTitle);
        page.ApplyForTest();
        await opening.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Empty(page.SentEffects);
        Assert.Equal(AgpLightMode.Steady, page.ActiveEffect);
    }

    [Theory]
    [InlineData(false, true, AgpLightMode.Blink)]
    [InlineData(true, false, AgpLightMode.Blink)]
    [InlineData(true, true, AgpLightMode.Steady)]
    public async Task UnavailableSettingsDoNotOpenOrWait(bool online, bool supported, AgpLightMode mode)
    {
        var page = new DeviceControlPage { IsDeviceOnline = online, EffectsSupported = supported };
        await page.OpenForTestAsync(mode).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(page.DialogIsVisible);
        Assert.False(page.DialogIsOpen);
        Assert.Empty(page.SentEffects);
    }

    [Fact]
    public async Task EachEffectKeepsItsOwnPeriodWhenEditingAndSwitching()
    {
        var page = new DeviceControlPage();
        var blinkOpening = page.OpenForTestAsync(AgpLightMode.Blink);
        page.ChangePeriodForTest(10_000);
        page.ApplyForTest();
        await blinkOpening.WaitAsync(TimeSpan.FromSeconds(1));

        var breatheOpening = page.OpenForTestAsync(AgpLightMode.Breathe);
        Assert.Equal(1_000, page.DialogPeriod);
        page.ChangePeriodForTest(3_400);
        page.ApplyForTest();
        await breatheOpening.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Empty(page.SentEffects);
        Assert.Equal(AgpLightMode.Steady, page.ActiveEffect);

        await page.SelectForTestAsync(AgpLightMode.Blink);
        await page.SelectForTestAsync(AgpLightMode.Breathe);
        Assert.Equal(
            [(AgpLightMode.Blink, (ushort)10_000), (AgpLightMode.Breathe, (ushort)3_400)],
            page.SentEffects);

        var reopening = page.OpenForTestAsync(AgpLightMode.Blink);
        Assert.Equal(10_000, page.DialogPeriod);
        page.CancelForTest();
        await reopening.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(AgpLightMode.Breathe, page.ActiveEffect);
    }

    [Fact]
    public async Task SavedParametersSurvivePageRecreationAndRemainDeviceSpecific()
    {
        var page = new DeviceControlPage();
        page.SynchronizeForTest(AgpLightMode.Blink, 7_000);
        page.SynchronizeForTest(AgpLightMode.Breathe, 2_200);

        var recreated = new DeviceControlPage();
        var otherDevice = new DeviceControlPage("11:22:33:44:55:66");
        Assert.Equal((ushort)7_000, recreated.PeriodForTest(AgpLightMode.Blink));
        Assert.Equal((ushort)2_200, recreated.PeriodForTest(AgpLightMode.Breathe));
        Assert.Equal((ushort)1_000, otherDevice.PeriodForTest(AgpLightMode.Blink));
        Assert.Equal((ushort)1_000, otherDevice.PeriodForTest(AgpLightMode.Breathe));

        var opening = recreated.OpenForTestAsync(AgpLightMode.Breathe);
        Assert.Equal(2_200, recreated.DialogPeriod);
        recreated.CancelForTest();
        await opening.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void DeviceStateUpdatesOnlyTheReportedEffectAndSteadyDoesNotOverwriteParameters()
    {
        var page = new DeviceControlPage();
        page.SynchronizeForTest(AgpLightMode.Blink, 8_000);
        page.SynchronizeForTest(AgpLightMode.Breathe, 2_500);
        page.SynchronizeForTest(AgpLightMode.Blink, 4_000);
        page.SynchronizeForTest(AgpLightMode.Steady, 4_000);

        Assert.Equal((ushort)4_000, page.PeriodForTest(AgpLightMode.Blink));
        Assert.Equal((ushort)2_500, page.PeriodForTest(AgpLightMode.Breathe));
    }

    [Fact]
    public async Task RejectedApplyDoesNotSaveEditedParameters()
    {
        var page = new DeviceControlPage { RejectEffect = true, ActiveEffect = AgpLightMode.Blink };
        var opening = page.OpenForTestAsync(AgpLightMode.Blink);
        page.ChangePeriodForTest(10_000);
        page.ApplyForTest();
        await opening.WaitAsync(TimeSpan.FromSeconds(1));

        var recreated = new DeviceControlPage();
        Assert.Equal((ushort)1_000, page.PeriodForTest(AgpLightMode.Blink));
        Assert.Equal((ushort)1_000, recreated.PeriodForTest(AgpLightMode.Blink));
        Assert.Equal((ushort)1_000, recreated.PeriodForTest(AgpLightMode.Breathe));
        Assert.Equal(AgpLightMode.Blink, page.ActiveEffect);
    }

    [Theory]
    [InlineData(AgpLightMode.Steady, AgpLightMode.Blink)]
    [InlineData(AgpLightMode.Breathe, AgpLightMode.Blink)]
    [InlineData(AgpLightMode.Blink, AgpLightMode.Breathe)]
    public async Task ApplyingInactiveEffectSavesParametersWithoutSendingOrChangingSelection(
        AgpLightMode active, AgpLightMode edited)
    {
        var page = new DeviceControlPage { ActiveEffect = active };
        var opening = page.OpenForTestAsync(edited);
        Assert.Equal(active, page.ActiveEffect);
        page.ChangePeriodForTest(10_000);
        page.ApplyForTest();
        await opening.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(active, page.ActiveEffect);
        Assert.Empty(page.SentEffects);
        Assert.Equal((ushort)10_000, page.PeriodForTest(edited));
        Assert.Equal((ushort)10_000, new DeviceControlPage().PeriodForTest(edited));
    }

    [Theory]
    [InlineData(AgpLightMode.Steady, AgpLightMode.Breathe)]
    [InlineData(AgpLightMode.Blink, AgpLightMode.Blink)]
    [InlineData(AgpLightMode.Breathe, AgpLightMode.Blink)]
    public async Task CancellingPreservesSelectionAndSavedParameters(
        AgpLightMode active, AgpLightMode edited)
    {
        var page = new DeviceControlPage { ActiveEffect = active };
        var opening = page.OpenForTestAsync(edited);
        page.ChangePeriodForTest(10_000);
        page.CancelForTest();
        await opening.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(active, page.ActiveEffect);
        Assert.Empty(page.SentEffects);
        Assert.Equal((ushort)1_000, page.PeriodForTest(edited));
        Assert.Equal((ushort)1_000, new DeviceControlPage().PeriodForTest(edited));
    }
}
