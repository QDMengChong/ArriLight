using Xunit;

namespace ArriLight.Tests;

public sealed class BleTimingTests
{
#if DEBUG
    [Fact]
    public async Task NestedStagesShareOperationAcrossAwaitAndRestoreTheirParent()
    {
        var diagnostics = new AppDiagnosticService();
        using var operation = diagnostics.BeginBleTiming("click", "device");

        async Task RunStageAsync()
        {
            using var stage = diagnostics.BeginBleTiming("connect");
            Assert.Equal(operation.OperationId, stage.OperationId);
            await Task.Yield();
            Assert.Same(stage, BleTimingScope.Current);
            stage.SetResult("cancelled");
            stage.Dispose();
            Assert.Same(operation, BleTimingScope.Current);
        }

        await RunStageAsync();
        Assert.Same(operation, BleTimingScope.Current);
        var ended = Assert.Single(diagnostics.Entries,
            entry => entry.Message.Contains("stage=connect event=end"));
        Assert.Contains($"parent={operation.SpanId:D6}", ended.Message);
        Assert.Contains("device=device", ended.Message);
        Assert.Contains("result=cancelled", ended.Message);
    }

    [Fact]
    public async Task ConcurrentOperationsHaveIndependentIdsAndCapturedCallbacksKeepTheirOrigin()
    {
        var diagnostics = new AppDiagnosticService();
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;

        async Task<long> RunAsync(string name)
        {
            using var operation = diagnostics.BeginBleTiming(name);
            if (Interlocked.Increment(ref started) == 2)
                barrier.SetResult();
            await barrier.Task;
            using var stage = diagnostics.BeginBleTiming("child");
            Assert.Equal(operation.OperationId, stage.OperationId);
            Action<string> mark = detail => stage.Mark(detail);
            Task callback;
            using (ExecutionContext.SuppressFlow())
            {
                callback = Task.Run(() =>
                {
                    Assert.Null(BleTimingScope.Current);
                    mark("nativeCallback");
                    Assert.Null(BleTimingScope.Current);
                });
            }
            await callback;
            return operation.OperationId;
        }

        var ids = await Task.WhenAll(RunAsync("first"), RunAsync("second"));
        Assert.NotEqual(ids[0], ids[1]);
        foreach (var id in ids)
            Assert.Single(diagnostics.Entries, entry =>
                entry.Message.Contains($"id={id:D6} ") && entry.Message.Contains("nativeCallback"));
        Assert.Null(BleTimingScope.Current);
    }

    [Fact]
    public async Task FirstPowerOperationCorrelatesEarlyProtocolResponsesAndSessionReuse()
    {
        await using var fixture = new PairingFixture();
        fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);
        fixture.Ble.Capabilities |= ArriOpto.BLE.Agp.AgpCapabilities.OnOff;
        var diagnostics = fixture.Connection.Diagnostics;

        using (var click = diagnostics.BeginBleTiming("click", fixture.Address))
        {
            await fixture.Service.SetOnOffAsync(fixture.Address, true);
            var responses = diagnostics.Entries.Where(entry =>
                entry.Message.Contains("responseReceived")).ToList();
            Assert.Contains(responses, entry => entry.Message.Contains("(AuthToken)"));
            Assert.Contains(responses, entry => entry.Message.Contains("(OnOffSet)"));
            Assert.All(responses, entry => Assert.Contains($"id={click.OperationId:D6} ", entry.Message));
        }

        await fixture.Service.SetOnOffAsync(fixture.Address, false);
        Assert.Contains(diagnostics.Entries, entry => entry.Message.Contains("stage=AGP.Session.Prepare event=end") &&
                                                     entry.Message.Contains("result=reused"));
        Assert.Single(fixture.Ble.ConnectAttempts);
        Assert.Single(fixture.Ble.Requests, opcode => opcode == ArriOpto.BLE.Agp.AgpOpcode.AuthToken);
    }
#else
    [Fact]
    public async Task ReleasePowerOperationDoesNotCreateTimingOutputOrContext()
    {
        await using var fixture = new PairingFixture();
        fixture.Repository.SetToken(fixture.Address, [1, 2, 3, 4]);
        fixture.Ble.Capabilities |= ArriOpto.BLE.Agp.AgpCapabilities.OnOff;
        var diagnostics = fixture.Connection.Diagnostics;
        using var scope = diagnostics.BeginBleTiming("release", fixture.Address);
        await fixture.Service.SetOnOffAsync(fixture.Address, true);
        Assert.Null(BleTimingScope.Current);
        Assert.DoesNotContain(diagnostics.Entries, entry => entry.Area == "BLE/Timing");
        Assert.Single(fixture.Ble.ConnectAttempts);
        Assert.Contains(ArriOpto.BLE.Agp.AgpOpcode.OnOffSet, fixture.Ble.Requests);
    }
#endif
}
