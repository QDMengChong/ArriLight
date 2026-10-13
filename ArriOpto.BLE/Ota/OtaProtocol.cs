namespace ArriOpto.BLE.Ota;

/// <summary>
/// 奉加微 OTA bootloader 使用的协议族。
/// </summary>
public enum OtaProtocol
{
    /// <summary>Self-loading Boot（SLB），固件通常为原始 <c>.bin</c>。</summary>
    Slb,

    /// <summary>Single Bank，固件通常为 Intel HEX 或资源文件。</summary>
    SingleBank
}

/// <summary>
/// 固件内容的存储格式。该类型描述文件内容，不等同于设备当前的 GATT 模式。
/// </summary>
public enum FirmwareKind
{
    /// <summary>连续的原始二进制数据。</summary>
    Binary,

    /// <summary>由一个或多个地址分区组成的 Intel HEX 数据。</summary>
    IntelHex,

    /// <summary>写入资源区而非普通程序区的数据。</summary>
    Resource
}

/// <summary>
/// 通过服务和特征发现得到的设备 OTA GATT 工作模式。
/// </summary>
public enum OtaGattMode
{
    /// <summary>尚未发现服务，或无法判断模式。</summary>
    Unknown,

    /// <summary>设备暴露 FEB3/FED5/FED7/FED8 SLB 服务。</summary>
    Slb,

    /// <summary>Single Bank APP 服务，只存在命令和通知特征。</summary>
    SingleBankApp,

    /// <summary>Single Bank OTA 服务，同时存在 FF04 数据特征。</summary>
    SingleBankOta
}
