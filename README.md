# ArriLight

dotnet publish -c Release -p:AndroidKeyStore=true -p:AndroidSigningKeyStore="C:\Users\RLGE\AppData\Local\Xamarin\Mono for Android\Keystore\" -p:AndroidSigningKeyAlias=ArriOpto -p:AndroidSigningStorePass=12345678 -p:AndroidSigningKeyPass=12345678
## OTA 在线升级

在线升级通过 Cloudflare Python Workers 访问 R2。部署文件、API 契约、R2 目录和
`uv run pywrangler deploy` 步骤见
[`cloudflare-workers/README.md`](cloudflare-workers/README.md)。列表 Worker 接收
硬件版本（例如 `PW01-A1`）和本地固件版本，下载 Worker 返回匹配的 `.hex16` 文件。

两个 Worker 的默认地址在 `ArriLight/Resources/Raw/appsettings.json` 中。首次读取时，APP
将它复制到应用数据目录；之后使用该目录中保存的配置。检测到更新后，APP 下载文件到缓存目录，沿用现有
Single Bank OTA 流程校验并升级设备。若 R2 对象设置了 `sha256` 自定义元数据，APP 会在
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
