#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

RID="${1:-osx-arm64}"
VERSION="${2:-2.1.0}"
PROJECT_PATH="${REPO_ROOT}/src/VelocityDM.Mac/VelocityDM.Mac.csproj"
PUBLISH_DIR="${REPO_ROOT}/dist/macos/${RID}/publish"
STAGE_DIR="${REPO_ROOT}/dist/macos/${RID}/dmg-stage"
APP_BUNDLE="${STAGE_DIR}/VelocityDM.app"
OUT_DMG="${REPO_ROOT}/dist/macos/VelocityDM-v${VERSION}-${RID}.dmg"
ICON_PNG="${REPO_ROOT}/src/VelocityDM.App/Assets/VelocityDM.png"
ICONSET_DIR="${REPO_ROOT}/dist/macos/${RID}/VelocityDM.iconset"

echo "==> Building VelocityDM macOS app (${RID}, v${VERSION})..."
rm -rf "${PUBLISH_DIR}" "${STAGE_DIR}" "${ICONSET_DIR}"
mkdir -p "${PUBLISH_DIR}" "${STAGE_DIR}" "$(dirname "${OUT_DMG}")"

dotnet publish "${PROJECT_PATH}" \
  -c Release \
  -r "${RID}" \
  --self-contained true \
  -p:Version="${VERSION}" \
  -p:DebugType=None \
  -p:DebugSymbols=false \
  -o "${PUBLISH_DIR}"

echo "==> Creating VelocityDM.app bundle..."
mkdir -p "${APP_BUNDLE}/Contents/MacOS" "${APP_BUNDLE}/Contents/Resources/ChromeExtension"
cp -R "${PUBLISH_DIR}/"* "${APP_BUNDLE}/Contents/MacOS/"
chmod +x "${APP_BUNDLE}/Contents/MacOS/VelocityDM"

cp "${REPO_ROOT}/extension/chrome/manifest.json" "${APP_BUNDLE}/Contents/Resources/ChromeExtension/"
cp "${REPO_ROOT}/extension/chrome/background.js" "${APP_BUNDLE}/Contents/Resources/ChromeExtension/"
cp "${REPO_ROOT}/extension/chrome/popup.html" "${APP_BUNDLE}/Contents/Resources/ChromeExtension/"
cp "${REPO_ROOT}/extension/chrome/popup.js" "${APP_BUNDLE}/Contents/Resources/ChromeExtension/"
cp "${REPO_ROOT}/extension/chrome/icon128.png" "${APP_BUNDLE}/Contents/Resources/ChromeExtension/"

echo "==> Generating VelocityDM.icns..."
mkdir -p "${ICONSET_DIR}"
sips -z 16 16     "${ICON_PNG}" --out "${ICONSET_DIR}/icon_16x16.png" >/dev/null
sips -z 32 32     "${ICON_PNG}" --out "${ICONSET_DIR}/icon_16x16@2x.png" >/dev/null
sips -z 32 32     "${ICON_PNG}" --out "${ICONSET_DIR}/icon_32x32.png" >/dev/null
sips -z 64 64     "${ICON_PNG}" --out "${ICONSET_DIR}/icon_32x32@2x.png" >/dev/null
sips -z 128 128   "${ICON_PNG}" --out "${ICONSET_DIR}/icon_128x128.png" >/dev/null
sips -z 256 256   "${ICON_PNG}" --out "${ICONSET_DIR}/icon_128x128@2x.png" >/dev/null
sips -z 256 256   "${ICON_PNG}" --out "${ICONSET_DIR}/icon_256x256.png" >/dev/null
sips -z 512 512   "${ICON_PNG}" --out "${ICONSET_DIR}/icon_256x256@2x.png" >/dev/null
iconutil -c icns "${ICONSET_DIR}" -o "${APP_BUNDLE}/Contents/Resources/VelocityDM.icns"
rm -rf "${ICONSET_DIR}"

cat > "${APP_BUNDLE}/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>VelocityDM</string>
    <key>CFBundleDisplayName</key>
    <string>VelocityDM</string>
    <key>CFBundleIdentifier</key>
    <string>com.velocitydm.app</string>
    <key>CFBundleVersion</key>
    <string>${VERSION}</string>
    <key>CFBundleShortVersionString</key>
    <string>${VERSION}</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleExecutable</key>
    <string>VelocityDM</string>
    <key>CFBundleIconFile</key>
    <string>VelocityDM.icns</string>
    <key>LSMinimumSystemVersion</key>
    <string>12.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
</dict>
</plist>
EOF

echo "==> Ad-hoc signing VelocityDM.app..."
codesign --force --deep --sign - "${APP_BUNDLE}"

ln -s /Applications "${STAGE_DIR}/Applications"

cat > "${STAGE_DIR}/Install Chrome Integration.command" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TARGET_APP="/Applications/VelocityDM.app"

if [ ! -d "${TARGET_APP}" ] && [ -d "${SCRIPT_DIR}/VelocityDM.app" ]; then
  echo "Installing VelocityDM.app to /Applications..."
  ditto "${SCRIPT_DIR}/VelocityDM.app" "${TARGET_APP}"
fi

if [ ! -x "${TARGET_APP}/Contents/MacOS/VelocityDM" ]; then
  echo "Error: Please drag VelocityDM.app into /Applications first."
  read -r -p "Press Enter to exit..."
  exit 1
fi

"${TARGET_APP}/Contents/MacOS/VelocityDM" --register-browser

EXT_DIR="$HOME/Library/Application Support/VelocityDM/ChromeExtension"
printf '%s' "${EXT_DIR}" | pbcopy

echo ""
echo "=========================================================="
echo " VelocityDM Chrome Native Host registered!"
echo " Extension folder: ${EXT_DIR}"
echo " (Copied to your clipboard!)"
echo ""
echo " Next in Google Chrome (one-time step required by Chrome):"
echo "  1. Enable 'Developer mode' in top-right of chrome://extensions"
echo "  2. Drag the opened 'ChromeExtension' folder onto chrome://extensions"
echo "     (or click 'Load unpacked' -> Cmd+Shift+G -> Cmd+V -> Select)"
echo "=========================================================="
echo ""

open "${EXT_DIR}"
open -a "Google Chrome" "chrome://extensions" || true
read -r -p "Press Enter to close..."
EOF
chmod +x "${STAGE_DIR}/Install Chrome Integration.command"

echo "==> Creating DMG at ${OUT_DMG}..."
rm -f "${OUT_DMG}"
hdiutil create \
  -volname "VelocityDM ${VERSION}" \
  -srcfolder "${STAGE_DIR}" \
  -ov \
  -format UDZO \
  "${OUT_DMG}"

echo "==> Done: ${OUT_DMG}"
