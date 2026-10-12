#!/bin/bash
# Build and bundle "Vortex Editor.app" (self-contained, Apple Silicon) — the native engine, the Avalonia editor,
# the standalone player (used for Play-in-window and exports) and the project templates in one double-clickable app.
#
#   tools/macos/make-app.sh              build into dist/macos/Vortex Editor.app
#   tools/macos/make-app.sh --install    ... and copy it to /Applications (falls back to ~/Applications)
#   tools/macos/make-app.sh --dmg        ... and pack a Vortex-Editor.dmg next to it
#   tools/macos/make-app.sh --skip-native   reuse the existing build/macos-release tree
#
# Requirements (one-time): xcode-select --install; brew install cmake ninja assimp sdl3 dotnet
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$ROOT"
INSTALL=0; DMG=0; SKIP_NATIVE=0; CONFIG=Release
for a in "$@"; do case "$a" in --install) INSTALL=1;; --dmg) DMG=1;; --skip-native) SKIP_NATIVE=1;; --debug) CONFIG=Debug;; esac; done

export DOTNET_ROOT="${DOTNET_ROOT:-/opt/homebrew/opt/dotnet/libexec}"
export PATH="$DOTNET_ROOT:/opt/homebrew/bin:/usr/local/bin:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
command -v dotnet >/dev/null || { echo "dotnet not found — brew install dotnet"; exit 1; }
command -v cmake  >/dev/null || { echo "cmake not found — brew install cmake ninja"; exit 1; }

ARCH="$(uname -m)"; RID="osx-arm64"; [ "$ARCH" = "x86_64" ] && RID="osx-x64"
DIST="$ROOT/dist/macos"; APP="$DIST/Vortex Editor.app"
PRESET="macos-release"; [ "$CONFIG" = "Debug" ] && PRESET="macos-debug"

echo "== 1/5 native engine ($PRESET)"
if [ "$SKIP_NATIVE" = "0" ]; then
  cmake --preset "$PRESET" >/dev/null
  cmake --build --preset "$PRESET"
fi
NATIVE="$ROOT/build/$PRESET/bin"
[ -f "$NATIVE/libVortexAPI.dylib" ] || { echo "native library missing in $NATIVE"; exit 1; }

echo "== 2/5 managed layer ($CONFIG, $RID, self-contained)"
PUB="$ROOT/dist/publish"
rm -rf "$PUB"
dotnet publish Managed/Vortex.Editor/Vortex.Editor.csproj -c "$CONFIG" -r "$RID" --self-contained true -o "$PUB/editor" -nologo -v q
dotnet publish Managed/Vortex.Player/Vortex.Player.csproj -c "$CONFIG" -r "$RID" --self-contained true -o "$PUB/player" -nologo -v q

echo "== 3/5 bundle"
rm -rf "$APP"; mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources/Player" "$APP/Contents/Resources/Templates"
cp -R "$PUB/editor/." "$APP/Contents/MacOS/"
cp -R "$PUB/player/." "$APP/Contents/Resources/Player/"
for d in "$APP/Contents/MacOS" "$APP/Contents/Resources/Player"; do
  cp "$NATIVE/libVortexAPI.dylib" "$d/"
  for lib in "$NATIVE"/lib*.dylib; do [ -f "$lib" ] && cp "$lib" "$d/" || true; done
  mkdir -p "$d/Shaders"; cp -R "$NATIVE/Shaders/." "$d/Shaders/"
  # SDL3, assimp & co. from Homebrew go into the bundle: the app (and every game it exports) runs without Homebrew
  "$ROOT/tools/macos/bundle-dylibs.sh" "$d"
done
# templates without their git metadata (a template repo's .git holds every LFS object a second time)
rsync -a --exclude ".git" "$ROOT/Templates/" "$APP/Contents/Resources/Templates/"
find "$APP" -name ".DS_Store" -delete 2>/dev/null || true

# icon: Logo.png -> AppIcon.icns
ICONSET="$DIST/AppIcon.iconset"; rm -rf "$ICONSET"; mkdir -p "$ICONSET"
SRC_PNG=""
for cand in "$ROOT/Managed/Vortex.Editor/Assets/Logo.png" "$ROOT/Editor/Assets/Images/Logo.png"; do [ -f "$cand" ] && SRC_PNG="$cand" && break; done
[ -n "$SRC_PNG" ] || echo "warning: no Logo.png for the app icon (looked in Managed/Vortex.Editor/Assets and Editor/Assets/Images)"
if [ -f "$SRC_PNG" ]; then
  for s in 16 32 64 128 256 512; do
    sips -z $s $s "$SRC_PNG" --out "$ICONSET/icon_${s}x${s}.png" >/dev/null 2>&1
    d=$((s*2)); [ $d -le 1024 ] && sips -z $d $d "$SRC_PNG" --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null 2>&1 || true
  done
  iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns" || echo "warning: iconutil failed — the app has no icon"
