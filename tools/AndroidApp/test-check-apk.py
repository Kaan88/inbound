"""Checks for check-apk.py's ELF alignment test. Run: python -I tools/AndroidApp/test-check-apk.py"""
import importlib.util
import io
from pathlib import Path
import struct
import sys
import unittest
import zipfile

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location("check_apk", Path(__file__).with_name("check-apk.py"))
check = importlib.util.module_from_spec(spec)
spec.loader.exec_module(check)


def elf(aligns, extra_type=None):
    """Minimal ELF64 LE: header plus one program header per align (PT_LOAD), plus an optional non-LOAD one."""
    types = [1] * len(aligns) + ([extra_type] if extra_type else [])
    vals = list(aligns) + ([0x8] if extra_type else [])
    h = bytearray(0x40)
    h[:6] = b"\x7fELF\x02\x01"
    struct.pack_into("<Q", h, 0x20, 0x40)
    struct.pack_into("<HH", h, 0x36, 0x38, len(types))
    for t, a in zip(types, vals):
        ph = bytearray(0x38)
        struct.pack_into("<I", ph, 0, t)
        struct.pack_into("<Q", ph, 0x30, a)
        h += ph
    return bytes(h)


def zip_of(files):
    b = io.BytesIO()
    with zipfile.ZipFile(b, "w") as z:
        for n, d in files.items(): z.writestr(n, d)
    return zipfile.ZipFile(io.BytesIO(b.getvalue()))


class ElfAlignmentTests(unittest.TestCase):
    def test_reads_only_pt_load_alignments(self):
        self.assertEqual(check.elf_load_alignments(elf([0x4000, 0x10000], extra_type=6)), [0x4000, 0x10000])

    def test_4k_library_is_reported_with_name_and_alignment(self):
        z = zip_of({"lib/arm64-v8a/good.so": elf([0x4000, 0x4000]), "lib/arm64-v8a/bad.so": elf([0x4000, 0x1000])})
        self.assertEqual(check.unaligned_libraries(z), [("lib/arm64-v8a/bad.so", "0x1000")])

    def test_aligned_libraries_pass_and_other_abis_are_ignored(self):
        z = zip_of({"lib/arm64-v8a/a.so": elf([0x10000]), "lib/x86_64/old.so": elf([0x1000])})
        self.assertEqual(check.unaligned_libraries(z), [])

    def test_non_elf_library_fails(self):
        self.assertEqual(len(check.unaligned_libraries(zip_of({"lib/arm64-v8a/x.so": b"nope"}))), 1)

    def test_hook_list_has_the_network_rule_hooks(self):
        self.assertIn("metered_override", check.HOOKS)
        self.assertIn("wifi_only", check.HOOKS)


if __name__ == "__main__":
    unittest.main()
