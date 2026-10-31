namespace ArriOpto.BLE.Agp;

public sealed partial class AgpClient
{
    /// <summary>
    /// 顺序执行整组命令，期间其他 Control Point 请求等待。业务失败返回进度；
    /// 只对允许重试的命令在 Busy 时使用新 TID 重试；恢复出厂不重试。已成功步骤不回滚。
    /// </summary>
    public async Task<AgpBatchResult> ExecuteBatchAsync(
        IReadOnlyList<AgpCommand> commands,
        AgpBatchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commands);
        var batch = commands.ToArray();
        foreach (var command in batch)
            ArgumentNullException.ThrowIfNull(command, nameof(commands));
        var settings = options ?? new AgpBatchOptions();
        settings.Validate();
        ThrowIfDisposed();

        var steps = new List<AgpBatchStepResult>();
        var attempts = new List<AgpBatchAttemptResult>();
        var index = 0;
        var gateHeld = false;
        AgpBatchResult Progress(bool success) => new(batch.Length, steps,
            success, success || batch.Length == 0 ? null : index);

        try
        {
            using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCancellation.Token);
            var token = execution.Token;
            token.ThrowIfCancellationRequested();
            if (batch.Length == 0)
                return Progress(true);

            await _requestLock.WaitAsync(token).ConfigureAwait(false);
            gateHeld = true;
            for (; index < batch.Length; index++)
            {
                var command = batch[index];
                attempts.Clear();
                var delay = settings.InitialBusyRetryDelay;
                while (true)
                {
                    ThrowIfDisposed();
                    token.ThrowIfCancellationRequested();
                    var transactionId = NextTransactionId();
                    attempts.Add(new(transactionId, null, null));
                    var message = await SendRequestOnceAsync(command.Opcode, command.Payload,
                        command.ExpectedResponseOpcodes, transactionId,
                        command.Timeout ?? settings.RequestTimeout, token, rejectInvalidResponse: true)
                        .ConfigureAwait(false);
                    var decoded = command.Decode(message);
                    attempts[^1] = new(transactionId, decoded.Result, decoded.Response);
                    if (decoded.Result != AgpResultCode.Busy || !command.RetryOnBusy ||
                        attempts.Count > settings.MaxBusyRetries)
                    {
                        steps.Add(new(index, command, attempts));
                        attempts.Clear();
                        if (decoded.Result != AgpResultCode.Success)
                            return Progress(false);
                        break;
                    }

                    // Keep the execution gate during backoff so no other command can enter this batch.
                    await Task.Delay(delay, token).ConfigureAwait(false);
                    delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, settings.MaximumBusyRetryDelay.Ticks));
                }
            }

            return Progress(true);
        }
        catch (Exception exception)
        {
            if (attempts.Count != 0)
                steps.Add(new(index, batch[index], attempts));
            var progress = Progress(false);
            if (exception is OperationCanceledException cancelled && !_disposed)
                throw new AgpBatchCanceledException(progress, cancelled, cancellationToken);
            if (exception is OperationCanceledException && _disposed)
                exception = new ObjectDisposedException(nameof(AgpClient));
            throw new AgpBatchException(progress, exception);
        }
        finally
        {
            if (gateHeld)
                _requestLock.Release();
        }
    }
}
