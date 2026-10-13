using Plugin.BLE.Abstractions;

namespace ArriOpto.BLE.Agp;

/// <summary>AGP 广播中的连接前状态。</summary>
public enum AgpBroadcastStatus
{
    Unknown = 0,
    PairingOpen = 1,
    BondedOnly = 2
}

/// <summary>AGP Broadcast Status v1（Service Data - 128-bit UUID）。</summary>
public static class AgpAdvertisement
{
    public const byte StatusFormatVersion = 0x01;
    public const byte StatusPairingOpen = 0x01;
    public const byte StatusBondedOnly = 0x02;

    /// <summary>
    /// 广播 Service Data 使用的 UUID，与连接后实际暴露的 AGP GATT Service 相同。
    /// </summary>
    public static Guid StatusServiceId => AgpProfile.ServiceId;

    // 广播 AD structure 中的 128-bit UUID 按 BLE little-endian 编码，不能使用
    // Guid.ToByteArray()（它采用 .NET Guid 的混合字节序）。
    private static readonly byte[] StatusServiceIdLittleEndian =
    [
        0xD7, 0xF9, 0x06, 0xF8, 0x60, 0xFC, 0x0F, 0x87,
        0x08, 0x44, 0x33, 0xC4, 0x01, 0x24, 0x09, 0x8F
    ];

    /// <summary>
    /// 从 Plugin.BLE 的广播记录中读取 AGP 连接前广播标志。
    /// Service Data - 128-bit UUID 的数据布局为：
    /// AGP Service UUID（BLE 小端）、format version、status。
    /// </summary>
    public static bool TryGetBroadcastStatus(
        IEnumerable<AdvertisementRecord>? records,
        out AgpBroadcastStatus status)
    {
        status = AgpBroadcastStatus.Unknown;
        if (records is null)
            return false;

        foreach (var record in records)
        {
            if (record.Type != AdvertisementRecordType.ServiceData128Bit ||
                record.Data is not { Length: >= 18 } data)
            {
                continue;
            }

            if (!data.AsSpan(0, StatusServiceIdLittleEndian.Length)
                    .SequenceEqual(StatusServiceIdLittleEndian) ||
                data[16] != StatusFormatVersion)
            {
                continue;
            }

            status = data[17] switch
            {
                StatusPairingOpen => AgpBroadcastStatus.PairingOpen,
                StatusBondedOnly => AgpBroadcastStatus.BondedOnly,
                _ => AgpBroadcastStatus.Unknown
            };
            return status != AgpBroadcastStatus.Unknown;
        }

        return false;
    }

    public static bool IsPairingOpen(IEnumerable<AdvertisementRecord>? records) =>
        TryGetBroadcastStatus(records, out var status) &&
        status == AgpBroadcastStatus.PairingOpen;
}
