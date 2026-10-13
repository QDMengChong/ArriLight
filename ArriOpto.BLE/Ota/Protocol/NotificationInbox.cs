using System.Threading.Channels;
using ArriOpto.BLE.Ota;

namespace ArriOpto.BLE.Ota.Protocol;

internal sealed class NotificationInbox : IDisposable
{
    private readonly IOtaGattConnection _connection;
    private readonly Channel<ReadOnlyMemory<byte>> _channel =
        Channel.CreateUnbounded<ReadOnlyMemory<byte>>(
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

    public async Task<ReadOnlyMemory<byte>> WaitAsync(
        Func<ReadOnlyMemory<byte>, bool> predicate,
        TimeSpan timeout,
        string description,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            while (await _channel.Reader.WaitToReadAsync(timeoutSource.Token).ConfigureAwait(false))
            {
                while (_channel.Reader.TryRead(out var data))
                {
                    if (predicate(data))
                    {
                        return data;
                    }
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
        _channel.Writer.TryWrite(args.Data.ToArray());
    }
}
