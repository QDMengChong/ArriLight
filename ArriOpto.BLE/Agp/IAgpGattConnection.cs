namespace ArriOpto.BLE.Agp;

/// <summary>AGP GATT characteristic 收包事件；数据在传输边界已经复制。</summary>
public sealed class AgpPacketEventArgs : EventArgs
{
    public AgpPacketEventArgs(ReadOnlyMemory<byte> data)
    {
        Data = data;
    }

    public ReadOnlyMemory<byte> Data { get; }
}

/// <summary>AGP 客户端使用的最小 GATT 连接抽象。</summary>
/// <remarks>
/// 实现者负责发现 AGP Service、开启 Control Point Indicate 和 Light State Notify、
/// 串行化平台 GATT 操作，并在释放时做 best-effort 清理。协议客户端只处理 AGP
/// 帧、事务 ID 和业务响应，因而可用内存连接独立测试。
/// </remarks>
public interface IAgpGattConnection : IAsyncDisposable
{
    event EventHandler<AgpPacketEventArgs>? ControlPointIndicated;
    event EventHandler<AgpPacketEventArgs>? LightStateNotified;

    string DeviceId { get; }

    Task<ReadOnlyMemory<byte>> ReadCapabilitiesAsync(
        CancellationToken cancellationToken = default);

    Task<ReadOnlyMemory<byte>> ReadLightStateAsync(
        CancellationToken cancellationToken = default);

    Task WriteControlPointAsync(
        ReadOnlyMemory<byte> packet,
        CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);
}

/// <summary>支持可选 AGP Color State Read/Notify 特征的 GATT 连接。</summary>
/// <remarks>
/// 单色产品可以实现此接口但返回 <see cref="HasColorStateCharacteristic"/> false；
/// 调用方仍必须以 Capabilities 的 Lightness16/HSL/CTL 位判断产品语义能力。
/// </remarks>
public interface IAgpColorGattConnection : IAgpGattConnection
{
    event EventHandler<AgpPacketEventArgs>? ColorStateNotified;

    bool HasColorStateCharacteristic { get; }

    Task<ReadOnlyMemory<byte>> ReadColorStateAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>支持可选 AGP Battery State Read/Notify 特征的 GATT 连接。</summary>
/// <remarks>仅支持 BATTERY_GET 的旧固件也可能声明 Battery 能力位，须独立发现此特征。</remarks>
public interface IAgpBatteryGattConnection : IAgpGattConnection
{
    event EventHandler<AgpPacketEventArgs>? BatteryStateNotified;

    bool HasBatteryStateCharacteristic { get; }

    Task<ReadOnlyMemory<byte>> ReadBatteryStateAsync(
        CancellationToken cancellationToken = default);
}
