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

### 3. Una biblioteca por usuario  (configuración, SIN parche)
Cada carpeta `Grabaciones/<usuario>` se añade como biblioteca propia, y en los permisos de
cada usuario se le da acceso solo a la suya. Esto ya existe en Emby; no hay que parchear.
- Alternativa si se prefiere una sola biblioteca: un parche que filtre las grabaciones por
  dueño al listar, pero es más frágil. Se prefiere la vía de permisos nativa.

### 4. Programación privada  (parche)
En la guía y en la lista de grabaciones programadas, que cada usuario vea y cancele solo las
suyas.
- Punto probable: el método que devuelve la lista de timers/series-timers a la app
  (GetTimers / GetSeriesTimers). Filtrar por el dueño (pieza 1) contra el usuario de la
  petición. Un usuario administrador puede ver todas (a decidir).

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
