"""Exercise Worker request handling with in-memory R2 and the workers API stubbed."""

import importlib.util
import json
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import patch


class FakeResponse:
    def __init__(self, body, status=200, headers=None):
        self.body = body
        self.status = status
        self.headers = headers or {}


def load_worker(name):
    path = Path(__file__).resolve().parents[1] / name / "src" / "entry.py"
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    with patch.dict(sys.modules, {"workers": SimpleNamespace(
        Response=FakeResponse, WorkerEntrypoint=object
    )}):
        spec.loader.exec_module(module)
    return module


LIST_WORKER = load_worker("arriLight-list")
GET_WORKER = load_worker("arriLight-get")


class FakeBucket:
    def __init__(self, pages=None, objects=None):
        self.pages = pages or [[]]
        self.objects = objects or {}
        self.list_calls = []
        self.get_calls = []

    async def list(self, **kwargs):
        self.list_calls.append(kwargs)
        index = int(kwargs.get("cursor", "0"))
        truncated = index + 1 < len(self.pages)
        return SimpleNamespace(objects=self.pages[index], truncated=truncated,
                               cursor=str(index + 1) if truncated else None)

    async def get(self, key):
        self.get_calls.append(key)
        return self.objects.get(key)


def firmware(key, **metadata):
    return SimpleNamespace(key=key, customMetadata=metadata)


def request(query):
    return SimpleNamespace(method="GET", url="https://worker.example/?" + query)


def worker(module, bucket):
    instance = module.Default()
    instance.env = SimpleNamespace(FIRMWARE_BUCKET=bucket)
    return instance


class ListWorkerTests(unittest.IsolatedAsyncioTestCase):
    async def check(self, bucket, current="1.1.0", hardware="PW01-A2"):
        response = await worker(LIST_WORKER, bucket).fetch(request(
            f"hardwareVersion={hardware}&currentVersion={current}"
        ))
        self.assertEqual(200, response.status)
        return json.loads(response.body)

    async def test_slb_bin_update_from_reported_hardware_directory(self):
        bucket = FakeBucket([[firmware("PW01_A2/1.1.1.bin", sha256="ab" * 32)]])
        result = await self.check(bucket)
        self.assertEqual("PW01_A2/1.1.1.bin", result["fileName"])
        self.assertEqual("1.1.1", result["version"])
        self.assertEqual("ab" * 32, result["sha256"])
        self.assertEqual("PW01_A2/", bucket.list_calls[0]["prefix"])

    async def test_both_formats_use_numeric_version_order(self):
        for extension in ("bin", "hex16", "BIN", "HEX16"):
            with self.subTest(extension=extension):
                key = f"PW01_A2/1.10.0.{extension}"
                bucket = FakeBucket([[firmware("PW01_A2/1.9.0.hex16"), firmware(key)]])
                self.assertEqual(key, (await self.check(bucket))["fileName"])

    async def test_no_update_for_equal_or_older_versions_of_either_format(self):
        bucket = FakeBucket([[firmware("PW01_A2/1.1.0.bin"), firmware("PW01_A2/1.0.9.hex16")]])
        result = await self.check(bucket)
        self.assertIsNone(result["fileName"])
        self.assertIsNone(result["version"])

    async def test_unsupported_nested_and_other_hardware_objects_are_ignored(self):
        bucket = FakeBucket([[firmware(key) for key in (
            "PW01_A2/1.2.0.bin.zip", "PW01_A2/1.2.0.hex16.bak",
            "PW01_A2/1.2.0.txt", "PW01_A2/bin/1.2.0.bin",
            "PW01_A1/1.2.0.bin", "PW01_A2/unversioned.bin"
        )]])
        self.assertIsNone((await self.check(bucket))["fileName"])

    async def test_bin_is_found_after_r2_pagination(self):
        bucket = FakeBucket([[firmware("PW01_A2/1.1.0.hex16")],
                             [firmware("PW01_A2/1.1.1.bin")]])
        self.assertEqual("PW01_A2/1.1.1.bin", (await self.check(bucket))["fileName"])
        self.assertEqual("1", bucket.list_calls[1]["cursor"])


class GetWorkerTests(unittest.IsolatedAsyncioTestCase):
    async def test_bin_and_hex16_are_downloaded_without_converting_contents(self):
        for extension, content in (("bin", b"\x00\xff\x01\x02"),
                                   ("BIN", b"\x00\xff\x01\x02"),
                                   ("hex16", b":00000001FF\n"),
                                   ("HEX16", b":00000001FF\n")):
            with self.subTest(extension=extension):
                key = f"PW01_A2/1.1.1.{extension}"
                obj = SimpleNamespace(body=content, customMetadata={"sha256": "ab" * 32})
                bucket = FakeBucket(objects={key: obj})
                response = await worker(GET_WORKER, bucket).fetch(request("fileName=" + key))
                self.assertEqual(200, response.status)
                self.assertEqual(content, response.body)
                self.assertEqual([key], bucket.get_calls)
                self.assertEqual("1.1.1", response.headers["X-Firmware-Version"])
                self.assertEqual("PW01-A2", response.headers["X-Hardware-Version"])
                self.assertEqual("ab" * 32, response.headers["X-Firmware-SHA256"])
                self.assertIn(f'filename="1.1.1.{extension}"', response.headers["Content-Disposition"])

    async def test_unsupported_or_nested_keys_are_rejected_before_r2_access(self):
        for key in ("PW01_A2/1.1.1.bin.zip", "PW01_A2/1.1.1.hex16.bak",
                    "PW01_A2/1.1.1.txt", "PW01_A2/sub/1.1.1.bin", "1.1.1.bin"):
            with self.subTest(key=key):
                bucket = FakeBucket()
                response = await worker(GET_WORKER, bucket).fetch(request("fileName=" + key))
                self.assertEqual(400, response.status)
                self.assertEqual("invalid_file_name", json.loads(response.body)["error"])
                self.assertEqual([], bucket.get_calls)

    async def test_missing_bin_returns_not_found(self):
        response = await worker(GET_WORKER, FakeBucket()).fetch(request("fileName=PW01_A2/1.1.1.bin"))
        self.assertEqual(404, response.status)


if __name__ == "__main__":
    unittest.main()
