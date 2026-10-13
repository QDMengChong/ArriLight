namespace ArriOpto.BLE.Mesh;

/// <summary>已完成网络层和传输层解密的 Mesh 访问消息。</summary>
public sealed record MeshAccessMessage(
    ushort Source,
    ushort Destination,
    byte[] AccessPdu);
