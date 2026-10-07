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
