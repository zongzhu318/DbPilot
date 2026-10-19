#!/bin/bash
set -euo pipefail
: "${VERSION:?}" "${RID:?}" "${PUBLIC_KEY:?}" "${REPOSITORY:?}"
if [[ -n "${APPLE_ID:-}" && -z "${APPLE_SIGNING_IDENTITY:-}" ]]; then
  echo 'Apple notarization requires a Developer ID signing identity.' >&2
  exit 1
fi
[[ "$RID" == osx-arm64 || "$RID" == osx-x64 ]]
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$ROOT"
SPARKLE_VERSION=2.10.0
mkdir -p artifacts/sparkle artifacts/installers
curl -fL --retry 3 "https://github.com/sparkle-project/Sparkle/releases/download/$SPARKLE_VERSION/Sparkle-$SPARKLE_VERSION.tar.xz" -o artifacts/sparkle.tar.xz
echo 'c2bf58aa8387266ac179357b1415d6f2635f044da8be41042af32425dae6da0c  artifacts/sparkle.tar.xz' | shasum -a 256 -c -
tar -xf artifacts/sparkle.tar.xz -C artifacts/sparkle
APP="$ROOT/artifacts/$RID/DbStudio.app"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Frameworks" "$APP/Contents/Resources"
dotnet publish src/DbStudio/DbStudio.csproj -c Release -r "$RID" --self-contained true \
  -p:Version="$VERSION" -p:DebugType=None -p:DebugSymbols=false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$APP/Contents/MacOS"
cp -R artifacts/sparkle/Sparkle.framework "$APP/Contents/Frameworks/"
mkdir -p "$APP/Contents/Resources/ThirdParty"
find artifacts/sparkle -maxdepth 1 -iname '*license*' -exec cp {} "$APP/Contents/Resources/ThirdParty/" \;
ARCH=arm64
[[ "$RID" != osx-x64 ]] || ARCH=x86_64
clang -arch "$ARCH" -mmacosx-version-min=12.0 -fobjc-arc -dynamiclib packaging/macos/Updater.m \
  -F artifacts/sparkle -framework Cocoa -framework Sparkle \
  -Wl,-rpath,@loader_path/../Frameworks -o "$APP/Contents/MacOS/libDbStudioUpdater.dylib"
export APP
python3 - <<'PY'
import os, plistlib, pathlib, subprocess
app=pathlib.Path(os.environ['APP'])
info=dict(CFBundleName='DbStudio',CFBundleDisplayName='DbStudio',CFBundleIdentifier='com.dbpilot.dbstudio',
 CFBundleExecutable='DbStudio',CFBundlePackageType='APPL',CFBundleVersion=os.environ['VERSION'],
 CFBundleShortVersionString=os.environ['VERSION'],LSMinimumSystemVersion='12.0',NSHighResolutionCapable=True,
 CFBundleIconFile='DbStudio.icns',SUFeedURL=f"https://github.com/{os.environ['REPOSITORY']}/releases/latest/download/appcast-{os.environ['RID']}.xml",
 SUPublicEDKey=os.environ['PUBLIC_KEY'],SUEnableAutomaticChecks=True,SUAutomaticallyUpdate=False)
(app/'Contents/Info.plist').write_bytes(plistlib.dumps(info))
iconset=pathlib.Path('artifacts/DbStudio.iconset');iconset.mkdir(exist_ok=True)
for size in (16,32,128,256,512):
 for scale in (1,2):
  name=f'icon_{size}x{size}'+('@2x' if scale==2 else '')+'.png'
  subprocess.run(['sips','-z',str(size*scale),str(size*scale),'src/DbStudio/Assets/logo.png','--out',str(iconset/name)],check=True,stdout=subprocess.DEVNULL)
subprocess.run(['iconutil','-c','icns',str(iconset),'-o',str(app/'Contents/Resources/DbStudio.icns')],check=True)
PY
chmod +x "$APP/Contents/MacOS/DbStudio"
IDENTITY="${APPLE_SIGNING_IDENTITY:--}"
# Sign nested helpers and libraries first; sign the enclosing app once, last.
python3 scripts/release/sign-macos-bundle.py --app "$APP" --identity "$IDENTITY" \
  --entitlements "$ROOT/packaging/macos/entitlements.plist"
if [[ -n "${APPLE_ID:-}" ]]; then
  : "${APPLE_TEAM_ID:?}" "${APPLE_APP_PASSWORD:?}"
  ditto -c -k --keepParent "$APP" artifacts/notarize.zip
  xcrun notarytool submit artifacts/notarize.zip --apple-id "$APPLE_ID" --team-id "$APPLE_TEAM_ID" --password "$APPLE_APP_PASSWORD" --wait
  xcrun stapler staple "$APP"
fi
STAGE="$(mktemp -d "$ROOT/artifacts/dmg-stage.XXXXXX")"
ditto "$APP" "$STAGE/DbStudio.app"
ln -s /Applications "$STAGE/Applications"
DMG="$ROOT/artifacts/installers/DbStudio-$VERSION-$RID.dmg"
hdiutil create -volname DbStudio -srcfolder "$STAGE" -ov -format UDZO "$DMG"
if [[ "$IDENTITY" != '-' ]]; then
  codesign --force --sign "$IDENTITY" --timestamp "$DMG"
fi
if [[ -n "${APPLE_ID:-}" ]]; then
  xcrun notarytool submit "$DMG" --apple-id "$APPLE_ID" --team-id "$APPLE_TEAM_ID" --password "$APPLE_APP_PASSWORD" --wait
  xcrun stapler staple "$DMG"
fi
hdiutil verify "$DMG"
