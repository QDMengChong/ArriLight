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
