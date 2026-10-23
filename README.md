# ArriLight

## iOS / TestFlight 构建

工作流位于 [`.github/workflows/ios.yml`](.github/workflows/ios.yml)。保留 .NET 10，
使用 GitHub 的 `macos-26` ARM64 runner、Xcode `26.6`、.NET SDK `10.0.401`
和 workload set `10.0.401`。SDK 固定只写入 CI checkout，不改变本地开发环境。
两个普通 .NET 测试项目在 Ubuntu 上运行。依赖应用 `project.assets.json` 的许可证覆盖测试
在 macOS 安装 workload、还原 iOS 应用后执行；全部检查通过后才继续构建与上传。
iOS 还原使用静态项目图并隔离应用的平台参数，引用的协议库仍以 `net10.0` 为目标。

将工作流提交并推送到默认分支后，在 GitHub **Actions → iOS build and TestFlight →
Run workflow** 中选择模式。涉及应用、协议库或测试的 Pull Request 自动执行 `build-check`。

| 模式 | 用途 | 是否需要苹果签名材料 |
| --- | --- | --- |
| `build-check`（默认） | 编译 iOS 模拟器 Debug 应用，保存日志和 ZIP；现在即可使用 | 否；产物不能安装到 iPhone |
| `ipa` | 编译并保存 App Store 签名的 iPhone Release IPA | 是；这个分发签名的 IPA 需要经过 TestFlight / App Store 安装 |
| `testflight` | 生成同样的 IPA，并验证、上传到 App Store Connect | 是，还需要 App Store Connect API Key |

### 注册账号后的配置

1. 注册 Apple Developer Program，确定正式 iOS Bundle ID。
2. 在苹果开发者后台注册对应 App ID，在 App Store Connect 中创建使用同一 Bundle ID
   的 ArriLight 应用记录。
3. 创建 **Apple Distribution** 证书，导出包含私钥的 `.p12` 并设置密码；仅有 `.cer`
   文件不能用于 CI 签名。创建并下载 **App Store Connect** 分发描述文件
   `.mobileprovision`，选择该 App ID 和证书。
4. 创建有权限上传该应用的 **App Store Connect 团队 API Key**，下载 `.p8`，记录
   Key ID 和 Issuer ID。可先使用具有该应用管理权限的 App Manager 角色。
5. 在仓库 **Settings → Secrets and variables → Actions** 中添加以下配置。

| 类型 | 名称 | 内容 |
| --- | --- | --- |
| Variable | `IOS_BUNDLE_ID` | 正式 iOS Bundle ID，必须匹配应用记录和描述文件 |
| Secret | `IOS_CERTIFICATE_BASE64` | 包含私钥的 `.p12` 文件的 Base64 |
| Secret | `IOS_CERTIFICATE_PASSWORD` | `.p12` 导出密码 |
| Secret | `IOS_PROFILE_BASE64` | App Store Connect `.mobileprovision` 文件的 Base64 |
| Secret | `APP_STORE_CONNECT_API_KEY_BASE64` | `.p8` 文件的 Base64，仅 `testflight` 必需 |
| Secret | `APP_STORE_CONNECT_KEY_ID` | API Key 的 10 位 Key ID，仅 `testflight` 必需 |
| Secret | `APP_STORE_CONNECT_ISSUER_ID` | 团队 API Key 的 Issuer ID，仅 `testflight` 必需 |

在 Windows PowerShell 中，将每个文件转换为 Base64 后复制到对应 Secret：

```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes('C:\AppleSigning\distribution.p12')) | Set-Clipboard
[Convert]::ToBase64String([IO.File]::ReadAllBytes('C:\AppleSigning\ArriLight_AppStore.mobileprovision')) | Set-Clipboard
[Convert]::ToBase64String([IO.File]::ReadAllBytes('C:\AppleSigning\AuthKey_YOURKEYID.p8')) | Set-Clipboard
```

每条命令后分别粘贴和保存对应 Secret。Base64 是编码，证书、私钥、密码应保存在 Secrets，
不提交到仓库。没有 Mac 时，也可以在 Windows 用 OpenSSL 生成 CSR 和私钥，向苹果申请证书后
将证书与原私钥导出为 `.p12`；必须保存生成 CSR 时的私钥。

先运行 `ipa` 核对签名和产物，再运行 `testflight`。工作流会检查必需配置、描述文件有效期、
分发类型和 Bundle ID，并自动选择描述文件对应的签名证书；不需要额外配置证书名称或 Profile UUID。
iOS Bundle ID 只通过构建参数覆盖，Android 包名仍使用项目配置。

