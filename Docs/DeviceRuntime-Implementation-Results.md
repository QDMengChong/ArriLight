# ArriLight 全局设备状态实施结果

日期：2026-10-08。依据 [设计文档](DeviceRuntime-State-Design.md)，先制定 [实施路径](DeviceRuntime-Implementation-Plan.md)，再按模型与队列、最小准备、原生会话、生命周期、页面接入及验证的顺序实施。

## 实现范围

- `DeviceRuntimeState` 保存每台设备的有序步骤、资料结果、快照版本、错误和最近用户请求；步骤区分待执行、执行中、成功、不适用、失败、失效。灯状态、颜色、范围和模式继续使用同一单例的共享缓存；延时和时钟读回值进入运行快照。
- `DeviceWorkScheduler` 按地址串行，跨地址并行。默认并发五台，在“设置 → 设备准备 → 同时更新设备数”选择一至五台并持久化。演示设备不占真实设备名额。启动按置顶、最近操作、加入时间、地址安排准备；主页显示排序仍独立。每个任务保留提交时的执行上下文，前一设备的取消不会污染随后设备的准备。
- `AgpLightControlService.Runtime` 启动已保存设备的共享准备，发布离线、在线、就绪。页面只取消自己的等待；后台、关闭、新窗口和恢复统一管理会话及任务。
- `AgpGattTransport` 将响应通道、灯状态和颜色订阅分别建立；正常启动使用本地能力，缺失或 OTA 失效时才补读。每个新连接仍按本地 SecureControl 要求认证，不自动获取新 Token。
- `BluetoothConnectionService` 按目标设备调度原生 GATT，连接代次过滤旧读写和通知。后台首先使所有目标不可操作，包括排队及连接中的设备，再执行物理清理；清理失败不会恢复在线。
- `RuntimeExclusive` 将 OTA、删除和重新绑定放入目标设备的独占队列，其他设备仍可工作。独占递增工作代次，禁止旧排队命令在结束后继续发送。删除失败保留本地记录和离线原因；OTA 尝试后失效能力及会话，再重新准备。

## 实际更新顺序

加载保存地址、Token 和能力是本地操作。以下顺序对应 `DeviceRuntimeStep`；不支持的能力或确认不存在的可选设备信息直接记为不适用。

| 顺序 | 步骤 | 对外状态 |
| --- | --- | --- |
| 1 | 使用保存地址直接连接 | 离线，显示准备阶段 |
| 2 | 建立 AGP 响应通道；本地能力缺失时补读 | 离线 |
| 3 | 按 SecureControl 认证，无需认证时跳过 | 在线，可发送 Toggle |
| 4 | 建立 Light State 订阅 | 在线 |
| 5 | 读取当前 Light State；已由当前操作确认时复用 | 在线 |
| 6 | EFFECT_GET | 在线 |
| 7 | MODE_GET | 在线 |
| 8 | DELAY_ON_GET | 在线 |
| 9 | DELAY_OFF_GET | 在线 |
| 10 | 建立 Color State 订阅 | 在线 |
| 11 | 读取颜色状态 | 在线 |
| 12 | CTL 范围 | 在线 |
| 13 | TIME_GET 与 TIME_SET 校时 | 在线 |
| 14 | 日程清单 | 在线 |
| 15 | 逐槽读取日程，每个槽位释放调度权 | 在线 |
| 16 | 固件版本与硬件版本，核对可选 DIS 特征 | 全部适用步骤完成后就绪 |

完整资料失败会保留在线和已完成资料，不以默认值替代成功读取。再次准备只补失败或失效项目；手动刷新重建本轮资料步骤，确实重新读取灯状态。最后一个缺失步骤被成功写入补齐时，立即恢复就绪并清除快照中的旧错误。资料在后台保留为历史值，相关步骤同时失效，不能据此宣告就绪。

延时写入、一次性自动关灯组合和用户日程校时的成功响应也同步到同一快照；灯效周期写入更新共享缓存，后续发布不会退回初始化旧值。资料表示最近一次明确取得的值及时间，剩余秒数不会靠 APP 轮询持续更新。

## 调度与操作结果

实际优先级为：目标独占操作 0 → 用户命令及其前置条件 1 → 设备关键准备 2 → 页面正在等待的资料 3 → 普通资料 4。数字越小越优先。已开始的同设备请求先结束，在步骤或日程槽位边界插入用户命令；连接和认证不被 Toggle 拆除。

Toggle 不读取旧开关值推断方向。命令接受后优先补结果订阅，等待完成通知；确认通知要求无剩余渐变、revision 高于接受响应，并高于已知的操作前版本。一秒内缺少可确认通知则读取一次，允许读回版本等于接受响应，但仍须满足操作前版本及渐变完成要求。接受响应与完成状态版本相同的即时操作因此可能等待这一秒，避免把先前操作的通知误认为本次完成。已发送但不能确认的操作记为结果未知，显示提示并使灯资料待补齐，绝不自动重放。明确的 InvalidValue、Busy、NotAuthorized 等协议拒绝记为失败；第一版普通 Toggle 不自动重试 Busy。原有组合指令保持协议库的有限 Busy 重试规则。

同设备同时只有一个翻转意图；每个用户请求带唯一 ID、创建时间、整体期限及执行阶段，迟到的前一个请求不会改变后一个请求的结果。页面离开不撤回已提交的控制；应用停止取消尚未发送的意图，已发送但未确认的记录为未知。一次性延时仍只在翻转后确认为开启时发送 DELAY_OFF_SET。

