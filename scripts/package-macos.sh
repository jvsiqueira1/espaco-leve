#!/bin/sh
set -eu
fail() { echo "$1" >&2; exit 1; }
[ "$#" -eq 4 ] || fail 'Uso: package-macos.sh <publicação> <versão> <runtime> <saída>'
publish="$1"; version="$2"; runtime="$3"; output="$4"
case "$version" in *[!0-9.]*|.*|*.|*..*) fail 'Versão inválida.' ;; *.*.*.*) fail 'Versão inválida.' ;; *.*.*) ;; *) fail 'Versão inválida.' ;; esac
case "$runtime" in osx-arm64|osx-x64) ;; *) fail 'Runtime macOS inválido.' ;; esac
[ -f "$publish/EspacoLeve" ] || fail 'Executável ausente na publicação.'
# A Developer ID identity turns on hardened runtime signing and notarization; without one the package stays ad hoc.
identity="${MACOS_SIGN_IDENTITY:-}"
if [ -z "$identity" ]; then
  [ "${MACOS_REQUIRE_NOTARIZATION:-}" != 1 ] || fail 'Notarização exigida, mas MACOS_SIGN_IDENTITY não foi definido.'
elif [ -n "${APPLE_API_KEY_PATH:-}${APPLE_API_KEY_ID:-}${APPLE_API_ISSUER_ID:-}" ]; then
  [ -f "${APPLE_API_KEY_PATH:-}" ] && [ -n "${APPLE_API_KEY_ID:-}" ] && [ -n "${APPLE_API_ISSUER_ID:-}" ] ||
    fail 'Credenciais da App Store Connect incompletas: defina APPLE_API_KEY_PATH, APPLE_API_KEY_ID e APPLE_API_ISSUER_ID.'
elif [ -z "${APPLE_ID:-}" ] || [ -z "${APPLE_TEAM_ID:-}" ] || [ -z "${APPLE_APP_PASSWORD:-}" ]; then
  fail 'Assinatura sem credenciais de notarização: defina APPLE_API_KEY_PATH, APPLE_API_KEY_ID e APPLE_API_ISSUER_ID, ou APPLE_ID, APPLE_TEAM_ID e APPLE_APP_PASSWORD.'
fi
# diskutil image replaces the deprecated hdiutil commands; older hosts keep hdiutil.
# macOS 15 already has diskutil image, but without --volumeName, so probe the options used below.
if [ -n "${ESPACO_LEVE_DMG_TOOL:-}" ]; then
  tool="$ESPACO_LEVE_DMG_TOOL"
elif diskutil image create from --help 2>&1 | grep -q -- --volumeName &&
  diskutil image attach --help 2>&1 | grep -q -- --nobrowse; then
  tool=diskutil
else
  tool=hdiutil
fi
case "$tool" in diskutil|hdiutil) ;; *) fail 'Ferramenta de imagem de disco inválida.' ;; esac
base="$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)"
work="$base/work/package-macos/$runtime-$$"
mount="$work/mount"
device=
mounted=0
tested=0
dmg="$output/EspacoLeve-$version-$runtime.dmg"
detach() {
  if [ "$tool" = diskutil ]; then
    target="${device:-$mount}"
    diskutil eject "$target" >/dev/null || { diskutil unmountDisk force "$target" >/dev/null && diskutil eject "$target" >/dev/null; }
  else
    hdiutil detach "$mount" -quiet || hdiutil detach "$mount" -force -quiet
  fi
}
cleanup() {
  if [ "$mounted" -eq 1 ]; then
    detach || { echo "Imagem de disco ainda montada em $mount" >&2; return; }
  fi
  [ "$tested" -eq 1 ] || rm -f "$dmg"
  rm -rf "$work"
}
trap cleanup EXIT
trap 'exit 1' HUP INT TERM
sign() {
  if [ -n "${MACOS_SIGN_KEYCHAIN:-}" ]; then
    codesign --force --timestamp --keychain "$MACOS_SIGN_KEYCHAIN" --sign "$identity" "$@"
  else
    codesign --force --timestamp --sign "$identity" "$@"
  fi
}
notary() {
  if [ -n "${APPLE_API_KEY_PATH:-}" ]; then
    xcrun notarytool "$@" --key "$APPLE_API_KEY_PATH" --key-id "$APPLE_API_KEY_ID" --issuer "$APPLE_API_ISSUER_ID"
  else
    xcrun notarytool "$@" --apple-id "$APPLE_ID" --team-id "$APPLE_TEAM_ID" --password "$APPLE_APP_PASSWORD"
  fi
}
notarize() {
  echo "Enviando $2 para notarização da Apple."
  notary submit "$1" --wait --timeout 30m --output-format json >"$work/notary.json" 2>"$work/notary.log" || true
  if [ "$(plutil -extract status raw -o - "$work/notary.json" 2>/dev/null)" != Accepted ]; then
    cat "$work/notary.json" "$work/notary.log" >&2
    submission="$(plutil -extract id raw -o - "$work/notary.json" 2>/dev/null)" && notary log "$submission" >&2
    fail "A Apple não aprovou $2 na notarização."
  fi
}
# The ticket can take a while to reach Apple's CDN after notarytool reports Accepted, so stapling is retried.
staple() {
  tries=1
  until xcrun stapler staple "$1" >"$work/staple.log" 2>&1; do
    [ "$tries" -lt 6 ] || { cat "$work/staple.log" >&2; fail "Falha ao anexar a notarização $2."; }
    tries=$((tries + 1)); sleep 20
  done
}
rm -rf "$work"
mkdir -p "$work/iconset.iconset" "$work/image" "$output"