签名脚本先用 OpenSSL 校验 `.p12` 与密码，再把原证书和私钥重新打包为 macOS 兼容的
`PBE-SHA1-3DES` / `SHA1 MAC` 格式，处理 OpenSSL 3 默认 PKCS#12 格式的导入兼容问题。
可继续使用原来的 `IOS_CERTIFICATE_BASE64` 和 `IOS_CERTIFICATE_PASSWORD`，无需手动转换。
临时 PEM 的私钥保持加密；临时导入密码随机生成并在日志中隐藏，转换文件在导入后或失败时清理。
日志会分别显示 OpenSSL 校验通过、兼容格式准备完成、macOS 导入通过，便于定位失败阶段。

### 版本、产物与测试者

`version` 留空时使用项目的 `ApplicationDisplayVersion`（当前 `0.1.3`）。
`build_number` 留空时使用 `(GitHub run number + 4).run attempt`，例如第一次运行是 `5.1`，
重新运行是 `5.2`，下一次是 `6.1`。此值只覆盖 iOS 的 `CFBundleVersion`，不修改源文件。
如果苹果后台已经有更高的构建号，填写更高的 `build_number`，或相应调整脚本中的起始偏移。
显式构建号需要自行保证递增；默认规则允许最多 9,995 次工作流运行、99 次单次重试。

在 Actions 运行页面的 **Artifacts** 中下载 IPA、可用的 dSYM、构建日志和测试结果，
保留 14 天。IPA 在上传苹果前保存，上传失败后仍可下载。

上传成功只表示 App Store Connect 已接收文件。等待苹果处理完成后，在 **App Store Connect →
ArriLight → TestFlight** 完成测试信息、加密合规信息和测试者分组。
外部测试者首次测试需要 TestFlight 审核；工作流不会自动提交审核或邀请测试者。
应用使用了 AES 等加密，请按真实用途回答苹果的出口合规问题，不要直接声明应用没有加密。

### 目前的真机验证范围

生成 IPA 不代表所有 iOS 功能已经验证。当前 Mesh 加解密调用 `AesCcm`，该 .NET 实现在 iOS
上不受支持，Mesh 真机测试前需要采用兼容实现。Android 的 MAC 与 iOS 的设备 UUID 不相同，
跨平台二维码导入与重连也需要实际验证。蓝牙、相机、权限拒绝、前后台切换、灯控和 OTA
都需要在 iPhone 上复测。应用的 `Preferences` UserDefaults 隐私声明已补齐。

本地 Windows 无法执行 macOS/Xcode 的编译与签名；首次 `build-check` 是完整 iOS 编译验证，
首次 `testflight` 是苹果签名和上传验证。更换 SDK 时要同时检查 workload 与 runner 中的
Xcode 版本，不要单独更新到不匹配的 workload。

