#!/usr/bin/env bash
set -euo pipefail
umask 077

mkdir -p "$SIGNING_DIRECTORY"
certificate_path="$SIGNING_DIRECTORY/certificate.p12"
profile_path="$SIGNING_DIRECTORY/profile.mobileprovision"
printf '%s' "$IOS_CERTIFICATE_BASE64" | base64 --decode -o "$certificate_path"
printf '%s' "$IOS_PROFILE_BASE64" | base64 --decode -o "$profile_path"
security cms -D -i "$profile_path" > "$SIGNING_DIRECTORY/profile.plist"

# Validate App Store distribution, expiration, and Bundle ID before importing.
python3 - <<'PY'
import datetime
import hashlib
import json
import os
import pathlib
import plistlib
import uuid

directory = pathlib.Path(os.environ["SIGNING_DIRECTORY"])
with (directory / "profile.plist").open("rb") as source:
    profile = plistlib.load(source)
entitlements = profile.get("Entitlements", {})
expiration = profile["ExpirationDate"]
if expiration.tzinfo is None:
    expiration = expiration.replace(tzinfo=datetime.timezone.utc)
if expiration <= datetime.datetime.now(datetime.timezone.utc):
    raise SystemExit("::error::The provisioning profile has expired.")
if ("ProvisionedDevices" in profile or profile.get("ProvisionsAllDevices")
        or entitlements.get("get-task-allow")):
    raise SystemExit("::error::Use an App Store Connect distribution profile, not a development, Ad Hoc, or enterprise profile.")
identifier = entitlements.get("application-identifier", "")
if identifier.partition(".")[2] != os.environ["IOS_BUNDLE_ID"]:
    raise SystemExit("::error::The provisioning profile does not match IOS_BUNDLE_ID.")
profile_uuid = profile["UUID"]
uuid.UUID(profile_uuid)
with (directory / "profile.json").open("w", encoding="utf-8") as output:
    json.dump({"uuid": profile_uuid,
               "certificates": [hashlib.sha1(cert).hexdigest().upper()
                                for cert in profile.get("DeveloperCertificates", [])]}, output)
with open(os.environ["GITHUB_ENV"], "a", encoding="utf-8") as output:
    output.write(f"IOS_PROFILE_UUID={profile_uuid}\n")
PY

if ! openssl pkcs12 -in "$certificate_path" -noout \
  -passin env:IOS_CERTIFICATE_PASSWORD; then
  echo "::error::OpenSSL could not verify the PKCS12 file. Check that IOS_CERTIFICATE_BASE64 and IOS_CERTIFICATE_PASSWORD belong to the same .p12 file."
  exit 1
fi
echo "OpenSSL PKCS12 verification passed."

# Repack OpenSSL 3's PBES2/AES defaults for the macOS PKCS12 importer.
# Keep the intermediate private key encrypted, and use a random ASCII password
# for the temporary bundle so the original password's encoding is irrelevant.
certificate_pem_path="$SIGNING_DIRECTORY/certificate-encrypted.pem"
certificate_import_path="$SIGNING_DIRECTORY/certificate-macos.p12"
trap 'rm -f "$certificate_pem_path" "$certificate_import_path"' EXIT
export IOS_CERTIFICATE_IMPORT_PASSWORD
IOS_CERTIFICATE_IMPORT_PASSWORD="$(openssl rand -hex 32)"
echo "::add-mask::$IOS_CERTIFICATE_IMPORT_PASSWORD"
if ! openssl pkcs12 -in "$certificate_path" -out "$certificate_pem_path" \
  -passin env:IOS_CERTIFICATE_PASSWORD -passout env:IOS_CERTIFICATE_IMPORT_PASSWORD \
  || ! openssl pkcs12 -export -in "$certificate_pem_path" -inkey "$certificate_pem_path" \
  -out "$certificate_import_path" -keypbe PBE-SHA1-3DES -certpbe PBE-SHA1-3DES \
  -macalg sha1 -passin env:IOS_CERTIFICATE_IMPORT_PASSWORD \
  -passout env:IOS_CERTIFICATE_IMPORT_PASSWORD; then
  echo "::error::Could not prepare the macOS-compatible PKCS12 bundle. Ensure the .p12 contains the certificate and its private key."
  exit 1
fi
echo "Prepared macOS-compatible PKCS12 bundle (3DES/SHA1)."

keychain_password="$(openssl rand -hex 32)"
echo "::add-mask::$keychain_password"
security create-keychain -p "$keychain_password" "$SIGNING_KEYCHAIN_PATH"
security set-keychain-settings -lut 21600 "$SIGNING_KEYCHAIN_PATH"
security unlock-keychain -p "$keychain_password" "$SIGNING_KEYCHAIN_PATH"
if ! security import "$certificate_import_path" -P "$IOS_CERTIFICATE_IMPORT_PASSWORD" \
  -k "$SIGNING_KEYCHAIN_PATH" -T /usr/bin/codesign; then
  echo "::error::macOS could not import the compatible PKCS12 bundle after OpenSSL verification passed. Check the keychain import error above."
  exit 1
fi
rm -f "$certificate_pem_path" "$certificate_import_path"
unset IOS_CERTIFICATE_IMPORT_PASSWORD
echo "macOS signing identity import passed."
security set-key-partition-list -S apple-tool:,apple:,codesign: -s \
  -k "$keychain_password" "$SIGNING_KEYCHAIN_PATH"
security list-keychains -d user -s "$SIGNING_KEYCHAIN_PATH" "$HOME/Library/Keychains/login.keychain-db"
security find-identity -v -p codesigning "$SIGNING_KEYCHAIN_PATH" > "$SIGNING_DIRECTORY/identities.txt"

python3 - <<'PY'
import json
import os
import pathlib
import re
import shutil

directory = pathlib.Path(os.environ["SIGNING_DIRECTORY"])
profile = json.loads((directory / "profile.json").read_text(encoding="utf-8"))
identities = re.findall(r'\b([A-Fa-f0-9]{40})\s+"([^"\r\n]+)"',
                        (directory / "identities.txt").read_text(encoding="utf-8"))
matches = [name for fingerprint, name in identities
           if fingerprint.upper() in profile["certificates"]
           and name.startswith(("Apple Distribution:", "iPhone Distribution:"))]
if not matches:
    raise SystemExit("::error::No valid distribution identity matches the profile. Export the Apple Distribution certificate together with its private key as a .p12.")
destination = pathlib.Path.home() / "Library/MobileDevice/Provisioning Profiles"
destination.mkdir(parents=True, exist_ok=True)
shutil.copyfile(directory / "profile.mobileprovision", destination / f"{profile['uuid']}.mobileprovision")
with open(os.environ["GITHUB_ENV"], "a", encoding="utf-8") as output:
    output.write(f"IOS_SIGNING_IDENTITY={matches[0]}\n")
PY

if [[ "$MODE" == "testflight" ]]; then
  mkdir -p "$SIGNING_DIRECTORY/private_keys"
  api_key_path="$SIGNING_DIRECTORY/private_keys/AuthKey_${APP_STORE_CONNECT_KEY_ID}.p8"
  printf '%s' "$APP_STORE_CONNECT_API_KEY_BASE64" | base64 --decode -o "$api_key_path"
  openssl pkey -in "$api_key_path" -noout
fi
