namespace ArriOpto.BLE.Mesh;

/// <summary>
/// Mesh 与 BLE GATT 之间的字节链路抽象（由宿主应用实现）。
/// </summary>
/// <remarks>
/// ArriLight 用 Plugin.BLE 实现：配网阶段写入 0x2ADB / 订阅 0x2ADC 通知，
/// 配网完成后切换到 0x2ADD / 0x2ADE（服务也由 0x1827 变为 0x1828，
/// 宿主负责重新发现并切换端点）。链路上传输的是完整 Proxy PDU。
/// </remarks>
public interface IMeshLink
{
    /// <summary>写入一个完整的 Proxy PDU（Write Without Response）。</summary>
    Task WriteAsync(ReadOnlySpan<byte> pdu, CancellationToken cancellationToken = default);

    /// <summary>从 Data Out 特征收到一个完整的 Proxy PDU（通知）。</summary>
    event EventHandler<byte[]> Received;

    /// <summary>协商 ATT MTU（建议 ≥ 247，单包容纳整个 Proxy PDU）。</summary>
    Task RequestMtuAsync(int mtu, CancellationToken cancellationToken = default);
}
