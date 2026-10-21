#!/bin/sh
# Aplica los parches de Live TV sobre una instalación existente de Emby Server:
#   Emby.LiveTV.dll                -> sin compartición de streams (+ TARGETDURATION en 4.10)
#   Emby.Server.MediaEncoding.dll  -> margen HLS para Fire TV con Vega OS (EMBY_HLS_HOLDBACK)
# Solo aplica los parches que falten. Uso: sudo ./aplicar-parche.sh [/opt/emby-server/system]
set -e
SYS="${1:-/opt/emby-server/system}"
DIR="$(cd "$(dirname "$0")" && pwd)"
case "$(uname -m)" in
  x86_64) BIN="$DIR/bin/linux-x64/EmbyNoShare" ;;
  aarch64|arm64) BIN="$DIR/bin/linux-arm64/EmbyNoShare" ;;
  *) echo "Arquitectura no soportada: $(uname -m)"; exit 1 ;;
esac

# Comprueba todas las DLL antes de tocar nada
PENDING=""
for D in Emby.LiveTV.dll Emby.Server.MediaEncoding.dll; do
  [ -f "$SYS/$D" ] || { echo "No existe $SYS/$D"; exit 1; }
  rc=0; "$BIN" "$SYS/$D" --check || rc=$?
  case "$rc" in
    0) PENDING="$PENDING $D" ;;
    3) ;;
    *) echo "El patrón no coincide en $D con esta versión de Emby; no se ha tocado nada."; exit 1 ;;
  esac
done
[ -n "$PENDING" ] || { echo "Nada que hacer."; exit 0; }

TS=$(date +%Y%m%d%H%M%S)
for D in $PENDING; do cp "$SYS/$D" "$SYS/$D.orig-$TS"; done
systemctl stop emby-server 2>/dev/null || true
for D in $PENDING; do "$BIN" "$SYS/$D"; done
systemctl start emby-server 2>/dev/null || true

echo "Listo. Copias de seguridad de las DLL anteriores:"
for D in $PENDING; do echo "  $SYS/$D.orig-$TS"; done
echo "Para revertir: copia cada una sobre su DLL (quitando .orig-$TS) y reinicia emby-server."
echo "(La copia más antigua de cada DLL, ls -tr \"$SYS\"/<dll>.orig-* | head -1, es la original de Emby.)"
