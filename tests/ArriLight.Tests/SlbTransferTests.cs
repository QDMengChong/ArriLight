using System.Buffers.Binary;
using ArriOpto.BLE.Ota;
using ArriOpto.BLE.Ota.Protocol;
using Xunit;

namespace ArriLight.Tests;

public sealed class SlbTransferTests
{
    [Theory]
    [InlineData(247, 240)]
    [InlineData(517, 240)]
    [InlineData(240, 232)]
    [InlineData(23, 16)]
    public async Task AlignedPayloadFitsOfficialSlbAndTransfersExactImage(int mtu, int capacity)
    {
        var device = new SlbDevice(mtu, 97492) { StartTimeAck = true, StalePreviousAck = true };
        await Run(device);
        Assert.Equal(device.Image, device.Received.ToArray());
        Assert.Equal((device.Image.Length + capacity - 1) / capacity, device.Packets.Count);
        Assert.All(device.Packets, p => Assert.InRange(p[3], 1, capacity));
        if (mtu == 247) Assert.Equal(52, device.Packets[^1][3]);
        Assert.True(device.Finished);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(7, false)]
    [InlineData(15, false)]
    [InlineData(7, true)]
    public async Task LostFrameResumesOriginalWindowTailWithSameMessageIds(int missing, bool lastAcceptedConvention)
    {
        var device = new SlbDevice(247, 97492)
        {
            MissingFrame = missing,
            LastAcceptedConvention = lastAcceptedConvention,
            DuplicatePartialAcks = 200
        };
        var run = Run(device);
        if (missing == 15)
        {
            // No later frame can reveal a missing tail: simulate the receiver's
            // silence timer, without making this test depend on wall-clock sleeps.
            await device.WindowSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            device.Acknowledge(15, 15, device.Received.Count);
        }
        await run;
        Assert.Equal(device.Image, device.Received.ToArray());
        Assert.Equal(407 + 16 - missing, device.Packets.Count);
        for (var index = missing; index < 16; index++)
            Assert.Equal(device.Packets[index], device.Packets[16 + index - missing]);
    }

    [Fact]
    public async Task QueuedPartialAndDuplicateAcksCoalesceToHighestOffset()
    {
        var device = new SlbDevice(247, 97492) { IntermediateAck = true };
        await Run(device);
        Assert.Equal(407, device.Packets.Count);
        Assert.Equal(device.Image, device.Received.ToArray());
    }

    [Fact]
    public async Task LostCompletionAckWaitsForReceiverReplayWithoutResendingData()
    {
        var device = new SlbDevice(247, 2452) { HoldDataAck = true };
        var run = Run(device);
        await device.WindowSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(run.IsCompleted);
        var writes = device.Packets.Count;
        device.Acknowledge(10, 10, device.Received.Count);
        await run;
        Assert.Equal(writes, device.Packets.Count);
        Assert.True(device.Finished);
    }

    [Theory]
    [InlineData(3841)]
    [InlineData(241)]
    public async Task ImpossibleAckOffsetFailsBeforeFinish(int offset)
    {
        var device = new SlbDevice(247, 97492) { InvalidOffset = offset };
        await Assert.ThrowsAsync<OtaException>(() => Run(device));
        Assert.False(device.Finished);
        Assert.Equal(16, device.Packets.Count);
    }

