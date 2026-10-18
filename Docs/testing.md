# ArriOpto.BLE 测试说明

测试项目位于 `tests/ArriOpto.BLE.Tests`，目标框架为 `net10.0`，使用 xUnit。

```powershell
dotnet restore ArriOpto.BLE.slnx
dotnet test tests\ArriOpto.BLE.Tests\ArriOpto.BLE.Tests.csproj --no-restore
```

测试覆盖 Mesh 字节向量、SIG 模型、网络与配网派生、CMAC/AES、Proxy PDU、AGP 规范向量/HSL/CTL/客户端事务、OTA 固件装载、协议帧、会话和脱敏诊断。

AGP OnOff 翻转回归对照 PW01 `fd92673`：`AgpOnOffTests` 校验 `onoff=2` 精确向量、0/1 兼容、可选 TLV、20 字节边界及非法值；客户端和批量测试验证结果码、新 TID、取消、Busy 重试、成功步骤不重放以及翻转超时后不自动重发。GATT 使用 fake，实际设备的渐变、灯效暗相位和事务缓存仍需硬件验证。

## 规则

- 编解码测试必须断言精确十六进制字节，不能只进行编码后再解码的自洽测试。
- 修复协议问题时，将最小失败帧固化为字节级回归测试；来自日志的密钥必须脱敏。
- BLE/GATT 测试使用 fake `IMeshLink`、fake `IAgpGattConnection` 或 fake `IOtaGattConnection`，不访问真实适配器。
- 时间相关逻辑使用可控时间或同步 fake，禁止真实等待。
- 每个失败分支至少断言异常类型、状态变化或没有写入，而不只断言未崩溃。

涉及真实连接时序、配网或 OTA 的变更，单元测试不能替代宿主应用中的硬件回归。

## ArriLight PW01 扫描回归（2026-10-15）

本仓库的扫描测试使用真实 `BluetoothDiscoveryService` 和 AGP 广播解析器，适配器由 fake 提供；运行命令：

```powershell
dotnet test tests\ArriLight.Discovery.Tests\ArriLight.Discovery.Tests.csproj
dotnet test tests\ArriLight.Tests\ArriLight.Tests.csproj
```

`AgpAdvertisementTests` 固化 PW01 的 18 字节服务数据，覆盖 Android `ServiceData128Bit`、iOS `ServiceData`、配对窗口打开/关闭，以及错误 UUID、字节序、版本、状态、截断和无关记录。`DiscoveryTests.Pw01AdvertisementIsDiscoveredWithPairingStatus` 经扫描事件验证协议识别、配对状态、iOS UUID 标识和设备缓存。修复前新增用例中 5 项 iOS 格式测试失败，修复后扫描测试 20 项、应用测试 495 项全部通过。iPhone/PW01 的无线扫描与绑定步骤见 [BLE 流程说明](ArriLight-BLE-Flow.md) 的添加页面部分，尚未执行实机验证。

Windows 上的 iOS 托管目标编译通过（`net10.0-ios/iossimulator-x64/ArriLight.dll`，0 警告、0 错误）；未验证原生打包、签名和设备安装。

## ArriLight AGP 电池 GET / Read / Notify 回归（2026-10-18）

本仓库中的库新增回归位于 `tests/ArriLight.Tests/AgpBatteryTests.cs` 和 `AgpBatteryStateTests.cs`，直接引用当前 `ArriOpto.BLE` 项目：

```powershell
dotnet test tests\ArriLight.Tests\ArriLight.Tests.csproj --filter FullyQualifiedName~AgpBattery
```

35 项用例覆盖 PW01 `docs/AGP/test-vectors.json` 中的 80%、0%、100%、未知电量及失败响应，精确请求帧、Battery 能力位、24 位小端分钟值、未知值与零值区分、四项 Flags、非法长度/字段/消息身份、结果码保留、无认证查询、opcode/TID 匹配、串行查询和新快照、取消以及批次 Busy 重试和协议异常。GATT 使用内存 fake；实际 PW01 电量采样、加密连接和 Indication 往返仍需硬件验证。

新增 29 项 Read/Notify 用例固化 `pw01_battery_status_80/unknown`，覆盖无 result 前缀的 17 字节 Status、共享字段解析、非法消息身份/长度/字段、特征 Read、Notify 数据复制和状态事件、无效通知诊断、通知与 GET 通道隔离、旧固件和旧连接接口兼容、取消/GATT 错误、Read＋Notify 属性校验、CCC 订阅失败清理、退订失败后继续释放、通知解绑和重连重新订阅。测试调用真实的 `PluginBleAgpConnection` 和 `AgpClient`，Plugin.BLE 设备与特征由代理提供；尚未实机验证 CCC、系统权限及推送时序。

GET 阶段验证：电池定向测试 35/35、扫描测试 20/20、应用全量复跑 551/551 通过。该阶段全量首次运行的 `DeviceReconnectQueueTests.TransientAuthenticationFailureRetriesWithoutRequiringADifferentToken` 曾出现认证次数预期 2、实际 3；该用例单独运行及随后全量复跑均通过，未修改重连逻辑。Read/Notify 阶段电池定向测试已扩展至 64/64 通过。

Read/Notify 阶段验证结果：电池定向测试 64/64、扫描测试 20/20、应用全量测试 580/580 全部通过；未进行 PW01 实机联调。

## 控制页电量显示（2026-10-18）

`AgpBatteryDisplayTests` 新增 17 项应用集成测试，使用真实 Runtime、连接服务、应用 GATT 适配器和控制页 ViewModel。覆盖支持判断与隐藏、80%/0%/100%/未知、Read 初始化、Notify 更新、GET 旧固件与手动刷新、错误报文、读取结果与通知竞争、可选订阅/读取失败后仍可灯控、离页/重入、切换设备及后台断连重连。与库测试一起运行：

```powershell
dotnet test tests\ArriLight.Tests\ArriLight.Tests.csproj --filter FullyQualifiedName~AgpBattery
```

电池相关 81 项、应用全量 597 项、扫描 20 项全部通过；Windows 上 iOS 托管目标和 XAML 源生成编译通过（0 警告、0 错误）。MAUI 原生页面布局、真机 CCC 和电量通知仍需设备验证。
