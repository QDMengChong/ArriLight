using Xunit;

namespace ArriLight.Tests;

public sealed class BackExitGateTests
{
    [Fact]
    public void SecondBackWithinTwoSecondsConfirmsExit()
    {
        var time = new ManualTimeProvider();
        var gate = new BackExitGate(time);

        Assert.False(gate.ShouldExit());
        time.Advance(TimeSpan.FromMilliseconds(1500));
        Assert.True(gate.ShouldExit());
        Assert.False(gate.ShouldExit());
    }

    [Fact]
    public void ExpiredPressStartsANewConfirmationWindow()
    {
        var time = new ManualTimeProvider();
        var gate = new BackExitGate(time);

        Assert.False(gate.ShouldExit());
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.False(gate.ShouldExit());
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(gate.ShouldExit());
    }

    [Fact]
    public void NavigationOrBackgroundResetRequiresAnotherFirstPress()
    {
        var time = new ManualTimeProvider();
        var gate = new BackExitGate(time);

        Assert.False(gate.ShouldExit());
        time.Advance(TimeSpan.FromMilliseconds(500));
        gate.Reset();
        Assert.False(gate.ShouldExit());
        Assert.True(gate.ShouldExit());
    }
}
