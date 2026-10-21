#!/usr/bin/env python3
"""Require the Windows x64 bridge and its original license/provenance files."""
import hashlib
import json
from pathlib import Path
import struct
import sys
import zipfile

DLL = "runtimes/win-x64/native/xivpiano_melodica.dll"
NOTICES = ("LICENSE.libpiano", "THIRD_PARTY_NOTICES.md", "SOURCE.json")


def check(path):
    if path.is_dir():
        read = lambda name: (path / name).read_bytes()
        names = [str(p.relative_to(path)) for p in path.rglob("*") if p.is_file()]
        archive = None
    else:
        archive = zipfile.ZipFile(path)
        read = archive.read
        names = archive.namelist()
    try:
        data = read(DLL)
        if len(data) < 64 or data[:2] != b"MZ":
            raise ValueError("native bridge is not a Windows PE file")
        offset = struct.unpack_from("<I", data, 60)[0]
        if data[offset:offset + 4] != b"PE\0\0" or len(data) < offset + 24:
            raise ValueError("native bridge has an invalid PE header")
        machine, = struct.unpack_from("<H", data, offset + 4)
        flags, = struct.unpack_from("<H", data, offset + 22)
        if machine != 0x8664 or not flags & 0x2000:
            raise ValueError("native bridge must be an AMD64 Windows DLL")
        base = "licenses/melodica/"
        record = json.loads(read(base + "SOURCE.json"))
        if record["ref"] != "v1.0.0" or record["commit"] != "7e5ed58ef53b04ea98b9e6d26cbb1d0236521ccc":
            raise ValueError("unexpected vendored melodica revision")
        for name in NOTICES[:-1]:
            if hashlib.sha256(read(base + name)).hexdigest() != record["files"][name]:
                raise ValueError("native license/provenance hash mismatch: " + name)
        if archive and any(name.endswith(".so") for name in names):
            raise ValueError("Linux shared library must not enter the Windows plugin ZIP")
    finally:
        if archive:
            archive.close()


if __name__ == "__main__":
    try:
        check(Path(sys.argv[1]))
    except (OSError, KeyError, ValueError, struct.error, zipfile.BadZipFile) as error:
        raise SystemExit("native package: " + str(error))
    print("Required Windows x64 bridge, licenses and pinned provenance verified")
