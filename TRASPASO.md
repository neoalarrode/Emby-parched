# Traspaso: TV en directo (Dispatcharr + Intro Relay + Emby parcheado)

Notas para continuar el trabajo desde una sesión que corre en el propio servidor `emby`.
Escritas el 2026-11-11 desde una sesión en la nube que no podía llegar al servidor.

## Cómo trabajar con el usuario

- Responder siempre en español. Quiere hechos y pruebas, no explicaciones largas.
- No entregar nada sin probarlo de punta a punta. Las dos últimas versiones del parche se hicieron suponiendo
  cómo se comporta el reproductor de Apple y fallaron.
- El vídeo se copia, nunca se recodifica.

## Límites que no se tocan

- La licencia Emby Premiere del usuario no se anula ni se esquiva bajo ningún concepto.
- `EMBY_PREMIERE_KEY` es una variable de entorno: no imprimirla, registrarla ni enviarla.
- No llamar a `GET /Plugins/SecurityInfo`.
- No pedir al usuario que pegue secretos. Al mostrar logs, quitar claves:
  `sed -E 's/(api_key|X-Emby-Token|Token)=[^&" ]*/\1=XXX/g'`
- Las URL de las fuentes DRM llevan credenciales en `u=` y `p=`: enmascararlas siempre.

## Montaje

- Emby 4.11.0.9 instalado de forma nativa en el host `emby` (no es un contenedor):
  `/opt/emby-server/system`, logs en `/var/lib/emby/logs`, servicio `emby-server`.
- Dispatcharr en docker (contenedor `dispatcharr`), con salida HLS hacia Emby.
- Plugin Intro Relay de Dispatcharr (zips `intro_relay-X.Y.Z.zip`). Última versión entregada: 0.16.3.
  **El usuario lo ha desinstalado** porque con él había fallos de reproducción. Su código no está en ningún
  repositorio: solo en los zips que tiene el usuario.
- Cadena: fuente -> relevo del plugin -> HLS de Dispatcharr -> ffmpeg de Emby -> HLS de Emby -> app.
  Lo que ve el iPhone o el Apple TV es el HLS de Emby, no el de Dispatcharr.

## Parcheador de Emby (este repositorio)

- `src/Program.cs` (Mono.Cecil), `./build.sh`, `aplicar-parche.sh`. Cada parche deja un tipo marcador
  `EmbyNoShare.<Marcador>` y solo se aplican los que faltan. Copia de seguridad `*.orig-<fecha>` junto a cada DLL.
- Parches de subtítulos en `Emby.Server.MediaEncoding.dll`, todos aplicados en el servidor:
  `live-subtitles-all`, `live-subtitles-job`, `subtitles-ocr-keep-codec`, `live-subtitles-nowait`,
  `live-subtitles-headers`, `dvb-subtitle-language`.
- `tools/diagnostico.sh`: resumen de las últimas reproducciones a partir de los logs, sin claves.

## Estado real a día de hoy

Arreglado y confirmado por el usuario:
- Los canales se cerraban solos al abrir ("cliente fantasma" de Dispatcharr): corregido en el plugin 0.16.1.

Sin confirmar en dispositivos reales:
- `live-subtitles-nowait` y `live-subtitles-headers`. El primero quitó una espera de 20 s al abrir, pero el
  usuario dice que con ellos los subtítulos no aparecen en iPhone / Apple TV. En web sí funcionaba.

Defecto encontrado en un banco de pruebas, sin arreglar:
- En directo, `Get(GetLiveSubtitlePlaylist)` devuelve tal cual la lista que escribe ffmpeg
  (`-f segment -segment_format webvtt -segment_time 3 -write_empty_segments 1`). ffmpeg solo cierra un trozo
  de subtítulos cuando llega la frase siguiente, así que:
  - hay trozos con `#EXTINF:0.000000` y otros de 5 a 7 s;
  - la lista se queda por detrás del vídeo (13 s a los 2 minutos de prueba);
  - la última frase dicha no se publica hasta que llega otra.
- Regla comprobada (35 de 35 frases): el fichero `<DIR>_s<idx>_<k>.vtt` contiene las frases cuyo inicio cae en
  `[3k, 3k+3)` s, con 0 = primer PTS del vídeo de salida (`X-TIMESTAMP-MAP=MPEGTS:900000`, vídeo desde PTS 10 s).
  El trozo abierto está en `<...>_<k>.vtt.tmp`.
- El segmento `.vtt` lo sirve `VideoHlsService.Get(GetHlsVideoSegmentLegacy)`, que lee el fichero directamente y
  falla si no existe.

Arreglo previsto (no escrito):
1. Generar la lista de subtítulos en Emby: un trozo de 3 s por cada 3 s de vídeo ya emitido (menos un trozo de
   margen), `TARGETDURATION` fijo, mismas cabeceras siempre.
2. Al pedir un trozo que ffmpeg no ha cerrado: servir el `.tmp` si es el suyo, o un WebVTT vacío.
3. Problema abierto: cuánto vídeo lleva emitido. En el banco, la suma de `#EXTINF` de la lista de vídeo quedaba
   19 s por debajo de los PTS reales a los 2 minutos, y `TranscodingJob.TranscodingPositionTicks` vale 0 en
   directo. Hay que medirlo por PTS (por ejemplo, leyendo el último segmento de vídeo) y comprobar primero si
   ese desfase se da también en el servidor real o era cosa de la fuente de prueba.

Sin causa conocida:
- Los "fallos de reproducción" con el plugin instalado. No se reproducen en el banco. Falta saber qué falla
  exactamente (no abre, se corta, se congela), en qué dispositivo y en qué canal.

## Primeros pasos recomendados en el servidor

1. `sudo sh tools/diagnostico.sh` y leer `/tmp/diagnostico.txt`: qué pidió cada dispositivo en las últimas
   sesiones y con qué respuesta. Los logs de antes de desinstalar el plugin siguen en disco.
2. Preguntar al usuario qué falla exactamente y pedirle una prueba concreta mientras se miran los logs en vivo.
3. Con una sesión real en marcha, comparar en `transcoding-temp/<DIR>/` la lista de vídeo con la de subtítulos
   para confirmar el defecto de arriba con canales reales (DRM con WebVTT y UDP con DVB + OCR).
4. Solo entonces escribir el arreglo, probarlo con el iPhone del usuario delante y entregarlo.

## Pendiente de más adelante

- Canales UDP (solo subtítulos DVB en imagen): reconocimiento por tabla de letras de la cadena para no depender
  del OCR. Hecho para La 1 en pruebas (`irelay/glyphs.py`, `irelay/letras.json`), sin integrar en el relevo.
- La variante de la intro falla al generarse para 720x512 (ffmpeg sale con 234).
