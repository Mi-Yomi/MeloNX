#!/usr/bin/env bash
# Source tree enters/exits with every locked patch applied. The v12 control
# reverses only c33ebc7e; each dylib is loaded by its own process on real Metal.
set -euo pipefail
SOURCE_ROOT="$1"
WORK_ROOT="$2"
OUTPUT_ROOT="$3"
BACKPORT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROBE_ROOT="$OUTPUT_ROOT/host-import-regression"
mkdir -p "$PROBE_ROOT/control" "$PROBE_ROOT/candidate"
HOST_PATCH="$(python3 - "$BACKPORT_DIR/source-lock.json" <<'PY'
import json, sys
patches = json.load(open(sys.argv[1]))['patches']
assert patches[-1]['upstream_commit'] == 'c33ebc7e1ea491529477c795479ab414b335ab57'
print(patches[-1]['file'])
PY
)"

build_mac_dylib() {
  local variant="$1"
  xcodebuild build -project "$SOURCE_ROOT/MoltenVKPackaging.xcodeproj" \
    -scheme 'MoltenVK Package (macOS only)' -destination 'generic/platform=macOS' \
    -configuration Release -derivedDataPath "$WORK_ROOT/DerivedDataMac" -jobs 3 \
    ARCHS=arm64 ONLY_ACTIVE_ARCH=NO MACOSX_DEPLOYMENT_TARGET=11.0 \
    CODE_SIGNING_ALLOWED=NO CODE_SIGNING_REQUIRED=NO \
    2>&1 | tee "$OUTPUT_ROOT/logs/macos-$variant.log"
  local source="$SOURCE_ROOT/Package/Release/MoltenVK/dynamic/dylib/macOS/libMoltenVK.dylib"
  test -s "$source"
  local dylib="$PROBE_ROOT/$variant/libMoltenVK.dylib"
  cp "$source" "$dylib"
  test "$(xcrun lipo -archs "$dylib")" = arm64
  xcrun install_name_tool -id '@rpath/libMoltenVK.dylib' "$dylib"
  # Apple Silicon requires an intact code signature after install-name changes.
  codesign --force --sign - "$dylib"
  codesign --verify --strict "$dylib"
}

python3 "$BACKPORT_DIR/source_lock.py" "$SOURCE_ROOT" --patches verify
git -C "$SOURCE_ROOT" apply --reverse --check "$BACKPORT_DIR/$HOST_PATCH"
git -C "$SOURCE_ROOT" apply --reverse "$BACKPORT_DIR/$HOST_PATCH"
# Restore the candidate source even if the control build fails.
trap 'git -C "$SOURCE_ROOT" apply "$BACKPORT_DIR/$HOST_PATCH"' EXIT
build_mac_dylib control
git -C "$SOURCE_ROOT" apply --check "$BACKPORT_DIR/$HOST_PATCH"
git -C "$SOURCE_ROOT" apply "$BACKPORT_DIR/$HOST_PATCH"
trap - EXIT
build_mac_dylib candidate
python3 "$BACKPORT_DIR/source_lock.py" "$SOURCE_ROOT" --patches verify
xcrun --sdk macosx clang++ -std=c++17 -fobjc-arc -arch arm64 \
  -I "$SOURCE_ROOT/External/Vulkan-Headers/include" \
  "$BACKPORT_DIR/host_import_probe.mm" -framework Foundation -framework Metal \
  -framework CoreGraphics -o "$PROBE_ROOT/host_import_probe" \
  2>&1 | tee "$OUTPUT_ROOT/logs/host-import-probe-compile.log"
python3 "$BACKPORT_DIR/run_host_import_probe.py" "$OUTPUT_ROOT" "$BACKPORT_DIR/source-lock.json"
