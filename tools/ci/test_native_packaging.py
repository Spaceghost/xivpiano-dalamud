#!/usr/bin/env python3
"""Regression tests for mandatory native deployment assets; no compiler is needed."""
import importlib.util
import os
from pathlib import Path
import shutil
import struct
import subprocess
import tempfile
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("native_package", ROOT / "tools/check-native-package.py")
native_package = importlib.util.module_from_spec(spec)
spec.loader.exec_module(native_package)


class NativePackageTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = Path(self.temp.name)
        dll = self.path / native_package.DLL
        dll.parent.mkdir(parents=True)
        data = bytearray(128)
        data[:2] = b"MZ"
        struct.pack_into("<I", data, 60, 64)
        data[64:68] = b"PE\0\0"
        struct.pack_into("<H", data, 68, 0x8664)
        struct.pack_into("<H", data, 86, 0x2000)
        dll.write_bytes(data)
        self.notices = self.path / "licenses/melodica"
        self.notices.mkdir(parents=True)
        for name in native_package.NOTICES:
            shutil.copyfile(ROOT / "native/vendor/melodica" / name, self.notices / name)

    def test_complete_directory_and_extracted_zip(self):
        native_package.check(self.path)
        archive = self.path / "plugin.zip"
        with zipfile.ZipFile(archive, "w") as out:
            for path in self.path.rglob("*"):
                if path.is_file() and path != archive:
                    out.write(path, path.relative_to(self.path))
        native_package.check(archive)

    def test_missing_bridge_fails(self):
        (self.path / native_package.DLL).unlink()
        with self.assertRaises(OSError):
            native_package.check(self.path)

    def test_package_script_requires_native_assets_and_keeps_notices(self):
        for name in ("XivPiano.dll", "XivPiano.Core.dll", "NAudio.Core.dll", "NAudio.Midi.dll",
                     "NAudio.WinMM.dll", "NLayer.dll", "System.Numerics.Tensors.dll"):
            (self.path / name).write_bytes(b"packaging fixture")
        out = self.path / "release"
        env = dict(os.environ, BIN=str(self.path), OUT=str(out), SOURCE_DATE_EPOCH="0", RELEASE_TAG="")
        result = subprocess.run(["bash", str(ROOT / "tools/package.sh")], env=env,
                                capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        native_package.check(out / "latest.zip")
        (self.path / native_package.DLL).unlink()
        result = subprocess.run(["bash", str(ROOT / "tools/package.sh")], env=env,
                                capture_output=True, text=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("native package:", result.stderr)

    def test_missing_or_changed_notice_fails(self):
        for name in native_package.NOTICES:
            with self.subTest(name=name):
                file = self.notices / name
                saved = file.read_bytes()
                file.unlink()
                with self.assertRaises(OSError):
                    native_package.check(self.path)
                file.write_bytes(saved)
        (self.notices / "LICENSE.libpiano").write_text("changed")
        with self.assertRaisesRegex(ValueError, "hash mismatch"):
            native_package.check(self.path)

    def test_wrong_architecture_or_non_dll_fails(self):
        dll = self.path / native_package.DLL
        data = bytearray(dll.read_bytes())
        struct.pack_into("<H", data, 68, 0x14c)
        dll.write_bytes(data)
        with self.assertRaisesRegex(ValueError, "AMD64"):
            native_package.check(self.path)
        struct.pack_into("<H", data, 68, 0x8664)
        struct.pack_into("<H", data, 86, 0)
        dll.write_bytes(data)
        with self.assertRaisesRegex(ValueError, "DLL"):
            native_package.check(self.path)

    def test_linux_library_in_windows_zip_fails(self):
        archive = self.path / "plugin.zip"
        with zipfile.ZipFile(archive, "w") as out:
            for path in self.path.rglob("*"):
                if path.is_file() and path != archive:
                    out.write(path, path.relative_to(self.path))
            out.writestr("libxivpiano_melodica.so", b"ELF")
        with self.assertRaisesRegex(ValueError, "Linux"):
            native_package.check(archive)


if __name__ == "__main__":
    unittest.main()