    [Fact]
    public async Task DuplicateFeedbackCannotPretendToBeThreeFreshFailedAttempts()
    {
        var device = new SlbDevice(247, 97492) { NeverProgress = true };
        await Assert.ThrowsAsync<OtaException>(() => Run(device));
        Assert.Equal(48, device.Packets.Count); // Three actual send attempts.
        Assert.False(device.Finished);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransferStillRequiresFinalVerification(bool reject)
    {
        var device = new SlbDevice(247, 2452) { HoldVerification = true };
        var run = Run(device);
        await device.FinishSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(run.IsCompleted);
        device.Notify(0x26, [(byte)(reject ? 0 : 1)]);
        if (reject) await Assert.ThrowsAsync<OtaException>(() => run);
        else await run;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Gatt133AfterFinishUsesActualVerificationResult(bool reject)
    {
        var device = new SlbDevice(247, 2452) { Gatt133OnFinish = true, RejectVerification = reject };
        if (reject) await Assert.ThrowsAsync<OtaException>(() => Run(device));
        else await Run(device);
        Assert.True(device.Finished);
    }

    [Fact]
    public async Task Gatt133WithoutVerificationCannotReportSuccess()
    {
        using var cancellation = new CancellationTokenSource();
        var device = new SlbDevice(247, 2452) { Gatt133OnFinish = true, HoldVerification = true };
        var run = Run(device, cancellation.Token);
        await device.FinishSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(run.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalWriteTimeoutUsesActualVerificationResult(bool reject)
    {
        var device = new SlbDevice(247, 2452) { TimeoutOnFinish = true, RejectVerification = reject };
        if (reject) await Assert.ThrowsAsync<OtaException>(() => Run(device));
        else await Run(device);
        Assert.True(device.Finished);
    }

    [Fact]
    public async Task FinalWriteTimeoutWithoutVerificationCannotReportSuccess()
    {
        using var cancellation = new CancellationTokenSource();
        var device = new SlbDevice(247, 2452) { TimeoutOnFinish = true, HoldVerification = true };
        var run = Run(device, cancellation.Token);
        await device.FinishSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(run.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task DeferredVerificationReportsSeparateNonterminalPhase()
    {
        var states = new List<OtaProgress>();
        var device = new SlbDevice(247, 2452) { HoldVerification = true };
        var run = Run(device, progress: new InlineProgress(states.Add));
        await device.FinishSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(run.IsCompleted);
        var verifying = Assert.Single(states, p => p.State == OtaState.VerifyingFirmware);
        Assert.Equal(device.Image.Length, verifying.BytesTransferred);
        Assert.False(verifying.State.IsTerminal());
        Assert.DoesNotContain(states, p => p.State == OtaState.UpgradeCompleteWaitingDisconnect);
        device.Notify(0x26, [1]);
        await run;
        Assert.Equal(OtaState.UpgradeCompleteWaitingDisconnect, states[^1].State);
    }

    private sealed class InlineProgress(Action<OtaProgress> callback) : IProgress<OtaProgress>
    {
        public void Report(OtaProgress progress) => callback(progress);
    }

    private static async Task Run(SlbDevice device, CancellationToken cancellationToken = default,
        IProgress<OtaProgress>? progress = null)
    {
        using var file = new MemoryStream(device.Image);
        var image = Assert.IsType<SlbFirmwareImage>(await FirmwareLoader.LoadAsync(file, "test.slb.bin"));
        await new SlbOtaSession(device, image, new OtaOptions
        {
            CommandTimeout = TimeSpan.FromSeconds(5),
            TransferAcknowledgementTimeout = TimeSpan.FromSeconds(5),
            SlbVerificationTimeout = TimeSpan.FromSeconds(5)
        }, progress).RunAsync(cancellationToken);
    }

    private sealed class SlbDevice(int mtu, int size) : IOtaGattConnection
    {
        public event EventHandler<OtaNotificationEventArgs>? NotificationReceived;
        public string DeviceId => "slb-regression";
        public OtaGattMode Mode => OtaGattMode.Slb;
        public int Mtu => mtu;
        public byte[] Image { get; } = Enumerable.Range(0, size).Select(i => (byte)((i * 17 + i / 251) & 255)).ToArray();
        public List<byte> Received { get; } = [];
        public List<byte[]> Packets { get; } = [];
        public TaskCompletionSource WindowSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FinishSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool StartTimeAck, StalePreviousAck, LastAcceptedConvention, IntermediateAck;
        public bool HoldDataAck, HoldVerification, NeverProgress, Finished;
        public bool Gatt133OnFinish, TimeoutOnFinish, RejectVerification;
        public int MissingFrame = -1, DuplicatePartialAcks, InvalidOffset;
        private int _next, _windowBase;
        private bool _dropped, _gapAcknowledged;

        public Task WriteCommandAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            var frame = SlbFrame.Parse(data);
            switch (frame.Opcode)
            {
                case 0x20: Notify(0x21, [0, 7, 0, 1, 0]); break;
                case 0x22:
                    Notify(0x23, [1, 0, 0, 0, 0, 15]);
                    if (StartTimeAck) Acknowledge(15, 0, 0);
                    break;
                case 0x25:
                    Assert.Equal(Image, Received.ToArray());
                    Finished = true;
                    FinishSent.TrySetResult();
                    if (!HoldVerification) Notify(0x26, [(byte)(RejectVerification ? 0 : 1)]);
                    if (Gatt133OnFinish) throw new IOException("GATT result 133");
                    if (TimeoutOnFinish) throw new TimeoutException("OTA GATT write did not complete within 10 seconds.");
                    break;
                default: throw new InvalidOperationException("Unexpected command.");
            }
            return Task.CompletedTask;
        }

        public Task WriteDataAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            var frame = SlbFrame.Parse(data);
            Assert.Equal(0x2f, frame.Opcode);
            Packets.Add(data.ToArray());
            var last = frame.TotalFrames - 1;
            if (StalePreviousAck && frame.FrameIndex == 0 && Received.Count > 0)
                Acknowledge(15, 15, Received.Count);
            if (NeverProgress)
            {
                if (frame.FrameIndex == last)
                    for (var i = 0; i < 200; i++) Acknowledge(last, 0, 0);
                return Task.CompletedTask;
            }
            if (!_dropped && _windowBase == 0 && frame.FrameIndex == MissingFrame)
                _dropped = true;
            else if (frame.FrameIndex == _next)
            {
                Assert.Equal(Image.AsSpan(Received.Count, frame.Payload.Length).ToArray(), frame.Payload.ToArray());
                Received.AddRange(frame.Payload.ToArray());
                _next++;
                if (IntermediateAck && frame.FrameIndex == 7)
                    for (var i = 0; i < 200; i++) Acknowledge(last, 8, Received.Count);
            }
            else if (!_gapAcknowledged)
            {
                var index = LastAcceptedConvention ? Math.Max(0, _next - 1) : _next;
                for (var i = 0; i <= DuplicatePartialAcks; i++) Acknowledge(last, index, Received.Count);
                _gapAcknowledged = true;
            }
            if (frame.FrameIndex == last)
            {
                WindowSent.TrySetResult();
                if (InvalidOffset != 0) Acknowledge(last, 1, InvalidOffset);
                else if (_next == frame.TotalFrames)
                {
                    if (!HoldDataAck) Acknowledge(last, last, Received.Count);
                    _next = 0; _windowBase = Received.Count; _gapAcknowledged = false;
                }
            }
            return Task.CompletedTask;
        }

        public void Acknowledge(int last, int index, int offset)
        {
            var payload = new byte[5];
            payload[0] = (byte)((last << 4) | index);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1), (uint)offset);
            Notify(0x24, payload);
        }

        public void Notify(byte opcode, byte[] payload)
        {
            var data = new byte[payload.Length + 4];
            data[0] = 1; data[1] = opcode; data[3] = (byte)payload.Length;
            payload.CopyTo(data, 4);
            NotificationReceived?.Invoke(this, new OtaNotificationEventArgs(data));
        }
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
