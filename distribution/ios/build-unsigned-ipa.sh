#!/usr/bin/env bash
# Produces an unprovisioned IPA. A sideload tool must re-sign it for the device.
set -euo pipefail

REPOSITORY_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$REPOSITORY_ROOT"
OUTPUT_ROOT="$REPOSITORY_ROOT/artifacts/ios"
mkdir -p "$OUTPUT_ROOT/logs"
exec > >(tee "$OUTPUT_ROOT/logs/build-driver.log") 2>&1
BUILD_INFO="$OUTPUT_ROOT/build-info.txt"
BUILD_STAGE=preflight
trap 'result=$?; printf "last_stage=%s\nexit_code=%s\n" "$BUILD_STAGE" "$result" >> "$BUILD_INFO"' EXIT

if [[ "$(uname -s)" != Darwin || "$(uname -m)" != arm64 ]]; then
  echo 'This script requires an Apple Silicon Mac with Xcode 26.2 or newer.' >&2
  exit 1
fi

export DEVELOPER_DIR="${DEVELOPER_DIR:-$(xcode-select -p)}"
if [[ ! -d "$DEVELOPER_DIR" ]]; then
  echo "Selected Xcode is unavailable: $DEVELOPER_DIR" >&2
  exit 1
fi
XCODE_VERSION="$(xcodebuild -version | awk '/^Xcode / { print $2 }')"
if ! awk -v version="$XCODE_VERSION" 'BEGIN { split(version, parts, "."); exit !(parts[1] > 26 || (parts[1] == 26 && parts[2] >= 2)) }'; then
  echo "Xcode 26.2 or newer is required; found $XCODE_VERSION." >&2
  exit 1
fi
export DOTNET="${DOTNET:-$(command -v dotnet || true)}"
if [[ -z "$DOTNET" || ! -x "$DOTNET" || "$("$DOTNET" --version)" != 10.0.400 ]]; then
  echo 'Install .NET SDK 10.0.400 for macOS ARM64 and put its dotnet executable on PATH.' >&2
  exit 1
fi
if [[ -n "$(git status --porcelain --untracked-files=normal)" ]]; then
  echo 'Commit source changes before packaging so the IPA and source archive identify the same revision.' >&2
  exit 1
fi

SOURCE_SHA="$(git rev-parse HEAD)"
SHORT_SHA="${SOURCE_SHA:0:12}"
WORK_ROOT="$(mktemp -d "$OUTPUT_ROOT/work.XXXXXX")"
PROJECT="$REPOSITORY_ROOT/src/MeloNX/MeloNX.xcodeproj"
PACKAGE_LOCK="$PROJECT/project.xcworkspace/xcshareddata/swiftpm/Package.resolved"
ENTITLEMENTS="$REPOSITORY_ROOT/src/MeloNX/MeloNX/MeloNX.entitlements"
SOURCE_MOLTENVK="$REPOSITORY_ROOT/src/MeloNX/MeloNX/Dependencies/libMoltenVK.dylib"
EXPECTED_MOLTENVK_SHA256="5735ca4aee60ed7e0475b80b8ecdcfc952e4e0b7d49018f11781e51e5106bfa5"
export MELO_NX_NATIVE_BINLOG="$OUTPUT_ROOT/logs/native-publish.binlog"

{
  printf 'source_commit=%s\nstarted_utc=%s\n' "$SOURCE_SHA" "$(date -u '+%Y-%m-%dT%H:%M:%SZ')"
  printf 'package_status=unprovisioned; re-signing required\n'
  printf 'signature=ad-hoc main executable, carrying project entitlements\n'
  printf 'developer_dir=%s\nwork_directory=%s\n' "$DEVELOPER_DIR" "$WORK_ROOT"
  sw_vers
  xcodebuild -version
  xcrun --sdk iphoneos --show-sdk-version
  "$DOTNET" --info
} >> "$BUILD_INFO" 2>&1
cp "$PACKAGE_LOCK" "$OUTPUT_ROOT/Package.resolved"
cp "$ENTITLEMENTS" "$OUTPUT_ROOT/MeloNX.entitlements"
cp LICENSE.txt "$OUTPUT_ROOT/LICENSE.txt"
cp distribution/legal/THIRDPARTY.md "$OUTPUT_ROOT/THIRDPARTY.txt"
git archive --format=tar.gz --output="$OUTPUT_ROOT/MeloNX-source-$SHORT_SHA.tar.gz" "$SOURCE_SHA"

