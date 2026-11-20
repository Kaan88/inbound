"""Loopback-only, read-only Paramiko SFTP fixture for Android onboarding proof."""

import argparse
import base64
import hashlib
import json
import posixpath
import socketserver
import stat
import threading

import paramiko
from paramiko.sftp import CMD_EXTENDED, CMD_EXTENDED_REPLY, CMD_INIT, CMD_VERSION, SFTPError


USERNAME = "android-proof"
PASSWORD = "fixture-only-password"
STATVFS_EXTENSION = "statvfs@openssh.com"
BLOCK_SIZE = 4096
TOTAL_BLOCKS = 100_000_000
FREE_BLOCKS = 50_000_000
FREE_BYTES = BLOCK_SIZE * FREE_BLOCKS
VIRTUAL_TREE = {
    "/": (("Documents", True, 0), ("Downloads", True, 0), ("welcome.txt", False, 32)),
    "/Documents": (("readme.txt", False, 48),),
    "/Downloads": (),
}


def fingerprint_sha256(key):
    digest = hashlib.sha256(key.asbytes()).digest()
    return "SHA256:" + base64.b64encode(digest).decode("ascii").rstrip("=")


class ReadOnlySftp(paramiko.SFTPServerInterface):
    """A fixed virtual tree; no host paths are read or exposed."""

    def _virtual_path(self, path):
        normalized = posixpath.normpath("/" + str(path).lstrip("/"))
        if normalized in VIRTUAL_TREE:
            return normalized
        parent, _, name = normalized.rpartition("/")
        if any(
            child_name == name
            for child_name, _is_directory, _size in VIRTUAL_TREE.get(parent or "/", ())
        ):
            return normalized
        return None

    def list_folder(self, path):
        virtual_path = self._virtual_path(path)
        entries = VIRTUAL_TREE.get(virtual_path)
        if entries is None:
            return paramiko.SFTP_NO_SUCH_FILE

        result = []
        for name, is_directory, size in entries:
            attrs = paramiko.SFTPAttributes()
            attrs.filename = name
            attrs.st_mode = (stat.S_IFDIR | 0o555) if is_directory else (stat.S_IFREG | 0o444)
            attrs.st_size = size
            result.append(attrs)
        return result

    def stat(self, path):
        return self._attributes(path)

    def lstat(self, path):
        return self._attributes(path)

    def _attributes(self, path):
        virtual_path = self._virtual_path(path)
        if virtual_path is None:
            return paramiko.SFTP_NO_SUCH_FILE
        if virtual_path in VIRTUAL_TREE:
            attrs = paramiko.SFTPAttributes()
            attrs.st_mode = stat.S_IFDIR | 0o555
            attrs.st_size = 0
            return attrs
        parent, _, name = virtual_path.rpartition("/")
        for child_name, is_directory, size in VIRTUAL_TREE.get(parent or "/", ()):
            if child_name == name:
                attrs = paramiko.SFTPAttributes()
                attrs.st_mode = (stat.S_IFDIR | 0o555) if is_directory else (stat.S_IFREG | 0o444)
                attrs.st_size = size
                return attrs
        return paramiko.SFTP_NO_SUCH_FILE

    def open(self, path, flags, attr):
        return paramiko.SFTP_PERMISSION_DENIED

    def remove(self, path):
        return paramiko.SFTP_PERMISSION_DENIED

    def rename(self, oldpath, newpath):
        return paramiko.SFTP_PERMISSION_DENIED

    def mkdir(self, path, attr):
        return paramiko.SFTP_PERMISSION_DENIED

    def rmdir(self, path):
        return paramiko.SFTP_PERMISSION_DENIED

    def chattr(self, path, attr):
        return paramiko.SFTP_PERMISSION_DENIED