app="$work/image/Espaço Leve.app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp -R "$publish/." "$app/Contents/MacOS/"
chmod +x "$app/Contents/MacOS/EspacoLeve"
for size in 16 32 128 256 512; do
  sips -s format png -z "$size" "$size" "$base/assets/app.png" --out "$work/iconset.iconset/icon_${size}x${size}.png" >/dev/null || fail 'Falha ao converter o ícone do aplicativo.'
  double=$((size * 2))
  sips -s format png -z "$double" "$double" "$base/assets/app.png" --out "$work/iconset.iconset/icon_${size}x${size}@2x.png" >/dev/null || fail 'Falha ao converter o ícone do aplicativo.'
done
iconutil -c icns "$work/iconset.iconset" -o "$app/Contents/Resources/EspacoLeve.icns" || fail 'Falha ao gerar o ícone do aplicativo.'
cat > "$app/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleIdentifier</key><string>io.github.jvsiqueira1.espaco-leve</string>
  <key>CFBundleName</key><string>Espaço Leve</string>
  <key>CFBundleDisplayName</key><string>Espaço Leve</string>
  <key>CFBundleExecutable</key><string>EspacoLeve</string>
  <key>CFBundleIconFile</key><string>EspacoLeve</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>CFBundleVersion</key><string>$version</string>
  <key>LSMinimumSystemVersion</key><string>15.0</string>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
EOF
plutil -lint -s "$app/Contents/Info.plist" || fail 'Info.plist inválido.'
if [ -z "$identity" ]; then
  codesign --force --deep --sign - "$app" || fail 'Falha na assinatura ad hoc do aplicativo.'
else
  # Sign inside out: .NET keeps managed assemblies beside the executable, so each nested file needs its own signature before the bundle seal.
  find "$app/Contents/MacOS" -type f ! -path "$app/Contents/MacOS/EspacoLeve" >"$work/nested.txt"
  while IFS= read -r file; do
    sign --options runtime "$file" || fail 'Falha na assinatura Developer ID de um componente do aplicativo.'
  done <"$work/nested.txt"
  sign --options runtime --entitlements "$base/scripts/macos-entitlements.plist" "$app" || fail 'Falha na assinatura Developer ID do aplicativo.'
fi
codesign --verify --deep --strict "$app" || fail 'A assinatura do aplicativo não foi verificada.'
if [ -n "$identity" ]; then
  # Stapling the app too lets it pass Gatekeeper offline after being copied out of the disk image.
  ditto -c -k --keepParent "$app" "$work/app.zip" || fail 'Falha ao compactar o aplicativo para notarização.'
  notarize "$work/app.zip" 'o aplicativo'
  rm -f "$work/app.zip"
  staple "$app" 'ao aplicativo'
fi
ln -s /Applications "$work/image/Applications"

