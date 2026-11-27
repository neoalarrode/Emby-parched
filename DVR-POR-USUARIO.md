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

### 5. Grabación única con enlaces  (parche, el más delicado)
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
3. Implementar en el parcheador, por partes: primero 1 y 2 (dueño + carpeta), luego 4
   (programación privada), luego 3 por configuración, y 5 al final si interesa.
4. Probar en el entorno aislado (Emby 4.11.0.9) con dos usuarios de prueba.
5. Aplicar en producción con copia de seguridad y verificar.

## Dudas a confirmar con el usuario

- ¿El administrador ve todas las grabaciones y toda la programación, o también solo las suyas?
- ¿Las grabaciones que ya existen hoy se reparten a alguien o se quedan en una carpeta común?
- ¿Las carpetas de los usuarios estarán en el mismo disco (para poder enlazar y no duplicar)?
- ¿Carpeta por nombre de usuario o por id? (el id no cambia si se renombra el usuario).
