#!/usr/bin/env bash
# Pinned MoltenVK 1.4.0, optionally with the buffer-view autorelease fix.
# Build on Apple Silicon macOS; never overwrites the tracked legacy binary.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPOSITORY_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
BACKPORT_DIR="$SCRIPT_DIR/moltenvk-backports"
OUTPUT_ROOT="${MELO_MVK_OUTPUT_DIR:-$REPOSITORY_ROOT/artifacts/moltenvk}"
VARIANT="${MELO_MVK_VARIANT:-patched}"
case "$VARIANT" in patched|baseline) ;; *) echo 'MELO_MVK_VARIANT must be patched or baseline.' >&2; exit 1 ;; esac
if [[ "$(uname -s)" != Darwin || "$(uname -m)" != arm64 ]]; then
  echo 'MoltenVK source build requires an Apple Silicon Mac with Xcode.' >&2
  exit 1
fi
for tool in git python3 xcodebuild xcrun shasum; do command -v "$tool" >/dev/null; done
mkdir -p "$OUTPUT_ROOT/logs"
OUTPUT_ROOT="$(cd "$OUTPUT_ROOT" && pwd)"
exec > >(tee "$OUTPUT_ROOT/logs/build-driver.log") 2>&1
WORK_ROOT="$(mktemp -d "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/melonx-moltenvk.XXXXXX")"
SOURCE_ROOT="$WORK_ROOT/MoltenVK"
export DEVELOPER_DIR="${DEVELOPER_DIR:-$(xcode-select -p)}"
STAGE=fetch-source
trap 'result=$?; printf "stage=%s\nexit_code=%s\n" "$STAGE" "$result" > "$OUTPUT_ROOT/build-status.txt"' EXIT

read -r SOURCE_URL SOURCE_REVISION < <(python3 - "$BACKPORT_DIR/source-lock.json" <<'PY'
import json, sys
source = json.load(open(sys.argv[1]))['source']
print(source['url'], source['revision'])
PY
)
git init "$SOURCE_ROOT"
git -C "$SOURCE_ROOT" remote add origin "$SOURCE_URL"
git -C "$SOURCE_ROOT" fetch --depth=1 origin "$SOURCE_REVISION"
git -C "$SOURCE_ROOT" checkout --detach FETCH_HEAD
test "$(git -C "$SOURCE_ROOT" rev-parse HEAD)" = "$SOURCE_REVISION"

STAGE=fetch-locked-dependencies
# Seed exact shallow revisions. Upstream fetchDependencies then finds them
# locally rather than downloading the full history of seven repositories.
python3 "$BACKPORT_DIR/source_lock.py" "$SOURCE_ROOT" --fetch
if [[ "$VARIANT" == patched ]]; then
  git -C "$SOURCE_ROOT" apply --check "$BACKPORT_DIR/0001-buffer-view-autoreleasepool.patch"
  git -C "$SOURCE_ROOT" apply "$BACKPORT_DIR/0001-buffer-view-autoreleasepool.patch"
fi
git -C "$SOURCE_ROOT" diff --check

STAGE=build-dependencies
(
  cd "$SOURCE_ROOT"
  # No live revisions, global compiler defines, or prebuilt replacement dylibs.
  ./fetchDependencies --ios --no-parallel-build
) 2>&1 | tee "$OUTPUT_ROOT/logs/dependencies.log"
python3 "$BACKPORT_DIR/source_lock.py" "$SOURCE_ROOT" > "$OUTPUT_ROOT/logs/dependency-revisions.txt"

STAGE=build-moltenvk
xcodebuild build -project "$SOURCE_ROOT/MoltenVKPackaging.xcodeproj" \
  -scheme 'MoltenVK Package (iOS only)' -destination 'generic/platform=iOS' \
  -configuration Release -derivedDataPath "$WORK_ROOT/DerivedData" -jobs 3 \
  ARCHS=arm64 ONLY_ACTIVE_ARCH=NO IPHONEOS_DEPLOYMENT_TARGET=13.0 \
  CODE_SIGNING_ALLOWED=NO CODE_SIGNING_REQUIRED=NO \
  GCC_GENERATE_DEBUGGING_SYMBOLS=YES DEBUG_INFORMATION_FORMAT=dwarf-with-dsym \
  STRIP_INSTALLED_PRODUCT=NO COPY_PHASE_STRIP=NO \
  2>&1 | tee "$OUTPUT_ROOT/logs/xcodebuild.log"

