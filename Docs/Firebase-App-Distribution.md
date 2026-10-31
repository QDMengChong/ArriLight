# Android Firebase 自动分发

工作流位于 `.github/workflows/android-firebase.yml`，从 GitHub Actions 手动启动，独立于现有 iOS/TestFlight 工作流。

## 配置 GitHub Actions

在仓库 **Settings → Secrets and variables → Actions** 中配置：

| 类型 | 名称 | 内容 |
| --- | --- | --- |
| Secret | `ANDROID_KEYSTORE_BASE64` | 发布 keystore 文件的 Base64 |
| Secret | `ANDROID_KEYSTORE_PASSWORD` | keystore 密码 |
| Secret | `ANDROID_KEY_PASSWORD` | Key 密码 |
| Variable | `ANDROID_KEY_ALIAS` | keystore 内实际密钥别名 |
| Variable，可选 | `ANDROID_APPLICATION_ID` | 默认为项目的 ApplicationId，目前为 `com.arriopto.arrilight` |
| Variable | `FIREBASE_APP_ID` | Firebase Android App ID，格式 `1:项目编号:android:十六进制标识` |
| Variable | `FIREBASE_GROUPS` | 已创建的测试组别名，多个组以逗号分隔 |
| Secret | `FIREBASE_SERVICE_ACCOUNT_JSON` | 完整服务账号私钥 JSON |

`ANDROID_KEY_ALIAS`、`FIREBASE_APP_ID` 和 `FIREBASE_GROUPS` 也接受同名 Secret，Variable 优先。Firebase 服务账号需要目标项目的 **Firebase App Distribution Admin** 角色；使用服务账号 JSON，不是客户端的 `google-services.json`。APK 模式不要求任何 Firebase 配置，两个模式都要求 Android 发布签名材料。

可在本机 PowerShell 中把现有 keystore 转为 Base64 并复制到剪贴板，再粘贴到 GitHub Secret。将路径替换为实际使用的发布签名文件：

```powershell
[Convert]::ToBase64String(
    [IO.File]::ReadAllBytes('C:\Users\RLGE\AppData\Local\Xamarin\Mono for Android\Keystore\ArriLightKey\ArriLightKey.keystore')
) | Set-Clipboard
```

密钥别名以 `keytool -list -keystore ...` 的实际输出为准，不能只依据文件名推断。签名文件及密码不放进仓库。工作流只把临时签名文件写入 Runner 临时目录，结束时清理；Firebase 认证 Action 自行清理临时 Google 凭据文件。

## 手动发布

提交并推送工作流及需要发布的应用改动；工作流需先存在于仓库默认分支，才能从 Actions 页面使用 **Run workflow**。

1. 打开 **Actions → Android build and Firebase → Run workflow**。
2. 选择包含待发布代码的分支。
3. 选择 `mode`：`apk` 只构建签名 APK，`firebase` 构建后分发给指定测试组。
4. `version` 可填写 `0.1.12` 一类的三段版本号，留空读取项目配置。
5. `build_number` 可填整数 versionCode，留空使用 `1000 + GitHub run number`。
6. `release_notes` 填本次更新说明，留空使用版本及提交号。
7. 点击运行，等待测试、签名、APK 校验及可选的 Firebase 上传完成。

首轮建议选择 `apk`，检查下载、覆盖安装、BLE 和权限行为后，再选择 `firebase`。Firebase 模式会实际分发并通知配置的测试组。

默认第一轮构建号为 1001，同一次运行的 Re-run 保持原构建号，新的一次 Run workflow 递增。若 Firebase 中已有更高版本，或曾手动指定更高构建号，后续仍需保证不低于已分发版本；必要时填写更高的 `build_number` 或调整默认偏移。Android versionCode 是整数，不使用 iOS 的带点构建号格式。

版本、包名只通过构建参数和 Runner 临时 AndroidManifest 同步，不改写仓库内的项目文件或 AndroidManifest，因此原清单中的固定 versionName 不会覆盖本轮输入。

## 工作流执行与产物

- 两个 .NET 测试项目先在 Linux 运行；许可证覆盖测试在移动项目还原后单独执行，未静默跳过。
- 固定 .NET SDK/workload set `10.0.401`、JDK 21、Firebase CLI `15.34.2`；Node.js 22 仅在 Firebase 模式安装。
- 所有移动还原和发布仅针对 `net10.0-android`，不要求 Apple 工作负载。
- 使用完整 Release publish 生成 APK，并核验 APK 签名证书与 keystore 一致、包名/versionName/versionCode 正确，拒绝 debuggable 包。
- 上传 Firebase 前先保存签名 APK 和 `apk-metadata.json` 到 Actions Artifacts，保留 14 天。即使云端认证或分发失败，已经保存的安装包仍可下载。
- `apk-metadata.json` 只含包名、版本、APK SHA-256 和公开证书 SHA-256；不会上传 keystore、密码或服务账号 JSON。
- 工作流只提供手动入口，不在 PR 或每次 push 时分发；同一时间只执行一个 Android 发布任务。

首次云端运行验证的是 Linux Android 工具链、签名凭据及 Firebase IAM/测试组的实际配置。本机脚本检查不代表已完成云端构建、Firebase 上传或真机验证。

## 失败定位

| 阶段 | 优先检查 |
| --- | --- |
| Configure | Secret/Variable 名称、Base64、Firebase App ID、测试组别名、整数构建号 |
| Keystore | 是否为发布 keystore、store password、Key Alias；Key password 在发布时校验 |
| License test | 还原的 NuGet 依赖与 `OpenSourceLicenses/catalog.json` 是否同步；不要直接关闭校验 |
| APK verify | 签名证书、生成包名、版本号、是否意外生成 Debug 包 |
| Firebase 403 | 服务账号是否拥有目标项目的 App Distribution Admin 权限 |
| Firebase app/group | App ID 是否误填为包名、测试组是否已创建、组别名是否正确 |

本地辅助脚本测试命令：

```powershell
python -m unittest discover -s .github/scripts/tests -v
```

参考：[Firebase CLI 分发](https://firebase.google.com/docs/app-distribution/android/distribute-cli)、[服务账号认证](https://firebase.google.com/docs/app-distribution/authenticate-service-account?platform=android)、[Google GitHub 认证 Action](https://github.com/google-github-actions/auth)、[MAUI APK 发布](https://learn.microsoft.com/en-us/dotnet/maui/android/deployment/publish-cli?view=net-maui-10.0)、[Android 依赖安装](https://learn.microsoft.com/en-us/dotnet/android/getting-started/installation/dependencies)。
