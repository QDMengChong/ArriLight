# SLB 传输修复与联调（2026-10-23）

本次针对 ArriLight 与 PW01 的 SLB 升级慢、重复传输和 99.9% 无进展问题修改了两端。Android 的 GATT 数据写仍使用串行 Write Without Response；没有增加固定包间延时，也没有去掉设备鉴权或固件完整性校验。

## 100% 后完成命令超时的后续修复

第二份实机日志中，100,340 字节已按预期通过 419 次数据写入和 27 个确认完成，首包至完整确认约 7.8 秒。随后 `0x25` 写入等待 10 秒超时，未收到 `0x26`。PW01 原来在 ATT 写回调内同步扫描整包和分区，这一阻塞路径现已改为独立任务逐步校验。

新版 PW01 的合法 `0x25` 会先返回 ATT 写响应，再以 5 ms 事件、每轮最多 240 字节处理校验；30 秒内未完成会明确失败，全部校验通过才提交 `OTAF` 和最终成功通知。校验时断连、锁定或没电会取消待提交状态。完整约束与新固件见 `D:/git/PW01/docs/SLB_OTA_TRANSFER.md`。

APP 新增 `VerifyingFirmware` 状态和中英文提示，成功前的进度上限为 99.9%。`SlbVerificationTimeout` 默认 45 秒，是完成命令与最终响应的总预算，独立于窗口 ACK 和普通 GATT 写的 10 秒限制。即使完成写回调发生 Timeout／GATT 133，也需要收到本次真实的 `0x26(01)` 才成功；拒绝响应、缺失响应、取消均不会变成成功。

## 发送端行为

`ArriOpto.BLE/Ota/Protocol/SlbOtaSession.cs` 按实际 ATT MTU 计算数据负载，上限 240 字节并向下对齐至 4 字节；MTU=247 时每帧 240 字节，MTU=23 时每帧 16 字节，末帧使用真实长度。

每个窗口先生成并缓存全部协议帧。设备只确认部分数据时，按绝对 offset 找出缺失帧，保留原消息号、窗口长度和帧编号，只补发该帧及后面的帧；不再从 offset 处建立一组新编号。兼容官方 SDK 部分确认中“下一缺失帧”和“最后正确帧”两种低四位约定。

通知收件箱为每条通知记录接收序号。窗口发送前记录序号，只使用之后的反馈；发送期间已排队的部分／重复确认合并为最高有效 offset。上一窗口的延迟反馈不会倒退进度，确认范围也不能越过本窗口已发送末端。队列中的重复确认不会被当作三次独立重试，只有重新发送后仍收到无进展确认才增加计数。

等待 ACK 超时会停止本次会话，不盲目重新发送整组。配套 PW01 在约 4 秒静默后重发当前确认。设备 60 秒无写入进展会取消会话，因此不要依赖数分钟积压的旧确认来驱动进度。

只有收到最终 `0x26` 成功响应才显示完成。发送 `0x25` 后 Android 返回 GATT 133 时，仍读取实际校验响应；没有响应会超时，不能单凭断连断言升级成功。只在复位时断连、从不发送最终确认的旧设备不再使用原来的猜测成功路径。

## 安装与对照测试

两端应配套更新。首次可通过串口更新 PW01 应用，以免继续经过有问题的旧接收端；匹配的 SLB loader 需保留，应用 HEX 不是整片合并镜像。PW01 产物和操作说明在 `D:/git/PW01/docs/SLB_OTA_TRANSFER.md`。

Android Debug 完整签名安装包：

`ArriLight/bin/Debug/net10.0-android/com.arriopto.arrilight-Signed.apk`

- 包名 `com.arriopto.arrilight`，版本仍为 `0.1.9` / versionCode10。
- 大小 93,288,803 字节，SHA256=`BE05DE3A94114CE1D746BE401A2F6F7DB7441CFCF308EA1AE799CD35667A47AA`。文件大小与前一版相同，但内容和哈希已经更新，请重新安装。
- 构建 0 错误、2 条既有 OpenJDK 处理器组告警；APK v1/v2/v3 签名验证通过。arm64-v8a 与 x86_64 的 APK 条目均逐字节包含当前构建的协议库，已排除误用旧 APK。
- 该 APK 未安装到手机，本次也未进行真实 BLE 升级、PHY/DLE 协商和升级后启动测试。

安装后，对相同设备、手机、固件包和无线条件分别用 PhyOTA 与 ArriLight 计时，拆分开始擦除、传输进度和最终校验／重启时间。记录 MTU、长耗时 GATT 写、数据帧总数、确认 offset 及最后 `0x25/0x26`。旧日志中的 97,492 字节包为 407 条，上一版 100,340 字节包为 419 条；当前新包 100,632 字节、CRC16=`BD11`，应为 420 条、末帧 72 字节，均指 MTU=247、无丢帧情况。请先经串口更新接收端，再测试新包；仅安装新版 APP 无法修复旧固件同步校验的阻塞。

## 回归与构建

```powershell
dotnet test tests/ArriLight.Tests/ArriLight.Tests.csproj --no-restore
dotnet build ArriLight/ArriLight.csproj -f net10.0-android -c Debug --no-restore --disable-build-servers -m:1 '-p:JavaSdkDirectory=C:\Program Files\Android\openjdk\jdk-21.0.8'
```

`SlbTransferTests.cs` 共 22 项：四种 MTU、末帧 52 字节、首／中／尾帧丢失、保留原帧的尾部补发、200 条重复确认、部分确认合并、过期反馈、丢失完整确认后等待设备补发、非法 offset、三次真实无进展、最终校验成功／拒绝和 GATT 133／Timeout 不误报成功、异步校验阶段不提前终结。使用内存 GATT 与显式通知驱动，不等待真实定时器。

应用全量 739 项测试通过。固件实际回调的 31 个 NOR／时钟／通知场景、既有业务回归及 SLB 构建也通过。日志分别为 `tests/ArriLight.Tests/slb-verify-test-results.log`、`ArriLight/obj/slb-verify-android-build.log`；固件侧见 PW01 的 `docs/BUILD.md`。本次未重新构建 iOS 原生包，尚未实机验证新的最终校验流程。
