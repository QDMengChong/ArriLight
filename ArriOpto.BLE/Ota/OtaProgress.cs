namespace ArriOpto.BLE.Ota;

/// <summary>
/// 一次 OTA 状态或传输进度快照。
/// </summary>
/// <param name="State">当前流程状态。</param>
/// <param name="Percentage">完成百分比，范围为 0～100。</param>
/// <param name="BytesTransferred">设备已确认或当前已发送的字节数，语义由状态决定。</param>
/// <param name="TotalBytes">本次固件总字节数；状态事件中可能为 0。</param>
/// <param name="Message">适合日志记录的诊断消息，不建议直接作为最终本地化 UI 文案。</param>
public sealed record OtaProgress(
    OtaState State,
    double Percentage,
    long BytesTransferred,
    long TotalBytes,
    string Message)
{
    /// <summary>
    /// 创建一个不包含字节进度的状态事件。
    /// </summary>
    public static OtaProgress Status(OtaState state, string message) =>
        new(state, 0, 0, 0, message);
}
