"""Android Actions configuration, signing material, and release artifact checks."""

import argparse
import base64
import binascii
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET

ANDROID_NS = "http://schemas.android.com/apk/res/android"
MAX_VERSION_CODE = 2_100_000_000


def require(names):
    missing = [name for name in names if not os.environ.get(name, "").strip()]
    if missing:
        raise ValueError("Configure the GitHub Actions variables/secrets: " + ", ".join(missing))


def decode_keystore():
    value = "".join(os.environ["ANDROID_KEYSTORE_BASE64"].split())
    try:
        decoded = base64.b64decode(value, validate=True)
    except (ValueError, binascii.Error):
        raise ValueError("ANDROID_KEYSTORE_BASE64 must contain the Base64-encoded keystore file.") from None
    if not decoded:
        raise ValueError("ANDROID_KEYSTORE_BASE64 decoded to an empty file.")
    return decoded


def export(values):
    with open(os.environ["GITHUB_ENV"], "a", encoding="utf-8") as output:
        for name, value in values.items():
            if "\n" in str(value) or "\r" in str(value):
                raise ValueError("A resolved workflow setting contains a newline.")
            output.write(f"{name}={value}\n")


def configure():
    mode = os.environ.get("MODE", "apk")
    if mode not in {"apk", "firebase"}:
        raise ValueError("Unknown Android workflow mode.")
    require(["ANDROID_KEYSTORE_BASE64", "ANDROID_KEYSTORE_PASSWORD",
             "ANDROID_KEY_PASSWORD", "ANDROID_KEY_ALIAS"])
    decode_keystore()

    project_path = Path(os.environ.get("PROJECT_PATH", "ArriLight/ArriLight.csproj"))
    project = ET.parse(project_path).getroot()
    source_manifest = project_path.parent / "Platforms/Android/AndroidManifest.xml"
    manifest = ET.parse(source_manifest)
    application_id = os.environ.get("ANDROID_APPLICATION_ID", "").strip() or project.findtext(".//ApplicationId", "")
    version = os.environ.get("VERSION_INPUT", "").strip() or project.findtext(".//ApplicationDisplayVersion", "")
    build_number = os.environ.get("BUILD_NUMBER_INPUT", "").strip()
    if not build_number:
        build_number = str(1000 + int(os.environ["GITHUB_RUN_NUMBER"]))

    if not re.fullmatch(r"[A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z][A-Za-z0-9_]*)+", application_id):
        raise ValueError("ANDROID_APPLICATION_ID must be a valid Android package name.")
    if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", version):
        raise ValueError("The version must contain three integers, for example 0.1.12.")
    if not re.fullmatch(r"[1-9][0-9]*", build_number) or int(build_number) > MAX_VERSION_CODE:
        raise ValueError("The Android build number must be an integer from 1 to 2100000000.")
    if int(build_number) < int(project.findtext(".//ApplicationVersion", "1")):
        raise ValueError("The build number is below the project ApplicationVersion. Choose a higher build_number.")

    firebase_groups = ""
    if mode == "firebase":
        require(["FIREBASE_APP_ID", "FIREBASE_GROUPS", "FIREBASE_SERVICE_ACCOUNT_JSON"])
        if not re.fullmatch(r"1:[0-9]+:android:[0-9a-fA-F]+", os.environ["FIREBASE_APP_ID"].strip()):
            raise ValueError("FIREBASE_APP_ID must be the Firebase 1:...:android:... ID, not the package name.")
        groups = [value.strip() for value in os.environ["FIREBASE_GROUPS"].split(",")]
        if any(not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_-]*", value) for value in groups):
            raise ValueError("FIREBASE_GROUPS must contain comma-separated tester group aliases.")
        firebase_groups = ",".join(groups)
        try:
            credentials = json.loads(os.environ["FIREBASE_SERVICE_ACCOUNT_JSON"])
        except (ValueError, TypeError):
            raise ValueError("FIREBASE_SERVICE_ACCOUNT_JSON must be valid service-account JSON.") from None
        if (not isinstance(credentials, dict) or credentials.get("type") != "service_account"
                or not credentials.get("client_email", "").endswith(".gserviceaccount.com")
                or "BEGIN PRIVATE KEY" not in credentials.get("private_key", "")):
            raise ValueError("Use the service-account private-key JSON, not google-services.json.")

    build_dir = Path(os.environ["RUNNER_TEMP"]).resolve() / "arrilight-android"
    build_dir.mkdir(parents=True, exist_ok=True)
    manifest_root = manifest.getroot()
    manifest_root.set("package", application_id)
    manifest_root.set(f"{{{ANDROID_NS}}}versionName", version)
    manifest_root.set(f"{{{ANDROID_NS}}}versionCode", build_number)
    ET.register_namespace("android", ANDROID_NS)
    manifest_path = build_dir / "AndroidManifest.xml"
    manifest.write(manifest_path, encoding="utf-8", xml_declaration=True)

    notes = os.environ.get("RELEASE_NOTES", "").strip()
    if not notes:
        commit = os.environ.get("GITHUB_SHA", "")[:12]
        notes = f"ArriLight {version} ({build_number})\nCommit: {commit}"
    notes_path = build_dir / "release-notes.txt"
    notes_path.write_text(notes + "\n", encoding="utf-8")
    values = {
        "ANDROID_APPLICATION_ID": application_id,
        "ANDROID_VERSION": version,
        "ANDROID_BUILD_NUMBER": build_number,
        "ANDROID_MANIFEST_PATH": manifest_path,
        "ANDROID_SIGNING_DIRECTORY": build_dir / "signing",
        "ANDROID_KEYSTORE_PATH": build_dir / "signing/ArriLight.keystore",
        "ANDROID_PUBLISH_DIRECTORY": build_dir / "publish",
        "ANDROID_ARTIFACT_DIRECTORY": build_dir / "artifacts",
        "ANDROID_RELEASE_NOTES_PATH": notes_path,
    }
    if mode == "firebase":
        values["FIREBASE_APP_ID"] = os.environ["FIREBASE_APP_ID"].strip()
        values["FIREBASE_GROUPS"] = firebase_groups
    export(values)
    with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as output:
        output.write(f"Mode: `{mode}`\n\nPackage: `{application_id}`\n\n"
                     f"Version: `{version}`\n\nBuild: `{build_number}`\n\n")
        if mode == "apk":
            output.write("The signed APK is saved as an Actions artifact; it will not be uploaded to Firebase.\n")
    return values


