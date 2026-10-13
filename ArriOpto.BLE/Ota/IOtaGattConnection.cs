
namespace ArriOpto.BLE.Ota;

/// <summary>
/// OTA 通知事件参数。数据在传输边界已经复制，不依赖 BLE 库内部缓冲区生命周期。
/// </summary>
public sealed class OtaNotificationEventArgs : EventArgs
{
    /// <summary>使用一条完整的 GATT notification 数据创建事件。</summary>
    public OtaNotificationEventArgs(ReadOnlyMemory<byte> data)
    {
        Data = data;
    }

    /// <summary>设备发送的原始 notification 字节。</summary>
    public ReadOnlyMemory<byte> Data { get; }
}

/// <summary>
/// OTA 协议状态机使用的最小 GATT 连接抽象。
/// </summary>
/// <remarks>
/// 实现者负责：连接已建立、服务和特征已发现、通知已启用、写操作串行化，以及在
/// <see cref="IAsyncDisposable.DisposeAsync"/> 时进行 best-effort 清理。Core 通过该接口与
/// Plugin.BLE 解耦，测试可使用内存实现模拟设备。
/// </remarks>
public interface IOtaGattConnection : IAsyncDisposable
{
    /// <summary>设备发来 OTA 协议 notification 时触发。</summary>
    event EventHandler<OtaNotificationEventArgs>? NotificationReceived;

    /// <summary>
    /// 用于日志关联的跨本连接标识；不得假设它是真实 BLE MAC。
    /// </summary>
    string DeviceId { get; }

    /// <summary>服务发现确定的 OTA GATT 模式。</summary>
    OtaGattMode Mode { get; }

    /// <summary>本连接实际协商到的 ATT MTU。</summary>
    int Mtu { get; }

    /// <summary>
    /// 通过命令特征发送完整协议命令。实现应使用 Write With Response。
    /// </summary>
    Task WriteCommandAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 通过数据特征发送一个已按 MTU 分片的数据包。实现应使用
    /// Write Without Response。
    /// </summary>
    Task WriteDataAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default);

    /// <summary>停止通知并断开连接。重复调用应安全返回。</summary>
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}
