# emby-livetv-noshare

Emby Server – Live TV sin compartición de streams

Parches para `Emby.LiveTV.dll` (probados con Emby Server 4.10.2.0):

1. **Sin compartición de streams**: cada cliente que reproduce un canal tiene su propia conexión con el
   proveedor, su propio buffer y su propio LiveStreamId.
2. **TARGETDURATION correcto** en el HLS de Live TV, que evita entrecortes en Fire TV / Android TV
   cuando el canal tiene keyframes espaciados.

El parcheador aplica solo los parches que falten, así que se puede ejecutar sobre una DLL ya parcheada.

## Parche 1: sin compartición de streams (2 métodos, a nivel IL)

1. `Emby.LiveTV.TunerHosts.LiveStream.EnableStreamSharing` siempre devuelve `false`.
   Así `EmbyTV.GetChannelStreamWithDirectStreamProvider` deja de reutilizar un stream ya
   abierto del mismo canal y llama a `tunerHost.GetChannelStream(...)`, que abre una conexión
   nueva. El buffer (`transcoding-temp/<UniqueId>/`) y las URLs `/LiveTv/LiveStreamFiles/<UniqueId>/...`
   ya eran distintas para cada `LiveStream`.
2. `Emby.LiveTV.LiveTvManager.GetChannelStream`: el `LiveStreamId` pasa de
   `md5(proveedor)_<mediaSourceId>` a `md5(proveedor)_<mediaSourceId>_<guid>`.
   Sin esto, el segundo stream del mismo canal sobrescribiría al primero en
   `MediaSourceManager._openStreams`, y al cerrar uno se cerraría el del otro cliente.

## Parche 2: TARGETDURATION correcto en el HLS de Live TV (Fire TV / Android TV)

Cuando un cliente reproduce Live TV en *direct stream*, Emby convierte el MPEG-TS en HLS con su
propio segmentador (`/LiveTv/LiveStreamFiles/<id>/hls/live.m3u8`). El segmentador corta solo en
keyframes y con un mínimo de 3 s, así que la duración real de los segmentos depende del GOP del
canal: con keyframes cada 2,5 s salen segmentos de 5 s; cada 10 s, segmentos de 10 s.

Sin embargo, `LiveStreamService.GetVariantPlaylistFileText` escribía siempre
`#EXT-X-TARGETDURATION:4`. El código calcula el máximo de las duraciones reales pero descarta el
resultado. Eso incumple RFC 8216 (EXTINF no puede superar TARGETDURATION) y afecta a ExoPlayer/Media3
(el reproductor de Fire TV y Android TV), que usa TARGETDURATION para:

- colocarse a **3 × TARGETDURATION** del directo (12 s con el 4 fijo). Con segmentos de 6–10 s eso deja
  solo uno o dos segmentos de margen y el reproductor se queda sin buffer: entrecortes;
- recargar la playlist cada TARGETDURATION;
- dar la playlist por atascada (`PlaylistStuckException`) si no cambia en 3,5 × TARGETDURATION.

El parche guarda ese máximo y lo usa (`Math.Ceiling(max.TotalSeconds)`). Con segmentos de ~3 s
no cambia nada (sigue siendo 4); con segmentos largos el valor pasa a ser el real.

Efecto secundario: con GOPs largos el cliente se coloca más lejos del directo (3 × la duración real),
así que tendrá más retraso que otros dispositivos.

Recomendación aparte: en *Live TV → Avanzado*, "Límite de tamaño del búfer de Transmisión en vivo"
viene en **Ilimitado**. La playlist en directo incluye todos los segmentos del buffer, así que crece
sin parar (unos 1200 segmentos por hora) y el cliente la descarga entera cada pocos segundos.
Ponerlo en 1 hora limita ese crecimiento (y el disco usado).

## Instalación

Compila el parcheador (requiere el SDK de .NET 8, solo en la máquina donde compiles):

    ./build.sh

Copia la carpeta al servidor y ejecuta (x64 o arm64; el servidor no necesita .NET):

    sudo ./aplicar-parche.sh                 # por defecto /opt/emby-server/system

El script comprueba primero que el patrón IL coincide (si Emby cambia ese código, falla sin tocar nada),
guarda un backup de la DLL, aplica el parche y reinicia `emby-server`. Si la DLL ya está parcheada no hace nada.

Para revertir: el script muestra la ruta exacta de la copia de seguridad que crea en cada ejecución. La copia más antigua (`ls -tr /opt/emby-server/system/Emby.LiveTV.dll.orig-* | head -1`) es la DLL original de Emby.

Cada actualización de Emby sobrescribe la DLL, así que habrá que volver a ejecutar `aplicar-parche.sh`.

Este repositorio no incluye binarios de Emby: el parche se aplica sobre tu propia instalación.

## A tener en cuenta

- Cada espectador ocupa ahora una conexión con el proveedor. Si tu proveedor IPTV limita las conexiones
  simultáneas, el segundo cliente fallará en vez de compartir. Revisa también el campo
  "límite de streams simultáneos" del sintonizador en Emby: cuenta cada stream por separado.
- Las grabaciones del DVR también abren su propia conexión (antes podían compartir con quien estuviera viendo).
- HDHomeRun: cada espectador ocupa un sintonizador físico.

## Revisión realizada

- Todas las búsquedas de streams abiertos usan `LiveStreamId` o `UniqueId`, nunca el canal.
- Cada stream abre su propio `HttpClient` (`SingleTcpConnection`), con su conexión TCP y reintentos propios.
- La transcodificación (ffmpeg) ya era independiente por `PlaySessionId`/`DeviceId`.
- `LiveStream` es la única implementación de `ILiveStream` en el servidor.
- El plugin oficial M3UTuner (1.0.47) usa `BaseTunerHost` y crea un `MediaSourceInfo` nuevo en cada apertura.

## Estructura

- `src/`: parcheador (`EmbyNoShare`, Mono.Cecil).
- `aplicar-parche.sh`: aplica el parche sobre una instalación existente.
- `build.sh`: compila el parcheador como binario autónomo para linux-x64 y linux-arm64.

## Verificar que funciona

En `embyserver.txt`, al abrir el mismo canal en dos dispositivos deberían aparecer dos
"Live stream opened after ...ms" y dos `LiveStreamId` distintos (acabados en un guid diferente),
y "consumer count is now 1" en cada uno.
