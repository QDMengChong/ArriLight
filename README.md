# ArriLight

dotnet publish -c Release -p:AndroidKeyStore=true -p:AndroidSigningKeyStore="C:\Users\RLGE\AppData\Local\Xamarin\Mono for Android\Keystore\" -p:AndroidSigningKeyAlias=ArriOpto -p:AndroidSigningStorePass=12345678 -p:AndroidSigningKeyPass=12345678
## OTA 在线清单

APP 的“固件升级 → 在线检查”使用 JSON 清单判断是否存在新版本。首次测试时会提示
输入清单 URL，并将地址保存在本机。清单格式示例见
`ArriLight/Resources/Raw/ota_manifest.sample.json`：

```json
{
  "productId": "PHY6222_LIGHT",
  "version": "1.1.0",
  "fileName": "arrilight-phy6222-1.1.0.bin",
  "downloadUrl": "https://firmware.example.com/arrilight/arrilight-phy6222-1.1.0.bin",
  "sha256": "64 个十六进制字符",
  "releaseNotes": "本版本说明"
}
```

`downloadUrl` 可以是绝对地址，也可以是相对于清单 URL 的地址。正式服务应使用
HTTPS 并提供真实 SHA-256。检测到更高版本后，“一键下载并升级”会在校验下载内容
后直接进入 SLB OTA，不会在下载完成后再次确认。

调试信息输出默认关闭。在“我的 → 应用版本”页面连续点击版本号 5 次，可以打开或
关闭文件输出。开启后日志追加写入应用数据目录的 `arrilight_debug.txt`。该开关只
控制文件：Debug 构建始终同步显示在 Visual Studio 的“输出 → 调试”窗口，Release
构建不产生 Visual Studio 调试输出。日志不会记录 AES Key 或固件正文。
