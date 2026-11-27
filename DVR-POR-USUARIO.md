# Grabaciones DVR por usuario (plan)

Objetivo: que cada usuario de Emby tenga sus propias grabaciones del DVR, que vea y gestione
solo las suyas, aprovechando que dos usuarios que graban lo mismo no dupliquen ni el espacio
ni la conexión al proveedor.

Versión destino: **4.11.0.9** (la instalada; sin la verificación de integridad de la 4.11.0.19).
Se hace con el parcheador EmbyNoShare, como el resto, con copia de seguridad y verificado
contra producción. Sin tocar la licencia de Emby Premiere.

Este documento es el plan de partida. Se ajustará a los nombres y la estructura reales en
cuanto la sesión del servidor localice la DLL y las clases de grabación.

---

## Lo que hace Emby hoy

- Las grabaciones programadas (timers y series-timers) son del servidor, no de cada usuario:
  no guardan quién las pidió. Cualquier usuario con acceso al DVR ve y borra las de todos.
- Todas las grabaciones van a la(s) carpeta(s) de grabación configuradas en el DVR, y esa
  carpeta es una biblioteca normal que se comparte según los permisos de biblioteca.
- La programación (lo que se va a grabar) se gestiona en Live TV y la ve todo el mundo igual.

## Lo que queremos

1. Cada grabación sabe de quién es (qué usuario la programó).
2. El archivo grabado va a una carpeta por usuario.
3. Cada usuario ve en su biblioteca solo sus grabaciones.
4. En la guía y en la lista de "programado", cada usuario ve y cancela solo lo suyo.
5. Si dos usuarios graban el mismo programa: se graba una sola vez y el archivo se enlaza
   (hardlink) en las dos carpetas. No se duplica el espacio ni se abre el canal dos veces
   (importante con DRM). Si uno lo borra, el otro conserva el suyo.

---

## Piezas del plan

### 1. Dueño en cada timer  (parche)
Al crear un timer o series-timer, guardar el id del usuario que lo pide.
- Punto probable: el método que crea/da de alta el TimerInfo (p. ej. en LiveTvManager o un
  RecordingsManager). El id de usuario sale del contexto de la petición (ISessionContext /
  AuthorizationInfo), igual que en otros parches.
- Dónde guardarlo sin cambiar el modelo de datos: reutilizar un campo de texto ya existente
  del TimerInfo. Candidatos a estudiar en el código real:
  - un campo libre tipo `Overview`, `Name`, o metadatos del timer, con una marca
    `[owner:<userId>]` al final; o
  - si TimerInfo serializa un diccionario de propiedades extra, una clave propia.
  Se elige según lo que exista, para no romper la base de datos del DVR.

### 2. Carpeta por usuario  (parche)
Cuando Emby decide la ruta del archivo de la grabación, anteponer/insertar el nombre (o id)
del usuario dueño:  `.../Grabaciones/<usuario>/<programa>/...`.
- Punto probable: el método que compone la ruta de salida de la grabación (GetRecordingPath /
  RecordingPathResolver o equivalente). Lee el dueño del timer (pieza 1).
- Sin dueño (grabaciones viejas o del propio servidor): va a una carpeta común
  `.../Grabaciones/_comunes/`, para no perder nada.

### Estado fase B (carpetas/bibliotecas): BORRADOR, a validar en el Emby de pruebas
Helper `DvrLibraryCode` (helpers/DvrLibraryCode.cs), aún NO activado como parche:
- `SafeName`, `EnsureUserFolder` (crea `<raíz>/<usuario>`), `UserRecordingPath` (inserta la subcarpeta del usuario
  en la ruta ya formada) — listos.
- Creación de biblioteca (ILibraryManager.AddVirtualFolder(name, collectionType, LibraryOptions, refresh) +
  AddMediaPaths(CollectionFolder, MediaPathInfo[]{Path}, refresh)) y permisos — a completar en vivo.
