# AGP 私有协议客户端

`ArriOpto.BLE.Agp` 提供 ArriOpto GATT Protocol（AGP）v1 的 .NET binding。它只负责协议和 GATT 连接适配，不包含 MAUI 页面、设备仓库、时区转换、连接重试或配对 UI。

## 分层与入口

```text
ArriLight 或其他宿主应用
  └─ AgpClient                         类型化命令、事务 ID、响应匹配、状态事件
      └─ IAgpGattConnection            可测试的最小 GATT 边界
          └─ PluginBleAgpConnection    Plugin.BLE 服务发现、Read/Write、Indicate/Notify
              └─ PW01 AGP Service      8f092401-c433-4408-870f-fc60f806f9d7
```

主要入口：

- `AgpCodec`：严格编码/解析 v1 公共帧；版本、flags、长度和 20 字节上限不放宽。
- `AgpModelCodec`：基础灯控、本地硬件延时模式、电池状态、UTC 时间、延时开/关灯、日程和灯效负载。
- `AgpColorModelCodec`：Lightness16、HSL、CTL、颜色范围/默认值和组合过渡编解码。
- `AgpClient`：一次一个在途请求，自动使用非零递增 transaction ID，同时匹配 opcode 与 transaction ID。
- `PluginBleAgpConnection`：发现 Capabilities、Control Point、Light State 和可选 Color/Battery State，先开启 Indicate，再开启状态 Notify。

## Plugin.BLE 用法

```csharp
using ArriOpto.BLE.Agp;

var connection = await PluginBleAgpConnection.OpenAsync(
    adapter,
    device,
    dispatcher: operation => MainThread.InvokeOnMainThreadAsync(operation),
    cancellationToken);
await using var client = new AgpClient(connection);

client.LightStateChanged += (_, state) =>
{
    // state.Revision 是设备真值版本；宿主应丢弃更旧的状态。
};

var capabilities = await client.ReadCapabilitiesAsync(cancellationToken);
var state = await client.ReadLightStateAsync(cancellationToken);
var response = await client.SetLightnessAsync(50, cancellationToken: cancellationToken);
if (response.Result != AgpResultCode.Success)
{
    // GATT 写成功不等于业务成功，以 Result 为准。
}
```

`AgpClient.DisposeAsync()` 会释放传入连接，因此上例只把 client 放入 `await using`。如宿主已有自己的 BLE 连接所有权，应实现 `IAgpGattConnection`，并在实现中定义符合应用架构的释放策略。

## 已支持模型

| 模型 | 客户端方法 | 关键约束 |
| --- | --- | --- |
| Capabilities / Light State | `ReadCapabilitiesAsync`、`ReadLightStateAsync`、`LightStateChanged` | 状态 TID 为 0，revision 由设备维护 |
| 基础灯控 | `SetOnOffAsync`、`ToggleOnOffAsync`、`SetLightnessAsync` | OnOff 支持关/开/翻转；亮度为线性协议百分比 0..100；APP 不做 Gamma |
| 本地硬件模式 | `SetModeAsync`、`GetModeAsync` | 能力 bit22 `LocalMode`；各启用方向为独立的 1..2,592,000 秒 |
| Battery | `GetBatteryAsync`、`ReadBatteryStateAsync`、`BatteryStateChanged` | bit11 判断 GET；`HasBatteryStateCharacteristic` 判断 Read/Notify；电量未知为 null |
| UTC Time | `SetTimeAsync`、`GetTimeAsync` | 仅 UTC；2024-01-01 至 2099-12-31；时区/DST 属于 APP |
| Relative Timer | `SetDelayOffAsync`、`GetDelayOffAsync`、`SetDelayOnAsync`、`GetDelayOnAsync` | 0 取消，最大 30 天；与 UTC 校时无关 |
| Scheduler | `Set/Get/DeleteScheduleAsync`、`GetScheduleListAsync` | 8 槽；APP 把本地时间/DST 转成 UTC |
| Effects | `SetEffectAsync`、`GetEffectAsync` | Steady/Blink/Breathe；周期 200..10000 ms |
| Lightness16 | `SetLightness16Async`、`GetLightness16Async` | 与旧百分比亮度及 HSL/CTL Lightness 共享同一权威状态 |
| HSL | `Set/GetHslAsync`、Target、Default、Range 方法 | Hue/Saturation 为完整 u16；Hue Range 允许跨越 0° |
| CTL | `Set/GetCtlAsync`、Target、Default、Range 方法 | 800..20000 K；仅 CTL Default 允许未知温度 `0xFFFF` |
| Color State | `ReadColorStateAsync`、`ColorStateChanged` | 能力相关的可选特征；先检查 `HasColorStateCharacteristic` |

