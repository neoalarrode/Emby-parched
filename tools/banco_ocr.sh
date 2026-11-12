#!/bin/sh
# Banco de medida del OCR de Emby (filtro graphicsub2text de su ffmpeg) con distintas imágenes y opciones.
# Uso: sh tools/banco_ocr.sh DIR canal [canal...]   (DIR contiene <canal>.ts grabados de la salida del relevo)
# Para cada canal: referencia con la tabla de letras (dvb_lee), y para cada imagen × opción de reconocimiento, el
# .vtt del OCR y su puntuación (puntua_ocr). Resumen al final en DIR/banco.txt. Solo lee las grabaciones.
set -u
DIR="$1"; shift
HERE="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$DIR/banco.txt"; : > "$OUT"
for c in "$@"; do
  python3 "$HERE/tools/dvb_lee.py" --irelay "$HERE/tools/irelay_pruebas" --fichero "$DIR/$c.ts" > "$DIR/$c-ref.txt" 2>&1
  echo "== $c: referencia $(tail -1 "$DIR/$c-ref.txt")" | tee -a "$OUT"
  for modo in igual relleno negro relleno2 negro2; do
    python3 "$HERE/tools/ocr_variantes.py" --irelay "$HERE/tools/irelay_pruebas" "$DIR/$c.ts" "$DIR/$c-$modo.ts" "$modo" > /dev/null 2>&1 || { echo "  $modo: fallo al generar" | tee -a "$OUT"; continue; }
    for rec in emby simple; do
      case $rec in
        emby) R="recognize=halign+valign+bold+italic+underline+font+fontsize+color+outlinecolor:" ;;
        simple) R="" ;;
      esac
      (cd /opt/emby-server && ./bin/ffmpeg -hide_banner -loglevel error -c:s dvbsub -canvas_size 1920x1080 -compute_edt 1 \
        -i "$DIR/$c-$modo.ts" \
        -filter_complex "[0:s:0]graphicsub2text=delay_when_no_duration=1:language=spa:ocr_mode=tesseract:${R}tessdata_path=/var/lib/emby/cache/OcrData[s]" \
        -map "[s]" -vn -an -c:s webvtt -y "$DIR/$c-$modo-$rec.vtt") > "$DIR/$c-$modo-$rec.log" 2>&1
      echo "-- $c $modo $rec: $(python3 "$HERE/tools/puntua_ocr.py" "$DIR/$c-ref.txt" "$DIR/$c-$modo-$rec.vtt" 0 2>&1 | head -1)" | tee -a "$OUT"
    done
    rm -f "$DIR/$c-$modo.ts"
  done
done