Punto DELICADO (permisos): cada usuario debe ver SOLO sus grabaciones PERO conservar sus bibliotecas normales
(pelis/series del NAS). O sea, no vale «darle una sola carpeta»: UserPolicy.EnableAllFolders=false y EnabledFolders =
bibliotecas normales + la suya de grabaciones; y la biblioteca de grabaciones de cada uno NO entra en EnabledFolders
de los demás. Hacerlo mal deja a un usuario sin acceso a nada, por eso se afina contra el Emby de pruebas.
Ruta real: lo más limpio sería apuntar el timer a la biblioteca del usuario (ParentFolderId) para que Emby grabe ahí
de forma nativa en todas las ramas (TV/película/serie); si no, usar UserRecordingPath sobre el retorno de
EmbyTV.GetRecordingPath / GetDefaultRecordingPathForSeries. A decidir con el Emby de pruebas.
Identidad del dueño = nombre de usuario (ya aplicado), así la carpeta es `Grabaciones/<nombre>`.

### 3. Una biblioteca por usuario, automática  (parche)
Requisito del usuario: NADA a mano. Solo se configura UNA carpeta raíz de grabaciones en el
DVR de Emby. Lo demás, solo:
- La primera vez que un usuario graba (o al crear su primer timer), el parche crea su
  subcarpeta `Grabaciones/<usuario>` si no existe.
- Crea una biblioteca para esa subcarpeta si no existe (vía ILibraryManager.AddVirtualFolder /
  AddMediaPath) y
- Ajusta los permisos de ese usuario para que tenga acceso SOLO a su biblioteca de grabaciones
  (y se la quita a los demás): en la configuración de usuario, EnableAllFolders=false y su
  carpeta dentro de EnabledFolders; para los demás usuarios, su carpeta fuera de EnabledFolders.
- Idempotente: si ya existe la carpeta, la biblioteca y el permiso, no hace nada.
- Punto probable: engancharse en la creación del timer (pieza 1), que es cuando ya sabemos el
  dueño, o en el arranque de la grabación. Mejor en la creación del timer: así la biblioteca
  está lista antes de que termine de grabar.
- A estudiar en el código real: nombre exacto de AddVirtualFolder/AddMediaPath y de los campos
  de permisos de biblioteca por usuario (UserConfiguration/UserPolicy: EnableAllFolders,
  EnabledFolders). Si crear bibliotecas desde ahí fuese inviable o arriesgado, se cae a la
  vía de una sola biblioteca filtrada por dueño al listar (más frágil, segunda opción).

### 4. Programación privada y aislada  (parche)
Cada usuario tiene su propio calendario y su propia programación. No ve, ni puede modificar o
cancelar, la programación de otros.
- Listar: el método que devuelve la lista de timers/series-timers a la app (GetTimers /
  GetSeriesTimers). Filtrar por el dueño (pieza 1) contra el usuario de la petición.
- Modificar/cancelar: los métodos de cancelar/editar timer (CancelTimer, CancelSeriesTimer,
  UpdateTimer) tienen que comprobar que el timer es del usuario que lo pide; si no, denegar.
  Esto es lo que impide que uno toque lo de otro, no solo que no lo vea.
- Administrador: a decidir si ve y gestiona todo o también solo lo suyo.

### 5. Límite de grabaciones simultáneas por usuario  (parche)
Cada usuario no puede tener más de N grabaciones a la vez (solapadas en el tiempo). Si programa
una nueva que se solaparía con otras suyas y superaría su límite, se rechaza con error.
- El número N NO es un campo nuevo: se reutiliza el límite de reproducciones/streams
  simultáneos que ya tiene cada usuario en su configuración (UserPolicy; campo a confirmar:
  SimultaneousStreamLimit / MaxActiveSessions). 1 pantalla → 1 grabación. Misma semántica que
  Emby para el 0 (confirmar: 0 = ilimitado).
- Es "a la vez", no total: puede programar las que quiera a lo largo del día; solo se cuentan
  las que se solapan en horario con la nueva.
