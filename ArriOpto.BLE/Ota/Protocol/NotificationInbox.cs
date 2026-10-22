using System.Threading.Channels;
using ArriOpto.BLE.Ota;

namespace ArriOpto.BLE.Ota.Protocol;

internal sealed class NotificationInbox : IDisposable
{
    private readonly IOtaGattConnection _connection;
    private readonly object _gate = new();
    private long _sequence;
    private readonly record struct Entry(long Sequence, ReadOnlyMemory<byte> Data);
    private readonly Channel<Entry> _channel =
        Channel.CreateUnbounded<Entry>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });

    // 在发送任何命令前构造 Inbox，确保设备快速响应时 notification 不会丢失。
    public NotificationInbox(IOtaGattConnection connection)
    {
        _connection = connection;
        _connection.NotificationReceived += OnNotificationReceived;
    }

    // A transfer attempt may only consume notifications received after it began.
    public long Sequence { get { lock (_gate) return _sequence; } }

    public bool TryRead(
        Func<ReadOnlyMemory<byte>, bool> predicate,
        long afterSequence,
        out ReadOnlyMemory<byte> data)
    {
        while (_channel.Reader.TryRead(out var entry))
        {
            if (entry.Sequence > afterSequence && predicate(entry.Data))
            {
                data = entry.Data;
                return true;
            }
        }
        data = default;
        return false;
    }

    public async Task<ReadOnlyMemory<byte>> WaitAsync(
        Func<ReadOnlyMemory<byte>, bool> predicate,
        TimeSpan timeout,
        string description,
        CancellationToken cancellationToken,
        long afterSequence = 0)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            while (await _channel.Reader.WaitToReadAsync(timeoutSource.Token).ConfigureAwait(false))
            {
                if (TryRead(predicate, afterSequence, out var data))
                {
                    return data;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Timed out after {timeout.TotalSeconds:0.###} seconds while waiting for {description}.");
        }

        throw new EndOfStreamException($"Notification stream ended while waiting for {description}.");
    }

    public void Dispose()
    {
        _connection.NotificationReceived -= OnNotificationReceived;
        _channel.Writer.TryComplete();
    }

    private void OnNotificationReceived(object? sender, OtaNotificationEventArgs args)
    {
        // BLE 库可能复用 characteristic Value 数组，进入异步队列前必须复制。
        lock (_gate)
        {
            _channel.Writer.TryWrite(new Entry(++_sequence, args.Data.ToArray()));
        }
    }
}
