# ArriLight

dotnet publish -c Release -p:AndroidKeyStore=true -p:AndroidSigningKeyStore="C:\Users\RLGE\AppData\Local\Xamarin\Mono for Android\Keystore\" -p:AndroidSigningKeyAlias=ArriOpto -p:AndroidSigningStorePass=12345678 -p:AndroidSigningKeyPass=12345678
## OTA 在线升级

在线升级通过 Cloudflare Python Workers 访问 R2。部署文件、API 契约、R2 目录和
`uv run pywrangler deploy` 步骤见
[`cloudflare-workers/README.md`](cloudflare-workers/README.md)。列表 Worker 接收
硬件版本（例如 `PW01-A1`）和本地固件版本，下载 Worker 返回匹配的 `.hex16` 文件。

首次点击“在线检查”时，APP 会分别询问列表 Worker 和下载 Worker 的 HTTPS 地址，
并保存在本机。检测到更新后，APP 下载文件到缓存目录，沿用现有 Single Bank OTA
流程校验并升级设备。若 R2 对象设置了 `sha256` 自定义元数据，APP 会在下载后执行
SHA-256 校验；没有该元数据时会记录未校验提示。

调试信息输出默认关闭。在“我的 → 应用版本”页面连续点击版本号 5 次，可以打开或
关闭文件输出。开启后日志追加写入应用数据目录的 `arrilight_debug.txt`。该开关只
控制文件：Debug 构建始终同步显示在 Visual Studio 的“输出 → 调试”窗口，Release
构建不产生 Visual Studio 调试输出。日志不会记录 AES Key 或固件正文。
