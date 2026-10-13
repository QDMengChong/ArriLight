namespace ArriOpto.BLE.Mesh;

/// <summary>
/// Bluetooth SIG Mesh message opcodes.
/// </summary>
/// <remarks>
/// Values follow the Bluetooth SIG Assigned Numbers document
/// ("Mesh Model Message Opcodes", last modified 2024-02-05), which is the
/// authoritative source for opcode numbers. 1-byte opcodes are used directly;
/// 2-byte opcodes are transmitted most-significant byte first (e.g. 0x8201
/// becomes bytes [0x82, 0x01]).
///
/// 常量命名约定：&lt;模型&gt;&lt;功能&gt;，如 LightCtlTemperatureRangeSet。
/// 新增消息时必须同时在 docs/opcode-reference.md 登记。
/// </remarks>
public static class MeshOpcode
{
    // ---- Config model (Mesh Protocol 1.1, Section 4.2.x) ----
    public const byte ConfigAppKeyAdd = 0x00;
    public const byte ConfigAppKeyUpdate = 0x01;
    public const byte ConfigCompositionDataStatus = 0x02;
    public const byte ConfigModelPublicationSet = 0x03;
    public const byte ConfigHeartbeatPublicationStatus = 0x06;
    public const ushort ConfigAppKeyDelete = 0x8000;
    public const ushort ConfigAppKeyGet = 0x8001;
    public const ushort ConfigAppKeyList = 0x8002;
    public const ushort ConfigAppKeyStatus = 0x8003;
    public const ushort ConfigCompositionDataGet = 0x8008;
    public const ushort ConfigBeaconGet = 0x8009;
    public const ushort ConfigBeaconSet = 0x800A;
    public const ushort ConfigBeaconStatus = 0x800B;
    public const ushort ConfigDefaultTtlGet = 0x800C;
    public const ushort ConfigDefaultTtlSet = 0x800D;
    public const ushort ConfigDefaultTtlStatus = 0x800E;
    public const ushort ConfigFriendGet = 0x800F;
    public const ushort ConfigFriendSet = 0x8010;
    public const ushort ConfigFriendStatus = 0x8011;
    public const ushort ConfigGattProxyGet = 0x8012;
    public const ushort ConfigGattProxySet = 0x8013;
    public const ushort ConfigGattProxyStatus = 0x8014;
    public const ushort ConfigKeyRefreshPhaseGet = 0x8015;
    public const ushort ConfigKeyRefreshPhaseSet = 0x8016;
    public const ushort ConfigKeyRefreshPhaseStatus = 0x8017;
    public const ushort ConfigModelPublicationGet = 0x8018;
    public const ushort ConfigModelPublicationStatus = 0x8019;
    public const ushort ConfigModelPublicationVirtualAddressSet = 0x801A;
    public const ushort ConfigModelSubscriptionAdd = 0x801B;
    public const ushort ConfigModelSubscriptionDelete = 0x801C;
    public const ushort ConfigModelSubscriptionDeleteAll = 0x801D;
    public const ushort ConfigModelSubscriptionOverwrite = 0x801E;
    public const ushort ConfigModelSubscriptionStatus = 0x801F;
    public const ushort ConfigModelSubscriptionVirtualAddressAdd = 0x8020;
    public const ushort ConfigModelSubscriptionVirtualAddressDelete = 0x8021;
    public const ushort ConfigModelSubscriptionVirtualAddressOverwrite = 0x8022;
    public const ushort ConfigNetworkTransmitGet = 0x8023;
    public const ushort ConfigNetworkTransmitSet = 0x8024;
    public const ushort ConfigNetworkTransmitStatus = 0x8025;
    public const ushort ConfigRelayGet = 0x8026;
    public const ushort ConfigRelaySet = 0x8027;
    public const ushort ConfigRelayStatus = 0x8028;
    public const ushort ConfigSigModelSubscriptionGet = 0x8029;
    public const ushort ConfigSigModelSubscriptionList = 0x802A;
    public const ushort ConfigVendorModelSubscriptionGet = 0x802B;
    public const ushort ConfigVendorModelSubscriptionList = 0x802C;
    public const ushort ConfigLowPowerNodePollTimeoutGet = 0x802D;
    public const ushort ConfigLowPowerNodePollTimeoutStatus = 0x802E;
    public const ushort ConfigHeartbeatPublicationGet = 0x8038;
    public const ushort ConfigHeartbeatPublicationSet = 0x8039;
    public const ushort ConfigHeartbeatSubscriptionGet = 0x803A;
    public const ushort ConfigHeartbeatSubscriptionSet = 0x803B;
    public const ushort ConfigHeartbeatSubscriptionStatus = 0x803C;
    public const ushort ConfigModelAppBind = 0x803D;
    public const ushort ConfigModelAppStatus = 0x803E;
    public const ushort ConfigModelAppUnbind = 0x803F;
    public const ushort ConfigNetKeyAdd = 0x8040;
    public const ushort ConfigNetKeyDelete = 0x8041;
    public const ushort ConfigNetKeyGet = 0x8042;
    public const ushort ConfigNetKeyList = 0x8043;
    public const ushort ConfigNetKeyStatus = 0x8044;
    public const ushort ConfigNetKeyUpdate = 0x8045;
    public const ushort ConfigNodeIdentityGet = 0x8046;
    public const ushort ConfigNodeIdentitySet = 0x8047;
    public const ushort ConfigNodeIdentityStatus = 0x8048;
    public const ushort ConfigNodeReset = 0x8049;
    public const ushort ConfigNodeResetStatus = 0x804A;
    public const ushort ConfigSigModelAppGet = 0x804B;
    public const ushort ConfigSigModelAppList = 0x804C;
    public const ushort ConfigVendorModelAppGet = 0x804D;
    public const ushort ConfigVendorModelAppList = 0x804E;