所有业务错误（如 `Busy`、`NotAuthorized`、`InvalidValue`）保留在响应对象的 `Result`。畸形帧抛 `AgpProtocolException`，响应超时抛 `AgpTimeoutException`，平台连接/读写错误沿用 GATT 异常或 `IOException`。

已登记的认证业务错误 `AuthTimeout=11`、`AuthInvalidLength=12`、`AuthBusy=13` 同样保留在 `Result`；未登记结果码仍拒绝解析。

### BATTERY_GET / Battery State Read、Notify

按 2026-10-18 PW01 工作区的 `docs/AGP/SPEC.md` 第 17 节和 `src/AGP/agp_battery_model.c/.h`，`BATTERY_GET (0x0701)` 是空负载查询，通过 Control Point Indicate 返回相同 opcode 和非零 TID。PW01 的电量查询和状态 Read/Notify 无需 Token 认证，仍遵守设备的 GATT 权限和运输锁。

Battery State 特征 UUID 为 `8f092406-c433-4408-870f-fc60f806f9d7`，支持 Read＋Notify，使用独立 CCC。`PluginBleAgpConnection.OpenAsync` 发现特征后自动开启 Notify；缺少此特征的旧固件仍可连接并使用 GET。只支持 GET 的固件也可能声明 Battery bit11，Read/Notify 支持须检查 `client.HasBatteryStateCharacteristic`。

```csharp
var capabilities = await client.ReadCapabilitiesAsync(cancellationToken);
if (client.HasBatteryStateCharacteristic)
{
    client.BatteryStateChanged += (_, battery) =>
    {
        byte? percent = battery.LevelPercent; // null 表示未知，0 表示确实为 0%。
        // 由宿主保存或显示收到的设备状态。
    };
    var initial = await client.ReadBatteryStateAsync(cancellationToken);
}
else if (capabilities.HasFlag(AgpCapabilities.Battery))
{
    var response = await client.GetBatteryAsync(cancellationToken);
    if (response.Result == AgpResultCode.Success)
    {
        byte? percent = response.State!.LevelPercent;
    }
}
```

`AgpBatteryResponse(Result, State)` 成功时包含 `AgpBatteryState`，失败时 `State=null`，结果码原样保留。成功负载严格为 9 字节：`result u8 + level u8 + time_to_discharge u24_le + time_to_charge u24_le + flags u8`；失败负载只有 1 字节 result。解析器拒绝错误消息类型/opcode、零 TID、错误长度、未知结果码、电量 101..254，以及 Flags 的保留 Serviceability 值 0。

Read 和 Notify 共用 `BATTERY_STATUS (0x0702)`：Status 类型、TID=0、8 字节电池状态，无 result 前缀，整包 17 字节。`AgpModelCodec.DecodeBatteryStatus` 返回 `AgpBatteryState`，并与 GET 共用字段校验。`ReadBatteryStateAsync` 返回快照；Notify 经 `BatteryStateChanged` 提供状态，畸形通知经 `InvalidPacketReceived` 报告且不影响后续有效通知。电池通知独立于 Light State 和 Control Point，不完成在途 GET，也不消耗请求 TID。

PW01 订阅成功后安排当前快照，此后只发送已确认电量变化，临时发送失败合并重试最新值；重连需要重新订阅。初始通知可能早于 `AgpClient` 的创建或事件注册，因此上例显式 Read 初始化状态。库不替设备重放历史状态，也不提供通知收件确认。

`State.Level` 保留原始电量，`LevelPercent` 将 `0xFF` 转为 null。两个 `TimeToDischargeMinutes` / `TimeToChargeMinutes` 保留原始 24 位分钟值，`0xFFFFFF` 表示未知；对应的 `KnownTimeToDischargeMinutes` / `KnownTimeToChargeMinutes` 将未知值转为 null。`Flags` 保留原始字节，`ParsedFlags` 提供 Presence、Indicator、Charging、Serviceability 四个 2-bit 字段，3 均表示未知。

