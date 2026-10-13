namespace ArriOpto.BLE.Mesh;

/// <summary>
/// SIG Mesh PB-GATT / Proxy 的 BLE Profile 常量（服务与 GATT 特征值）。
/// </summary>
/// <remarks>
/// 常量来源是 Bluetooth SIG 定义的 Mesh Provisioning Service（0x1827）与
/// Mesh Proxy Service（0x1828）。两者属于同一设备的不同配网阶段：未配网设备
/// 只暴露 Provisioning 服务，配网完成后固件会切换为 Proxy 服务，宿主必须
/// 重新发现 GATT（见调用方 <c>IMeshLink</c> 实现的端点切换）。
/// 与 <see cref="ArriOpto.BLE.Agp.AgpProfile"/> 对称：把 GATT 身份常量放在
/// 协议库，宿主应用不再重复定义 UUID。
/// </remarks>
public static class MeshProfile
{
    /// <summary>Mesh Provisioning Service（0x1827），未配网阶段。</summary>
    public static readonly Guid ProvisioningServiceId =
        Guid.Parse("00001827-0000-1000-8000-00805F9B34FB");

    /// <summary>Mesh Proxy Service（0x1828），已配网阶段。</summary>
    public static readonly Guid ProxyServiceId =
        Guid.Parse("00001828-0000-1000-8000-00805F9B34FB");

    /// <summary>Mesh Provisioning Data In（0x2ADB），Provisioner 写入。</summary>
    public static readonly Guid ProvisioningDataInId =
        Guid.Parse("00002ADB-0000-1000-8000-00805F9B34FB");

    /// <summary>Mesh Provisioning Data Out（0x2ADC），设备通知。</summary>
    public static readonly Guid ProvisioningDataOutId =
        Guid.Parse("00002ADC-0000-1000-8000-00805F9B34FB");

    /// <summary>Mesh Proxy Data In（0x2ADD），Provisioner 写入。</summary>
    public static readonly Guid ProxyDataInId =
        Guid.Parse("00002ADD-0000-1000-8000-00805F9B34FB");

    /// <summary>Mesh Proxy Data Out（0x2ADE），设备通知。</summary>
    public static readonly Guid ProxyDataOutId =
        Guid.Parse("00002ADE-0000-1000-8000-00805F9B34FB");
}
