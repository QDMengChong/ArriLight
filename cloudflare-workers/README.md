# ArriLight Cloudflare OTA Worker

本目录包含 ArriLight 测试版在线 OTA 服务使用的两个 Python Worker：列表 Worker
和下载 Worker。两个 Worker 绑定同一个 R2 存储桶。桶名称分别写在各自的
`wrangler.toml` 中，切换到正式桶时只需修改配置，不需要修改 Python 代码。

R2 对象目录格式如下：

```text
PW01_A1/<固件文件>.hex16
PW01_A2/<固件文件>.hex16
```

文件名必须是带小数点的数字版本号，例如 `1.0.2.hex16`。列表 Worker 会将文件版本
与请求中的本地版本比较，并返回严格高于本地版本的最新文件。

## 接口说明

列表接口：

```text
GET https://<列表 Worker 地址>?hardwareVersion=PW01-A1&currentVersion=1.0.1
```

检测到更新时返回：

```json
{
  "hardwareVersion": "PW01-A1",
  "version": "1.0.2",
  "fileName": "PW01_A1/1.0.2.hex16",
  "sha256": null
}
```

`fileName` 是 R2 对象键。返回完整对象键而不是只有文件名，可以避免不同硬件目录
中的同名文件产生歧义。

目录不存在、目录中没有 `.hex16` 文件，或没有高于本地版本的文件时返回：

```json
{
  "hardwareVersion": "PW01-A1",
  "version": null,
  "fileName": null,
  "sha256": null
}
```

下载接口：

```text
GET https://<下载 Worker 地址>?fileName=PW01_A1%2F1.0.2.hex16
```

响应正文就是 R2 对象内容。Worker 还会返回以下响应头：

- `X-Firmware-Version`：固件版本。
- `X-Hardware-Version`：硬件版本。
- `X-Firmware-SHA256`：对象设置了 `sha256` 自定义元数据时返回。

## 部署

先安装 `uv` 和 Node.js，然后登录 Cloudflare：

```powershell
npx wrangler login
```

在每个 Worker 目录中执行以下命令。`--group dev` 用于明确同步开发依赖，避免
项目虚拟环境中缺少 `click` 等 `pywrangler` 依赖：

```powershell
cd cloudflare-workers\arriLight-list
uv sync --group dev
uv run --group dev pywrangler dev
uv run --group dev pywrangler deploy

cd ..\arriLight-get
uv sync --group dev
uv run --group dev pywrangler dev
uv run --group dev pywrangler deploy
```

### Windows 注意事项

部分 `uv`/Pyodide 组合在原生 Windows 下会在 Worker 源码加载前失败。常见错误是
`uv venv` 创建 `.venv-workers/pyodide-venv` 时出现：

```text
ModuleNotFoundError: No module named 'python'
```

这是 Windows 本地 Pyodide 初始化的路径问题，不是 `src/entry.py` 或 R2 绑定错误。
相关问题见 [`workers-py` 问题记录](https://github.com/cloudflare/workers-py/issues/270)。

在 Windows 下请使用 WSL2 或 Linux CI 环境进行本地运行和部署。在 Ubuntu WSL 终端中执行：

```bash
cd /mnt/d/git/ArriLight/cloudflare-workers/arriLight-list
rm -rf .venv .venv-workers
uv sync --group dev
uv run --group dev pywrangler dev
uv run --group dev pywrangler deploy

cd ../arriLight-get
rm -rf .venv .venv-workers
uv sync --group dev
uv run --group dev pywrangler dev
uv run --group dev pywrangler deploy
```

如果之前的 Windows 命令留下了损坏的 `.venv-workers`，可以在 PowerShell 中删除
该目录；从 WSL 运行时不要复用这个 Windows 虚拟环境。

本地开发默认使用本地 R2 模拟。如果需要连接真实 R2 测试桶，可临时在 R2 绑定中
设置 `remote = true`。正式部署始终使用绑定中配置的桶。

部署完成后，两个 `workers.dev` 地址由
`ArriLight/Resources/Raw/appsettings.json` 统一配置，APP 不会在固件升级页面中询问地址。
上传固件与部署 Worker 是两个独立步骤；可以使用 R2 控制台或 S3 兼容客户端，将文件
放入上面规定的硬件目录。

上传一个小型测试固件时，可以直接使用 Wrangler：

```powershell
npx wrangler r2 object put arrilight-firmware-dev/PW01_A1/1.0.2.hex16 `
  --file .\1.0.2.hex16 --content-type application/octet-stream
```

对象键和文件名必须包含列表 Worker 用于比较的同一个小数版本号。切换正式环境时，
请先修改两个 `wrangler.toml` 中的 `bucket_name`，再分别部署两个 Worker。