PW01 当前只提供电量估算，两个时间固定为 `0xFFFFFF`，Flags 固定为 `0xFF`。GET、Read、Notify 共用设备的已确认电量，首次周期采样前为未知，后续采样失败保留已确认值。读取不改变灯光或 revision；库不缓存电量，也不触发采样。旧固件未声明 Battery 能力且缺少电池特征时，宿主应跳过电量操作。

批量入口为 `AgpCommands.GetBattery()`，返回 `AgpCommand<AgpBatteryResponse>`；与普通查询共用事务匹配和串行门，沿用批量 Busy 重试规则。普通 `GetBatteryAsync` 不自动重试。

宿主自定义 GATT 连接可实现可选的 `IAgpBatteryGattConnection`，提供 `HasBatteryStateCharacteristic`、`ReadBatteryStateAsync` 和 `BatteryStateNotified`，并负责订阅、退订及断连清理。原 `IAgpGattConnection` 无新增必需成员。

ArriLight 自有的 `AgpGattTransport` 已实现该接口，沿用应用连接所有权及地址/连接代次校验。共享 Runtime 分别准备 BatterySubscription、BatteryState；没有新特征时按 Battery 能力位回退 GET。电量是可选资料，读取或订阅失败仍保留在线灯控会话。控制页顶部仅为支持电量的真实 AGP 设备显示电量行；未知及离线显示“未知”，0% 保留为 0%。通知更新共享控制资料和当前可见页面，离页后只更新共享缓存；重连建立新的电量快照。Read/GET 返回时若已收到较新的通知，不覆盖该通知值。手动刷新会重新读取电量。

### ONOFF_SET 开关与翻转

按 PW01 `fd92673` 的协议，`ONOFF_SET (0x0101)` 使用单字节 `onoff`，之后可跟原有 TLV：`AgpOnOff.Off=0`、`On=1`、`Toggle=2`。编码器、客户端和命令工厂都保留原有 bool 重载；枚举重载在发送前拒绝 3..255，帧仍最多 20 字节。

```csharp
var toggled = await client.ToggleOnOffAsync(cancellationToken: cancellationToken);
if (toggled.Result != AgpResultCode.Success)
{
    // 包括旧固件的 InvalidValue、未认证的 NotAuthorized 或 Busy。
}
```

也可调用 `client.SetOnOffAsync(AgpOnOff.Toggle, cancellationToken: cancellationToken)`。负载编码使用 `AgpModelCodec.EncodeOnOff(AgpOnOff.Toggle, tlvs)`；批量命令使用 `AgpCommands.ToggleOnOff(tlvs)` 或 `AgpCommands.SetOnOff(AgpOnOff.Toggle, tlvs)`。所有入口都发送设备端翻转指令。

设备在稳定状态下翻转逻辑开关状态，在渐变中翻转当前开关目标；闪烁或呼吸的暗相位仍视为开灯。PW01 从当前输出接续普通渐变，保留灯效模式、周期和亮度记忆规则；有效请求只取消触摸待执行动作，保留 APP 一次性计时，也不触发 MODE 的本地自动策略。客户端不根据读取的亮度或缓存状态推算开关目标。

响应沿用 `AgpControlResponse` 的 5 字节 `result + state_revision`，成功表示请求已接受；PW01 默认约 200 ms 的渐变完成后才发送一次 Light State Notify。翻转仍要求 Token 认证。线协议版本、OnOff 能力位及 PW01 能力值 `0x402287` 均未新增；旧固件收到 2 返回 `InvalidValue`，不能单凭 OnOff 能力位判断翻转支持。

每次客户端调用或命令执行代表新操作，分配新的非零 TID。同一请求的报文重传须复用原 TID 和原负载，PW01 在最近 4 项、6000 ms 缓存窗口内只重放结果，不再次翻转。类型化单命令接口不自动重试，也不提供指定原 TID 的重传入口；批次仅对明确的 `Busy=7` 使用新 TID 重试，因为此结果保证没有执行。超时、取消等待或断连可能发生在设备已执行之后，不能重新调用翻转来假定补发；应读取状态后决定下一操作。断连或缓存失效后不保证事务去重。

