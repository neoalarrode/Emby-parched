#!/bin/sh
# Compila el parcheador como binario autónomo (no requiere .NET en el servidor).
# Requiere el SDK de .NET 8. Salida: bin/linux-x64/EmbyNoShare y bin/linux-arm64/EmbyNoShare
set -e
DIR="$(cd "$(dirname "$0")" && pwd)"
for r in linux-x64 linux-arm64; do
  dotnet publish "$DIR/src" -c Release -r "$r" --self-contained \
    -p:PublishSingleFile=true -p:PublishTrimmed=true -p:TrimMode=partial \
    -p:DebugType=none -o "$DIR/bin/$r"
done