def run_tool(arguments, failure_message):
    try:
        result = subprocess.run(arguments, check=True, capture_output=True, text=True)
        return result.stdout
    except (OSError, subprocess.CalledProcessError):
        # Never include a signing command, credential file, or secret in an error.
        raise ValueError(failure_message) from None


def signing():
    directory = Path(os.environ["ANDROID_SIGNING_DIRECTORY"])
    directory.mkdir(parents=True, exist_ok=True, mode=0o700)
    store = Path(os.environ["ANDROID_KEYSTORE_PATH"])
    with os.fdopen(os.open(store, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600), "wb") as output:
        output.write(decode_keystore())
    alias = os.environ["ANDROID_KEY_ALIAS"]
    common = ["-keystore", str(store), "-alias", alias, "-storepass:env", "ANDROID_KEYSTORE_PASSWORD"]
    run_tool(["keytool", "-list", *common],
             "Keystore validation failed. Check the Base64 file, store password, and ANDROID_KEY_ALIAS.")
    certificate = directory / "signing-certificate.der"
    run_tool(["keytool", "-exportcert", *common, "-file", str(certificate)],
             "Could not export the signing certificate for the configured alias.")
    digest = hashlib.sha256(certificate.read_bytes()).hexdigest()
    export({"ANDROID_SIGNING_CERT_SHA256": digest})
    print("Keystore alias verified. Certificate SHA-256: " + digest)


