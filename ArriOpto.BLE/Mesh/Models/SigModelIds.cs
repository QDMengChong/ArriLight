namespace ArriOpto.BLE.Mesh.Models;

/// <summary>
/// SIG-assigned Mesh model identifiers (Assigned Numbers, "Mesh Model
/// Identifiers by Value", last modified 2024-02-05).
/// </summary>
/// <remarks>
/// 常量名采用 &lt;模型系列&gt;&lt;角色&gt; 命名，如 LightCtlTemperatureServer。
/// Vendor 模型 ID 不在本表（需要 Company ID + Model ID 两个字段）。
/// </remarks>
public static class SigModelIds
{
    public const ushort ConfigurationServer = 0x0000;
    public const ushort ConfigurationClient = 0x0001;
    public const ushort HealthServer = 0x0002;
    public const ushort HealthClient = 0x0003;

    public const ushort GenericOnOffServer = 0x1000;
    public const ushort GenericOnOffClient = 0x1001;
    public const ushort GenericLevelServer = 0x1002;
    public const ushort GenericLevelClient = 0x1003;
    public const ushort GenericDefaultTransitionTimeServer = 0x1004;
    public const ushort GenericDefaultTransitionTimeClient = 0x1005;
    public const ushort GenericPowerOnOffServer = 0x1006;
    public const ushort GenericPowerOnOffSetupServer = 0x1007;
    public const ushort GenericPowerOnOffClient = 0x1008;
    public const ushort GenericPowerLevelServer = 0x1009;
    public const ushort GenericPowerLevelSetupServer = 0x100A;
    public const ushort GenericPowerLevelClient = 0x100B;
    public const ushort GenericBatteryServer = 0x100C;
    public const ushort GenericBatteryClient = 0x100D;
    public const ushort GenericLocationServer = 0x100E;
    public const ushort GenericLocationSetupServer = 0x100F;
    public const ushort GenericLocationClient = 0x1010;
    public const ushort GenericAdminPropertyServer = 0x1011;
    public const ushort GenericManufacturerPropertyServer = 0x1012;
    public const ushort GenericUserPropertyServer = 0x1013;
    public const ushort GenericClientPropertyServer = 0x1014;
    public const ushort GenericPropertyClient = 0x1015;

    public const ushort TimeServer = 0x1200;
    public const ushort TimeSetupServer = 0x1201;
    public const ushort TimeClient = 0x1202;
    public const ushort SceneServer = 0x1203;
    public const ushort SceneSetupServer = 0x1204;
    public const ushort SceneClient = 0x1205;
    public const ushort SchedulerServer = 0x1206;
    public const ushort SchedulerSetupServer = 0x1207;
    public const ushort SchedulerClient = 0x1208;

    public const ushort LightLightnessServer = 0x1300;
    public const ushort LightLightnessSetupServer = 0x1301;
    public const ushort LightLightnessClient = 0x1302;
    public const ushort LightCtlServer = 0x1303;
    public const ushort LightCtlSetupServer = 0x1304;
    public const ushort LightCtlClient = 0x1305;
    public const ushort LightCtlTemperatureServer = 0x1306;
    public const ushort LightHslServer = 0x1307;
    public const ushort LightHslSetupServer = 0x1308;
    public const ushort LightHslClient = 0x1309;
    public const ushort LightHslHueServer = 0x130A;
    public const ushort LightHslSaturationServer = 0x130B;
}
