namespace ArriOpto.BLE.Ota;

/// <summary>
/// 奉加微 OTA 使用的标准 GATT UUID。
/// </summary>
/// <remarks>
/// UUID 来自官方 Android SDK 和 PHY6222 APP OTA 文档。修改这里会直接影响服务
/// 发现，必须有新 bootloader 文档或真机服务表作为依据。
/// </remarks>
public static class OtaConstants
{
    /// <summary>Client Characteristic Configuration Descriptor（CCCD）。</summary>
    public static readonly Guid ClientConfigurationDescriptor =
        Guid.Parse("00002902-0000-1000-8000-00805F9B34FB");

    /// <summary>SLB OTA 的服务和特征 UUID。</summary>
    public static class Slb
    {
        /// <summary>SLB OTA 主服务。</summary>
        public static readonly Guid Service =
            Guid.Parse("0000FEB3-0000-1000-8000-00805F9B34FB");

        /// <summary>命令特征，使用 Write With Response。</summary>
        public static readonly Guid Command =
            Guid.Parse("0000FED5-0000-1000-8000-00805F9B34FB");

        /// <summary>固件数据特征，使用 Write Without Response。</summary>
        public static readonly Guid Data =
            Guid.Parse("0000FED7-0000-1000-8000-00805F9B34FB");

        /// <summary>设备协议响应通知特征。</summary>
        public static readonly Guid Notify =
            Guid.Parse("0000FED8-0000-1000-8000-00805F9B34FB");
    }

    /// <summary>Single Bank APP/OTA 共用的服务和特征 UUID。</summary>
    public static class SingleBank
    {
        /// <summary>Single Bank 主服务。</summary>
        public static readonly Guid Service =
            Guid.Parse("5833FF01-9B8B-5191-6142-22A4536EF123");

        /// <summary>命令特征，使用 Write With Response。</summary>
        public static readonly Guid Command =
            Guid.Parse("5833FF02-9B8B-5191-6142-22A4536EF123");

        /// <summary>设备协议响应通知特征。</summary>
        public static readonly Guid Notify =
            Guid.Parse("5833FF03-9B8B-5191-6142-22A4536EF123");

        /// <summary>
        /// OTA 数据特征，使用 Write Without Response。APP 模式没有该特征，因此它
        /// 也用于区分 APP 和 bootloader 模式。
        /// </summary>
        public static readonly Guid Data =
            Guid.Parse("5833FF04-9B8B-5191-6142-22A4536EF123");
    }
}