XCODE_ARGUMENTS=(
  -project "$PROJECT"
  -scheme MeloNX
  -configuration Release
  -destination 'generic/platform=iOS'
  -derivedDataPath "$WORK_ROOT/DerivedData"
  -clonedSourcePackagesDirPath "$WORK_ROOT/SourcePackages"
  -disableAutomaticPackageResolution
)

BUILD_STAGE=resolve-swift-packages
xcodebuild "${XCODE_ARGUMENTS[@]}" -resolvePackageDependencies 2>&1 |
  tee "$OUTPUT_ROOT/logs/swift-package-resolution.log"
cmp "$PACKAGE_LOCK" "$OUTPUT_ROOT/Package.resolved"

# MeloNX's Ryujinx legacy target invokes build.sh and dotnet publish. Let publish
# restore its NativeAOT runtime pack; plain dotnet restore uses different inputs.
BUILD_STAGE=compile-native-and-swift
xcodebuild "${XCODE_ARGUMENTS[@]}" build -jobs 2 \
  "CONFIGURATION_BUILD_DIR=$WORK_ROOT/products" \
  "MELO_NX_SOURCE_COMMIT=$SOURCE_SHA" \
  CODE_SIGNING_ALLOWED=NO CODE_SIGNING_REQUIRED=NO CODE_SIGN_IDENTITY= DEVELOPMENT_TEAM= \
  2>&1 | tee "$OUTPUT_ROOT/logs/xcodebuild.log"
cmp "$PACKAGE_LOCK" "$OUTPUT_ROOT/Package.resolved"

BUILD_STAGE=package
APP="$WORK_ROOT/products/MeloNX.app"
test -d "$APP"
test -s "$APP/Frameworks/Ryujinx.Library.dylib"
MOLTENVK="$APP/Frameworks/libMoltenVK.dylib"
test -s "$MOLTENVK"
# A rebuilt driver is a separate, source-locked artifact. Preserve the clean app
# checkout and replace only its packaged dynamic library after the Xcode build.
if [[ -n "${MELO_MVK_ARTIFACT_DIR:-}" ]]; then
  python3 distribution/ios/verify-moltenvk-artifact.py "$MELO_MVK_ARTIFACT_DIR"
  SOURCE_MOLTENVK="$MELO_MVK_ARTIFACT_DIR/libMoltenVK.dylib"
  EXPECTED_MOLTENVK_SHA256="$(shasum -a 256 "$SOURCE_MOLTENVK" | awk '{ print $1 }')"
  cp "$SOURCE_MOLTENVK" "$MOLTENVK"
  tar -C "$MELO_MVK_ARTIFACT_DIR" -czf "$OUTPUT_ROOT/MeloNX-MoltenVK-$SHORT_SHA.tar.gz" .
  printf 'moltenvk_artifact_dir=%s\n' "$MELO_MVK_ARTIFACT_DIR" >> "$BUILD_INFO"