class CapacitySftpServer(paramiko.SFTPServer):
    """Expose a synthetic filesystem capacity over the authenticated SFTP channel."""

    def _send_server_version(self):
        packet, data = self._read_packet()
        if packet != CMD_INIT or len(data) < 4:
            raise SFTPError("Invalid SFTP initialization")
        version = paramiko.Message(data).get_int()
        reply = paramiko.Message()
        reply.add_int(3)
        reply.add("check-file", "md5,sha1", STATVFS_EXTENSION, "2")
        self._send_packet(CMD_VERSION, reply)
        return version

    def _process(self, packet, request_number, message):
        if packet != CMD_EXTENDED:
            return super()._process(packet, request_number, message)
        extension = paramiko.Message(message.get_remainder())
        if extension.get_text() != STATVFS_EXTENSION:
            return super()._process(packet, request_number, message)
        if self.server._virtual_path(extension.get_text()) not in VIRTUAL_TREE:
            return self._send_status(request_number, paramiko.SFTP_NO_SUCH_FILE)
        reply = paramiko.Message()
        reply.add_int(request_number)
        fields = (
            BLOCK_SIZE, BLOCK_SIZE, TOTAL_BLOCKS, FREE_BLOCKS, FREE_BLOCKS,
            16, 8, 8, 1, 1, 255,
        )
        for field in fields:
            reply.add_int64(field)
        self._send_packet(CMD_EXTENDED_REPLY, reply)


class FixtureSshServer(paramiko.ServerInterface):
    def __init__(self, authenticated):
        self.authenticated = authenticated

    def check_auth_password(self, username, password):
        if username == USERNAME and password == PASSWORD:
            self.authenticated.set()
            return paramiko.AUTH_SUCCESSFUL
        return paramiko.AUTH_FAILED

    def get_allowed_auths(self, username):
        return "password"

    def check_channel_request(self, kind, channel_id):
        if kind == "session":
            return paramiko.OPEN_SUCCEEDED
        return paramiko.OPEN_FAILED_ADMINISTRATIVELY_PROHIBITED

    def check_channel_shell_request(self, channel):
        return False

    def check_channel_exec_request(self, channel, command):
        return False


class _RequestHandler(socketserver.BaseRequestHandler):
    def handle(self):
        transport = paramiko.Transport(self.request)
        transport.add_server_key(self.server.host_key)
        handler = CapacitySftpServer if self.server.provide_capacity else paramiko.SFTPServer
        transport.set_subsystem_handler("sftp", handler, ReadOnlySftp)
        try:
            transport.start_server(server=FixtureSshServer(self.server.authenticated))
            transport.join()
        except (EOFError, OSError, paramiko.SSHException):
            pass
        finally:
            transport.close()


class _LoopbackTcpServer(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True
    address_family = socketserver.socket.AF_INET

    def __init__(self, address, host_key, provide_capacity):
        self.host_key = host_key
        self.provide_capacity = provide_capacity
        self.authenticated = threading.Event()
        super().__init__(address, _RequestHandler)


class SftpFixture:
    def __init__(self, port=0, *, provide_capacity=False):
        self.host_key = paramiko.RSAKey.generate(2048)
        self.server = _LoopbackTcpServer(("127.0.0.1", port), self.host_key, provide_capacity)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)

    @property
    def port(self):
        return self.server.server_address[1]

    def start(self):
        self.thread.start()
        return self

    def close(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=5)

    def metadata(self):
        return {
            "host": "127.0.0.1",
            "port": self.port,
            "username": USERNAME,
            "password": PASSWORD,
            "hostKeyFingerprint": fingerprint_sha256(self.host_key),
        }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=0, help="TCP port (default: choose an ephemeral port)")
    args = parser.parse_args()
    if not 0 <= args.port <= 65535:
        parser.error("--port must be between 0 and 65535")

    fixture = SftpFixture(args.port).start()
    print(json.dumps(fixture.metadata(), separators=(",", ":")), flush=True)
    try:
        fixture.thread.join()
    except KeyboardInterrupt:
        pass
    finally:
        fixture.close()


if __name__ == "__main__":
    main()
