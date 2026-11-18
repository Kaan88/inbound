"""Regression checks for real Windows build-tools formats and rejection cases.

Run: python -I tools/AndroidApp/test-release-apk.py
No device, signing key, password or Java process is used.
"""
import importlib.util
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location("release_apk", Path(__file__).with_name("release-apk.py"))
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)


class ReleaseOutputTests(unittest.TestCase):
    def test_windows_signer_formats(self):
        for label in ("Signer #1 certificate", "V3.0 Signer: certificate"):
            for newline in ("\n", "\r\n"):
                with self.subTest(label=label, newline=repr(newline)):
                    release.check_signer(newline.join([
                        "Verifies", "Number of signers: 1",
                        f"{label} SHA-256 digest: {release.RELEASE_CERT_SHA256}", ""]))

    def test_wrong_certificate_is_refused_even_if_source_stamp_matches(self):
        with self.assertRaises(ValueError):
            release.check_signer("Number of signers: 1\nSigner #1 certificate SHA-256 digest: " + "0" * 64 +
                                 "\nSource Stamp certificate SHA-256 digest: " + release.RELEASE_CERT_SHA256)

    def test_multiple_signers_are_refused(self):
        with self.assertRaises(ValueError):
            release.check_signer("Number of signers: 2\nSigner #1 certificate SHA-256 digest: " +
                                 release.RELEASE_CERT_SHA256)

    def test_missing_count_or_certificate_is_refused(self):
        for output in ("Verifies", "Number of signers: 1", "Signer #1 certificate SHA-256 digest: " +
                       release.RELEASE_CERT_SHA256):
            with self.subTest(output=output), self.assertRaises(ValueError):
                release.check_signer(output)

    @staticmethod
    def manifest(min_field="minSdkVersion", minimum="31", target="35", abi="'arm64-v8a'", package="app.inbound.android"):
        return "\r\n".join([f"package: name='{package}' versionCode='1' versionName='0.0.1'",
                              f"{min_field}:'{minimum}'", f"targetSdkVersion:'{target}'", f"native-code: {abi}", ""])

    def test_old_and_new_windows_minimum_sdk_formats(self):
        for field in ("sdkVersion", "minSdkVersion"):
            with self.subTest(field=field):
                release.check_manifest(self.manifest(min_field=field))

    def test_test_minimum_wrong_target_package_and_abis_are_refused(self):
        for fields in ({"minimum": "30"}, {"target": "34"}, {"abi": "'x86_64'"},
                       {"abi": "'arm64-v8a' 'x86_64'"}, {"package": "app.inbound.trial"}):
            with self.subTest(fields=fields), self.assertRaises(ValueError):
                release.check_manifest(self.manifest(**fields))

    def test_debuggable_is_refused(self):
        with self.assertRaises(ValueError):
            release.check_manifest(self.manifest() + "application-debuggable\r\n")


class PublicationTests(unittest.TestCase):
    def exercise_sign(self, root, *, fail_verify=False, fail_sign=False, race=False):
        source, key, password = [root / name for name in ("source.apk", "test.jks", "test.pass")]
        for path in (source, key, password):
            path.write_bytes(b"test fixture only")
        target = root / "release.apk"

        def fake_run(args):
            if "badging" in args:
                return ReleaseOutputTests.manifest()
            if "sign" in args:
                pending = Path(args[args.index("--out") + 1])
                pending.write_bytes(b"signed bytes")
                if fail_sign:
                    raise ValueError("signing failed")
            return ""

        def fake_verify(pending, *tools):
            self.assertFalse(target.exists(), "Final filename exposed before verification")
            self.assertEqual(pending.read_bytes(), b"signed bytes")
            if fail_verify:
                raise ValueError("wrong certificate")
            if race:
                target.write_bytes(b"existing output")

        with patch.object(release, "run", side_effect=fake_run), patch.object(release, "verify", side_effect=fake_verify):
            release.sign_release(target, source, key, password, "java", "apksigner", "aapt")
        return target

    def test_final_is_published_only_after_verification(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            target = self.exercise_sign(root)
            self.assertEqual(target.read_bytes(), b"signed bytes")
            self.assertFalse(list(root.glob("*.pending")))

    def test_failure_never_exposes_final_and_cleans_pending(self):
        for failure in ("fail_sign", "fail_verify"):
            with self.subTest(failure=failure), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                with self.assertRaises(ValueError):
                    self.exercise_sign(root, **{failure: True})
                self.assertFalse((root / "release.apk").exists())
                self.assertFalse(list(root.glob("*.pending")))

    def test_racing_existing_artifact_is_preserved(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            with self.assertRaises(FileExistsError):
                self.exercise_sign(root, race=True)
            self.assertEqual((root / "release.apk").read_bytes(), b"existing output")
            self.assertFalse(list(root.glob("*.pending")))


if __name__ == "__main__":
    unittest.main()