    // ---- Generic model ----
    public const ushort GenericOnOffGet = 0x8201;
    public const ushort GenericOnOffSet = 0x8202;
    public const ushort GenericOnOffSetUnack = 0x8203;
    public const ushort GenericOnOffStatus = 0x8204;
    public const ushort GenericOnPowerUpGet = 0x8211;
    public const ushort GenericOnPowerUpStatus = 0x8212;
    public const ushort GenericOnPowerUpSet = 0x8213;
    public const ushort GenericOnPowerUpSetUnack = 0x8214;
    public const ushort GenericBatteryGet = 0x8223;
    public const ushort GenericBatteryStatus = 0x8224;
    public const ushort GenericLocationGlobalGet = 0x8225;
    public const ushort GenericLocationLocalGet = 0x8226;
    public const ushort GenericLocationLocalStatus = 0x8227;
    public const ushort GenericLocationLocalSet = 0x8228;
    public const ushort GenericLocationLocalSetUnack = 0x8229;
    public const byte GenericLocationGlobalStatus = 0x40;
    public const byte GenericLocationGlobalSet = 0x41;
    public const byte GenericLocationGlobalSetUnack = 0x42;
    public const byte GenericManufacturerPropertiesStatus = 0x43;
    public const byte GenericManufacturerPropertySet = 0x44;
    public const byte GenericManufacturerPropertySetUnack = 0x45;
    public const byte GenericManufacturerPropertyStatus = 0x46;
    public const byte GenericAdminPropertiesStatus = 0x47;
    public const byte GenericAdminPropertySet = 0x48;
    public const byte GenericAdminPropertySetUnack = 0x49;
    public const byte GenericAdminPropertyStatus = 0x4A;
    public const byte GenericUserPropertiesStatus = 0x4B;
    public const byte GenericUserPropertySet = 0x4C;
    public const byte GenericUserPropertySetUnack = 0x4D;
    public const byte GenericUserPropertyStatus = 0x4E;
    public const byte GenericClientPropertiesGet = 0x4F;
    public const byte GenericClientPropertiesStatus = 0x50;
    public const ushort GenericManufacturerPropertiesGet = 0x822A;
    public const ushort GenericManufacturerPropertyGet = 0x822B;
    public const ushort GenericAdminPropertiesGet = 0x822C;
    public const ushort GenericAdminPropertyGet = 0x822D;
    public const ushort GenericUserPropertiesGet = 0x822E;
    public const ushort GenericUserPropertyGet = 0x822F;

    // ---- Time model ----
    public const ushort TimeGet = 0x8237;
    public const ushort TimeRoleGet = 0x8238;
    public const ushort TimeRoleSet = 0x8239;
    public const ushort TimeRoleStatus = 0x823A;
    public const ushort TimeZoneGet = 0x823B;
    public const ushort TimeZoneSet = 0x823C;
    public const ushort TimeZoneStatus = 0x823D;
    public const ushort TaiUtcDeltaGet = 0x823E;
    public const ushort TaiUtcDeltaSet = 0x823F;
    public const ushort TaiUtcDeltaStatus = 0x8240;
    public const byte TimeSet = 0x5C;
    public const byte TimeStatus = 0x5D;

