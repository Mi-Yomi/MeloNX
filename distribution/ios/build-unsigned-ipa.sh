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
  CODE_SIGNING_ALLOWED=NO CODE_SIGNING_REQUIRED=NO CODE_SIGN_IDENTITY= DEVELOPMENT_TEAM= \
  2>&1 | tee "$OUTPUT_ROOT/logs/xcodebuild.log"
cmp "$PACKAGE_LOCK" "$OUTPUT_ROOT/Package.resolved"

BUILD_STAGE=package
APP="$WORK_ROOT/products/MeloNX.app"
test -d "$APP"
test -s "$APP/Frameworks/Ryujinx.Library.dylib"
EXECUTABLE="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$APP/Info.plist")"
case "$EXECUTABLE" in
  ''|*/*|.|..) echo 'Invalid CFBundleExecutable in built app.' >&2; exit 1 ;;
esac
xcrun lipo "$APP/$EXECUTABLE" -verify_arch arm64
xcrun lipo "$APP/Frameworks/Ryujinx.Library.dylib" -verify_arch arm64

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
  shasum -a 256 "MeloNX-$SHORT_SHA-unprovisioned.ipa" "MeloNX-source-$SHORT_SHA.tar.gz" > SHA256SUMS.txt
)
BUILD_STAGE=complete
printf 'ipa=%s\n' "$IPA_PATH" >> "$BUILD_INFO"
echo "Created $IPA_PATH (requires re-signing before installation)."
