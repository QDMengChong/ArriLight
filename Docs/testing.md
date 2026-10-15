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
