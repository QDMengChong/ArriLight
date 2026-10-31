namespace ArriOpto.BLE.Agp;

/// <summary>仅重试可重试命令的 Busy 响应；恢复出厂不重试。次数不包含首次发送。</summary>
public sealed record AgpBatchOptions
{
    public int MaxBusyRetries { get; init; } = 3;
    public TimeSpan InitialBusyRetryDelay { get; init; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan MaximumBusyRetryDelay { get; init; } = TimeSpan.FromMilliseconds(400);
    /// <summary>每次尝试的响应超时；取 Token 命令使用其工厂指定的超时。</summary>
    public TimeSpan RequestTimeout { get; init; } = AgpClient.DefaultResponseTimeout;

    internal void Validate()
    {
        if (MaxBusyRetries < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxBusyRetries));
        ValidateTimeout(RequestTimeout, nameof(RequestTimeout));
        ValidateDelay(InitialBusyRetryDelay, nameof(InitialBusyRetryDelay));
        ValidateDelay(MaximumBusyRetryDelay, nameof(MaximumBusyRetryDelay));
        if (MaximumBusyRetryDelay < InitialBusyRetryDelay)
            throw new ArgumentException("Maximum retry delay must be at least the initial delay.");
    }

    internal static void ValidateTimeout(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero || value.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(name);
    }

    private static void ValidateDelay(TimeSpan value, string name)
    {
        if (value < TimeSpan.Zero || value.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(name);
    }
}

/// <summary>一次发送尝试；没有可解码响应时 Result 和 Response 为 null。</summary>
public sealed record AgpBatchAttemptResult(
    ushort TransactionId, AgpResultCode? Result, object? Response);

/// <summary>一个步骤的所有尝试；最后一项决定步骤结果。</summary>
public sealed class AgpBatchStepResult
{
    internal AgpBatchStepResult(int index, AgpCommand command, IEnumerable<AgpBatchAttemptResult> attempts)
    {
        Index = index;
        Command = command;
        AttemptResults = Array.AsReadOnly(attempts.ToArray());
    }

    /// <summary>步骤索引从 0 开始。</summary>
    public int Index { get; }
    public AgpCommand Command { get; }
    public AgpOpcode Opcode => Command.Opcode;
    public IReadOnlyList<AgpBatchAttemptResult> AttemptResults { get; }
    public int Attempts => AttemptResults.Count;
    public ushort TransactionId => AttemptResults[^1].TransactionId;
    public AgpResultCode? Result => AttemptResults[^1].Result;
    public object? Response => AttemptResults[^1].Response;
    public bool IsSuccess => Result == AgpResultCode.Success;

    public TResponse GetResponse<TResponse>() where TResponse : class =>
        Response as TResponse ?? throw new InvalidOperationException("No response of the requested type is available.");

    /// <summary>按原命令获取响应，保留工厂的响应类型。</summary>
    public TResponse GetResponse<TResponse>(AgpCommand<TResponse> command) where TResponse : class
    {
        if (!ReferenceEquals(Command, command))
            throw new ArgumentException("The command does not belong to this step.", nameof(command));
        return GetResponse<TResponse>();
    }
}

/// <summary>批量进度快照。已成功步骤保留；失败和取消不回滚。</summary>
public sealed class AgpBatchResult
{
    internal AgpBatchResult(int commandCount, IEnumerable<AgpBatchStepResult> steps, bool isSuccess, int? failedStepIndex)
    {
        CommandCount = commandCount;
        Steps = Array.AsReadOnly(steps.ToArray());
        IsSuccess = isSuccess;
        FailedStepIndex = failedStepIndex;
    }

    public int CommandCount { get; }
    public IReadOnlyList<AgpBatchStepResult> Steps { get; }
    public bool IsSuccess { get; }
    public int CompletedCount => Steps.Count(step => step.IsSuccess);
    /// <summary>失败或中止的步骤索引；未开始的步骤可能尚无 StepResult。</summary>
    public int? FailedStepIndex { get; }
}

/// <summary>传输、超时或协议异常；Progress 保留已执行步骤，原异常在 InnerException。</summary>
public sealed class AgpBatchException : Exception
{
    internal AgpBatchException(AgpBatchResult progress, Exception innerException)
        : base($"AGP batch stopped at step {progress.FailedStepIndex}.", innerException) => Progress = progress;

    public AgpBatchResult Progress { get; }
}

/// <summary>批次取消；保持 OperationCanceledException 语义并附带执行进度。</summary>
public sealed class AgpBatchCanceledException : OperationCanceledException
{
    internal AgpBatchCanceledException(AgpBatchResult progress, OperationCanceledException innerException,
        CancellationToken cancellationToken)
        : base($"AGP batch cancelled at step {progress.FailedStepIndex}.", innerException, cancellationToken) =>
        Progress = progress;

    public AgpBatchResult Progress { get; }
}