def android_tools():
    sdk = Path(os.environ.get("ANDROID_HOME") or os.environ.get("ANDROID_SDK_ROOT", ""))
    build_tools = sdk / "build-tools"
    candidates = []
    if build_tools.is_dir():
        candidates = [entry for entry in build_tools.iterdir()
                      if entry.is_dir() and re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", entry.name)]
    if not candidates:
        raise ValueError("Android SDK build-tools are missing. Run InstallAndroidDependencies first.")
    directory = max(candidates, key=lambda entry: tuple(map(int, entry.name.split("."))))
    return directory / "apksigner", directory / "aapt"


def validate_apk_metadata(signature, badging):
    certificate = re.search(r"Signer #1 certificate SHA-256 digest:\s*([0-9a-fA-F]{64})", signature)
    if not certificate or certificate.group(1).lower() != os.environ["ANDROID_SIGNING_CERT_SHA256"].lower():
        raise ValueError("The APK signing certificate does not match the configured keystore.")
    package_line = next((line for line in badging.splitlines() if line.startswith("package:")), "")
    metadata = dict(re.findall(r"([A-Za-z]+)='([^']*)'", package_line))
    expected = {
        "name": os.environ["ANDROID_APPLICATION_ID"],
        "versionName": os.environ["ANDROID_VERSION"],
        "versionCode": os.environ["ANDROID_BUILD_NUMBER"],
    }
    if any(metadata.get(key) != value for key, value in expected.items()):
        raise ValueError("APK package/version metadata does not match the resolved release settings.")
    if any(line.strip() == "application-debuggable" for line in badging.splitlines()):
        raise ValueError("The release APK is debuggable.")
    return {**expected, "certificateSha256": certificate.group(1).lower()}


def verify():
    publish_dir = Path(os.environ["ANDROID_PUBLISH_DIRECTORY"])
    candidates = [path for path in publish_dir.glob("*.apk") if path.stem.lower().endswith("-signed")]
    if len(candidates) != 1:
        raise ValueError("Expected exactly one *-Signed.apk in the publish directory.")
    apk = candidates[0]
    apksigner, aapt = android_tools()
    signature = run_tool([str(apksigner), "verify", "--verbose", "--print-certs", str(apk)],
                         "APK signature verification failed.")
    badging = run_tool([str(aapt), "dump", "badging", str(apk)], "Could not read the APK package metadata.")
    metadata = validate_apk_metadata(signature, badging)
    directory = Path(os.environ["ANDROID_ARTIFACT_DIRECTORY"])
    directory.mkdir(parents=True, exist_ok=True)
    artifact = directory / f"ArriLight-{os.environ['ANDROID_VERSION']}-{os.environ['ANDROID_BUILD_NUMBER']}-Signed.apk"
    shutil.copy2(apk, artifact)
    metadata["apkSha256"] = hashlib.sha256(artifact.read_bytes()).hexdigest()
    (directory / "apk-metadata.json").write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8")
    export({"ANDROID_APK_PATH": artifact})
    with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as output:
        output.write(f"Verified signed APK: `{artifact.name}`\n\n"
                     f"APK SHA-256: `{metadata['apkSha256']}`\n\n"
                     f"Certificate SHA-256: `{metadata['certificateSha256']}`\n\n")


def cleanup():
    root = Path(os.environ["RUNNER_TEMP"]).resolve()
    directory = root / "arrilight-android/signing"
    if directory.is_symlink() or not directory.resolve().is_relative_to(root):
        raise ValueError("Refusing to clean a signing directory outside RUNNER_TEMP.")
    if directory.exists():
        shutil.rmtree(directory)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=["configure", "signing", "verify", "cleanup"])
    args = parser.parse_args()
    try:
        globals()[args.command]()
    except (ValueError, ET.ParseError, OSError) as error:
        print(f"::error::{error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