STAGE=validate-abi
NATIVE_INCLUDE="$SOURCE_ROOT/Package/Release/MoltenVK/include"
xcrun --sdk iphoneos clang -target arm64-apple-ios13.0 -std=c11 -c \
  -I "$NATIVE_INCLUDE" -I "$SOURCE_ROOT/External/Vulkan-Headers/include" \
  "$BACKPORT_DIR/mvk_abi_probe.c" -o "$OUTPUT_ROOT/mvk_abi_probe.o" \
  2>&1 | tee "$OUTPUT_ROOT/logs/abi-probe.log"

STAGE=package-dylib
FRAMEWORK="$SOURCE_ROOT/Package/Release/MoltenVK/dynamic/MoltenVK.xcframework/ios-arm64/MoltenVK.framework"
test -s "$FRAMEWORK/MoltenVK"
DYLIB="$OUTPUT_ROOT/libMoltenVK.dylib"
cp "$FRAMEWORK/MoltenVK" "$DYLIB"
xcrun install_name_tool -id '@rpath/libMoltenVK.dylib' "$DYLIB"
# Preserve full external dSYM information while keeping the embedded release
# binary's local/debug symbol tables small. Export checks run after stripping.
xcrun strip -S -x "$DYLIB"
python3 "$BACKPORT_DIR/verify_macho.py" "$DYLIB" > "$OUTPUT_ROOT/macho.json"
xcrun nm -gU "$DYLIB" > "$OUTPUT_ROOT/logs/exported-symbols.txt"
for symbol in vkGetInstanceProcAddr vkGetDeviceProcAddr vkGetMoltenVKConfigurationMVK vkSetMoltenVKConfigurationMVK; do
  if ! awk -v expected="_$symbol" '$NF == expected { found=1 } END { exit !found }' "$OUTPUT_ROOT/logs/exported-symbols.txt"; then
    echo "Required exported ABI is missing: $symbol" >&2
    exit 1
  fi
done
xcrun otool -L "$DYLIB" > "$OUTPUT_ROOT/logs/dylib-dependencies.txt"

STAGE=package-symbols
# Xcode's framework packaging does not carry dSYMs into the iOS XCFramework.
# Retain the product dSYM from this exact build and validate its UUID, rather
# than synthesizing a symbol-less dSYM from an already stripped binary.
DSYM_SOURCE="$(python3 - "$WORK_ROOT/DerivedData" "$OUTPUT_ROOT/macho.json" <<'PY'
import json, pathlib, re, struct, subprocess, sys
expected = json.load(open(sys.argv[2]))['uuid']
for candidate in sorted(pathlib.Path(sys.argv[1]).rglob('MoltenVK.framework.dSYM')):
    result = subprocess.run(['xcrun', 'dwarfdump', '--uuid', str(candidate)], capture_output=True, text=True, check=True)
    matches = re.findall(r'UUID: ([0-9A-Fa-f-]+) \(arm64\)', result.stdout)
    if len(matches) != 1 or matches[0].upper() != expected:
        continue
    # Reject a UUID-only, symbol-less dSYM. Release in upstream 1.4.0 normally
    # disables debug generation; our explicit build setting must take effect.
    dwarf = candidate / 'Contents/Resources/DWARF/MoltenVK'
    data = dwarf.read_bytes()
    if len(data) < 32 or struct.unpack_from('<I', data)[0] != 0xFEEDFACF:
        continue
    count, size = struct.unpack_from('<2I', data, 16)
    offset, end = 32, 32 + size
    has_debug_info = False
    for _ in range(count):
        if offset + 8 > min(end, len(data)):
            raise SystemExit('Truncated dSYM load commands')
        command, length = struct.unpack_from('<2I', data, offset)
        if length < 8 or offset + length > min(end, len(data)):
            raise SystemExit('Invalid dSYM load command')
        if command == 0x19:
            if length < 72:
                raise SystemExit('Truncated dSYM segment')
            sections = struct.unpack_from('<I', data, offset + 64)[0]
            if 72 + sections * 80 > length:
                raise SystemExit('Truncated dSYM sections')
            for i in range(sections):
                section = offset + 72 + i * 80
                if data[section:section + 16].split(b'\0', 1)[0] == b'__debug_info':
                    section_size = struct.unpack_from('<Q', data, section + 40)[0]
                    section_offset = struct.unpack_from('<I', data, section + 48)[0]
                    has_debug_info = section_size > 0 and section_offset + section_size <= len(data)
        offset += length
    if has_debug_info:
        print(candidate)
        break
