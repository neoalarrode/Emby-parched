# emby-livetv-noshare

Emby Server – Live TV sin compartición de streams

Parches para Emby Server (probados con 4.10.2.0 y 4.11.0.9):

1. **Sin compartición de streams**: cada cliente que reproduce un canal tiene su propia conexión con el
   proveedor, su propio buffer y su propio LiveStreamId.
2. **TARGETDURATION correcto** en el HLS de Live TV cuando el canal tiene keyframes espaciados
   (solo 4.10; Emby 4.11 ya lo corrige y el parcheador lo omite).
3. **Margen HLS para Fire TV con Vega OS**, que evita los cortes cuando la fuente llega a golpes.
4. **Caché de la guía XMLTV**: los plugins "Xml TV" y M3U dejan de acumular una copia de la guía por cada
   actualización.
5. **Pista de audio según el idioma del perfil** en canales con varias pistas (`esp`, `ads`, `qaa`…).
6. **Robustez de la TV en directo**: abrir un canal no bloquea al resto, cierre de streams huérfanos,
   reconexiones más tolerantes con el proveedor y ventana deslizante en el HLS convertido (disco acotado).

El parcheador aplica solo los parches que falten (y omite los que la versión de Emby ya no necesita), así que se puede ejecutar sobre una instalación ya parcheada.

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

## Parche 2: TARGETDURATION correcto en el HLS de Live TV (solo Emby 4.10)

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

## Parche 3: margen HLS para Fire TV con Vega OS (`Emby.Server.MediaEncoding.dll`)

La app de Emby para Fire TV con Vega OS no admite MPEG-TS, así que Emby le convierte el canal a HLS
con ffmpeg y le sirve un `live.m3u8` (`BaseHlsService.GetLivePlaylistText`). El reproductor se coloca a
3 × TARGETDURATION = 12 s del directo. Si la fuente llega a golpes con huecos más largos (se han visto
huecos de 12–18 s cada 40–90 s), se queda sin buffer y se corta. Los clientes que reciben el `.ts`
directo (Apple TV) no lo notan.

Para los clientes cuyo User-Agent contiene `Kepler` (Vega OS), el parche añade a esa playlist:

    #EXT-X-START:TIME-OFFSET=-30
    #EXT-X-SERVER-CONTROL:HOLD-BACK=30

que indican al reproductor que se coloque a 30 s del directo. El resto de clientes no cambia.

Se ajusta en `/etc/emby-server.conf` (se conserva al actualizar Emby) y se aplica tras
`sudo systemctl restart emby-server`:

    EMBY_HLS_HOLDBACK=30          # segundos de margen; 0 lo desactiva (por defecto 30)
    EMBY_HLS_HOLDBACK_UA=Kepler   # texto del User-Agent que lo activa; vacío = todos los clientes

Para que haya ese margen desde el primer segundo, Dispatcharr debe entregar al menos esos segundos al
abrir el canal: "New Client Buffer (seconds)" ≥ EMBY_HLS_HOLDBACK. Si es menor, el reproductor empieza
con el margen que haya.

Efecto secundario: el Fire irá ~30 s por detrás del directo.

## Parche 4: caché de la guía XMLTV (plugins "Xml TV" y M3U)

Los plugins "Xml TV" (`Emby.XmlTV.dll`) y M3U (`Emby.M3UTuner.dll`) guardan cada copia descomprimida
de la guía como `md5(URL)_<clave>.xml` en `/var/lib/emby/cache/xmltv` o `/var/lib/emby/cache/m3u-xmltv`.
La clave es aleatoria y cambia en cada actualización de la guía (`ClearProgramCaches`, al empezar y al
terminar) y en cada reinicio de Emby, pero las copias anteriores nunca se borran: solo las elimina la tarea
"Eliminar archivos de caché" cuando tienen más de 30 días. Con guías grandes se acumulan muchos GB.

