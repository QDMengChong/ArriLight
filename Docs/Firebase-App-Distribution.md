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

临时清单有两个路径：`ANDROID_MANIFEST_PATH` 是绝对文件路径，`ANDROID_MANIFEST_RELATIVE_PATH` 是相对 `.csproj` 所在目录的路径。发布时 `-p:AndroidManifest` 必须使用后者，因为当前 Android SDK 会直接拼接 `$(ProjectDir)$(AndroidManifest)`；传入绝对路径会导致 XA1018 和重复的目录前缀。

## 工作流执行与产物

- 两个 .NET 测试项目先在 Linux 运行；许可证覆盖测试在移动项目还原后单独执行，未静默跳过。
- 固定 .NET SDK/workload set `10.0.401`、JDK 21、Firebase CLI `15.34.2`；Node.js 22 用于诊断脚本测试和 Firebase 分发。
- 所有移动还原和发布仅针对 `net10.0-android`，不要求 Apple 工作负载。
- 使用完整 Release publish 生成 APK，并核验 APK 签名证书与 keystore 一致、包名/versionName/versionCode 正确，拒绝 debuggable 包。
- 上传 Firebase 前先保存签名 APK 和 `apk-metadata.json` 到 Actions Artifacts，保留 14 天。即使云端认证或分发失败，已经保存的安装包仍可下载。
- `apk-metadata.json` 只含包名、版本、APK SHA-256 和公开证书 SHA-256；不会上传 keystore、密码或服务账号 JSON。
- 工作流只提供手动入口，不在 PR 或每次 push 时分发；同一时间只执行一个 Android 发布任务。

Firebase 上传前的 `Diagnose Firebase identity and target access` 步骤会记录 ADC 中实际使用的服务账号邮箱、凭据项目 ID、App ID 中的目标项目编号，以及从 Firebase 查到的目标项目 ID、Android 包名、发布读写权限和 App Distribution 读取结果。邮箱和项目标识为公开标识；不输出密钥、访问令牌或发布下载链接。附加 API 的诊断失败不会阻止 CLI 上传，也不能单独证明缺少上传权限。

上传失败时，工作流从 CLI 调试文件中仅提取 App Distribution 错误的 HTTP 状态、Google 状态码及 `ErrorInfo` 中的 reason/service/consumer/permission/resource。原始日志和命令输出仅临时保存于 RUNNER_TEMP，并在该步骤退出时删除，不上传到 Artifacts。诊断也会写入 Actions Summary；成功读取发布列表只验证读权限，最终是否能上传以实际 CLI 结果为准。

首次云端运行验证的是 Linux Android 工具链、签名凭据及 Firebase IAM/测试组的实际配置。本机脚本检查不代表已完成云端构建、Firebase 上传或真机验证。

## 失败定位

| 阶段 | 优先检查 |
| --- | --- |
| Configure | Secret/Variable 名称、Base64、Firebase App ID、测试组别名、整数构建号 |
| Keystore | 是否为发布 keystore、store password、Key Alias；Key password 在发布时校验 |
| License test | 还原的 NuGet 依赖与 `OpenSourceLicenses/catalog.json` 是否同步；不要直接关闭校验 |
| APK verify：证书解析失败 | Build Tools 37 的指纹标签改为 `V3.0 Signer:` 等格式；旧脚本只识别 `Signer #1`，会误报证书不匹配。推送新版脚本后从对应分支新建 Run workflow |
| APK verify：证书不匹配 | 新版日志会分别输出 keystore 预期指纹与 APK 实际指纹（均为公开 SHA-256）。检查发布 keystore、alias 和 publish 签名参数；不要跳过校验或仅为通过校验重新生成发布密钥 |
| APK verify：包元数据 | 生成包名、版本号、是否意外生成 Debug 包 |
| XA1018 / 清单路径重复 | 发布参数是否使用 `ANDROID_MANIFEST_RELATIVE_PATH`；推送修复后从包含修复的分支新建一次 Run workflow |
| Firebase 403 | 服务账号是否拥有目标项目的 App Distribution Admin 权限 |
| 启用 API 后仍然 403 | 查看 `Diagnose Firebase identity and target access` 和上传步骤中的过滤诊断。`SERVICE_DISABLED` 按 metadata 中的 service/consumer 检查实际未启用 API 的项目；`release-permissions.missing` 检查是否包含 `firebaseappdistro.releases.update`；`android-package.matches=false` 检查 App ID。若诊断 API 本身失败，权限状态应视为未知，不为诊断额外授予管理员权限 |
| App Distribution 读取 404 | 在 Firebase 控制台确认应用属于目标项目，并打开 App Distribution 页面；若出现 Get started/开始使用，完成控制台初始化后再试。不要仅凭 403 认定尚未初始化 |
| Firebase app/group | App ID 是否误填为包名、测试组是否已创建、组别名是否正确 |

本地辅助脚本测试命令：

```powershell
python -m unittest discover -s .github/scripts/tests -v
node --test .github/scripts/tests/firebase-diagnostics.test.cjs
```

参考：[Firebase CLI 分发](https://firebase.google.com/docs/app-distribution/android/distribute-cli)、[服务账号认证](https://firebase.google.com/docs/app-distribution/authenticate-service-account?platform=android)、[Google GitHub 认证 Action](https://github.com/google-github-actions/auth)、[MAUI APK 发布](https://learn.microsoft.com/en-us/dotnet/maui/android/deployment/publish-cli?view=net-maui-10.0)、[Android 依赖安装](https://learn.microsoft.com/en-us/dotnet/android/getting-started/installation/dependencies)。
