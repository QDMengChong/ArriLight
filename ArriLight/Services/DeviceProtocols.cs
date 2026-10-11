namespace ArriLight;

/// <summary>
/// 设备使用的空口协议，决定 Binding/控制走哪条链路。
/// </summary>
/// <remarks>
/// 这是宿主侧的 UI 与设备清单身份（持久化在 <c>SavedDevice.Protocol</c>、
/// 展示为扫描页/首页的协议标签），不属于传输层协议事实本身。
/// Mesh 的 GATT UUID 与广播识别常量位于
/// <c>ArriOpto.BLE.Mesh.MeshProfile</c> / <c>ArriOpto.BLE.Mesh.MeshAdvertisement</c>。
/// </remarks>
public enum BluetoothDeviceProtocol
{
    /// <summary>未记录协议；按旧数据兼容处理为 <see cref="Agp"/>。</summary>
    Unknown,

    /// <summary>ArriOpto GATT Protocol（私有 128 位服务），UI 显示为 GATT。</summary>
    Agp,

    /// <summary>SIG Mesh（PB-GATT 配网 + Proxy 控制）。</summary>
    Mesh
}
