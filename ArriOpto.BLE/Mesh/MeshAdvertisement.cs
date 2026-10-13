using System.Buffers.Binary;
using Plugin.BLE.Abstractions;

namespace ArriOpto.BLE.Mesh;

/// <summary>
/// Mesh 广播所处的配网阶段。设备可能同时广播 Provisioning 与 Proxy，
/// 此时以 Proxy（已配网）为准：它决定宿主能否直接建立控制连接。
/// </summary>
public enum MeshAdvertisementKind
{
    None,
    Provisioning,
    Proxy
}

/// <summary>
/// 从 Plugin.BLE 的广播记录里识别 SIG Mesh 设备（PB-GATT/Proxy）。
/// </summary>
/// <remarks>
/// 未配网设备广播 <see cref="MeshProfile.ProvisioningServiceId"/>（0x1827），
/// 已配网设备广播 <see cref="MeshProfile.ProxyServiceId"/>（0x1828）。16 位
/// UUID 在广播里是小端，Service Data 的前两个字节即服务 UUID，
/// UUID 列表则按 2 字节依次排列。
/// 与 <see cref="ArriOpto.BLE.Agp.AgpAdvertisement"/> 对称：广播识别属于协议库。
/// </remarks>
public static class MeshAdvertisement
{
    public const ushort ProvisioningServiceUuid = 0x1827;
    public const ushort ProxyServiceUuid = 0x1828;

    public static bool IsMeshDevice(
        IEnumerable<AdvertisementRecord>? records) =>
        TryGetKind(records, out _);

    public static bool TryGetKind(
        IEnumerable<AdvertisementRecord>? records,
        out MeshAdvertisementKind kind)
    {
        kind = MeshAdvertisementKind.None;
        if (records is null)
            return false;

        foreach (var record in records)
        {
            if (record.Data is not { Length: >= 2 } data)
                continue;

            if (record.Type == AdvertisementRecordType.ServiceData)
            {
                kind = Merge(kind, GetKind(ReadUuid(data)));
                continue;
            }

            if (record.Type is AdvertisementRecordType.UuidsComplete16Bit or
                AdvertisementRecordType.UuidsIncomple16Bit)
            {
                for (var offset = 0; offset + 1 < data.Length; offset += 2)
                    kind = Merge(kind, GetKind(ReadUuid(data.AsSpan(offset, 2))));
            }
        }

        return kind != MeshAdvertisementKind.None;
    }

    private static ushort ReadUuid(ReadOnlySpan<byte> data) =>
        BinaryPrimitives.ReadUInt16LittleEndian(data);

    private static MeshAdvertisementKind GetKind(ushort uuid) => uuid switch
    {
        ProvisioningServiceUuid => MeshAdvertisementKind.Provisioning,
        ProxyServiceUuid => MeshAdvertisementKind.Proxy,
        _ => MeshAdvertisementKind.None
    };

    private static MeshAdvertisementKind Merge(
        MeshAdvertisementKind current,
        MeshAdvertisementKind candidate) =>
        candidate == MeshAdvertisementKind.Proxy ||
        current == MeshAdvertisementKind.None
            ? candidate
            : current;
}
