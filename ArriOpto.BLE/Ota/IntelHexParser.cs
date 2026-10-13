using System.Globalization;
using System.Text;

namespace ArriOpto.BLE.Ota;

internal static class IntelHexParser
{
    // 解析结果按地址连续性分区。不能对地址空洞自动补 0x00/0xFF，因为这会扩大
    // bootloader 的实际写入范围，甚至覆盖文件未描述的 flash 区域。
    public static async Task<IReadOnlyList<FirmwarePartition>> ParseAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: true);
        var partitions = new List<FirmwarePartition>();
        var current = new List<byte>();
        uint? currentStart = null;
        uint expectedAddress = 0;
        uint addressBase = 0;
        var lineNumber = 0;

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)
               is { } sourceLine)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lineNumber++;

            var line = sourceLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var record = ParseRecord(line, lineNumber);
            // Intel HEX 地址由“扩展地址记录 + 16 位记录内地址”共同组成。
            switch (record.Type)
            {
                case 0x00:
                    {
                        var absoluteAddress = checked(addressBase + record.Address);
                        if (currentStart is null || absoluteAddress != expectedAddress)
                        {
                            FlushPartition(partitions, current, ref currentStart);
                            currentStart = absoluteAddress;
                        }

                        current.AddRange(record.Data);
                        expectedAddress = checked(absoluteAddress + (uint)record.Data.Length);
                        break;
                    }
                case 0x01:
                    EnsureLength(record, 0, lineNumber);
                    if (record.Address != 0)
                    {
                        throw new InvalidDataException(
                            $"Intel HEX end-of-file record has a non-zero address at line {lineNumber}.");
                    }

                    FlushPartition(partitions, current, ref currentStart);
                    return partitions;
                case 0x02:
                    EnsureLength(record, 2, lineNumber);
                    FlushPartition(partitions, current, ref currentStart);
                    addressBase = (uint)((record.Data[0] << 8) | record.Data[1]) << 4;
                    break;
                case 0x04:
                    EnsureLength(record, 2, lineNumber);
                    FlushPartition(partitions, current, ref currentStart);
                    addressBase = (uint)((record.Data[0] << 8) | record.Data[1]) << 16;
                    break;
                case 0x03:
                case 0x05:
                    // Start-address records do not contain firmware payload.
                    break;
                default:
                    throw new InvalidDataException(
                        $"Unsupported Intel HEX record type 0x{record.Type:X2} at line {lineNumber}.");
            }
        }

        throw new InvalidDataException("Intel HEX file does not contain an end-of-file record.");
    }

    private static IntelHexRecord ParseRecord(string line, int lineNumber)
    {
        if (!line.StartsWith(':') || line.Length < 11 || (line.Length - 1) % 2 != 0)
        {
            throw new InvalidDataException($"Malformed Intel HEX record at line {lineNumber}.");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(line[1..]);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException(
                $"Intel HEX record contains invalid hexadecimal data at line {lineNumber}.",
                exception);
        }

        var declaredLength = bytes[0];
        if (bytes.Length != declaredLength + 5)
        {
            throw new InvalidDataException(
                $"Intel HEX byte count mismatch at line {lineNumber}.");
        }

        if ((bytes.Sum(value => value) & 0xFF) != 0)
        {
            throw new InvalidDataException(
                $"Intel HEX checksum mismatch at line {lineNumber}.");
        }

        var address = ushort.Parse(
            line.AsSpan(3, 4),
            NumberStyles.AllowHexSpecifier,
            CultureInfo.InvariantCulture);
        var type = bytes[3];
        var data = bytes.AsSpan(4, declaredLength).ToArray();
        return new IntelHexRecord(type, address, data);
    }

    private static void EnsureLength(IntelHexRecord record, int expectedLength, int lineNumber)
    {
        if (record.Data.Length != expectedLength)
        {
            throw new InvalidDataException(
                $"Intel HEX record type 0x{record.Type:X2} has an invalid length at line {lineNumber}.");
        }
    }

    private static void FlushPartition(
        ICollection<FirmwarePartition> partitions,
        List<byte> current,
        ref uint? currentStart)
    {
        if (currentStart.HasValue && current.Count > 0)
        {
            partitions.Add(new FirmwarePartition(currentStart.Value, current.ToArray()));
        }

        current.Clear();
        currentStart = null;
    }

    private sealed record IntelHexRecord(byte Type, ushort Address, byte[] Data);
}
