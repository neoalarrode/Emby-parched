#!/bin/sh
# Desactiva la compartición de streams de Live TV en una instalación existente de Emby Server.
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
cp "$DLL" "$DLL.orig-$(date +%Y%m%d%H%M%S)"
systemctl stop emby-server 2>/dev/null || true
"$BIN" "$DLL"
systemctl start emby-server 2>/dev/null || true
echo "Listo. Para revertir: copia el backup $DLL.orig-* sobre $DLL y reinicia emby-server."
