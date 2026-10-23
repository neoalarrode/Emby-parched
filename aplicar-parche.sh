#!/bin/sh
# Aplica los parches de Live TV sobre una instalación existente de Emby Server:
#   Emby.LiveTV.dll                -> sin compartición de streams (+ TARGETDURATION en 4.10)
#   Emby.Server.MediaEncoding.dll  -> margen HLS para Fire TV con Vega OS (EMBY_HLS_HOLDBACK)
#   Emby.Server.Implementations.dll, MediaBrowser.Model.dll (+ MediaEncoding) -> selección de la pista
#                                     de audio por idioma (alias esp..., audiodescripción al final, TV en directo)
#   plugins Emby.XmlTV.dll y Emby.M3UTuner.dll (si están instalados) -> borrar las copias
#                                     antiguas de la guía XMLTV en cada actualización
#   Robustez de la TV en directo: aperturas sin bloqueo global (Implementations), cierre de streams
#                                     huérfanos y reconexiones más tolerantes (LiveTV), ventana deslizante
#                                     en el HLS convertido (MediaEncoding)
# Solo aplica los parches que falten; si una DLL lleva el código de apoyo de otra versión del parcheador, la vuelve
# a parchear entera desde su copia original guardada (misma versión de Emby).
# Uso: sudo ./aplicar-parche.sh [/opt/emby-server/system] [/var/lib/emby/plugins]
set -e
SYS="${1:-/opt/emby-server/system}"
PLUGINS="${2:-/var/lib/emby/plugins}"
DIR="$(cd "$(dirname "$0")" && pwd)"
case "$(uname -m)" in
  x86_64) BIN="$DIR/bin/linux-x64/EmbyNoShare" ;;
  aarch64|arm64) BIN="$DIR/bin/linux-arm64/EmbyNoShare" ;;
  *) echo "Arquitectura no soportada: $(uname -m)"; exit 1 ;;
esac

# DLL a revisar: las de Emby son obligatorias; los plugins, solo si están instalados
FILES="$SYS/Emby.LiveTV.dll $SYS/Emby.Server.MediaEncoding.dll $SYS/Emby.Server.Implementations.dll $SYS/MediaBrowser.Model.dll"
for F in $FILES; do
  [ -f "$F" ] || { echo "No existe $F"; exit 1; }
done
for F in "$PLUGINS/Emby.XmlTV.dll" "$PLUGINS/Emby.M3UTuner.dll"; do
  [ -f "$F" ] && FILES="$FILES $F"
done

# Copia original (sin parchear) de la misma versión de Emby que la DLL actual, de las que guarda este script
original_of() {
  cur=$("$BIN" "$1" --info | sed -n 's/.*version=\([^ ]*\).*/\1/p')
  for B in $(ls -tr "$1".orig-* 2>/dev/null); do
    info=$("$BIN" "$B" --info 2>/dev/null) || continue
    case "$info" in
      *"version=$cur "*"patched=0"*) echo "$B"; return 0 ;;
    esac
  done
  return 1
}

# Comprueba todas las DLL antes de tocar nada. Si una lleva el código de apoyo de otra versión del parcheador,
# se vuelve a parchear entera desde su copia original.
PENDING=""
FRESH=""
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT
for F in $FILES; do
  rc=0; "$BIN" "$F" --check || rc=$?
  case "$rc" in
    0) PENDING="$PENDING $F" ;;
    3) ;;
    4)
      O=$(original_of "$F") || { echo "$F necesita partir de la DLL original y no hay copia sin parchear de esta versión de Emby; no se ha tocado nada."; exit 1; }
      cp -p "$(dirname "$F")"/*.dll "$TMP/" 2>/dev/null || true
      cp -p "$O" "$TMP/$(basename "$F")"
      rc2=0; "$BIN" "$TMP/$(basename "$F")" --check || rc2=$?
      [ "$rc2" = 0 ] || { echo "El patrón no coincide en la original de $F; no se ha tocado nada."; exit 1; }
      PENDING="$PENDING $F"; FRESH="$FRESH $F=$O"
      echo "$F se vuelve a parchear desde $O"
      ;;
    *) echo "El patrón no coincide en $F con esta versión; no se ha tocado nada."; exit 1 ;;
  esac
done
[ -n "$PENDING" ] || { echo "Nada que hacer."; exit 0; }

TS=$(date +%Y%m%d%H%M%S)
for F in $PENDING; do cp -p "$F" "$F.orig-$TS"; done
systemctl stop emby-server 2>/dev/null || true
for P in $FRESH; do cp -p "${P#*=}" "${P%%=*}"; done
for F in $PENDING; do "$BIN" "$F"; done
systemctl start emby-server 2>/dev/null || true

echo "Listo. Copias de seguridad de las DLL anteriores:"
for F in $PENDING; do echo "  $F.orig-$TS"; done
echo "Para revertir: copia cada una sobre su DLL (quitando .orig-$TS) y reinicia emby-server."
echo "(La copia más antigua de cada DLL, ls -tr <dll>.orig-* | head -1, es la original.)"
