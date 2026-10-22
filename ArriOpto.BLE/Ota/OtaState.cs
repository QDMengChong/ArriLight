namespace ArriOpto.BLE.Ota;

/// <summary>
/// OTA 生命周期状态。数值沿用官方 Android SDK，便于现有日志、UI 和服务端统计兼容。
/// </summary>
/// <remarks>
/// 状态同时包含“过程事件”和“错误结果”，不能通过数值区间判断是否失败；应调用
/// <see cref="OtaStateExtensions.IsError(OtaState)"/>。
/// </remarks>
public enum OtaState
{
    /// <summary>尚未开始升级。</summary>
    Idle = 0,

    /// <summary>正在建立 BLE 连接。</summary>
    Connecting = 11001,

    /// <summary>连接成功，正在发现 OTA 服务和特征。</summary>
    ConnectedDiscovering = 11002,

    /// <summary>BLE 连接已经断开。</summary>
    Disconnected = 11003,

    /// <summary>发现 Single Bank APP 模式服务。</summary>
    SingleBankAppService = 11004,

    /// <summary>发现 Single Bank OTA bootloader 模式服务。</summary>
    SingleBankOtaService = 11005,

    /// <summary>发现 SLB OTA 服务。</summary>
    SlbService = 11006,

    /// <summary>设备未暴露受支持的 OTA 服务。</summary>
    ErrorNoService = 11007,

    /// <summary>服务组合异常，例如同时暴露互斥的 OTA 服务。</summary>
    ErrorAbnormalCharacteristic = 11008,

    /// <summary>当前服务模式不允许执行请求的协议阶段。</summary>
    ErrorServiceState = 11009,

    /// <summary>已确认设备处于 Single Bank APP 模式。</summary>
    ConfirmedSingleBankApp = 11010,

    /// <summary>已确认设备处于 Single Bank OTA 模式。</summary>
    ConfirmedSingleBankOta = 11011,

    /// <summary>已确认设备使用 SLB 模式。</summary>
    ConfirmedSlb = 11012,

    /// <summary>必要特征缺失或不支持要求的写入/通知属性。</summary>
    ErrorCharacteristic = 11013,

    /// <summary>固件协议、Product ID 或设备模式不匹配。</summary>
    ErrorFileMismatch = 11014,

    /// <summary>MTU 协商失败或得到无法承载协议帧的 MTU。</summary>
    ErrorMtuChange = 11015,

    /// <summary>设备/连接返回的 MTU 与协议要求不匹配。</summary>
    ErrorMtuMismatch = 11016,

    /// <summary>启用 OTA 后需要重新连接。</summary>
    EnableFailedReconnect = 11017,

    /// <summary>通知描述符配置失败。</summary>
    ErrorDescriptor = 11018,

    /// <summary>通知启用后的状态不符合预期。</summary>
    ErrorEnableState = 11019,

    /// <summary>Single Bank APP 模式已订阅通知，可以执行版本检查和切换。</summary>
    SingleBankAppReady = 11020,

    /// <summary>Single Bank OTA 模式已准备好接收分区。</summary>
    SingleBankOtaReady = 11021,

    /// <summary>SLB 模式已准备好认证或传输。</summary>
    SlbReady = 11022,

    /// <summary>已发送 APP→OTA 切换命令，正在等待重新扫描 bootloader。</summary>
    AppModeEndWaitingScan = 11023,

    /// <summary>正在获取设备身份或版本信息。</summary>
    GetMacVersion = 11024,

    /// <summary>已收到设备 boot version。</summary>
    FeedbackBootVersion = 11025,

    /// <summary>设备已确认固件完成，等待重启或断开。</summary>
    UpgradeCompleteWaitingDisconnect = 11026,

    /// <summary>固件传输进度更新。</summary>
    FeedbackProgress = 11027,

    /// <summary>无法连接设备或在规定时间内找不到 OTA bootloader。</summary>
    ErrorCannotConnect = 11028,

    /// <summary>APP→OTA 切换后找到了匹配的 bootloader 设备。</summary>
    FoundOtaDevice = 11029,

    /// <summary>设备返回拒绝、校验失败、非法 offset 或其他协议错误。</summary>
    ErrorFirmwareFeedback = 11030,

    /// <summary>升级失败后连接断开。</summary>
    UpgradeFailedDisconnected = 11031,

    /// <summary>升级成功后连接断开。</summary>
    UpgradeSucceededDisconnected = 11032,

    /// <summary>应用扩展状态：数据已传输，设备正在校验固件。原有状态编号不变。</summary>
    VerifyingFirmware = 11033
}

/// <summary>
/// <see cref="OtaState"/> 的语义判断方法。
/// </summary>
public static class OtaStateExtensions
{
    /// <summary>
    /// 判断状态是否表示失败。使用显式枚举集合，避免把相邻的确认状态误判为错误。
    /// </summary>
    public static bool IsError(this OtaState state) =>
        state is OtaState.ErrorNoService
            or OtaState.ErrorAbnormalCharacteristic
            or OtaState.ErrorServiceState
            or OtaState.ErrorCharacteristic
            or OtaState.ErrorFileMismatch
            or OtaState.ErrorMtuChange
            or OtaState.ErrorMtuMismatch
            or OtaState.EnableFailedReconnect
            or OtaState.ErrorDescriptor
            or OtaState.ErrorEnableState
            or OtaState.ErrorCannotConnect
            or OtaState.ErrorFirmwareFeedback
            or OtaState.UpgradeFailedDisconnected;

    /// <summary>判断调用方是否可把状态视为本次升级的终止状态。</summary>
    public static bool IsTerminal(this OtaState state) =>
        state.IsError()
        || state is OtaState.UpgradeCompleteWaitingDisconnect
            or OtaState.UpgradeSucceededDisconnected;
}