参考：[MAUI CLI 发布](https://learn.microsoft.com/en-us/dotnet/maui/ios/deployment/publish-cli?view=net-maui-10.0)、
[workload / Xcode 对应版本](https://github.com/dotnet/macios/releases/tag/dotnet-10.0.1xx-xcode26.5-10318)、
[GitHub 证书导入](https://docs.github.com/en/actions/how-tos/deploy/deploy-to-third-party-platforms/sign-xcode-applications)、
[App Store Connect 上传](https://developer.apple.com/help/app-store-connect/manage-builds/upload-builds/)。

## Android 发布

dotnet publish -c Release -p:AndroidKeyStore=true -p:AndroidSigningKeyStore="C:\Users\RLGE\AppData\Local\Xamarin\Mono for Android\Keystore\" -p:AndroidSigningKeyAlias=ArriOpto -p:AndroidSigningStorePass=12345678 -p:AndroidSigningKeyPass=12345678
## OTA 在线升级

在线升级通过 Cloudflare Python Workers 访问 R2。部署文件、API 契约、R2 目录和
`uv run pywrangler deploy` 步骤见
[`cloudflare-workers/README.md`](cloudflare-workers/README.md)。列表 Worker 接收
硬件版本（例如 `PW01-A1`）和本地固件版本，支持 `.hex16`（Single Bank）和 `.bin`（SLB）文件。

两个 Worker 的默认地址在 `ArriLight/Resources/Raw/appsettings.json` 中。首次读取时，APP
将它复制到应用数据目录；之后使用该目录中保存的配置。检测到更新后，APP 下载文件到缓存目录，沿用现有
对应协议的 OTA 流程校验并升级设备；设备 OTA 服务与固件协议不匹配时会在发送升级命令及
固件数据前拒绝升级。若 R2 对象设置了 `sha256` 自定义元数据，APP 会在
下载后执行 SHA-256 校验；没有该元数据时会记录未校验提示。

调试模式默认关闭。在“我的 → 应用版本”页面连续点击版本号 5 次，相邻点击间隔不超过
2 秒，确认后可以开启或关闭调试模式。开启后日志追加写入应用数据目录的 `arrilight_debug.txt`，
该页面同时显示“编辑 appsettings.json”入口，可直接修改 JSON 文本并保存。目前支持
`FirmwareUpdate.ListWorkerUrl` 和 `FirmwareUpdate.GetWorkerUrl` 两个地址。保存时校验 JSON、
配置字段和 HTTP/HTTPS 地址；校验失败会保留原文件和编辑内容。保存成功后下一次在线检查
直接使用新地址，无需重启。关闭调试模式会隐藏编辑入口并停止日志追加，已保存的配置继续生效，
已有日志也会保留。Debug 构建始终同步显示在 Visual Studio 的“输出 → 调试”窗口，Release
构建不产生 Visual Studio 调试输出。日志不会记录 AES Key 或固件正文。

控制页延时卡片合并一次性自动关灯与硬件触摸模式配置，模式 Switch 开启表示常亮，关闭表示
自动关灯，共用秒数输入框。全局初始化通过 MODE_GET 取得设备保存配置；点击设置才发送 MODE_SET，
编辑不发送指令，失败保留草稿。模式秒数范围为 1～2,592,000，配置由设备保存；
该模式用于 PW01 T1 触摸，与 APP 的一次性自动关灯配置独立。
主页和控制页开关发送 `ONOFF_SET(2)`，由设备翻转逻辑状态或渐变目标，未知或离线状态也可点击连接并操作。
控制页勾选“延时”时，在翻转完成并确认设备开启后发送 `DELAY_OFF_SET(seconds)`，由设备倒计时自动关灯。
特效设置先发送 `EFFECT_SET`，再显式开灯，三种特效均适用。
组合指令使用协议库的 `ExecuteBatchAsync` 连续串行执行；中途失败停止后续发送并保留已确认
成功的状态。翻转关灯保留选中特效。延时栏继续按设备保存复选框和秒数
（默认未选中、60 秒）；不再使用 APP 等待任务或延时开灯消息。

主页“我的设备”右侧提供排序选择：按上次操作时间（默认，最近优先）、按加入时间（最近优先）、
按名字（升序），全部方式都置顶优先。排序方式和设备操作时间保存在本机；尚未操作的设备按加入时间排序。
列表重排保留正在操作的设备行及忙碌状态。

BLE 广播扫描仅在添加设备页运行；离开该页、开始绑定或 APP 进入后台时停止。
主页显示缓存及已连接设备的状态；连接和重连使用已保存的 MAC/UUID，不触发设备扫描。

SIG Mesh 控制页复用 GATT 的颜色、色温、亮度和本地预设布局。首次进入时读取节点
Composition Data，确认对应 Server 模型的 AppKey 绑定及元素地址，并缓存控制能力；
旧节点无法读取组成数据时，通过模型 Status 探测能力。页面只显示已确认可用的控件。
HSL/CTL 查询回填颜色和色温，CTL 使用设备温度范围；调亮度沿用当前颜色模型。
本地预设保存所选的 HSL/CTL 模型，重新进入页面后应用色温预设仍发送 CTL。
当前 Mesh 控制页不开放 AGP 特效、相对延时、触摸模式和日程功能。

模型注册只能说明协议接受某类指令，不能证明硬件具有对应输出通道。核查时 PW01-MESH
沿用 SDK HSL/CTL 模型，板级适配仅输出单路 Lightness，H/S、色温与 Delta-UV 只保留
协议状态，造成模型与实际灯光能力不一致。核查时该固件也未编译 PW01 产品的
延时业务模块；不能从复制的延时文档或 SIG 消息中的短延迟字段推导出自动关灯、
触摸延时模式能力。APP 不按设备名猜测或伪造这些能力。

Mesh 与 GATT 都在应用启动、恢复前台和异常断线后按保存地址主动准备设备，
共用设备操作调度器。后台和关闭会取消排队、连接及查询，释放 Proxy 会话；
页面退出只取消自己的等待，准备由应用继续完成。重连仅恢复会话和读取状态，
不重放用户操作。新 Proxy 会话重新读取节点模型，固件改变能力后更新缓存及控件。
Mesh GATT 适配层补充组成数据的下层传输分段重组、MIC 校验和 Segment Acknowledgment；
重复分段不会重复交付能力结果，断线后清空重组状态。

全局设备状态在应用启动时开始准备，默认五台并行，可在设置中选择一至五台；同设备请求串行。
连接、响应通道和按需认证完成即“在线”，无需等待开关状态即可翻转；本轮全部适用资料完成后“就绪”。
页面跳转保留共享准备和已提交操作；后台、关闭和 OTA/删除统一清理，翻转结果未知时不自动重放。

实施文档：[设计](Docs/DeviceRuntime-State-Design.md)、[实施路径](Docs/DeviceRuntime-Implementation-Plan.md)、
[实施结果与验证范围](Docs/DeviceRuntime-Implementation-Results.md)。