### 批量命令 ExecuteBatchAsync

`AgpCommands` 是类型化 Control Point 请求工厂，提供与客户端相对应的 Set/Get、认证、MODE 和颜色方法。工厂创建 `AgpCommand<TResponse>` 时调用现有编解码器校验参数并复制负载，不发送报文；执行器在写入前检查整组命令及选项并复制命令列表。

```csharp
var effectCommand = AgpCommands.SetEffect(AgpLightMode.Breathe, 1000);
var result = await client.ExecuteBatchAsync(
    [effectCommand, AgpCommands.SetOnOff(true)],
    options: new AgpBatchOptions { MaxBusyRetries = 3 },
    cancellationToken: cancellationToken);

if (result.IsSuccess)
{
    var effect = result.Steps[0].GetResponse(effectCommand); // AgpEffectResponse
}
else
{
    // FailedStepIndex 从 0 开始；业务失败时 Steps 包含该失败步骤。
    var failed = result.Steps[result.FailedStepIndex!.Value];
    // failed.Result / failed.Response / failed.Attempts
    // result.CompletedCount 是此前成功步骤数；它们已经在设备上执行。
}
```

同一个 `AgpClient` 的普通 Control Point 请求和批次共用执行门。批次从开始发送到完成或中止持续持有执行门，包括 Busy 退避期间；每步等待匹配 opcode/TID 的响应，解码并检查业务结果后才继续。后续批次和普通请求不能插入当前组，但等待操作的先后次序不承诺 FIFO。状态通知和特征读取不属于批量命令；直接 GATT 写入或另一个客户端实例由宿主统一管理。推荐每条 BLE 连接只有一个客户端和命令入口。

只有明确的 `Busy=7` 重试当前步骤，保持原操作码和冻结的负载，使用新的非零 TID；已成功步骤不会重复。默认 `MaxBusyRetries=3` 不包含首次发送，`InitialBusyRetryDelay=100 ms`、`MaximumBusyRetryDelay=400 ms`，等待 100/200/400 ms。可配置次数、初始延时和延时上限，`MaxBusyRetries=0` 禁用重试；其他业务错误（包括 `AuthBusy`）立即停止并返回 `IsSuccess=false`。

`RequestTimeout` 默认每次尝试 6 秒；`GetProvisionToken(timeout)` 默认使用认证的 60 秒超时。超时、传输失败和协议解析失败不自动重试：抛出 `AgpBatchException`，原错误在 `InnerException`，进度在 `Progress`。排队、等待响应和退避均支持取消；取消抛出派生自 `OperationCanceledException` 的 `AgpBatchCanceledException`，同样附带 `Progress`。需要限制整个批次（含排队）时，调用方可使用带截止时间的 CancellationToken。客户端 Dispose 会中止活动批次和排队请求，并等待发送流程退出后释放同步资源。

进度包含总命令数 `CommandCount`、成功步骤数 `CompletedCount`、失败/中止索引 `FailedStepIndex` 和已开始步骤 `Steps`。每步的 `AttemptResults` 保存所有已开始尝试的 TID、结果及类型化响应，`Attempts` 包含首次发送；`Response`/`Result` 表示最后一次尝试。没有可解码响应的尝试为 null，设备可能已执行该命令，不能根据 null 推断未执行。取消尚未发送的步骤可能只有失败索引，没有 StepResult。业务失败或异常都不回滚此前成功步骤。空批次成功返回零步骤。

批量命令保留模型各自的响应规则：MODE 成功为变长配置、失败为单字节结果；颜色 Get 成功使用专用 `*_STATUS` opcode，失败使用原请求 opcode 的标准 Control Response。能匹配当前批次请求头部的畸形帧立即终止；无法归属的异常帧继续通过 `InvalidPacketReceived` 报告，等待匹配响应直到超时。现有单命令方法不自动重试，也不改变原业务错误返回方式。

上述“特效后开灯”例子按 PW01 `01e939e` 的解耦语义执行：`EFFECT_SET` 保持当前开关状态；成功后再发送 `ONOFF_SET=1` 启动保存的效果。蓝牙关灯保留效果配置，开灯恢复；批次只保证客户端命令组不被插入，设备会逐步执行并发出正常状态通知。

