namespace ArriOpto.BLE.Ota;

/// <summary>
/// 表示可归类到 <see cref="OtaState"/> 的 OTA 领域错误。
/// </summary>
/// <remarks>
/// 文件系统错误、格式错误、超时和调用方取消仍保留标准 .NET 异常类型，便于上层
/// 分别处理。捕获本异常时可使用 <see cref="State"/> 生成稳定的错误分类。
/// </remarks>
public class OtaException : Exception
{
    /// <summary>使用 OTA 状态和诊断消息创建异常。</summary>
    public OtaException(OtaState state, string message)
        : base(message)
    {
        State = state;
    }

    /// <summary>使用 OTA 状态、诊断消息和底层异常创建异常。</summary>
    public OtaException(OtaState state, string message, Exception innerException)
        : base(message, innerException)
    {
        State = state;
    }

    /// <summary>与错误对应的稳定 OTA 状态码。</summary>
    public OtaState State { get; }
}