- Cuenta contra TODO, sin distinguir DRM (Emby no sabe qué canal es DRM).
- Punto: al crear timer en LiveTvService.Post(CreateTimer) (y CreateSeriesTimer). Se sacan los
  timers del usuario (por OwnerUserId, pieza 1), se cuentan los que solapan [StartDate-padding,
  EndDate+padding] con el nuevo, y si (solapados + 1) > límite, se rechaza (excepción -> la app
  muestra el error).
- Serie con límite 1 y dos episodios a la vez en canales distintos: el segundo episodio no se
  graba (se salta), igual que un choque manual. (Confirmado por el usuario como comportamiento
  por defecto.)
- El administrador: a decidir si tiene límite o no (seguramente ilimitado).

### 6. Grabación única con enlaces  (parche, el más delicado)
Si dos usuarios programan el mismo evento (mismo canal + mismo horario), no abrir dos
grabaciones: grabar una vez y, al terminar (o al crear el segundo timer), enlazar el archivo
(hardlink) dentro de la carpeta del segundo usuario.
- Punto probable: donde se decide arrancar una grabación o se detecta solape de timers.
- Requiere que las carpetas de los usuarios estén en el mismo sistema de ficheros (para el
  hardlink). Si no lo están, se cae a copia o a grabación doble (a decidir).
- Al borrar: un hardlink borrado no afecta al otro; el archivo real desaparece cuando se
  borra el último enlace. Encaja con "si uno lo borra, el otro lo conserva".
- Esta pieza es opcional en una primera fase: se puede entregar 1-4 primero (cada usuario sus
  grabaciones, duplicando espacio si coinciden) y añadir 5 después.

---

## Orden de trabajo propuesto

1. Localizar en el código real la DLL y las clases/métodos (sesión del servidor).
2. Ajustar este plan a los nombres reales: dónde está el dueño, dónde se compone la ruta,
   dónde se listan los timers.
3. Implementar en el parcheador, por fases (todo en Emby.LiveTV.Api.LiveTvService, que es la
   única capa con ISessionContext; el motor EmbyTV/LiveTvManager no conoce el usuario):
   - Fase A (bajo riesgo): dueño en el timer (pieza 1), filtrado y guardia de la programación
     (pieza 4) y límite de simultáneas (pieza 5).
   - Fase B: carpeta por usuario (pieza 2) y biblioteca automática con permisos (pieza 3).
   - Fase C: grabación única con enlaces (pieza 6), si interesa.
4. Probar en el entorno aislado (Emby 4.11.0.9) con dos usuarios de prueba.
5. Aplicar en producción con copia de seguridad y verificar.

## Dónde van los parches (código real 4.11.0.9)
- Dueño, filtrado, guardia y límite: Emby.LiveTV.Api.LiveTvService (tiene ISessionContext;
  User = sessionContext.GetUser(Request)). Manejadores: Post(CreateTimer), Post(CreateSeriesTimer),
  Post(UpdateTimer), Post(UpdateSeriesTimer), Get(GetTimers), Get(GetSeriesTimers),
  Any(CancelTimer), Any(CancelSeriesTimer).
- Dueño guardado en TimerInfo.ProviderIds["OwnerUserId"] y SeriesTimerInfo.ProviderIds (ambos
  tienen ProviderIds; no viaja a la app porque los DTO no llevan ProviderIds). En timers nacidos
  de serie, copiar el dueño en EmbyTV.CreateTimerInfoForSeries.
- Carpeta: apuntar TimerInfo.ParentFolderId a la biblioteca del usuario (opción A), sin tocar
  GetRecordingPath.
- Biblioteca automática: ILibraryManager.AddVirtualFolder + AddMediaPaths; permisos con
  IUserManager.GetUserPolicy/UpdateUserPolicy (UserPolicy.EnableAllFolders=false, EnabledFolders).

## Dudas a confirmar con el usuario

- ¿El administrador ve todas las grabaciones y toda la programación, o también solo las suyas?
- ¿Las grabaciones que ya existen hoy se reparten a alguien o se quedan en una carpeta común?
- ¿Las carpetas de los usuarios estarán en el mismo disco (para poder enlazar y no duplicar)?
- ¿Carpeta por nombre de usuario o por id? (el id no cambia si se renombra el usuario).
