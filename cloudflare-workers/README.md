# ArriLight Cloudflare OTA Workers

This directory contains the two Python Workers used by the ArriLight test OTA
service. Both Workers bind the same R2 bucket. The bucket name is deliberately
kept in each `wrangler.toml`, so the production bucket can replace the test
bucket without changing Python code.

The R2 object layout is:

```text
PW01_A1/<firmware-file>.hex16
PW01_A2/<firmware-file>.hex16
```

The filename must contain a dotted numeric firmware version, for example
`PW01_A1_1.0.2.hex16` or `firmware-v1.0.2.hex16`. The list Worker compares that
version with the requested local version and returns the newest strictly newer
file.

## API contract

List:

```text
GET https://<list-worker>/api/firmware?hardwareVersion=PW01-A1&currentVersion=1.0.1
```

Response when an update exists:

```json
{
  "hardwareVersion": "PW01-A1",
  "version": "1.0.2",
  "fileName": "PW01_A1/PW01_A1_1.0.2.hex16",
  "sha256": null
}
```

`fileName` is the R2 object key. Returning the key rather than only the basename
keeps two hardware directories from having ambiguous filenames.

Response when there is no directory, no `.hex16` file, or no newer version:

```json
{
  "hardwareVersion": "PW01-A1",
  "version": null,
  "fileName": null,
  "sha256": null
}
```

Get:

```text
GET https://<get-worker>/api/firmware?fileName=PW01_A1%2FPW01_A1_1.0.2.hex16
```

The response body is the R2 object body. The Worker also emits
`X-Firmware-Version`, `X-Hardware-Version`, and (when the object has a
`sha256` custom metadata value) `X-Firmware-SHA256` headers.

## Deploy

Install `uv` and Node.js, then log in to Cloudflare once:

```powershell
npx wrangler login
```

From each Worker directory, run the Python Workers CLI documented by Cloudflare:

```powershell
cd cloudflare-workers\arriLight-list
uv run pywrangler dev
uv run pywrangler deploy

cd ..\arriLight-get
uv run pywrangler dev
uv run pywrangler deploy
```

### Windows note

Native Windows currently fails before the Worker source is loaded for some
`uv`/Pyodide combinations. The typical error is
`ModuleNotFoundError: No module named 'python'` while `uv venv` creates
`.venv-workers/pyodide-venv`. This is a Windows path issue in the local
Pyodide bootstrap, not an error in `src/entry.py`; changing the Worker code or
the R2 binding does not fix it. The same failure is tracked in the
[`workers-py` issue tracker](https://github.com/cloudflare/workers-py/issues/270).

Use WSL2 or a Linux CI runner for local development and deployment until the
Windows bootstrap is fixed. In an Ubuntu WSL terminal:

```bash
cd /mnt/d/git/ArriLight/cloudflare-workers/arriLight-list
uv run pywrangler dev
uv run pywrangler deploy

cd ../arriLight-get
uv run pywrangler dev
uv run pywrangler deploy
```

If the failed Windows attempt left a `.venv-workers` directory, remove that
directory from PowerShell before retrying on Windows or leave it unused when
running from WSL.

For local testing against the real R2 bucket, set `remote = true` on the R2
binding temporarily. Without that setting, local development uses a local R2
simulation. Production deployment always uses the bucket named in the binding.

After deployment, put the two `workers.dev` URLs into the ArriLight firmware
page when prompted. Uploading a file is separate from deploying the Workers; use
the R2 dashboard or an S3-compatible client to place objects under the hardware
directory shown above.

For a small test firmware, Wrangler can upload one object directly:

```powershell
npx wrangler r2 object put arrilight-firmware-dev/PW01_A1/PW01_A1_1.0.2.hex16 `
  --file .\PW01_A1_1.0.2.hex16 --content-type application/octet-stream
```

The object key and filename must contain the same dotted version that the list
Worker should compare. For production, change `bucket_name` in both
`wrangler.toml` files before deploying.