else:
    raise SystemExit('No matching arm64 MoltenVK dSYM with native debug information')
PY
)"
DSYM="$OUTPUT_ROOT/libMoltenVK.dylib.dSYM"
test ! -e "$DSYM"
cp -R "$DSYM_SOURCE" "$DSYM"
xcrun dwarfdump --uuid "$DSYM" > "$OUTPUT_ROOT/logs/dsym-uuid.txt"
python3 - "$OUTPUT_ROOT/macho.json" "$OUTPUT_ROOT/logs/dsym-uuid.txt" <<'PY'
import json, re, sys
binary = json.load(open(sys.argv[1]))
matches = re.findall(r'UUID: ([0-9A-Fa-f-]+) \(arm64\)', open(sys.argv[2]).read())
if len(matches) != 1 or matches[0].upper() != binary['uuid']:
    raise SystemExit('MoltenVK dylib/dSYM UUID mismatch')
PY
ditto -c -k --sequesterRsrc --keepParent "$DSYM" "$OUTPUT_ROOT/libMoltenVK.dylib.dSYM.zip"

STAGE=archive-source-and-provenance
python3 "$BACKPORT_DIR/source_lock.py" "$SOURCE_ROOT" --archive "$OUTPUT_ROOT/source-repositories"
cp -R "$BACKPORT_DIR" "$OUTPUT_ROOT/backports"
cp "$BACKPORT_DIR/source-lock.json" "$OUTPUT_ROOT/source-lock.json"
git -C "$SOURCE_ROOT" diff --binary HEAD > "$OUTPUT_ROOT/applied-source.patch"
if [[ "$VARIANT" == patched ]]; then
  git -C "$SOURCE_ROOT" apply --reverse --check "$BACKPORT_DIR/0001-buffer-view-autoreleasepool.patch"
  test "$(git -C "$SOURCE_ROOT" diff --name-only HEAD)" = 'MoltenVK/MoltenVK/GPUObjects/MVKBuffer.mm'
else
  git -C "$SOURCE_ROOT" diff --exit-code HEAD
fi
xcodebuild -version > "$OUTPUT_ROOT/logs/xcode-version.txt"
xcrun --sdk iphoneos --show-sdk-version > "$OUTPUT_ROOT/logs/iphoneos-sdk-version.txt"
xcrun clang --version > "$OUTPUT_ROOT/logs/clang-version.txt"
python3 - "$OUTPUT_ROOT" "$REPOSITORY_ROOT" "$VARIANT" <<'PY'
import datetime, hashlib, json, pathlib, subprocess, sys
out, repository, variant = pathlib.Path(sys.argv[1]), sys.argv[2], sys.argv[3]
lock = json.loads((out / 'source-lock.json').read_text())
manifest = {
    'schema': 1, 'variant': variant, 'moltenvk_version': lock['version'],
    'moltenvk_source_commit': lock['source']['revision'],
    'melonx_source_commit': subprocess.check_output(['git', '-C', repository, 'rev-parse', 'HEAD'], text=True).strip(),
    'created_utc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'configuration_prefix_bytes': 152, 'runtime_device_test': 'required',
    'xcode': (out / 'logs/xcode-version.txt').read_text().strip(),
    'iphoneos_sdk': (out / 'logs/iphoneos-sdk-version.txt').read_text().strip(),
    'clang': (out / 'logs/clang-version.txt').read_text().strip(),
    'binary': json.loads((out / 'macho.json').read_text()),
    'applied_upstream_commits': [p['upstream_commit'] for p in lock['patches']] if variant == 'patched' else [],
    'files': {str(p.relative_to(out)): hashlib.sha256(p.read_bytes()).hexdigest()
              for p in sorted(out.rglob('*')) if p.is_file() and p.name not in ('build-manifest.json', 'build-driver.log', 'build-status.txt')},
}
(out / 'build-manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
PY
STAGE=complete
printf 'Built %s MoltenVK: %s\n' "$VARIANT" "$DYLIB"