fi
rm -rf "$ICONSET"

VERSION="$(grep -o 'Version(\([0-9]*, *\)\{2\}[0-9]*' "$ROOT/Editor/Core/EngineInfo.cs" 2>/dev/null | head -1 | grep -o '[0-9, ]*' | tr -d ' ' | tr ',' '.' || true)"
[ -z "$VERSION" ] && VERSION="1.0.0"
# The oldest macOS the app runs on is the newest one any bundled library was built for (Homebrew bottles target the
# build machine's macOS) — say so in Info.plist instead of letting an older macOS crash at the first dlopen.
MINOS=13.0
for f in "$APP/Contents/MacOS"/*.dylib; do
  v="$(otool -l "$f" 2>/dev/null | awk '/LC_BUILD_VERSION/{b=1} b&&/minos/{print $2; exit}')"
  [ -n "$v" ] && [ "$(printf '%s\n%s\n' "$MINOS" "$v" | sort -V | tail -1)" = "$v" ] && MINOS="$v"
done
echo "   minimum macOS $MINOS"
cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Vortex Editor</string>
  <key>CFBundleDisplayName</key><string>Vortex Editor</string>
  <key>CFBundleIdentifier</key><string>dev.vortexstudio.editor</string>
  <key>CFBundleExecutable</key><string>Vortex.Editor</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>LSMinimumSystemVersion</key><string>$MINOS</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSSupportsAutomaticGraphicsSwitching</key><true/>
  <key>LSApplicationCategoryType</key><string>public.app-category.developer-tools</string>
  <key>CFBundleDocumentTypes</key>
  <array>
    <dict>
      <key>CFBundleTypeName</key><string>Vortex Project</string>
      <key>CFBundleTypeRole</key><string>Editor</string>
      <key>LSHandlerRank</key><string>Owner</string>
      <key>LSItemContentTypes</key><array><string>dev.vortexstudio.project</string></array>
    </dict>
  </array>
  <key>UTExportedTypeDeclarations</key>
  <array>
    <dict>
      <key>UTTypeIdentifier</key><string>dev.vortexstudio.project</string>
      <key>UTTypeDescription</key><string>Vortex Project</string>
      <key>UTTypeConformsTo</key><array><string>public.json</string></array>
      <key>UTTypeTagSpecification</key><dict><key>public.filename-extension</key><array><string>vortex</string></array></dict>
    </dict>
  </array>
</dict>
</plist>
PLIST
chmod +x "$APP/Contents/MacOS/Vortex.Editor" "$APP/Contents/Resources/Player/Vortex.Player"

echo "== 4/5 sign (ad-hoc, local use; replace '-' with your Developer ID for distribution)"
codesign --force --deep --sign - "$APP" 2>/dev/null || echo "codesign not available — the app still runs locally"
xattr -dr com.apple.quarantine "$APP" 2>/dev/null || true

echo "== 5/5 done: $APP"
du -sh "$APP" | awk '{print "   size " $1}'
if [ "$DMG" = "1" ]; then
  DMGF="$DIST/Vortex-Editor-$VERSION.dmg"; rm -f "$DMGF"
  hdiutil create -volname "Vortex Editor" -srcfolder "$APP" -ov -format UDZO "$DMGF" >/dev/null && echo "   dmg  $DMGF"
fi
if [ "$INSTALL" = "1" ]; then
  TARGET="/Applications"; [ -w "$TARGET" ] || TARGET="$HOME/Applications"; mkdir -p "$TARGET"
  # one copy only: remove stale installs in the other location and the staging copy (Launchpad/Spotlight
  # would otherwise show "Vortex Editor" twice)
  rm -rf "/Applications/Vortex Editor.app" "$HOME/Applications/Vortex Editor.app" 2>/dev/null || true
  ditto "$APP" "$TARGET/Vortex Editor.app"
  rm -rf "$APP"
  echo "   installed to $TARGET/Vortex Editor.app  —  open -a 'Vortex Editor'  (staging copy in dist/ removed)"
fi
