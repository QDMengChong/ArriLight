import json
import re
from urllib.parse import parse_qs, urlparse

from workers import Response, WorkerEntrypoint


KEY_PATTERN = re.compile(
    r"^[A-Za-z0-9][A-Za-z0-9._-]*/[^/]+\.hex16$",
    re.IGNORECASE,
)
VERSION_PATTERN = re.compile(r"(?<!\d)v?(\d+(?:\.\d+){1,3})(?!\d)", re.IGNORECASE)


def error_response(message: str, status: int) -> Response:
    return Response(
        json.dumps({"error": message}),
        status=status,
        headers={
            "Content-Type": "application/json; charset=utf-8",
            "Cache-Control": "no-store",
            "Access-Control-Allow-Origin": "*",
        },
    )


def first_query_value(query: dict[str, list[str]], name: str) -> str:
    return (query.get(name) or [""])[0].strip()


class Default(WorkerEntrypoint):
    async def fetch(self, request):
        if request.method == "OPTIONS":
            return Response(
                None,
                status=204,
                headers={
                    "Access-Control-Allow-Origin": "*",
                    "Access-Control-Allow-Methods": "GET, OPTIONS",
                    "Access-Control-Allow-Headers": "Content-Type",
                },
            )

        if request.method != "GET":
            return error_response("method_not_allowed", 405)

        key = first_query_value(
            parse_qs(urlparse(request.url).query),
            "fileName",
        )
        if not KEY_PATTERN.fullmatch(key):
            return error_response("invalid_file_name", 400)

        obj = await self.env.FIRMWARE_BUCKET.get(key)
        if obj is None:
            return error_response("firmware_not_found", 404)

        basename = key.rsplit("/", 1)[-1]
        headers = {
            "Content-Type": "application/octet-stream",
            "Content-Disposition": f'attachment; filename="{basename}"',
            "Cache-Control": "public, max-age=300",
            "Access-Control-Allow-Origin": "*",
            "X-Firmware-Object-Key": key,
        }
        version = VERSION_PATTERN.search(basename)
        if version is not None:
            headers["X-Firmware-Version"] = version.group(1)
        headers["X-Hardware-Version"] = key.split("/", 1)[0].replace("_", "-", 1)
        metadata = getattr(obj, "customMetadata", None)
        if metadata is None:
            metadata = getattr(obj, "custom_metadata", None)
        if metadata and metadata.get("sha256"):
            headers["X-Firmware-SHA256"] = metadata["sha256"]
        if getattr(obj, "httpEtag", None):
            headers["ETag"] = obj.httpEtag
        return Response(obj.body, headers=headers)
