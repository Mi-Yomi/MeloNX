#!/usr/bin/env python3
"""Validate the exact dylib that MeloNX will embed, independently of file/otool text."""
import argparse
import hashlib
import json
from pathlib import Path
import struct
import uuid


def require(condition, reason):
    if not condition:
        raise ValueError(reason)


def inspect(data):
    require(len(data) >= 32, "Truncated Mach-O header")
    magic, cpu, subtype, filetype, count, size, flags, reserved = struct.unpack_from("<8I", data)
    require(magic == 0xFEEDFACF, "Expected a thin 64-bit little-endian Mach-O")
    require(cpu == 0x0100000C and subtype & 0xFFFFFF == 0, "Expected arm64, not arm64e or another architecture")
    require(filetype == 6, "Expected MH_DYLIB")
    require(size <= len(data) - 32 and count <= size // 8, "Invalid load-command header")
    offset, end = 32, 32 + size
    identity, platform, minimum_os, binary_uuid = None, None, None, None
    dependencies = []
    for _ in range(count):
        require(offset + 8 <= end, "Truncated load command")
        command, length = struct.unpack_from("<2I", data, offset)
        require(length >= 8 and length % 8 == 0 and offset + length <= end, "Invalid load-command size")
        if command in (0x0D, 0x0C, 0x80000018, 0x8000001F, 0x20, 0x80000023):
            require(length >= 24, "Truncated dylib command")
            name_offset = struct.unpack_from("<I", data, offset + 8)[0]
            require(24 <= name_offset < length, "Invalid dylib name offset")
            raw = data[offset + name_offset:offset + length]
            require(b"\0" in raw, "Unterminated dylib name")
            name = raw.split(b"\0", 1)[0].decode("utf-8")
            if command == 0x0D:
                require(identity is None, "Duplicate install name")
                identity = name
            else:
                require(name.startswith(("/System/Library/", "/usr/lib/")), "Unexpected external dependency: " + name)
                dependencies.append(name)
        elif command == 0x32:
            require(length >= 24, "Truncated build-version command")
            platform, minimum_os = struct.unpack_from("<2I", data, offset + 8)
        elif command == 0x1B:
            require(length == 24, "Invalid UUID command")
            binary_uuid = str(uuid.UUID(bytes=data[offset + 8:offset + 24])).upper()
        offset += length
    require(offset == end, "Load commands do not fill the declared table")
    require(platform == 2, "Expected iPhoneOS, not simulator/macOS/Catalyst")
    require(minimum_os == 13 << 16, "Expected pinned iOS 13.0 minimum deployment target")
    require(identity == "@rpath/libMoltenVK.dylib", "MeloNX's expected install name is missing")
    require(binary_uuid is not None, "Missing LC_UUID needed for crash symbolication")
    return {"architecture": "arm64", "platform": "iPhoneOS", "minimum_os": "13.0",
            "install_name": identity, "uuid": binary_uuid, "dependencies": dependencies,
            "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("dylib", type=Path)
    args = parser.parse_args()
    print(json.dumps(inspect(args.dylib.read_bytes()), indent=2))


if __name__ == "__main__":
    main()
