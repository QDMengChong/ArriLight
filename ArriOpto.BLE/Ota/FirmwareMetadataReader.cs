
namespace ArriOpto.BLE.Ota;

internal static class FirmwareMetadataReader
{
    // 官方工具把固定 128 字节 marker 写入镜像，其后依次是 2 字节 Product ID
    //（设备协议使用相反字节序）和 6 字节 boot version。
    private static readonly byte[] Marker = Convert.FromHexString(
        "3B03F2C00112EC9815F75357228A61330ACA4C23C47477CBA6AB1E2F04CC8269" +
        "EF96CA95B02EDF949FC5297E684CB1F1CEDF50E2EF976EB54AAB6C751DBDE3AA5" +
        "1622C9E838F0F286F34E2073D4519477FE971558AE969BC92E366E70E16922973" +
        "59E7B7FB8179F845C1D829D538A66647A8130D6D52F559AD99052062B6CFC6");

    public static FirmwareMetadata Read(ReadOnlySpan<byte> data)
    {
        var markerIndex = data.IndexOf(Marker);
        if (markerIndex < 0)
        {
            return new FirmwareMetadata(null, null);
        }

        var metadataStart = markerIndex + Marker.Length;
        if (data.Length - metadataStart < 8)
        {
            throw new InvalidDataException(
                "Firmware metadata marker is present, but Product ID and Boot version are incomplete.");
        }

        var raw = data.Slice(metadataStart, 8);
        var productId = Convert.ToHexString([raw[1], raw[0]]);
        var bootVersion = Convert.ToHexString(raw[2..8]);
        return new FirmwareMetadata(productId, bootVersion);
    }
}
