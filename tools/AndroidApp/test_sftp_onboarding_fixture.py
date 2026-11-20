"""Host-only security and listing tests for the SFTP onboarding fixture."""

import importlib.util
from pathlib import Path
import unittest

import paramiko


FIXTURE_PATH = Path(__file__).with_name("sftp-onboarding-fixture.py")
SPEC = importlib.util.spec_from_file_location("sftp_onboarding_fixture", FIXTURE_PATH)
fixture_module = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(fixture_module)


class SftpOnboardingFixtureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.fixture = fixture_module.SftpFixture().start()

    @classmethod
    def tearDownClass(cls):
        cls.fixture.close()

    def connect(self, authenticate=False):
        transport = paramiko.Transport(("127.0.0.1", self.fixture.port))
        transport.start_client(timeout=5)
        if authenticate:
            transport.auth_password(fixture_module.USERNAME, fixture_module.PASSWORD)
        return transport

    def test_no_login_probe_gets_host_key_without_authenticating(self):
        transport = self.connect()
        try:
            self.assertFalse(transport.is_authenticated())
            self.assertEqual(
                fixture_module.fingerprint_sha256(transport.get_remote_server_key()),
                self.fixture.metadata()["hostKeyFingerprint"],
            )
        finally:
            transport.close()

    def test_authenticated_sftp_lists_only_the_read_only_virtual_tree(self):
        transport = self.connect(authenticate=True)
        try:
            self.assertTrue(transport.is_authenticated())
            with paramiko.SFTPClient.from_transport(transport) as sftp:
                self.assertEqual(
                    sorted(sftp.listdir("/")),
                    ["Documents", "Downloads", "welcome.txt"],
                )
                self.assertEqual(sftp.listdir("/Documents"), ["readme.txt"])
                with self.assertRaises(FileNotFoundError):
                    sftp.listdir("/not-a-real-directory")
                with self.assertRaises((IOError, OSError)):
                    sftp.mkdir("/created")
        finally:
            transport.close()

    def test_invalid_credentials_are_rejected(self):
        transport = paramiko.Transport(("127.0.0.1", self.fixture.port))
        try:
            transport.start_client(timeout=5)
            with self.assertRaises(paramiko.AuthenticationException):
                transport.auth_password(fixture_module.USERNAME, "not-the-fixture-password")
            self.assertFalse(transport.is_authenticated())
        finally:
            transport.close()

    def test_exec_and_forwarding_channels_are_not_available(self):
        transport = self.connect(authenticate=True)
        try:
            with self.assertRaises(paramiko.ChannelException):
                transport.open_channel("direct-tcpip", ("127.0.0.1", 22), ("127.0.0.1", 1))
            channel = transport.open_session(timeout=5)
            try:
                with self.assertRaises(paramiko.SSHException):
                    channel.exec_command("whoami")
            finally:
                channel.close()
        finally:
            transport.close()


if __name__ == "__main__":
    unittest.main()