### MODE_SET / MODE_GET 本地硬件延时策略

新版 PW01 把本地硬件开关灯策略登记为 `MODE_SET (0x0121)` 和 `MODE_GET (0x0122)`，通过 `AgpCapabilities.LocalMode (bit22)` 判断支持情况。`MODE_SET` 要求连接 Token 认证，`MODE_GET` 为空负载查询，不要求 Token；响应回显请求 opcode 和非零 TID。

`AgpModeConfig` 使用 `AgpLocalMode`，与 `AgpLightMode` 的 Steady/Blink/Breathe 灯效独立。配置只影响本地硬件操作，PW01 当前对应 T1 触摸开关灯：

| AgpLocalMode | OnSeconds | OffSeconds | Set 负载 |
| --- | --- | --- | --- |
| `DelayNone=0` | 0 | 0 | `mode u8`，1 字节 |
| `DelayOn=1` | 1..2,592,000 | 0 | `mode + on_seconds u32_le`，5 字节 |
| `DelayOff=2` | 0 | 1..2,592,000 | `mode + off_seconds u32_le`，5 字节 |
| `DelayBoth=3` | 1..2,592,000 | 1..2,592,000 | `mode + on_seconds u32_le + off_seconds u32_le`，9 字节 |
| `OnAutoOff=4` | 0 | 1..2,592,000 | `mode + auto_off_seconds u32_le`，5 字节 |
| `OffAutoOn=5` | 1..2,592,000 | 0 | `mode + auto_on_seconds u32_le`，5 字节 |

未启用方向必须为 0，出现的时长必须大于 0；秒数不压缩、不近似。编码器在 GATT 写入前拒绝未知模式、越界秒数和模式/方向不一致。负载不接受 TLV 或 flags。

`OnAutoOff` 对应 `ON_AUTO_OFF`：触摸开灯/切换亮度立即执行，再按 `OffSeconds` 自动关灯。`OffAutoOn` 对应 `OFF_AUTO_ON`：触摸关灯立即执行，再按 `OnSeconds` 自动开灯。重复同方向触摸重新计时，反向操作立即执行并取消自动动作；到期只执行一次。模式值 4/5 不是方向位掩码，编解码明确将参数映射到自动动作的目标方向。蓝牙即时灯控和设置配置本身均不触发自动动作。

```csharp
if (capabilities.HasFlag(AgpCapabilities.LocalMode))
{
    // 硬件开灯操作等待 60 秒；硬件关灯操作等待独立的 120 秒。
    var configured = await client.SetModeAsync(
        new AgpModeConfig(AgpLocalMode.DelayBoth, OnSeconds: 60, OffSeconds: 120),
        cancellationToken);
    var current = await client.GetModeAsync(cancellationToken);
    if (current.Result == AgpResultCode.Success)
    {
        // current.Config 是设备保存的配置；不是剩余倒计时。
    }
    var immediate = await client.SetModeAsync(new AgpModeConfig(AgpLocalMode.DelayNone), cancellationToken);
    // 触摸开灯立即执行，60 秒后自动关灯。
    var autoOff = await client.SetModeAsync(
        new AgpModeConfig(AgpLocalMode.OnAutoOff, OffSeconds: 60), cancellationToken);
}
```

两种命令均返回 `AgpModeResponse`：成功负载为 `result=0 + mode + 对应秒数`，六种模式严格为 2/6/6/10/6/6 字节，最大包长 19 字节，`Config` 为设备当前配置；失败负载仅 1 字节 `result`，`Config=null`。MODE 响应没有 state revision，不能按标准 Control Response 或 Relative Timer Response 解码。

PW01 在配置变化时先保存，再取消旧触摸计时；不立即开关灯、不停止灯效或渐变、不取消 APP 一次性计时。相同配置和事务重放不重复执行；保存失败保留配置和运行状态。配置在断电、Reset 和正常 OTA 后保留，运行中的倒计时不保留。MODE GET 不改变状态，MODE 配置变化不增加 Light State revision、不发送 Light State 通知，Light State 的 `Mode` 继续表示灯效。

OnOff 使用单字节值及原有可选 TLV，两个一次性 Delay Set 使用严格 4 字节秒数；这些消息不携带模式 flags，也不修改本地硬件模式。蓝牙即时灯控和一次性延时按各自语义执行。

