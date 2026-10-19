# OTA 升级与恢复

2026-10-19 实现，配套固件说明：`D:/git/OTA/docs/OTA_RECOVERY.md`。

本地文件加载后只进行校验，随后选择“立即升级”或“设备已处于 OTA：恢复升级”。在线、本地及恢复操作均在开始前显示风险提示；确认后取消按钮隐藏，页面返回被阻止，屏幕保持唤醒。失败后可重新操作。手机系统中断、断连和断电仍可能使单 bank 固件不完整。

设备已红灯慢闪时，进入固件升级页，选择完整、匹配设备的固件，点击恢复升级并选择目标 `PPlusOTA`。请仅让目标设备处于 OTA 模式。恢复扫描不连接原应用，不要求 AGP 服务或旧应用认证；iOS 的 UUID 不用于推算 MAC。该流程会重新发送全部分区。

配套新 loader 在 60 秒没有升级通信后软复位：有效旧应用或完整新应用可启动，否则继续 OTA 广播等待重试。旧 loader 可能需要重新上电。只安装新 App 或升级 PW01 用户应用不能更新 loader；新 loader 产物为 `D:/git/OTA/bin/ota_single_bank.hex`。

`PluginBleOtaTransport` 为可选的平台适配入口，协议层和库仍不依赖 MAUI。App 的 iOS 实现把 Plugin.BLE 3.2.1 的 payload 长度加回 ATT 的 3 字节，并使用 CoreBluetooth 的发送能力信号发送 WithoutResponse，避免等待每包不存在的 ready 回调。固件写入成功仍以协议通知为准。参考 [Apple 发送能力说明](https://developer.apple.com/documentation/corebluetooth/cbperipheral/cansendwritewithoutresponse)、[写入响应约定](https://developer.apple.com/documentation/corebluetooth/cbperipheral/writevalue(_:for:type:))。

每次 GATT 写入最长等待 10 秒。切换/重启命令可容忍设备已断连时的普通 iOS 异常；其他命令失败仍报错。最后一次数据发送进度到 100% 不能替代最终提交确认，错误通知不得转为成功。

恢复仍使用目标 runtime 与连接队列的独占机制，跳过旧应用连接和认证，操作后清理原会话。后台中断沿用应用生命周期取消策略，并在界面提示恢复升级。直接恢复不自动重启旧应用准备任务。

测试入口：`dotnet test tests/ArriLight.Tests/ArriLight.Tests.csproj --no-restore`。新增回归位于 `FirmwareRecoveryTests.cs`，测试真实会话及连接适配器，射频与原生 UI 使用内存边界模拟。Android 编译、Windows 上 iOS managed Compile 与主机回归均不等同于手机实测或签名 iOS 安装包；具体结果记录在 `Docs/AI_CONTEXT.md`。本次未连接或升级硬件。
