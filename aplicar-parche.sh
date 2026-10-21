#!/bin/sh
# Aplica los parches de Live TV sobre una instalación existente de Emby Server:
#   Emby.LiveTV.dll                -> sin compartición de streams (+ TARGETDURATION en 4.10)
#   Emby.Server.MediaEncoding.dll  -> margen HLS para Fire TV con Vega OS (EMBY_HLS_HOLDBACK)
#   Emby.Server.Implementations.dll, MediaBrowser.Model.dll (+ MediaEncoding) -> selección de la pista
#                                     de audio por idioma (alias esp..., audiodescripción al final, TV en directo)
#   plugins Emby.XmlTV.dll y Emby.M3UTuner.dll (si están instalados) -> borrar las copias
#                                     antiguas de la guía XMLTV en cada actualización
# Solo aplica los parches que falten.
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

# Comprueba todas las DLL antes de tocar nada
PENDING=""
for F in $FILES; do
  rc=0; "$BIN" "$F" --check || rc=$?
  case "$rc" in
    0) PENDING="$PENDING $F" ;;
    3) ;;
    *) echo "El patrón no coincide en $F con esta versión; no se ha tocado nada."; exit 1 ;;
  esac
done
[ -n "$PENDING" ] || { echo "Nada que hacer."; exit 0; }

TS=$(date +%Y%m%d%H%M%S)
for F in $PENDING; do cp -p "$F" "$F.orig-$TS"; done
systemctl stop emby-server 2>/dev/null || true
for F in $PENDING; do "$BIN" "$F"; done
systemctl start emby-server 2>/dev/null || true

echo "Listo. Copias de seguridad de las DLL anteriores:"
for F in $PENDING; do echo "  $F.orig-$TS"; done
echo "Para revertir: copia cada una sobre su DLL (quitando .orig-$TS) y reinicia emby-server."
echo "(La copia más antigua de cada DLL, ls -tr <dll>.orig-* | head -1, es la original.)"