    // ---- Scene model ----
    public const ushort SceneGet = 0x8241;
    public const ushort SceneRecall = 0x8242;
    public const ushort SceneRecallUnack = 0x8243;
    public const ushort SceneRegisterGet = 0x8244;
    public const ushort SceneRegisterStatus = 0x8245;
    public const ushort SceneStore = 0x8246;
    public const ushort SceneStoreUnack = 0x8247;
    public const byte SceneStatus = 0x5E;
    public const ushort SceneDelete = 0x829E;
    public const ushort SceneDeleteUnack = 0x829F;

    // ---- Scheduler model ----
    public const ushort SchedulerActionGet = 0x8248;
    public const ushort SchedulerGet = 0x8249;
    public const ushort SchedulerStatus = 0x824A;
    public const byte SchedulerActionStatus = 0x5F;
    public const byte SchedulerActionSet = 0x60;
    public const byte SchedulerActionSetUnack = 0x61;

    // ---- Light Lightness ----
    public const ushort LightLightnessGet = 0x824B;
    public const ushort LightLightnessSet = 0x824C;
    public const ushort LightLightnessSetUnack = 0x824D;
    public const ushort LightLightnessStatus = 0x824E;
    public const ushort LightLightnessLinearGet = 0x824F;
    public const ushort LightLightnessLinearSet = 0x8250;
    public const ushort LightLightnessLinearSetUnack = 0x8251;
    public const ushort LightLightnessLinearStatus = 0x8252;
    public const ushort LightLightnessLastGet = 0x8253;
    public const ushort LightLightnessLastStatus = 0x8254;
    public const ushort LightLightnessDefaultGet = 0x8255;
    public const ushort LightLightnessDefaultStatus = 0x8256;
    public const ushort LightLightnessRangeGet = 0x8257;
    public const ushort LightLightnessRangeStatus = 0x8258;
    public const ushort LightLightnessDefaultSet = 0x8259;
    public const ushort LightLightnessDefaultSetUnack = 0x825A;
    public const ushort LightLightnessRangeSet = 0x825B;
    public const ushort LightLightnessRangeSetUnack = 0x825C;

    // ---- Light CTL ----
    public const ushort LightCtlGet = 0x825D;
    public const ushort LightCtlSet = 0x825E;
    public const ushort LightCtlSetUnack = 0x825F;
    public const ushort LightCtlStatus = 0x8260;
    public const ushort LightCtlTemperatureGet = 0x8261;
    public const ushort LightCtlTemperatureRangeGet = 0x8262;
    public const ushort LightCtlTemperatureRangeStatus = 0x8263;
    public const ushort LightCtlTemperatureSet = 0x8264;
    public const ushort LightCtlTemperatureSetUnack = 0x8265;
    public const ushort LightCtlTemperatureStatus = 0x8266;
    public const ushort LightCtlDefaultGet = 0x8267;
    public const ushort LightCtlDefaultStatus = 0x8268;
    public const ushort LightCtlDefaultSet = 0x8269;
    public const ushort LightCtlDefaultSetUnack = 0x826A;
    public const ushort LightCtlTemperatureRangeSet = 0x826B;
    public const ushort LightCtlTemperatureRangeSetUnack = 0x826C;

    // ---- Light HSL ----
    public const ushort LightHslGet = 0x826D;
    public const ushort LightHslHueGet = 0x826E;
    public const ushort LightHslHueSet = 0x826F;
    public const ushort LightHslHueSetUnack = 0x8270;
    public const ushort LightHslHueStatus = 0x8271;
    public const ushort LightHslSaturationGet = 0x8272;
    public const ushort LightHslSaturationSet = 0x8273;
    public const ushort LightHslSaturationSetUnack = 0x8274;
    public const ushort LightHslSaturationStatus = 0x8275;
    public const ushort LightHslSet = 0x8276;
    public const ushort LightHslSetUnack = 0x8277;
    public const ushort LightHslStatus = 0x8278;
    public const ushort LightHslTargetGet = 0x8279;
    public const ushort LightHslTargetStatus = 0x827A;
    public const ushort LightHslDefaultGet = 0x827B;
    public const ushort LightHslDefaultStatus = 0x827C;
    public const ushort LightHslRangeGet = 0x827D;
    public const ushort LightHslRangeStatus = 0x827E;
    public const ushort LightHslDefaultSet = 0x827F;
    public const ushort LightHslDefaultSetUnack = 0x8280;
    public const ushort LightHslRangeSet = 0x8281;
    public const ushort LightHslRangeSetUnack = 0x8282;

    /// <summary>Encodes a 1-byte opcode.</summary>
    public static byte[] Encode(byte opcode) => [opcode];

    /// <summary>Encodes a 2-byte opcode, most-significant byte first.</summary>
    public static byte[] Encode(ushort opcode) =>
        [(byte)(opcode >> 8), (byte)opcode];
}
