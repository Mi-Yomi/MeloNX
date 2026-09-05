import struct
import unittest

from verify_macho import inspect


def dylib_name(command, name):
    encoded = name.encode() + b"\0"
    length = (24 + len(encoded) + 7) & ~7
    return struct.pack("<6I", command, length, 24, 0, 0, 0) + encoded + bytes(length - 24 - len(encoded))


def image(cpu=0x0100000C, subtype=0, filetype=6, platform=2, minimum_os=13 << 16,
          identity="@rpath/libMoltenVK.dylib", dependency="/usr/lib/libc++.1.dylib"):
    commands = [dylib_name(0x0D, identity), dylib_name(0x0C, dependency),
                struct.pack("<6I", 0x32, 24, platform, minimum_os, 26 << 16, 0),
                struct.pack("<2I", 0x1B, 24) + bytes(range(16))]
    table = b"".join(commands)
    return struct.pack("<8I", 0xFEEDFACF, cpu, subtype, filetype, len(commands), len(table), 0, 0) + table


class MachoValidationTests(unittest.TestCase):
    def test_accepts_thin_ios_dylib_with_expected_identity_and_uuid(self):
        info = inspect(image())
        self.assertEqual(info["uuid"], "00010203-0405-0607-0809-0A0B0C0D0E0F")
        self.assertEqual(info["install_name"], "@rpath/libMoltenVK.dylib")

    def test_rejects_wrong_architecture_or_arm64e(self):
        for options in ({"cpu": 0x01000007}, {"subtype": 2}):
            with self.subTest(options=options), self.assertRaises(ValueError):
                inspect(image(**options))

    def test_rejects_simulator_macos_or_wrong_minimum_os(self):
        for options in ({"platform": 7}, {"platform": 1}, {"minimum_os": 15 << 16}):
            with self.subTest(options=options), self.assertRaises(ValueError):
                inspect(image(**options))

    def test_rejects_static_executable_or_wrong_install_name(self):
        for options in ({"filetype": 2}, {"identity": "@rpath/MoltenVK.framework/MoltenVK"}):
            with self.subTest(options=options), self.assertRaises(ValueError):
                inspect(image(**options))

    def test_rejects_build_directory_or_unbundled_dependency(self):
        for dependency in ("/Users/runner/build/libSPIRVCross.dylib", "@rpath/unbundled.dylib"):
            with self.subTest(dependency=dependency), self.assertRaises(ValueError):
                inspect(image(dependency=dependency))

    def test_truncated_or_invalid_load_commands_fail_closed(self):
        valid = image()
        for data in (valid[:16], valid[:-1], valid[:36] + struct.pack("<I", 0) + valid[40:]):
            with self.subTest(size=len(data)), self.assertRaises(ValueError):
                inspect(data)


if __name__ == "__main__":
    unittest.main()
