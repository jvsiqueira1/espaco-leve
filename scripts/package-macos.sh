#!/bin/sh
set -eu
fail() { echo "$1" >&2; exit 1; }
[ "$#" -eq 4 ] || fail 'Uso: package-macos.sh <publicação> <versão> <runtime> <saída>'
publish="$1"; version="$2"; runtime="$3"; output="$4"
case "$version" in *[!0-9.]*|.*|*.|*..*) fail 'Versão inválida.' ;; *.*.*.*) fail 'Versão inválida.' ;; *.*.*) ;; *) fail 'Versão inválida.' ;; esac
case "$runtime" in osx-arm64|osx-x64) ;; *) fail 'Runtime macOS inválido.' ;; esac
[ -f "$publish/EspacoLeve" ] || fail 'Executável ausente na publicação.'
# diskutil image replaces the deprecated hdiutil commands; older hosts keep hdiutil.
if [ -n "${ESPACO_LEVE_DMG_TOOL:-}" ]; then
  tool="$ESPACO_LEVE_DMG_TOOL"
elif diskutil image info -h >/dev/null 2>&1; then
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
codesign --force --deep --sign - "$app" || fail 'Falha na assinatura ad hoc do aplicativo.'
codesign --verify --deep --strict "$app" || fail 'A assinatura do aplicativo não foi verificada.'
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
