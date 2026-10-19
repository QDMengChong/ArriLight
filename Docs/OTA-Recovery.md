# OTA 升级与恢复

2026-10-19 实现，配套固件说明：`D:/git/OTA/docs/OTA_RECOVERY.md`。

本地文件加载后只进行校验。设备在线或就绪时，页面仅提供正常升级入口：在线固件可下载并升级，本地固件可立即升级；不显示检测 OTA 状态或恢复说明。设备离线时，仅对完整、可用的 Single Bank 固件显示“检测 OTA 状态”，隐藏正常升级入口。连接状态和 runtime 的在线/就绪状态变化会立即更新入口。原“设备已处于 OTA：恢复升级”按钮已删除。

设备已红灯慢闪时，进入固件升级页，选择完整、匹配设备的 Single Bank 固件，再点击“检测 OTA 状态”。扫描到设备后显示标题为“选择待恢复的OTA设备”的弹层，列表通过单选圆点、边框和背景高亮显示选择状态。点击列表项仅选中，不关闭弹层。未选择时“恢复升级”禁用；选中后点击位于“取消”之前的“恢复升级”，校验所选设备并直接开始恢复，不再弹出第二次确认。弹层中显示不可取消、断电/断连损坏固件和持续供电/蓝牙畅通的风险提示；正常升级仍使用原确认对话框。

检测会连接所选设备并检查 GATT 模式：必须有 Single Bank 服务和可用的 Command / Notify / Data（FF04）特征，不能只凭同名广播或断线判断。检测只发现服务、协商 MTU、订阅通知并断开，不发送切换、擦除或固件写入命令。未找到、非 OTA、取消或检测失败均不开始恢复。取消、返回、更换固件、离开页面或原应用恢复在线会关闭设备选择，不触发升级。

恢复开始时重新扫描，只接受已选设备 ID，并在实际传输连接上再次验证 OTA 模式。目标消失或退出 OTA 时终止，不改选其他设备，也不自动从 APP 切换。开始升级后取消按钮隐藏，页面返回被阻止，屏幕保持唤醒。失败后可重新检测并恢复。检测结果只代表检测时的状态，loader 仍可能因空闲超时退出 OTA。手机系统中断、断连和断电仍可能使单 bank 固件不完整。

请仅让目标设备处于 OTA 模式，并在检测时手动确认所选目标；当前 loader 广播不能可靠映射回原应用身份，iOS 的 UUID 不用于推算 MAC。检测与恢复扫描不连接原应用，不要求 AGP 服务或旧应用认证。恢复会重新发送全部分区。

配套新 loader 在 60 秒没有升级通信后软复位：有效旧应用或完整新应用可启动，否则继续 OTA 广播等待重试。旧 loader 可能需要重新上电。只安装新 App 或升级 PW01 用户应用不能更新 loader；新 loader 产物为 `D:/git/OTA/bin/ota_single_bank.hex`。

`PluginBleOtaTransport` 为可选的平台适配入口，协议层和库仍不依赖 MAUI。App 的 iOS 实现把 Plugin.BLE 3.2.1 的 payload 长度加回 ATT 的 3 字节，并使用 CoreBluetooth 的发送能力信号发送 WithoutResponse，避免等待每包不存在的 ready 回调。固件写入成功仍以协议通知为准。参考 [Apple 发送能力说明](https://developer.apple.com/documentation/corebluetooth/cbperipheral/cansendwritewithoutresponse)、[写入响应约定](https://developer.apple.com/documentation/corebluetooth/cbperipheral/writevalue(_:for:type:))。

每次 GATT 写入最长等待 10 秒。切换/重启命令可容忍设备已断连时的普通 iOS 异常；其他命令失败仍报错。最后一次数据发送进度到 100% 不能替代最终提交确认，错误通知不得转为成功。

恢复仍使用目标 runtime 与连接队列的独占机制，跳过旧应用连接和认证，操作后清理原会话。后台中断沿用应用生命周期取消策略，并在界面提示恢复升级。直接恢复不自动重启旧应用准备任务。

测试入口：`dotnet test tests/ArriLight.Tests/ArriLight.Tests.csproj --no-restore`。新增回归位于 `FirmwareRecoveryTests.cs`，测试真实会话及连接适配器，射频与原生 UI 使用内存边界模拟。Android 编译、Windows 上 iOS managed Compile 与主机回归均不等同于真实 OTA 或签名 iOS 安装包。Android 完整 APK 的应用覆盖安装已在 vivo V2337A 上验证成功，尚未进行真实固件升级或恢复弹层的手机交互验证；具体结果记录在 `Docs/AI_CONTEXT.md`。
