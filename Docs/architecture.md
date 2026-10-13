# ArriOpto.BLE 架构说明

`ArriOpto.BLE` 是面向 .NET 10 的协议库，负责可验证的蓝牙 Mesh、PB-GATT、SIG 模型、AGP 私有 GATT 协议、加密及 PHY OTA 字节与状态机。它不引用 MAUI、页面、平台持久化或应用服务；设备身份、网络拓扑和运行时灯状态由调用应用负责保存。

## 组件

| 范围 | 入口与职责 |
|---|---|
| Mesh 访问、传输与网络层 | `Mesh/MeshClient.cs`：Access 消息编码、上/下层传输、网络加密和 Proxy PDU 封装/解析 |
| Mesh 配网 | `Mesh/Provisioning/`：PB-GATT 状态机、认证与网络密钥派生 |
| Mesh GATT Profile | `Mesh/MeshProfile.cs`：PB-GATT/Proxy 服务与特征值 UUID（0x1827/0x1828/0x2ADB-0x2ADE） |
| Mesh 广播识别 | `Mesh/MeshAdvertisement.cs`：从广播记录判定配网阶段（Provisioning/Proxy） |
| SIG 模型 | `Mesh/Models/`：Config、Generic、Light、Scene、Time 与 Scheduler 消息编码和解析 |
| Mesh 加密 | `Mesh/Crypto/`：CMAC、K1/K2/K3/K4、nonce、网络 PDU 加解密 |
| AGP 私有协议 | `Agp/`：v1 帧/TLV、Light/Time/Relative Timer/Scheduler/Lightness16/HSL/CTL 模型、事务客户端和 Plugin.BLE GATT 适配 |
| OTA | `Ota/`：固件解析、诊断、SLB 与单分区 OTA 会话 |
| 二进制基础设施 | `Protocol/BinaryCodec.cs`：端序与定长字段读写 |

## 协议约束

- Mesh 序列号属于调用方的网络/Provisioner 身份；库不会持久化或跨网络复用它。
  序列号在**构造网络 PDU 时**即被消耗，`MeshClient.SequenceNumberAllocated` 在每次
  分配后立即回调宿主去持久化；宿主不应改用"写入成功后再保存"，那样会在写入失败、
  取消或断连时丢号，导致下次连接触发设备的回放保护。
- 单播命令的响应匹配必须同时考虑 opcode 与源单播地址。
- 多字节 Mesh 参数采用小端；两字节 opcode 按 opcode 字节顺序输出。
- 连接节点只是 GATT/Proxy 传输路由；Mesh 目标地址始终编码在 PDU 中。
- Provisioning 服务 `0x1827` 与 Proxy 服务 `0x1828` 属于不同连接阶段；调用方在重连后必须重新发现 GATT。
- 不在异常、诊断或日志中记录 NetKey、AppKey、DeviceKey、认证值、签名密码或固件正文。
- AGP v1 严格接受版本 1.0、flags=0、最多 20 字节；Control Point 请求必须使用非零事务 ID且一次只有一个在途请求。
- AGP 业务结果通过类型化响应的 `Result` 返回；只有线格式损坏、超时或 GATT 失败才使用异常。
- AGP 的 UTC、时区/DST 换算、连接重试、绑定 UI 和设备状态持久化属于宿主应用。
- 颜色产品的 Lightness16、旧百分比亮度、HSL/CTL Lightness 共享一个权威亮度和 revision；颜色空间转换、通道校准及 Gamma 属于产品适配层。
- Color State 是能力相关的可选 Read/Notify 特征；单色 PW01 不暴露它，GATT 连接不得把缺少该特征视为发现失败。

## 调用方边界

应用应把 BLE 身份、Mesh 网络拓扑和灯的运行状态分开存储。页面不能直接组装 Mesh/OTA 字节；连接生命周期、重试、网关选择、持久化与用户交互都应位于应用服务层。

判据：**"这个字节、常量或语义，换一个宿主语言/平台会变吗？"** 不会 → 属于库；会（依赖 Plugin.BLE、平台持久化、UI）→ 属于应用。

据此：

| 属于库 | 属于应用 |
|---|---|
| 消息编码**与解析**、加解密、序列号语义 | Plugin.BLE GATT 读写、连接生命周期与重试 |
| GATT 服务/特征 UUID 常量（`MeshProfile`、`AgpProfile`） | 网络/节点的持久化介质（Preferences、数据库） |
| 广播识别（`MeshAdvertisement`、`AgpAdvertisement`） | 设备清单、协议标签/图标等 UI 身份 |
| PB-GATT 状态机、模型绑定规则 | 页面、ViewModel、DI、本地化 |

应用不应该"绕开库重新实现一遍协议"：如果某个库 API 解析错字节或语义不对，应当修库。
历史上 `ArriLight` 曾用 `MeshAppKeyConfigurator`（重解析 Config AppKey Status）和
`MeshSequencePersistingLink`（包装 `IMeshLink` 抢先保存序列号）两个补偿类绕过
`MeshClient.AddAppKeyAsync` 的 opcode/状态码误读和序列号保存时机问题；二者已在
2026-10-11 的根因修复（Status 改用打包索引解析、`MeshClient.SequenceNumberAllocated`
钩子）后删除。GATT UUID 与广播识别也已从应用下沉到 `MeshProfile` / `MeshAdvertisement`，
与既有的 `AgpProfile` / `AgpAdvertisement` 对称。

应用侧的例外保留：`BluetoothDeviceProtocol`（Unknown/Agp/Mesh）留在应用——它是设备清单的
持久化身份与 UI 展示标签，不是传输层协议事实。
