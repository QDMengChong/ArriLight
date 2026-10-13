using ArriOpto.BLE.Ota;
using ArriOpto.BLE.Protocol;

namespace ArriOpto.BLE.Ota.Protocol;

/// <summary>
/// 构造 Single Bank OTA 命令中的固定字节布局。
/// </summary>
/// <remarks>所有多字节整数均按官方协议使用小端序。</remarks>
public static class SingleBankCommands
{
    /// <summary>构造 <c>01 + 分区数 + 00</c> 的升级开始命令。</summary>
    public static byte[] BuildPartitionCount(int partitionCount)
    {
        if (partitionCount is < 1 or > 255)
        {
            throw new ArgumentOutOfRangeException(nameof(partitionCount));
        }

        return [0x01, checked((byte)partitionCount), 0x00];
    }

    /// <summary>
    /// 构造普通分区头：索引、flash 地址、运行地址、长度和 CRC16。
    /// </summary>
    public static byte[] BuildPartition(
        int index,
        uint flashAddress,
        FirmwarePartition partition)
    {
        ArgumentNullException.ThrowIfNull(partition);
        if (index is < 0 or > 255)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var result = new byte[16];
        result[0] = 0x02;
        result[1] = checked((byte)index);
        BinaryCodec.WriteUInt32LittleEndian(result.AsSpan(2), flashAddress);
        BinaryCodec.WriteUInt32LittleEndian(result.AsSpan(6), partition.RunAddress);
        BinaryCodec.WriteUInt32LittleEndian(result.AsSpan(10), checked((uint)partition.Length));
        BinaryCodec.WriteUInt16LittleEndian(
            result.AsSpan(14),
            CrcAlgorithms.Crc16A001(partition.Data.Span));
        return result;
    }

    /// <summary>
    /// 构造带四字节 MIC 的加密分区头。
    /// </summary>
    /// <remarks>
    /// 该字节构造器仅用于协议研究；当前 Session 不开放 Single Bank 加密升级。
    /// </remarks>
    public static byte[] BuildSecurePartition(
        int index,
        uint flashAddress,
        FirmwarePartition partition)
    {
        if (partition.Length < 4)
        {
            throw new InvalidDataException("Secure partition is too short to contain a MIC.");
        }

        var result = new byte[18];
        result[0] = 0x02;
        result[1] = checked((byte)index);
        BinaryCodec.WriteUInt32LittleEndian(result.AsSpan(2), flashAddress);
        BinaryCodec.WriteUInt32LittleEndian(result.AsSpan(6), partition.RunAddress);
        BinaryCodec.WriteUInt32LittleEndian(result.AsSpan(10), checked((uint)partition.Length));
        partition.Data.Span[^4..].CopyTo(result.AsSpan(14));
        return result;
    }

    /// <summary>
    /// 根据第一个分区地址和总长度构造页对齐的资源区声明命令。
    /// </summary>
    public static byte[] BuildResource(SingleBankFirmwareImage firmware)
    {
        ArgumentNullException.ThrowIfNull(firmware);
        var startAddress = firmware.Partitions[0].RunAddress;
        var flashAddress = startAddress & 0xFFFFF000u;
        long size = startAddress & 0xFFFu;
        size += firmware.Partitions.Sum(partition => (long)partition.Length);
        var alignedSize = checked((uint)((size + 0xFFF) & ~0xFFFL));

        var result = new byte[9];
        result[0] = 0x05;
        BinaryCodec.WriteUInt32LittleEndian(result.AsSpan(1), flashAddress);
        BinaryCodec.WriteUInt32LittleEndian(result.AsSpan(5), alignedSize);
        return result;
    }
}
