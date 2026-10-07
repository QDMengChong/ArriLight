# ArriLight MVVM

应用使用 `CommunityToolkit.Mvvm 8.4.2`。所有含业务逻辑的页面都有对应的 ViewModel，
位于 `ArriLight/ViewModels`；设备控制按连接、灯光、预设、特效和延时分为 partial 文件，通过 `MauiProgram` 注册为 transient；同一页面在其存活期间
保留自己的 ViewModel，不同控制页不共享草稿、忙碌状态或取消令牌。BLE 和仓储服务仍为 singleton。

| 层 | 职责 |
| --- | --- |
| ViewModel | 可观察状态、设备及预设集合、亮度与颜色值、能力判断、校验、命令、忙碌状态、错误提示、业务任务取消 |
| Service | 扫描、连接、认证、协议、设备仓储、固件校验与升级、语言及用户设置持久化 |
| View | 生命周期转发、XAML 绑定、动画、首帧观察、LazyView、原生对话框、相机与图片解码、触摸和拖拽、焦点与主题绘制 |

`ViewModelBase` 继承 `ObservableObject`，提供错误状态和生命周期入口。
普通属性使用 `[ObservableProperty]`，关联展示值使用 `[NotifyPropertyChangedFor]`；
业务操作使用 `[RelayCommand]` 生成的同步或异步命令。在线固件检查直接使用 `AsyncRelayCommand`。
涉及格式转换或额外副作用的属性可继续使用 `SetProperty` 和显式通知。

## 页面覆盖

首页、设备控制（亮度/HSL/CTL 和本机模式共用 ViewModel）、扫描绑定、二维码导入、设备信息、
设备分享、固件升级、日程、语言与主题设置、版本及调试开关、调试配置、消息中心、
我的导航、关于我们、法律文档及两种开源许可页面均已迁移。
商城、使用说明及 LazyView 本身保留为 View；它们没有独立设备业务状态。

View 构造时解析并绑定 ViewModel，`OnAppearing` / `OnDisappearing` 和查询参数转交对应入口。
原生控件事件先提取普通值，再调用命令或 ViewModel 方法，不把控件、事件参数传入 ViewModel。
预设拖拽只在 View 中解析拖拽对象；交换槽位和保存由 ViewModel 完成。

## 平台边界

ViewModel 不引用 MAUI 控件、Shell、MainThread、MAUI Color 或 Android/iOS API。
`IAppNavigator`、`IUserInteraction`、`IUiDispatcher`、`IApplicationPresentation` 和
`IFirmwareFilePicker` 是导航、提示、UI 调度、应用主题与版本、固件文件选择的边界。
实现位于 `MauiPresentationServices`。固件文件使用 `IFirmwareFile`，保留可重新打开的流，
用于先校验、后升级，原有 `FileResult` 服务入口仍兼容。

`PresentationColor` 只保存颜色值，XAML 通过 `PresentationColorConverter` 转为 MAUI 颜色。
因此共享业务逻辑仍可用于 Android/iOS，平台绘制和交互代码保留在各 View 或 adapter 中。

## 关键生命周期和并发行为

- 首页在显示期间订阅设备事件，离开时解除订阅。开关命令允许不同设备并发进入，
  同一设备通过 `IsPowerChanging` 防止重复点击；认证时读到的旧灯状态不覆盖待完成的用户选择。
  设备卡片命令通过 `x:Reference HomePage` 绑定主页的 `ViewModel`（在 `InitializeComponent` 前赋值），
  设备行属性和命令参数仍绑定当前行。列表刷新移除卡片时，命令源不依赖会暂时消失的祖先关系，
  避免编译绑定访问空祖先而触发调试器的 `NullReferenceException` 中断。
- 进入设备控制页时仍立即导航，首帧后分别启动 BLE 准备、LazyView 和动画。
  View 把本次显示的取消令牌传给 ViewModel，连接与刷新判断使用同一份状态。
- 控制页延时卡片在自定义配置后合并一次性自动关灯与触摸模式，两行共用 `TurnOnDelaySecondsText`。
  `DeviceControlPageViewModel.LocalMode` 管理模式读取、Switch 草稿、设置命令和离开取消；
  模式读取不延迟控制 Profile 初始化和 LazyView 创建，迟到响应不覆盖共用秒数的新编辑。
  信息页只展示设备资料。两项操作各自保留原有能力要求、秒数范围和发送语义。
- 控制资料先更新 ViewModel 的亮度、色彩、能力和特效状态，再由 View 绘制已加载的原生控件。
  应用状态时 View 抑制控件回调，避免状态回写产生新的 BLE 指令。
- 扫描绑定的原生提示框由 View 呈现；阶段、倒计时及取消由 ViewModel 管理。
  离开绑定页仍取消等待，并按原有成功/失败规则保留或清理设备记录。
- 语言命令只保存选择并提示重启；本次运行语言不立即切换。
  新窗口仍由 `App` 在创建界面前应用保存的语言，并恢复 BLE 前台状态。

## 验证

宿主测试直接编译真实 ViewModel、服务和需要验证的 View 生命周期代码；只替换 UI 与无线电边界。
原有回归覆盖首次开关、退出重开、导航/首帧、扫描绑定取消、特效参数、模式设置与固件连接。
`ViewModelTests` 额外检查语言与主题命令、能力恢复、延时草稿、认证及状态通知、预设排序持久化、
二维码错误/重复输入和离线许可筛选/读取。更新包后同时重新生成开源许可清单。

```powershell
dotnet restore ArriLight/ArriLight.csproj
dotnet test tests/ArriLight.Tests/ArriLight.Tests.csproj
dotnet build ArriLight/ArriLight.csproj -f net10.0-android
```

Android/iOS 真机仍需检查语言、开关和控制页 UI、长按/拖拽、相机与原生提示框，
并检查真实 BLE 连接、断开、重新进入和固件升级。Windows 上的 Android 构建不等同于 iOS 编译或真机验证。