echo "Criando a imagem de disco com $tool."
rm -f "$dmg"
# hdiutil occasionally reports a busy device on hosted runners.
attempt=1
if [ "$tool" = diskutil ]; then
  # UDZO keeps the same zlib format as the hdiutil path; the folder source yields an APFS volume.
  until diskutil image create from --format UDZO --volumeName 'Espaço Leve' "$work/image" "$dmg" >/dev/null 2>"$work/create.log"; do
    [ "$attempt" -lt 3 ] || { cat "$work/create.log" >&2; fail 'Falha ao criar a imagem de disco macOS.'; }
    attempt=$((attempt + 1)); rm -f "$dmg"; sleep 5
  done
  # There is no diskutil verify: info only reads the image structure; codesign and the smoke test below read the app contents.
  [ "$(diskutil image info --plist "$dmg" | plutil -extract 'Image Format' raw -o - -)" = UDZO ] || fail 'A imagem de disco macOS está corrompida.'
else
  until hdiutil create -volname 'Espaço Leve' -srcfolder "$work/image" -fs HFS+ -format UDZO -ov "$dmg" >/dev/null; do
    [ "$attempt" -lt 3 ] || fail 'Falha ao criar a imagem de disco macOS.'
    attempt=$((attempt + 1)); sleep 5
  done
  hdiutil verify -quiet "$dmg" || fail 'A imagem de disco macOS está corrompida.'
fi
if [ -n "$identity" ]; then
  sign "$dmg" || fail 'Falha na assinatura Developer ID da imagem de disco.'
  notarize "$dmg" 'a imagem de disco'
  staple "$dmg" 'à imagem de disco'
  xcrun stapler validate -q "$dmg" || fail 'A notarização da imagem de disco não foi validada.'
  spctl -a -t open --context context:primary-signature -vv "$dmg" || fail 'O Gatekeeper recusou a imagem de disco.'
fi

mkdir -p "$mount"
if [ "$tool" = diskutil ]; then
  diskutil image attach --plist --readOnly --nobrowse --mountPoint "$mount" "$dmg" >"$work/attach.plist" || fail 'Não foi possível montar a imagem de disco.'
  mounted=1
  device="$(plutil -extract system-entities.0.dev-entry raw -o - "$work/attach.plist")" || fail 'Dispositivo da imagem de disco não identificado.'
else
  hdiutil attach -nobrowse -readonly -noautoopen -mountpoint "$mount" "$dmg" >/dev/null || fail 'Não foi possível montar a imagem de disco.'
  mounted=1
fi
[ -L "$mount/Applications" ] || fail 'Atalho para Aplicativos ausente na imagem de disco.'
executable="$mount/Espaço Leve.app/Contents/MacOS/EspacoLeve"
codesign --verify --deep --strict "$mount/Espaço Leve.app" || fail 'A assinatura do aplicativo na imagem não foi verificada.'
if [ -n "$identity" ]; then
  xcrun stapler validate -q "$mount/Espaço Leve.app" || fail 'A notarização do aplicativo na imagem não foi validada.'
  spctl -a -t exec -vv "$mount/Espaço Leve.app" || fail 'O Gatekeeper recusou o aplicativo na imagem.'
fi
[ "$("$executable" --version)" = "$version" ] || fail 'A versão do aplicativo na imagem não corresponde ao pacote.'
"$executable" --smoke-test >"$work/smoke.log" 2>&1 &
smoke=$!
elapsed=0
while kill -0 "$smoke" 2>/dev/null; do
  if [ "$elapsed" -ge 60 ]; then kill "$smoke" 2>/dev/null || true; wait "$smoke" 2>/dev/null || true; fail 'A verificação do aplicativo excedeu o tempo disponível.'; fi
  sleep 1; elapsed=$((elapsed + 1))
done
wait "$smoke" || { cat "$work/smoke.log" >&2; fail 'Falha no aplicativo da imagem de disco.'; }
detach || fail 'Não foi possível desmontar a imagem de disco.'
mounted=0
tested=1
if [ -n "$identity" ]; then
  echo 'Pacote macOS assinado com Developer ID e notarizado pela Apple.'
else
  echo 'Pacote macOS com assinatura ad hoc: não notarizado pela Apple.'
fi
