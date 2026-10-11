namespace ArriLight;

/// <summary>设备在 GATT 服务表上呈现出的 Mesh 配网阶段。</summary>
public enum MeshDeviceProbeKind
{
    NotMesh,
    Unprovisioned,
    Provisioned
}

/// <summary>
/// <see cref="MeshLightControlService.ProbeMeshStateAsync"/> 的探测结果：
/// 除配网阶段外还携带当时枚举到的 GATT 服务表，交给
/// <see cref="MeshLightControlService.EnsureProvisionedAsync"/> 复用，
/// 避免配网阶段再枚举一次（每次枚举都会 Connect + ClearServices）。
/// </summary>
public readonly record struct MeshDeviceProbeState(
    MeshDeviceProbeKind Kind,
    IReadOnlyList<GattServiceInfo> Services = null!)
{
    public static MeshDeviceProbeState NotMesh(
        IReadOnlyList<GattServiceInfo> services = null!) =>
        new(MeshDeviceProbeKind.NotMesh, services);

    public static MeshDeviceProbeState Unprovisioned(
        IReadOnlyList<GattServiceInfo> services = null!) =>
        new(MeshDeviceProbeKind.Unprovisioned, services);

    public static MeshDeviceProbeState Provisioned(
        IReadOnlyList<GattServiceInfo> services = null!) =>
        new(MeshDeviceProbeKind.Provisioned, services);
}