El parche hace que `XmlTvProvider.ClearProgramCaches()`, tras cambiar la clave, borre la carpeta de
caché de ese plugin (todas sus copias han quedado obsoletas; la siguiente descarga la vuelve a crear).
Lo hace con el semáforo de descargas tomado, para no pisar una descarga en curso, e ignora cualquier
error. Si el nombre de la carpeta estuviera vacío no borra nada.

El script parchea los plugins solo si están instalados en `/var/lib/emby/plugins` (o en la carpeta que
se le pase como segundo argumento). Al actualizar un plugin desde Emby se pierde el parche: basta con
volver a ejecutar el script.

## Parche 5: pista de audio según el idioma del perfil

Con canales de varias pistas (`esp`, `spa`, `ads`, `qaa`…) Emby podía elegir una que no es el idioma
preferido del usuario. Había cuatro causas, y se corrigen todas:

1. **Códigos de idioma no reconocidos** (`Emby.Server.Implementations.dll`, `LocalizationManager`):
   la tabla ISO 639 de Emby solo reconoce `spa`, `es` y `Spanish` como español; `esp` o `cas` contaban
   como "sin idioma". Ahora, si un código no está en la tabla, se busca en `EMBY_LANG_ALIASES`
   (por defecto `esp=spa,cas=spa,castellano=spa,espanol=spa,español=spa,castilian=spa`).
2. **Pista "predeterminada" del origen por delante del idioma** (`MediaSourceManager.SetDefaultAudioStreamIndex`):
   con la opción de perfil "reproducir la pista de audio predeterminada", la marca del canal ganaba al
   idioma. En TV en directo (streams infinitos) ahora manda el idioma; en vídeos normales no cambia nada.
3. **Audiodescripción** (`MediaBrowser.Model.dll`, `MediaStreamSelector.GetDefaultAudioStreamIndex`):
   Emby no la distingue. Ahora las pistas cuyo idioma está en `EMBY_AUDIO_DESC_LANGS` (por defecto
   `ads,qad`) o cuyo título contiene "audiodescrip"/"audio descrip" van al final: solo se eligen si no hay otra.
4. **Número de pista enviado por la app al abrir el canal** (`Emby.Server.MediaEncoding.dll`,
   `MediaInfoService.OpenMediaSource`): si la petición traía `AudioStreamIndex` (p. ej. 1, la primera pista
   antes de conocer las del canal), Emby lo usaba en lugar de su elección por idioma. Al abrir un canal se
   usa ahora siempre la pista elegida por el servidor. Cambiar de pista con el canal ya abierto sigue
   respetando la que elija el usuario.

Variables (en `/etc/emby-server.conf`, reiniciando `emby-server` después):

    EMBY_LANG_ALIASES=esp=spa,cas=spa     # alias=código ISO 639-2, separados por comas
    EMBY_AUDIO_DESC_LANGS=ads,qad         # códigos de idioma de pistas de audiodescripción

## Parche 6: robustez de la TV en directo

1. **Abrir un canal sin bloquear al resto** (`Emby.Server.Implementations.dll`,
   `MediaSourceManager.OpenLiveStreamInternal2`): Emby retenía un semáforo global durante toda la apertura de
   un canal (conexión con el proveedor, espera del buffer, ffprobe), así que mientras un cliente abría un canal
   lento, cualquier otro cliente que abriera o cerrara un canal esperaba. Sin compartición de streams no hace
   falta (los streams se guardan en un `ConcurrentDictionary`): ahora las aperturas van en paralelo. Cerrar
   streams sigue usando el semáforo.
2. **Cerrar streams huérfanos** (`Emby.LiveTV.dll`, `SharedHttpPipelineSource`): si una app se cierra sin
   avisar (Fire TV que se apaga, app que se mata), Emby nunca cerraba el stream: seguía descargando del proveedor
   y escribiendo el buffer en disco indefinidamente, ocupando una conexión de Dispatcharr. Ahora cada stream
   anota cuándo lo leyó alguien por última vez (stream `.ts`, playlists y segmentos HLS) y, si nadie lo lee en
   `EMBY_LIVE_IDLE_MINUTES` minutos (por defecto 20), se cierra igual que cuando el cliente para. En el log:
   `EmbyNoShare: nadie lee el stream ... se cierra`. Las grabaciones leen continuamente, así que no les afecta;
   una pausa de más de esos minutos sí cierra el canal (al reanudar, la app lo vuelve a abrir).
