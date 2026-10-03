import json
import re
from urllib.parse import parse_qs, urlparse

from workers import Response, WorkerEntrypoint


VERSION_PATTERN = re.compile(r"(?<!\d)v?(\d+(?:\.\d+){1,3})(?!\d)", re.IGNORECASE)
HARDWARE_PATTERN = re.compile(
    r"^[A-Za-z0-9][A-Za-z0-9._-]*-[A-Za-z0-9][A-Za-z0-9._-]*$"
)


def json_response(payload: dict, status: int = 200) -> Response:
    return Response(
        json.dumps(payload, separators=(",", ":")),
        status=status,
        headers={
            "Content-Type": "application/json; charset=utf-8",
            "Cache-Control": "no-store",
            "Access-Control-Allow-Origin": "*",
        },
    )


def first_query_value(query: dict[str, list[str]], name: str) -> str:
    return (query.get(name) or [""])[0].strip()


def version_parts(value: str) -> tuple[int, ...] | None:
    match = VERSION_PATTERN.search(value)
    if match is None:
        return None
    parts = tuple(int(item) for item in match.group(1).split("."))
    return parts + (0,) * (4 - len(parts))


def object_version(key: str) -> tuple[int, ...] | None:
    return version_parts(key.rsplit("/", 1)[-1])


def object_version_text(key: str) -> str | None:
    match = VERSION_PATTERN.search(key.rsplit("/", 1)[-1])
    return match.group(1) if match is not None else None


def object_custom_metadata(obj: object) -> dict:
    metadata = getattr(obj, "customMetadata", None)
    if metadata is None:
        metadata = getattr(obj, "custom_metadata", None)
    return metadata or {}


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
            return json_response({"error": "method_not_allowed"}, 405)

        query = parse_qs(urlparse(request.url).query)
        hardware_version = first_query_value(query, "hardwareVersion")
        current_version = first_query_value(query, "currentVersion")
        if not HARDWARE_PATTERN.fullmatch(hardware_version):
            return json_response({"error": "invalid_hardware_version"}, 400)
        if not current_version:
            return json_response({"error": "missing_current_version"}, 400)

        current_parts = version_parts(current_version) or (0, 0, 0, 0)
        directory = hardware_version.replace("-", "_")
        prefix = f"{directory}/"
        listed = await self.env.FIRMWARE_BUCKET.list(
            prefix=prefix,
            limit=1000,
            include=["customMetadata"],
        )
        objects = list(listed.objects)
        truncated = listed.truncated
        cursor = listed.cursor if truncated else None
        while truncated:
            listed = await self.env.FIRMWARE_BUCKET.list(
                prefix=prefix,
                limit=1000,
                cursor=cursor,
                include=["customMetadata"],
            )
            objects.extend(list(listed.objects))
            truncated = listed.truncated
            cursor = listed.cursor if truncated else None

        candidates = []
        for obj in objects:
            key = str(obj.key)
            if not key.lower().endswith(".hex16") or not key.startswith(prefix):
                continue
            parsed = object_version(key)
            if parsed is None or parsed <= current_parts:
                continue
            candidates.append((parsed, key, object_custom_metadata(obj)))

        if not candidates:
            return json_response(
                {
                    "hardwareVersion": hardware_version,
                    "version": None,
                    "fileName": None,
                    "sha256": None,
                }
            )

        _selected_parts, selected_key, metadata = max(
            candidates,
            key=lambda item: (item[0], item[1]),
        )
        version = object_version_text(selected_key)
        return json_response(
            {
                "hardwareVersion": hardware_version,
                "version": version,
                "fileName": selected_key,
                "sha256": metadata.get("sha256"),
            }
        )