### Relative Timer 延时开/关灯

`RelativeTimer (0x80)` 同时表示支持延时开灯和关灯，不要求 `RtcTime` 或 `Scheduler`。延时关灯沿用 `DELAY_OFF_SET (0x0201)` / `DELAY_OFF_GET (0x0202)`；延时开灯使用 `DELAY_ON_SET (0x0203)` / `DELAY_ON_GET (0x0204)`。

两个 Set 请求均为 4 字节小端秒数，范围 0..2,592,000；0 取消当前倒计时，非零替换倒计时的动作及期限。两个 Get 请求负载为空，不要求 Token。Set 仍按设备的安全控制要求认证。

四个命令的响应均为 6 字节：`result u8 + active u8 + remaining_seconds u32_le`，并回显请求 opcode 和 TID。`AgpDelayOnResponse` 与 `AgpDelayOffResponse` 分别表示对应方向的状态；例如当前执行延时开灯时，查询延时关灯得到 `IsActive=false` 和 `RemainingSeconds=0`。业务失败也保留这 6 字节状态，不使用通用 5 字节 Control Response。

```csharp
var timer = await client.SetDelayOnAsync(60, cancellationToken);
var current = await client.GetDelayOnAsync(cancellationToken);
var cancelled = await client.SetDelayOnAsync(0, cancellationToken);
```

## PW01 基线与颜色产品

正式语义和测试向量来自 `D:\git\PW01\docs\AGP`，可移植 C 实现来自 `D:\git\PW01\src\AGP`。颜色模型基线为 PW01 `97bed2d`；MODE 与恢复的基础灯控格式按 2026-10-07 的 PW01 工作区同步（基于 `877214b`，新增 `agp_mode_model.c/.h`）。2026-10-08 同步 PW01 `fd92673` 的 `ONOFF_SET=2` 翻转；2026-10-18 同步 PW01 工作区新增的 `BATTERY_GET`。MODE 正式语义同时见 PW01 `docs/DELAY_MODE.md`。增加 Battery bit11 后，当前 PW01 工作区能力为 `0x0000000000402A87`。

当前 PW01 仍是单 PWM 单色硬件，因此颜色能力位全部为 0、不暴露 Color State，格式正确的颜色请求会返回 `UnsupportedFeature`。这不影响协议 binding 用于后续 RGB、RGBW、CCT 或 RGB+CCT 产品：

- 成功 Color Get 返回对应 `*_STATUS` opcode 和查询 TID；失败则以原请求 opcode 返回标准 Control Response；
- HSL 与 CTL 共存时由 `active_model` 指示当前模式，不假设两种颜色空间可逆；
- `COLOR_TRANSITION (0x83)` 把 Transition Time 与 5 ms Execution Delay 合并在一个 TLV 中，以满足 11 字节负载上限；
- 颜色转换、通道校准、混色及 Gamma 由产品适配层完成，APP 和协议库不直接生成 PWM。

当前 PW01 的 Control Point 写、Control Point CCC 及 Light State 读需要加密。`PluginBleAgpConnection` 会执行这些 GATT 操作，但系统配对提示、Bond 管理和失败重试必须由宿主应用处理。

## ArriLight 后续接入边界

本次只修改协议库。ArriLight 手动接入时，两个分支都可以继续引用现有 `ArriOpto.BLE.csproj`：

- `master` 保留当前 Mesh/组控路径，按设备能力或产品类型另建 AGP 直连服务；不要把 AGP 帧送入 `MeshClient`。
- `gatt` 可在现有 Plugin.BLE 连接服务上实现 `IAgpGattConnection`，或直接使用 `PluginBleAgpConnection`；避免同一物理连接同时由两个对象断开。
- 连接后顺序是开启 Control Point Indicate、开启 Light State Notify（颜色产品还要开启 Color State Notify）、读取 Capabilities、读取当前状态，再串行发请求。
- 每次连接后由 APP 下发 UTC；以 Light State revision 合并读/通知结果；本地时区和 DST 变化时重写 UTC 日程。

真实设备仍需验证服务发现、系统配对、加密权限、Indication 确认、Notify 顺序、断线重连和事务超时。
