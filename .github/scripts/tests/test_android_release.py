import base64
from contextlib import redirect_stdout
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET


SPEC = importlib.util.spec_from_file_location(
    "android_release", Path(__file__).resolve().parents[1] / "android-release.py")
release = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(release)


class AndroidReleaseTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name).resolve()
        project_dir = self.root / "app"
        manifest_dir = project_dir / "Platforms/Android"
        manifest_dir.mkdir(parents=True)
        self.project = project_dir / "ArriLight.csproj"
        self.project.write_text(
            "<Project><PropertyGroup><ApplicationId>com.arriopto.arrilight</ApplicationId>"
            "<ApplicationDisplayVersion>0.1.12</ApplicationDisplayVersion>"
            "<ApplicationVersion>13</ApplicationVersion></PropertyGroup></Project>", encoding="utf-8")
        self.manifest = manifest_dir / "AndroidManifest.xml"
        self.manifest.write_text(
            '<manifest xmlns:android="http://schemas.android.com/apk/res/android" '
            'package="com.old.app" android:versionName="0.0.1">'
            '<uses-permission android:name="android.permission.CAMERA"/></manifest>', encoding="utf-8")
        self.original_project = self.project.read_bytes()
        self.original_manifest = self.manifest.read_bytes()
        self.environment = {
            "MODE": "apk", "PROJECT_PATH": str(self.project),
            "RUNNER_TEMP": str(self.root), "GITHUB_ENV": str(self.root / "env"),
            "GITHUB_STEP_SUMMARY": str(self.root / "summary"),
            "GITHUB_RUN_NUMBER": "7", "GITHUB_RUN_ATTEMPT": "1", "GITHUB_SHA": "abc123",
            "ANDROID_KEY_ALIAS": "fixture-alias", "ANDROID_KEYSTORE_PASSWORD": "fixture-store-password",
            "ANDROID_KEY_PASSWORD": "fixture-key-password",
            "ANDROID_KEYSTORE_BASE64": base64.b64encode(b"fixture-keystore").decode(),
        }
        self.environment_patch = patch.dict(os.environ, self.environment, clear=True)
        self.environment_patch.start()
        self.addCleanup(self.environment_patch.stop)

    def configure(self):
        values = release.configure()
        os.environ.update({key: str(value) for key, value in values.items()})
        return values

    def firebase(self):
        os.environ.update({
            "MODE": "firebase", "FIREBASE_APP_ID": "1:123456:android:abcdef012345",
            "FIREBASE_GROUPS": "internal-testers, external-testers",
            "FIREBASE_SERVICE_ACCOUNT_JSON": json.dumps({
                "type": "service_account", "client_email": "ci@fixture.iam.gserviceaccount.com",
                "private_key": "-----BEGIN PRIVATE KEY-----\nfixture\n-----END PRIVATE KEY-----",
            }),
        })

    def apk(self):
        values = self.configure()
        os.environ["ANDROID_SIGNING_CERT_SHA256"] = "ab" * 32
        sdk = self.root / "sdk"
        (sdk / "build-tools/36.0.0").mkdir(parents=True)
        os.environ["ANDROID_HOME"] = str(sdk)
        values["ANDROID_PUBLISH_DIRECTORY"].mkdir()
        (values["ANDROID_PUBLISH_DIRECTORY"] / "app-Signed.apk").write_bytes(b"fixture-apk")
        (values["ANDROID_PUBLISH_DIRECTORY"] / "app.apk").write_bytes(b"unsigned-fixture")
        signature = "Signer #1 certificate SHA-256 digest: " + "ab" * 32
        badging = "package: name='com.arriopto.arrilight' versionCode='1007' versionName='0.1.12'\n"
        return values, signature, badging

    def test_apk_mode_requires_no_firebase_credentials_and_preserves_sources(self):
        values = self.configure()
        self.assertEqual("1007", values["ANDROID_BUILD_NUMBER"])
        self.assertEqual("0.1.12", values["ANDROID_VERSION"])
        self.assertNotIn("FIREBASE_APP_ID", values)
        self.assertEqual(self.original_project, self.project.read_bytes())
        self.assertEqual(self.original_manifest, self.manifest.read_bytes())
        self.assertIn("will not be uploaded", Path(os.environ["GITHUB_STEP_SUMMARY"]).read_text())

    def test_override_versions_are_applied_to_temporary_manifest_only(self):
        os.environ.update({"VERSION_INPUT": "1.2.3", "BUILD_NUMBER_INPUT": "2000",
                           "ANDROID_APPLICATION_ID": "com.arriopto.testing"})
        values = self.configure()
        manifest = ET.parse(values["ANDROID_MANIFEST_PATH"]).getroot()
        self.assertEqual("com.arriopto.testing", manifest.get("package"))
        self.assertEqual("1.2.3", manifest.get(f"{{{release.ANDROID_NS}}}versionName"))
        self.assertEqual("2000", manifest.get(f"{{{release.ANDROID_NS}}}versionCode"))
        self.assertIsNotNone(manifest.find("uses-permission"))
        self.assertEqual(self.original_manifest, self.manifest.read_bytes())

    def test_manifest_argument_resolves_with_android_sdk_project_directory_prefix(self):
        values = self.configure()
        argument = values["ANDROID_MANIFEST_RELATIVE_PATH"]
        self.assertFalse(Path(argument).is_absolute())
        # Xamarin.Android.Common.targets uses the literal $(ProjectDir)$(AndroidManifest).
        resolved_by_sdk = Path(str(self.project.parent) + os.sep + argument).resolve()
        self.assertEqual(values["ANDROID_MANIFEST_PATH"], resolved_by_sdk)
        self.assertTrue(resolved_by_sdk.is_file())
        self.assertEqual(self.original_manifest, self.manifest.read_bytes())

    def test_relative_project_path_uses_project_directory_instead_of_checkout_root(self):
        previous_directory = Path.cwd()
        try:
            os.chdir(self.root)
            os.environ["PROJECT_PATH"] = "app/ArriLight.csproj"
            values = self.configure()
        finally:
            os.chdir(previous_directory)
        resolved_by_sdk = Path(
            str(self.project.parent) + os.sep + values["ANDROID_MANIFEST_RELATIVE_PATH"]).resolve()
        self.assertEqual(values["ANDROID_MANIFEST_PATH"], resolved_by_sdk)
        self.assertTrue(resolved_by_sdk.is_file())

    def test_rerun_preserves_default_version_code(self):
        first = self.configure()["ANDROID_BUILD_NUMBER"]
        os.environ["GITHUB_RUN_ATTEMPT"] = "2"
        self.assertEqual(first, self.configure()["ANDROID_BUILD_NUMBER"])

    def test_missing_signing_secret_is_reported_without_exposing_other_secrets(self):
        del os.environ["ANDROID_KEYSTORE_PASSWORD"]
        with self.assertRaisesRegex(ValueError, "ANDROID_KEYSTORE_PASSWORD") as failure:
            release.configure()
        self.assertNotIn(os.environ["ANDROID_KEY_PASSWORD"], str(failure.exception))
        self.assertFalse((self.root / "arrilight-android").exists())

    def test_invalid_inputs_fail_before_any_generated_files(self):
        cases = [("MODE", "push"), ("VERSION_INPUT", "1.2;Injected=true"),
                 ("BUILD_NUMBER_INPUT", "0"), ("BUILD_NUMBER_INPUT", "12"),
                 ("BUILD_NUMBER_INPUT", "1.2"), ("BUILD_NUMBER_INPUT", "2100000001"),
                 ("ANDROID_APPLICATION_ID", "com.app\nINJECTED=value"),
                 ("ANDROID_KEYSTORE_BASE64", "not-base64!")]
        for name, value in cases:
            with self.subTest(name=name, value=value), patch.dict(os.environ, {name: value}):
                with self.assertRaises(ValueError):
                    release.configure()
                self.assertFalse((self.root / "arrilight-android").exists())

    def test_firebase_requires_app_id_groups_and_service_account(self):
        os.environ["MODE"] = "firebase"
        with self.assertRaisesRegex(ValueError, "FIREBASE_APP_ID"):
            release.configure()
        self.firebase()
        values = self.configure()
        self.assertEqual("internal-testers,external-testers", values["FIREBASE_GROUPS"])
        self.assertNotIn("PRIVATE KEY", Path(os.environ["GITHUB_ENV"]).read_text())

    def test_firebase_rejects_package_as_app_id_and_client_configuration_json(self):
        self.firebase()
        for name, value in [("FIREBASE_APP_ID", "com.arriopto.arrilight"),
                            ("FIREBASE_GROUPS", "internal,,external"),
                            ("FIREBASE_SERVICE_ACCOUNT_JSON", "not-json"),
                            ("FIREBASE_SERVICE_ACCOUNT_JSON", '{"project_info": {}}')]:
            with self.subTest(name=name), patch.dict(os.environ, {name: value}):
                with self.assertRaises(ValueError):
                    release.configure()

    def test_multiline_release_notes_are_kept_literal_and_out_of_github_env(self):
        notes = "First line\n$(do-not-execute)\n中文说明"
        os.environ["RELEASE_NOTES"] = notes
        values = self.configure()
        self.assertEqual(notes + "\n", values["ANDROID_RELEASE_NOTES_PATH"].read_text(encoding="utf-8"))
        self.assertNotIn("do-not-execute", Path(os.environ["GITHUB_ENV"]).read_text())

    def test_signing_restores_binary_and_exports_only_public_certificate_fingerprint(self):
        values = self.configure()
        def tool(arguments, failure_message):
            self.assertNotIn(os.environ["ANDROID_KEYSTORE_PASSWORD"], arguments)
            if "-exportcert" in arguments:
                Path(arguments[arguments.index("-file") + 1]).write_bytes(b"public-certificate")
            return ""
        output = io.StringIO()
        with patch.object(release, "run_tool", side_effect=tool), redirect_stdout(output):
            release.signing()
        self.assertEqual(b"fixture-keystore", values["ANDROID_KEYSTORE_PATH"].read_bytes())
        self.assertIn(hashlib.sha256(b"public-certificate").hexdigest(), output.getvalue())
        self.assertNotIn(os.environ["ANDROID_KEYSTORE_PASSWORD"], output.getvalue())

    def test_verify_saves_only_signed_apk_and_public_metadata(self):
        values, signature, badging = self.apk()
        with patch.object(release, "run_tool", side_effect=[signature, badging]):
            release.verify()
        artifacts = list(values["ANDROID_ARTIFACT_DIRECTORY"].iterdir())
        self.assertEqual({"ArriLight-0.1.12-1007-Signed.apk", "apk-metadata.json"}, {p.name for p in artifacts})
        metadata = json.loads((values["ANDROID_ARTIFACT_DIRECTORY"] / "apk-metadata.json").read_text())
        self.assertEqual(hashlib.sha256(b"fixture-apk").hexdigest(), metadata["apkSha256"])
        self.assertEqual("com.arriopto.arrilight", metadata["name"])

    def test_wrong_certificate_package_version_or_debug_apk_cannot_be_published(self):
        values, signature, badging = self.apk()
        cases = [(signature.replace("ab" * 32, "cd" * 32), badging),
                 (signature, badging.replace("com.arriopto.arrilight", "com.other.app")),
                 (signature, badging.replace("1007", "13")),
                 (signature, badging.replace("0.1.12", "0.0.1")),
                 (signature, badging + "application-debuggable\n")]
        for actual_signature, actual_badging in cases:
            with self.subTest(badging=actual_badging):
                with self.assertRaises(ValueError):
                    release.validate_apk_metadata(actual_signature, actual_badging)
                self.assertFalse(values["ANDROID_ARTIFACT_DIRECTORY"].exists())

    def test_ambiguous_signed_apks_are_rejected(self):
        values, _, _ = self.apk()
        (values["ANDROID_PUBLISH_DIRECTORY"] / "other-Signed.apk").write_bytes(b"other")
        with self.assertRaisesRegex(ValueError, "exactly one"), patch.object(release, "run_tool") as tool:
            release.verify()
        tool.assert_not_called()

    def test_cleanup_removes_signing_material_but_keeps_artifacts(self):
        values = self.configure()
        values["ANDROID_SIGNING_DIRECTORY"].mkdir()
        values["ANDROID_KEYSTORE_PATH"].write_bytes(b"private-material")
        values["ANDROID_ARTIFACT_DIRECTORY"].mkdir()
        artifact = values["ANDROID_ARTIFACT_DIRECTORY"] / "keep.apk"
        artifact.write_bytes(b"apk")
        os.environ["ANDROID_SIGNING_DIRECTORY"] = str(values["ANDROID_ARTIFACT_DIRECTORY"])
        release.cleanup()
        self.assertFalse(values["ANDROID_SIGNING_DIRECTORY"].exists())
        self.assertTrue(artifact.exists())


if __name__ == "__main__":
    unittest.main()