fi
MOLTENVK_SHA256="$(shasum -a 256 "$MOLTENVK" | awk '{ print $1 }')"
test "$MOLTENVK_SHA256" = "$EXPECTED_MOLTENVK_SHA256"
cmp "$SOURCE_MOLTENVK" "$MOLTENVK"
EXECUTABLE="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$APP/Info.plist")"
case "$EXECUTABLE" in
  ''|*/*|.|..) echo 'Invalid CFBundleExecutable in built app.' >&2; exit 1 ;;
esac
xcrun lipo "$APP/$EXECUTABLE" -verify_arch arm64
xcrun lipo "$APP/Frameworks/Ryujinx.Library.dylib" -verify_arch arm64
xcrun lipo "$MOLTENVK" -verify_arch arm64

# Preserve matching symbols outside the IPA. They are required to turn an iOS .ips report
# or NativeAOT offset into an actionable source location, and must match SOURCE_SHA exactly.
BUILD_STAGE=validate-and-package-symbols
SYMBOL_ROOT="$WORK_ROOT/symbols"
mkdir -p "$SYMBOL_ROOT"
SYMBOL_MANIFEST="$SYMBOL_ROOT/symbol-uuids.txt"

arm64_uuid() {
  xcrun dwarfdump --uuid "$1" | awk '$1 == "UUID:" && $3 == "(arm64)" { print toupper($2); exit }'
}

APP_BINARY_UUID="$(arm64_uuid "$APP/$EXECUTABLE")"
NATIVE_BINARY_UUID="$(arm64_uuid "$APP/Frameworks/Ryujinx.Library.dylib")"
MOLTENVK_BINARY_UUID="$(arm64_uuid "$MOLTENVK")"
test -n "$APP_BINARY_UUID"
test -n "$NATIVE_BINARY_UUID"
test -n "$MOLTENVK_BINARY_UUID"

{
  printf 'source_commit=%s\n' "$SOURCE_SHA"
  printf 'app_binary_arm64_uuid=%s\n' "$APP_BINARY_UUID"
  printf 'native_binary_arm64_uuid=%s\n' "$NATIVE_BINARY_UUID"
  printf 'moltenvk_binary_arm64_uuid=%s\n' "$MOLTENVK_BINARY_UUID"
} > "$SYMBOL_MANIFEST"

APP_DSYM="$WORK_ROOT/products/MeloNX.app.dSYM"
if [[ ! -d "$APP_DSYM" ]]; then
  echo 'MeloNX.app.dSYM was not generated; refusing an unsymbolicatable diagnostic build.' >&2
  exit 1
fi
APP_DSYM_UUID="$(arm64_uuid "$APP_DSYM")"
if [[ -z "$APP_DSYM_UUID" || "$APP_DSYM_UUID" != "$APP_BINARY_UUID" ]]; then
  echo "MeloNX dSYM UUID mismatch: binary=$APP_BINARY_UUID dsym=${APP_DSYM_UUID:-missing}." >&2
  exit 1
fi
ditto "$APP_DSYM" "$SYMBOL_ROOT/MeloNX.app.dSYM"
printf 'app_dsym_arm64_uuid=%s\n' "$APP_DSYM_UUID" >> "$SYMBOL_MANIFEST"

NATIVE_SYMBOL_BASE="$REPOSITORY_ROOT/src/Ryujinx.Library/bin/Release/net10.0/ios-arm64/native"
NATIVE_DSYM="$NATIVE_SYMBOL_BASE/Ryujinx.Library.dylib.dSYM"
if [[ ! -d "$NATIVE_DSYM" ]]; then
  echo 'Ryujinx.Library.dylib.dSYM was not generated; refusing an unsymbolicatable diagnostic build.' >&2
  exit 1
fi
NATIVE_DSYM_UUID="$(arm64_uuid "$NATIVE_DSYM")"
if [[ -z "$NATIVE_DSYM_UUID" || "$NATIVE_DSYM_UUID" != "$NATIVE_BINARY_UUID" ]]; then
  echo "Ryujinx.Library dSYM UUID mismatch: binary=$NATIVE_BINARY_UUID dsym=${NATIVE_DSYM_UUID:-missing}." >&2
  exit 1
fi
ditto "$NATIVE_DSYM" "$SYMBOL_ROOT/Ryujinx.Library.dylib.dSYM"
printf 'native_dsym_arm64_uuid=%s\n' "$NATIVE_DSYM_UUID" >> "$SYMBOL_MANIFEST"

if [[ -n "${MELO_MVK_ARTIFACT_DIR:-}" ]]; then
  ditto -x -k "$MELO_MVK_ARTIFACT_DIR/libMoltenVK.dylib.dSYM.zip" "$SYMBOL_ROOT"
  MOLTENVK_DSYM_UUID="$(arm64_uuid "$SYMBOL_ROOT/libMoltenVK.dylib.dSYM")"
  test -n "$MOLTENVK_DSYM_UUID"
  test "$MOLTENVK_DSYM_UUID" = "$MOLTENVK_BINARY_UUID"
  printf 'moltenvk_dsym_arm64_uuid=%s\n' "$MOLTENVK_DSYM_UUID" >> "$SYMBOL_MANIFEST"
fi

tar -C "$SYMBOL_ROOT" -czf "$OUTPUT_ROOT/MeloNX-symbols-$SHORT_SHA.tar.gz" .

MOLTENVK_ID="$(otool -D "$MOLTENVK" | sed -n '2p')"
test "$MOLTENVK_ID" = '@rpath/libMoltenVK.dylib'
otool -L "$APP/$EXECUTABLE" | grep -Fq '@rpath/libMoltenVK.dylib'
nm -g "$MOLTENVK" > "$OUTPUT_ROOT/logs/moltenvk-symbols.log"
grep -Eq '(^|[[:space:]_])vkGetMoltenVKConfigurationMVK$' "$OUTPUT_ROOT/logs/moltenvk-symbols.log"
grep -Eq '(^|[[:space:]_])vkSetMoltenVKConfigurationMVK$' "$OUTPUT_ROOT/logs/moltenvk-symbols.log"
printf 'moltenvk_sha256=%s\n' "$MOLTENVK_SHA256" >> "$BUILD_INFO"
test "$(/usr/libexec/PlistBuddy -c 'Print :MeloNXSourceCommit' "$APP/Info.plist")" = "$SOURCE_SHA"
nm -g "$APP/Frameworks/Ryujinx.Library.dylib" > "$OUTPUT_ROOT/logs/native-symbols.log"
grep -Eq '(^|[[:space:]_])report_memory_pressure$' "$OUTPUT_ROOT/logs/native-symbols.log"
grep -Eq '(^|[[:space:]_])get_jit_cache_usage$' "$OUTPUT_ROOT/logs/native-symbols.log"
grep -Eq '(^|[[:space:]_])GetMemoryForensicSnapshot$' "$OUTPUT_ROOT/logs/native-symbols.log"

# Ad-hoc sign the main Mach-O only to preserve the requested memory entitlement
# for the sideload tool. This does not create a device provisioning profile.
codesign --force --sign - --entitlements "$ENTITLEMENTS" "$APP/$EXECUTABLE"
codesign --display --entitlements - --xml "$APP/$EXECUTABLE" \
  > "$OUTPUT_ROOT/embedded-entitlements.plist" 2> "$OUTPUT_ROOT/logs/codesign.log"
test "$(/usr/libexec/PlistBuddy -c 'Print :com.apple.developer.kernel.increased-memory-limit' "$OUTPUT_ROOT/embedded-entitlements.plist")" = true

mkdir -p "$WORK_ROOT/package/Payload"
ditto "$APP" "$WORK_ROOT/package/Payload/MeloNX.app"
IPA_PATH="$OUTPUT_ROOT/MeloNX-$SHORT_SHA-unprovisioned.ipa"
ditto -c -k --keepParent "$WORK_ROOT/package/Payload" "$IPA_PATH"
unzip -t "$IPA_PATH" > "$OUTPUT_ROOT/logs/ipa-archive-check.log"
(
  cd "$OUTPUT_ROOT"
  checksum_files=("MeloNX-$SHORT_SHA-unprovisioned.ipa" "MeloNX-source-$SHORT_SHA.tar.gz")
  if [[ -f "MeloNX-symbols-$SHORT_SHA.tar.gz" ]]; then
    checksum_files+=("MeloNX-symbols-$SHORT_SHA.tar.gz")
  fi
  if [[ -f "MeloNX-MoltenVK-$SHORT_SHA.tar.gz" ]]; then
    checksum_files+=("MeloNX-MoltenVK-$SHORT_SHA.tar.gz")
  fi
  shasum -a 256 "${checksum_files[@]}" > SHA256SUMS.txt
)
BUILD_STAGE=complete
printf 'ipa=%s\n' "$IPA_PATH" >> "$BUILD_INFO"
echo "Created $IPA_PATH (requires re-signing before installation)."
