# 应用语言

当前支持简体中文与英语。入口为“我的 → 设置 → 语言”，可选择“跟随系统”、“简体中文”或“English”。
选择保存在本机，关闭并重新启动应用后生效。保存选择不会改变当前页面、返回栈或蓝牙操作的语言与状态。
修改选择后会弹出重启生效提示，设置页同时保留提示文字。重新选回本次启动已应用的选择时，不再需要重启。
设置页的选择、命令和重启状态由 `SettingsPageViewModel` 管理；View 仅同步原生 RadioButton 事件。

## 初始化与持久化

`MauiProgram.CreateMauiApp` 在构建 MAUI 应用及创建界面前调用 `Localization.Initialize()`。
Android 退出 Activity 后可能保留应用进程，因此 `App.CreateWindow` 也会在创建新界面前应用待生效的语言选择，
让退出后再次打开应用即可生效。此时“跟随系统”仍以启动时捕获的系统语言为准。
`LanguagePreference` 使用 `Preferences` 的 `app_language` 键，保存 `system`、`zh-Hans` 或 `en`。
显式选择覆盖系统语言；默认跟随系统时，中文及其地区变体使用简体中文，其余系统语言统一使用英语。
不支持的旧设置也回退到“跟随系统”。

`ILocalizationService` 提供资源查询、带参数的格式化、启动初始化及语言选择变化通知。
`SelectedLanguage` 表示保存的选择，`Culture` 表示本次运行已应用的语言，`RequiresRestart` 表示选择是否尚未应用。
保存选择只触发 `LanguagePreferenceChanged`；初始化触发 `CultureChanged`。
应用启动仅设置 `CurrentUICulture` 和 `DefaultThreadCurrentUICulture`，不改变数字、协议、持久化使用的 `CurrentCulture`。
资源读取和用户文案格式化显式使用语言服务的 `Culture`。

## 增加与使用文案

中文资源为 `ArriLight/Resources/Localization/AppResources.resx`，英语为 `AppResources.en.resx`。
两份资源使用相同的语义键，格式化文案必须保留相同的参数索引，允许根据语序调整参数顺序。
`AppResources.cs` 提供供 XAML 和代码使用的静态属性，所有属性均通过统一语言服务查询资源。
增加键时，在两份 `.resx` 中增加对应条目，并在 `AppResources.cs` 增加同名属性。
现有静态访问类已经由项目管理，资源编辑器应保持“不生成代码”，避免生成第二个同名类。

XAML 使用 `xmlns:strings="clr-namespace:ArriLight.Resources.Localization"` 和
`Text="{x:Static strings:AppResources.Common_Save}"`。
普通 C# 文案使用 `AppResources.Common_Save`，带参数的文案使用
`Localization.Format(nameof(AppResources.Home_DeviceCount), count)`。

用户输入的设备名、配置名、二维码内容、地址和协议标识不翻译。
演示设备的默认名称仅在展示时翻译，不修改保存的名称；用户修改后的演示名称也保留。
固件版本未读取时持久化稳定的 `SavedDevice.UnknownFirmwareVersion`，界面单独显示本地化的“未知”。
旧版本保存的中文“未知”会在读取设备记录时迁移。

## 平台资源与验证

iOS 在 `Info.plist` 中声明 `zh-Hans`、`en`，以英语作为默认开发语言，并在对应 `.lproj/InfoPlist.strings` 中提供蓝牙、相机权限用途说明。
这些原生系统文案由系统语言机制选择，独立于应用内保存的语言选择。

`LocalizationTests` 检查启动恢复、显式覆盖、系统回退、保存后延迟应用、格式参数、资源完整性及数据迁移。
Android/iOS 手工验证应包含：设置语言后当前页面不变、重新启动后 Shell 标签和页面一致、
弹窗和日程显示正确、真实设备及自定义名称不被改写。
Android 根页面无法继续返回时，第一次返回提示“再按一次返回键退出本程序”，2 秒内再次返回退出。
正常子页面返回与弹层关闭沿用 MAUI 处理；导航或切到后台会清除退出确认状态。
