#!/bin/bash
set -euo pipefail
# 开发版 macOS 应用包：独立 bundle id 与数据目录，不包含 Sparkle，不会覆盖 /Applications 的正式版。
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$ROOT"

RID="${RID:-osx-arm64}"
INSTALL=0
if [[ $# -gt 1 ]]; then
  echo "用法：$0 [--install]" >&2
  exit 2
fi
if [[ $# -eq 1 ]]; then
  [[ "$1" == "--install" ]] || { echo "未知参数：$1" >&2; exit 2; }
  INSTALL=1
fi
[[ "$RID" == osx-arm64 || "$RID" == osx-x64 ]] || { echo "仅支持 osx-arm64 / osx-x64：$RID" >&2; exit 2; }

SDK="${DOTNET:-dotnet}"
VERSION="$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' src/DbPilot/DbPilot.csproj | head -n1)"
[[ -n "$VERSION" ]] || { echo '无法读取 src/DbPilot/DbPilot.csproj 中的版本号' >&2; exit 1; }

APP="$ROOT/artifacts/macos-dev/DbPilot Dev.app"
PUBLISH_DIR="$(mktemp -d "$ROOT/artifacts/macos-dev-publish.XXXXXX")"
trap 'rm -rf "$PUBLISH_DIR"' EXIT

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
"$SDK" publish src/DbPilot/DbPilot.csproj -c Release -r "$RID" --self-contained true \
  -p:BuildChannel=Development -p:Version="$VERSION" -p:DebugType=None -p:DebugSymbols=false \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$PUBLISH_DIR"
ditto "$PUBLISH_DIR" "$APP/Contents/MacOS"
rm -f "$APP/Contents/MacOS/libDbPilotUpdater.dylib"

export APP VERSION
python3 - <<'PY'
import os, plistlib, pathlib
app = pathlib.Path(os.environ['APP'])
version = os.environ['VERSION']
info = {
    'CFBundleName': 'DbPilot Dev',
    'CFBundleDisplayName': 'DbPilot Dev',
    'CFBundleIdentifier': 'com.dbpilot.dbpilot.dev',
    'CFBundleExecutable': 'DbPilot',
    'CFBundlePackageType': 'APPL',
    'CFBundleVersion': version,
    'CFBundleShortVersionString': version,
    'LSMinimumSystemVersion': '12.0',
    'NSHighResolutionCapable': True,
    'CFBundleAllowMixedLocalizations': True,
    'CFBundleLocalizations': ['en', 'zh-Hans', 'zh-Hant'],
    'CFBundleIconFile': 'DbPilot.icns',
}
(app / 'Contents/Info.plist').write_bytes(plistlib.dumps(info))
PY

ICONSET="$ROOT/artifacts/DbPilot.iconset"
mkdir -p "$ICONSET"
for size in 16 32 128 256 512; do
  for scale in 1 2; do
    if [[ "$scale" == 2 ]]; then suffix='@2x'; else suffix=''; fi
    name="icon_${size}x${size}${suffix}.png"
    sips -z "$((size * scale))" "$((size * scale))" src/DbPilot/Assets/AppIcon.png --out "$ICONSET/$name" >/dev/null
  done
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/DbPilot.icns"

codesign --force --deep --sign - "$APP"
codesign --verify --deep --strict "$APP"

if [[ "$INSTALL" == 1 ]]; then
  DEST="$HOME/Applications/DbPilot Dev.app"
  mkdir -p "$HOME/Applications"
  rm -rf "$DEST"
  ditto "$APP" "$DEST"
  echo "已安装开发版：$DEST"
fi
echo "开发版应用：$APP"
echo "开发版数据目录：$HOME/Library/Application Support/DbPilot Dev"
