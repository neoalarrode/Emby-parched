# emby-livetv-noshare

Emby Server – Live TV sin compartición de streams

Parche para `Emby.LiveTV.dll` (probado con Emby Server 4.10.2.0). Hace que cada cliente que
reproduce un canal tenga su propia conexión con el proveedor, su propio buffer y su propio
LiveStreamId.

## Qué cambia (2 métodos, a nivel IL)

1. `Emby.LiveTV.TunerHosts.LiveStream.EnableStreamSharing` siempre devuelve `false`.
   Así `EmbyTV.GetChannelStreamWithDirectStreamProvider` deja de reutilizar un stream ya
   abierto del mismo canal y llama a `tunerHost.GetChannelStream(...)`, que abre una conexión
   nueva. El buffer (`transcoding-temp/<UniqueId>/`) y las URLs `/LiveTv/LiveStreamFiles/<UniqueId>/...`
   ya eran distintas para cada `LiveStream`.
2. `Emby.LiveTV.LiveTvManager.GetChannelStream`: el `LiveStreamId` pasa de
   `md5(proveedor)_<mediaSourceId>` a `md5(proveedor)_<mediaSourceId>_<guid>`.
   Sin esto, el segundo stream del mismo canal sobrescribiría al primero en
   `MediaSourceManager._openStreams`, y al cerrar uno se cerraría el del otro cliente.

## Instalación

Compila el parcheador (requiere el SDK de .NET 8, solo en la máquina donde compiles):

    ./build.sh

Copia la carpeta al servidor y ejecuta (x64 o arm64; el servidor no necesita .NET):

    sudo ./aplicar-parche.sh                 # por defecto /opt/emby-server/system

El script comprueba primero que el patrón IL coincide (si Emby cambia ese código, falla sin tocar nada),
guarda un backup de la DLL, aplica el parche y reinicia `emby-server`. Si la DLL ya está parcheada no hace nada.

Para revertir: copia el backup `Emby.LiveTV.dll.orig-*` sobre `Emby.LiveTV.dll` y reinicia el servicio.

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