3. **Reconexiones más tolerantes** (`Emby.LiveTV.dll`, `LimitingRetryHandler`): cuando el proveedor corta la
   conexión, Emby reconecta, pero como mucho 2 veces en 5 minutos, 4 en 30 y 6 en una hora; a la siguiente
   cerraba el canal ("Shut down stream ... does not allow more than ..."). Ahora esos límites se multiplican por
   `EMBY_LIVE_RECONNECT_FACTOR` (por defecto 5: 10 / 20 / 30). `1` deja los de Emby.
4. **Ventana deslizante en el HLS convertido** (`Emby.Server.MediaEncoding.dll`, `BaseHlsService.GetLivePlaylistText`):
   cuando Emby convierte un canal a HLS con ffmpeg (Fire TV), la playlist era de tipo EVENT y nunca borraba
   segmentos: la carpeta de transcodificación crecía sin parar (del orden de 1 GB por hora, según la calidad) y la playlist, que el cliente descarga cada
   pocos segundos, también. Ahora la playlist contiene solo los últimos `EMBY_LIVE_HLS_WINDOW_MINUTES` minutos
   (por defecto 30), con `#EXT-X-MEDIA-SEQUENCE` correcto, y los segmentos que salen de la ventana se borran del
   disco. `0` deja el comportamiento original.

Variables (en `/etc/emby-server.conf`, reiniciando `emby-server` después):

    EMBY_LIVE_IDLE_MINUTES=20          # minutos sin lectores para cerrar un stream; 0 lo desactiva
    EMBY_LIVE_RECONNECT_FACTOR=5       # multiplica los límites de reconexión; 1 = los de Emby
    EMBY_LIVE_HLS_WINDOW_MINUTES=30    # minutos que conserva el HLS convertido; 0 = sin límite (original)

## Instalación

Desde la última release (ya compilado, no requiere .NET):

    cd /tmp
    curl -fsSL -o emby-parched-linux.tar.gz \
      https://github.com/neoalarrode/Emby-parched/releases/latest/download/emby-parched-linux.tar.gz
    tar xzf emby-parched-linux.tar.gz
    cd emby-parched
    sudo ./aplicar-parche.sh

O compilándolo tú (requiere el SDK de .NET 8, solo en la máquina donde compiles):

    ./build.sh

Copia la carpeta al servidor y ejecuta (x64 o arm64; el servidor no necesita .NET):

    sudo ./aplicar-parche.sh                 # por defecto /opt/emby-server/system

El script comprueba primero que el patrón IL coincide (si Emby cambia ese código, falla sin tocar nada),
guarda un backup de la DLL, aplica el parche y reinicia `emby-server`. Si la DLL ya está parcheada no hace nada.

Para revertir: el script muestra la ruta exacta de cada copia de seguridad que crea (`<dll>.orig-<fecha>`, junto a cada DLL). Copia la que quieras sobre su DLL y reinicia `emby-server`. La copia más antigua de cada DLL es la original.

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
- `.github/workflows/release.yml`: al crear una etiqueta `v*` compila y publica la release.
- `build.sh`: compila el parcheador como binario autónomo para linux-x64 y linux-arm64.
- `tools/emby-sesiones.sh`: diagnóstico a partir de los logs de Emby. Sin argumentos lista las sesiones
  de reproducción (dispositivo, app, canal, PlaySessionId); con un PlaySessionId muestra IP, motivos de
  conversión, tiempos de entrega de segmentos y el ffmpeg asociado.

## Verificar que funciona

En `embyserver.txt`, al abrir el mismo canal en dos dispositivos deberían aparecer dos
"Live stream opened after ...ms" y dos `LiveStreamId` distintos (acabados en un guid diferente),
y "consumer count is now 1" en cada uno.
