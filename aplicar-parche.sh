#!/bin/sh
# Aplica los parches de Live TV (sin compartición de streams y TARGETDURATION HLS correcto)
# sobre una instalación existente de Emby Server. Solo aplica los que falten.
# Uso: sudo ./aplicar-parche.sh [/opt/emby-server/system]
set -e
SYS="${1:-/opt/emby-server/system}"
DLL="$SYS/Emby.LiveTV.dll"
DIR="$(cd "$(dirname "$0")" && pwd)"
case "$(uname -m)" in
  x86_64) BIN="$DIR/bin/linux-x64/EmbyNoShare" ;;
  aarch64|arm64) BIN="$DIR/bin/linux-arm64/EmbyNoShare" ;;
  *) echo "Arquitectura no soportada: $(uname -m)"; exit 1 ;;
esac
[ -f "$DLL" ] || { echo "No existe $DLL"; exit 1; }
rc=0; "$BIN" "$DLL" --check || rc=$?
[ "$rc" = 3 ] && { echo "Nada que hacer."; exit 0; }
[ "$rc" = 0 ] || { echo "El patrón no coincide con esta versión de Emby; no se ha tocado nada."; exit 1; }
BACKUP="$DLL.orig-$(date +%Y%m%d%H%M%S)"
cp "$DLL" "$BACKUP"
systemctl stop emby-server 2>/dev/null || true
"$BIN" "$DLL"
systemctl start emby-server 2>/dev/null || true
echo "Listo. Copia de seguridad de la DLL anterior: $BACKUP"
echo "Para revertir: sudo cp \"$BACKUP\" \"$DLL\" && sudo systemctl restart emby-server"
echo "(La copia más antigua, ls -tr \"$DLL\".orig-* | head -1, es la DLL original de Emby.)"
