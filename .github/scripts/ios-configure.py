"""Validate workflow inputs without exposing secrets or changing project files."""

import os
import re
import sys
import uuid
import xml.etree.ElementTree as ET


def fail(message):
    print(f"::error::{message}", file=sys.stderr)
    raise SystemExit(1)


mode = os.environ.get("MODE", "build-check")
if mode not in {"build-check", "ipa", "testflight"}:
    fail("Unknown iOS workflow mode.")

required = []
if mode != "build-check":
    required += [
        "IOS_BUNDLE_ID",
        "IOS_CERTIFICATE_BASE64",
        "IOS_CERTIFICATE_PASSWORD",
        "IOS_PROFILE_BASE64",
    ]
if mode == "testflight":
    required += [
        "APP_STORE_CONNECT_API_KEY_BASE64",
        "APP_STORE_CONNECT_KEY_ID",
        "APP_STORE_CONNECT_ISSUER_ID",
    ]
missing = [name for name in required if not os.environ.get(name, "").strip()]
if missing:
    fail("Configure the GitHub Actions variable/secrets before this run: " + ", ".join(missing))

project = ET.parse(os.environ.get("PROJECT_PATH", "ArriLight/ArriLight.csproj")).getroot()
bundle_id = os.environ.get("IOS_BUNDLE_ID", "").strip() or project.findtext(".//ApplicationId", "")
version = os.environ.get("VERSION_INPUT", "").strip() or project.findtext(".//ApplicationDisplayVersion", "")
build_number = os.environ.get("BUILD_NUMBER_INPUT", "").strip()
if not build_number:
    # A rerun gets a different build number, even if an earlier upload succeeded.
    build_number = f"{int(os.environ['GITHUB_RUN_NUMBER']) + 4}.{int(os.environ['GITHUB_RUN_ATTEMPT'])}"

if not re.fullmatch(r"[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+", bundle_id):
    fail("IOS_BUNDLE_ID must be a reverse-DNS identifier containing letters, digits, dots, and hyphens.")
if not re.fullmatch(r"\d+\.\d+\.\d+", version, flags=re.ASCII):
    fail("The app version must contain three integers, for example 0.1.3.")
if not re.fullmatch(r"[1-9]\d{0,3}(?:\.\d{1,2}){0,2}", build_number, flags=re.ASCII):
    fail("The build number must have a positive first component of up to 4 digits and up to two 2-digit components. Set build_number explicitly if necessary.")

if mode == "testflight":
    if not re.fullmatch(r"[A-Z0-9]{10}", os.environ["APP_STORE_CONNECT_KEY_ID"]):
        fail("APP_STORE_CONNECT_KEY_ID must be the 10-character Apple API Key ID.")
    try:
        uuid.UUID(os.environ["APP_STORE_CONNECT_ISSUER_ID"])
    except ValueError:
        fail("APP_STORE_CONNECT_ISSUER_ID must be a UUID.")

with open(os.environ["GITHUB_ENV"], "a", encoding="utf-8") as output:
    output.write(f"IOS_BUNDLE_ID={bundle_id}\nIOS_VERSION={version}\nIOS_BUILD_NUMBER={build_number}\n")
with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as output:
    output.write(f"Mode: `{mode}`\n\nBundle ID: `{bundle_id}`\n\nVersion: `{version}`\n\nBuild: `{build_number}`\n\n")
    if mode == "build-check":
        output.write("The artifact is an unsigned simulator app. It cannot be installed on an iPhone.\n")