灯状态与同类颜色状态按各自 revision 拒绝较旧值。灯资料失效后仍保留同一连接的历史版本下限；翻转结果未知后的补读必须满足该操作的结果版本下限，拒绝旧读取不能使设备恢复就绪。新连接重新建立版本基线。应用周期、连接代次、工作代次及取消令牌共同防止旧任务回写和旧排队命令继续执行。

## 期限与恢复

| 工作 | 第一版规则 |
| --- | --- |
| 连接 | 每次最多 20 秒 |
| 响应通道、认证 | 各最多 10 秒 |
| 关键准备整轮 | 最多 40 秒；暂时连接/传输错误最多两次，轮间退避 500 毫秒并释放名额 |
| 普通资料、单个日程槽 | 每项最多 10 秒 |
| 用户命令 | 从提交起整体 30 秒，包括排队和前置条件；页面取消不取消已提交命令 |
| OTA 后清理 | 独立 5 秒期限，完成后才恢复目标准备 |

Token 缺失或协议认证拒绝不会自动重试或获取 Token。在线会话异常断开时先使步骤失效，再安排有上限的新准备；只重建状态，不重放旧命令。资料失败可通过页面等待、手动重试和刷新补齐，不建立高频轮询。

## 页面行为

主页显示离线、在线、就绪及准备/失败说明，忙碌状态来自全局集合。控制页及设备信息页加入共享准备，缺失资料的等待获得较高调度优先级。控制页按已经成功的资料步骤应用缓存，即使其他资料失败，也可使用已取得的模式等功能；迟到结果保留用户的模式、秒数、亮度与颜色草稿。

正常运行从 Window.Created 开始准备，恢复后建立新周期并重新认证。切换页面不清空运行集合。添加页仍是唯一扫描入口；启动、控制、离线重试及恢复都使用保存地址直连。

## 验证记录

2026-10-08 在 Windows / .NET 10 环境验证，主测试 445 项与扫描专项 5 项全部通过，无失败或跳过；Android Debug 构建通过，零警告、零错误。

| 验证 | 命令 | 结果 |
| --- | --- | --- |
| 应用服务、页面及生命周期回归 | `dotnet test tests\ArriLight.Tests\ArriLight.Tests.csproj --no-restore --verbosity minimal` | 445 / 445 通过 |
| 添加页扫描及保存地址直连 | `dotnet test tests\ArriLight.Discovery.Tests\ArriLight.Discovery.Tests.csproj --no-restore --verbosity minimal` | 5 / 5 通过 |
| Android Debug | `dotnet build ArriLight\ArriLight.csproj -f net10.0-android -c Debug --no-restore --verbosity minimal` | 0 警告、0 错误 |
| 文件及资源检查 | `git diff --check`，XAML / RESX 解析及资源键去重，文档链接检查 | 通过 |

本次运行集合改造新增 77 项回归，覆盖 [运行集合与调度](../tests/ArriLight.Tests/DeviceRuntimeTests.cs)、[原生会话与订阅](../tests/ArriLight.Tests/NativeSessionRuntimeTests.cs)、[OTA / 删除 / 绑定独占](../tests/ArriLight.Tests/RuntimeExclusiveTests.cs)、[部分资料可用与草稿保护](../tests/ArriLight.Tests/RuntimePartialProfileTests.cs)、[执行上下文与页面等待边界](../tests/ArriLight.Tests/RuntimeFinalRegressionTests.cs) 及 [未知结果后的版本有效性](../tests/ArriLight.Tests/RuntimeLightValidityTests.cs)。原有后台、重开和页面离开测试同步到新的应用生命周期语义。

测试使用真实应用服务、ViewModel、协议客户端及编解码器，替换无线和 MAUI 平台边界。重点覆盖并发一/三/五台、在线早于资料、严格读取顺序、能力缓存、用户插入、页面等待取消、失败保留会话、未知结果不重放、后台与重开、独占清理和旧回调过滤；补充验证接受前旧通知、渐变未结束、版本回绕、旧读取拒绝及新连接重建。测试不等同于手机无线验证。

## 主要代码位置

- [运行模型](../ArriLight/Services/DeviceRuntimeState.cs)、[按设备调度器](../ArriLight/Services/DeviceWorkScheduler.cs)。
- [应用级准备和资料步骤](../ArriLight/Services/AgpLightControlService.Runtime.cs)、[目标独占与清理](../ArriLight/Services/AgpLightControlService.RuntimeExclusive.cs)。
- [最小响应通道与渐进订阅](../ArriLight/Services/AgpGattTransport.cs)、[原生会话及代次](../ArriLight/Services/BluetoothConnectionService.cs)。
- [窗口生命周期](../ArriLight/App.xaml.cs)、[主页三态](../ArriLight/ViewModels/MainPageViewModel.cs)、[控制页资料复用](../ArriLight/ViewModels/DeviceControlPageViewModel.Connection.cs)、[并发设置](../ArriLight/ViewModels/SettingsPageViewModel.cs)。

## 真机验证范围

本环境完成 Windows 上的 Android 编译及模拟 BLE 回归；未进行 Android/iOS 手机与多台 PW01 的无线实测，也未编译 iOS。默认五台是调度配置，需测量首台在线、五台在线、全部就绪的耗时和失败率，并验证不同手机的原生建连并发。

初始化并发与保持连接的数量分别计算。第一版前台保留已准备会话，尚未增加连接总数预算和空闲淘汰；较多设备时需要依据手机 BLE 上限验证。还需验证 OTA 后 GATT 表变化、同一原生设备实例跨连接的迟到断连事件，以及平台 SDK 对取消令牌和期限的响应；SDK 若不结束原生调用，物理清理时间仍取决于平台。
