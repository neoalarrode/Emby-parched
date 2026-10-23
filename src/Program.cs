// Parches para Emby Server (Live TV). Cada parche pertenece a una DLL y lleva su propio
// marcador en ella, así que el parcheador solo aplica los que falten. Un parche cuyo arreglo
// ya trae la versión de Emby se da por "no necesario" y se omite.
//
// Emby.LiveTV.dll
//   noshare (marcador EmbyNoShare.Patched): desactiva la compartición de streams de Live TV.
//     a) Emby.LiveTV.TunerHosts.LiveStream::get_EnableStreamSharing -> siempre false.
//        EmbyTV.GetChannelStreamWithDirectStreamProvider deja de reutilizar un
//        ILiveStream abierto del mismo canal y abre una conexión nueva al proveedor
//        (con su propio buffer en disco/HLS, ya que cada LiveStream tiene UniqueId).
//     b) Emby.LiveTV.LiveTvManager::GetChannelStream -> LiveStreamId único por stream.
//        Originalmente LiveStreamId = md5(provider) + "_" + mediaSource.Id, igual para
//        todos los clientes del mismo canal; sin compartición el segundo stream pisaría
//        al primero en MediaSourceManager._openStreams. Se añade "_" + Guid.
//
//   hls-targetduration (marcador EmbyNoShare.HlsTargetDuration):
//     Emby.LiveTV.Api.LiveStreamService::GetVariantPlaylistFileText escribe siempre
//     #EXT-X-TARGETDURATION:4 (SegmentDuration 3 s + 1), pero el segmentador solo corta en
//     keyframes, así que con GOPs largos los segmentos duran 5, 6, 10 s... El método ya
//     calcula max(duración de los segmentos, SegmentDuration + 1) y descarta el resultado;
//     el parche lo guarda y lo usa para TARGETDURATION, como exige RFC 8216 (4.3.3.1).
//     Emby 4.11 ya lo corrige (usa segments.Max), así que ahí no es necesario.
//
// Emby.Server.MediaEncoding.dll
//   hls-holdback (marcador EmbyNoShare.HlsHoldBack):
//     BaseHlsService::GetLivePlaylistText genera el live.m3u8 de las conversiones a HLS de
//     Live TV. Los reproductores HLS se colocan a 3 x TARGETDURATION del directo (12 s), y si
//     la fuente llega a golpes con huecos más largos se quedan sin buffer. Para los clientes
//     cuyo User-Agent contiene EMBY_HLS_HOLDBACK_UA (por defecto "Kepler", Fire TV con Vega OS)
//     se añaden #EXT-X-START:TIME-OFFSET=-N y #EXT-X-SERVER-CONTROL:HOLD-BACK=N, con N en
//     segundos de EMBY_HLS_HOLDBACK (por defecto 30; 0 lo desactiva). Ambas variables se leen
//     del entorno en cada petición (en Debian/Ubuntu, /etc/emby-server.conf).
//
// Plugins "Xml TV" (Emby.XmlTV.dll) y M3U (Emby.M3UTuner.dll), en /var/lib/emby/plugins
//   xmltv-cache-cleanup (marcador EmbyNoShare.XmlTvCacheCleanup):
//     Emby.XmlTV.XmlTvProvider guarda cada copia de la guía como md5(URL)_<clave>.xml en
//     cache/<CachePathFolderName> ("xmltv" o "m3u-xmltv"), y ClearProgramCaches() genera una
//     clave nueva en cada actualización de la guía (y cada reinicio crea otra), pero nunca borra
//     las copias anteriores: solo las elimina la tarea de limpieza de caché a los 30 días. El
//     parche hace que ClearProgramCaches() borre esa carpeta tras cambiar la clave (todas sus
//     copias han quedado obsoletas), con el semáforo de descargas tomado para no pisar una
//     descarga en curso e ignorando cualquier error. Si el nombre de carpeta está vacío no borra nada.
//
// Selección de pista de audio (que gane siempre el idioma preferido del usuario):
//   lang-aliases (Emby.Server.Implementations.dll, marcador EmbyNoShare.LangAliases):
//     LocalizationManager.FindLanguageInfosInternal solo reconoce los códigos de su tabla ISO 639
//     (para español: spa, es, Spanish). Si el código no está, se busca en EMBY_LANG_ALIASES
//     ("alias=código,...", por defecto esp/cas/castellano/espanol/español/castilian=spa).
//   live-audio-language-first (Emby.Server.Implementations.dll, EmbyNoShare.LiveAudioLanguageFirst):
//     MediaSourceManager.SetDefaultAudioStreamIndex: en streams infinitos (TV en directo) la opción
//     "reproducir la pista predeterminada" no pasa por delante del idioma preferido.
//   audio-description-last (MediaBrowser.Model.dll, EmbyNoShare.AudioDescriptionLast):
//     MediaStreamSelector.GetDefaultAudioStreamIndex relega al final las pistas de audiodescripción:
//     idioma en EMBY_AUDIO_DESC_LANGS (por defecto "ads,qad") o título con "audiodescrip"/"audio descrip".
//   live-open-server-audio (Emby.Server.MediaEncoding.dll, EmbyNoShare.LiveOpenServerAudio):
//     MediaInfoService.OpenMediaSource (abrir un canal, también desde PlaybackInfo con
//     AutoOpenLiveStream) ya no pasa el AudioStreamIndex de la petición, así que se usa la pista
//     elegida por el servidor. Cambiar de pista con el stream ya abierto sigue respetando la del cliente.
//
// Robustez de TV en directo:
//   live-open-no-global-lock (Emby.Server.Implementations.dll, EmbyNoShare.LiveOpenNoGlobalLock):
//     MediaSourceManager.OpenLiveStreamInternal2 retenía el semáforo global _liveStreamSemaphore
//     durante toda la apertura de un canal (conexión, espera del buffer, ffprobe), así que abrir o
//     cerrar cualquier otro canal esperaba. Sin compartición de streams no hace falta: los streams
//     se guardan en un ConcurrentDictionary. Se elimina la espera y la liberación en ese método
//     (cerrar streams sigue usando el semáforo entre sí).
//   live-idle-close (Emby.LiveTV.dll, EmbyNoShare.LiveIdleClose):
//     Si una app desaparece sin parar la reproducción, el stream seguía abierto para siempre (conexión con el
//     proveedor y buffer en disco). SharedHttpPipelineSource.GetSegments (lo usan todos los lectores) anota la
//     hora de lectura en LiveStream.EmbyNoShare_LastRead; OnSegmentCompleted comprueba si nadie lee desde hace
//     EMBY_LIVE_IDLE_MINUTES (por defecto 20; 0 lo desactiva) y lo cierra con MediaSourceManager.CloseLiveStream.
//   live-reconnect-factor (Emby.LiveTV.dll, EmbyNoShare.LiveReconnectFactor):
//     LimitingRetryHandler.Add multiplica el máximo de reconexiones por EMBY_LIVE_RECONNECT_FACTOR (por
//     defecto 5). Emby solo permitía 2 reconexiones en 5 min, 4 en 30 min y 6 en 1 h antes de cerrar el canal.
// Emby.Server.MediaEncoding.dll
//   live-hls-window (marcador EmbyNoShare.LiveHlsWindow):
//     BaseHlsService.GetLivePlaylistText (HLS convertido con ffmpeg) era una playlist EVENT que nunca borraba
//     segmentos. Ahora solo lista los últimos EMBY_LIVE_HLS_WINDOW_MINUTES (por defecto 30; 0 = original), sin
//     EVENT y con MEDIA-SEQUENCE igual a los segmentos descartados, y borra del disco los que salen de la ventana.
// Emby.LiveTV.dll
//   live-fast-open (marcador EmbyNoShare.LiveFastOpen):
//     SharedHttpPipelineSource.OpenInternal: al abrir un canal, Emby trocea lo recibido en segmentos internos de
//     3 s (a ritmo real) y no da el canal por abierto (ffprobe y respuesta al cliente) hasta tener 2 completos:
//     ~7-8 s de espera aunque la fuente (p. ej. la intro de Dispatcharr) llegue al instante. Ahora los segmentos
//     duran EMBY_LIVE_SEGMENT_SECONDS (por defecto 1; 3 = original) y se espera a EMBY_LIVE_OPEN_SEGMENTS (por
//     defecto 1; 2 = original). La duración de segmento también la usan las conversiones a HLS de esos canales.
//
// Uso: EmbyNoShare <ruta/Emby.LiveTV.dll | ruta/Emby.Server.MediaEncoding.dll | ruta/plugin.dll | ...> [--check]
//   --check: 0 = hay parches pendientes y todos los patrones coinciden, 3 = nada que hacer
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

if (args.Length < 1)
{
    Console.Error.WriteLine("Uso: EmbyNoShare <DLL de Emby o plugin> [--check]");
    return 2;
}

var path = Path.GetFullPath(args[0]);
var checkOnly = args.Contains("--check");
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(path)!);

using var asm = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { AssemblyResolver = resolver, InMemory = true });
var module = asm.MainModule;

var patches = new (string Assembly, string Marker, string Name, Func<ModuleDefinition, Action?> Prepare)[]
{
    ("Emby.LiveTV", "Patched", "noshare", PrepareNoShare),
    ("Emby.LiveTV", "HlsTargetDuration", "hls-targetduration", PrepareHlsTargetDuration),
    ("Emby.Server.MediaEncoding", "HlsHoldBack", "hls-holdback", PrepareHlsHoldBack),
    ("Emby.XmlTV", "XmlTvCacheCleanup", "xmltv-cache-cleanup", PrepareXmlTvCacheCleanup),
    ("Emby.M3UTuner", "XmlTvCacheCleanup", "xmltv-cache-cleanup", PrepareXmlTvCacheCleanup),
    ("Emby.Server.Implementations", "LangAliases", "lang-aliases", PrepareLangAliases),
    ("Emby.Server.Implementations", "LiveAudioLanguageFirst", "live-audio-language-first", PrepareLiveAudioLanguageFirst),
    ("MediaBrowser.Model", "AudioDescriptionLast", "audio-description-last", PrepareAudioDescriptionLast),
    ("Emby.Server.MediaEncoding", "LiveOpenServerAudio", "live-open-server-audio", PrepareLiveOpenServerAudio),
    ("Emby.Server.Implementations", "LiveOpenNoGlobalLock", "live-open-no-global-lock", PrepareLiveOpenNoGlobalLock),
    ("Emby.LiveTV", "LiveIdleClose", "live-idle-close", PrepareLiveIdleClose),
    ("Emby.LiveTV", "LiveReconnectFactor", "live-reconnect-factor", PrepareLiveReconnectFactor),
    ("Emby.Server.MediaEncoding", "LiveHlsWindow", "live-hls-window", PrepareLiveHlsWindow),
    ("Emby.LiveTV", "LiveFastOpen", "live-fast-open", PrepareLiveFastOpen),
};

var applicable = patches.Where(x => x.Assembly == asm.Name.Name).ToList();
if (applicable.Count == 0)
{
    Console.Error.WriteLine($"No hay parches para {asm.Name.Name}");
    return 2;
}

// Localiza y valida todos los parches pendientes antes de tocar nada
var pending = new List<(string Marker, string Name, Action Apply)>();
foreach (var (_, marker, name, prepare) in applicable)
{
    if (module.Types.Any(t => t.FullName == "EmbyNoShare." + marker))
    {
        Console.WriteLine($"[{name}] ya aplicado");
        continue;
    }
    var apply = prepare(module);
    if (apply == null)
    {
        Console.WriteLine($"[{name}] no necesario en esta versión de Emby");
        continue;
    }
    pending.Add((marker, name, apply));
}

if (pending.Count == 0)
{
    Console.WriteLine("Nada que hacer: " + path);
    return checkOnly ? 3 : 0;
}

if (checkOnly)
{
    Console.WriteLine("Comprobación OK, el patrón coincide. Pendientes: " + string.Join(", ", pending.Select(x => x.Name)));
    return 0;
}

foreach (var (marker, name, apply) in pending)
{
    apply();
    // Marcador para no aplicar dos veces el mismo parche
    module.Types.Add(new TypeDefinition("EmbyNoShare", marker, TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed, module.TypeSystem.Object));
    Console.WriteLine($"[{name}] aplicado");
}

asm.Write(path);
Console.WriteLine("Parche aplicado: " + path);
return 0;

static MethodDefinition GetMoveNext(TypeDefinition type, string asyncMethod)
{
    var sm = type.NestedTypes.Where(t => t.Name.StartsWith("<" + asyncMethod + ">")).ToList();
    if (sm.Count != 1) throw new Exception($"Se esperaba 1 state machine {asyncMethod}, hay {sm.Count}");
    return sm[0].Methods.Single(m => m.Name == "MoveNext");
}

static Action? PrepareNoShare(ModuleDefinition module)
{
    // a) LiveStream.EnableStreamSharing
    var liveStream = module.GetType("Emby.LiveTV.TunerHosts.LiveStream") ?? throw new Exception("No se encuentra LiveStream");
    var getter = liveStream.Properties.Single(p => p.Name == "EnableStreamSharing").GetMethod;
    Console.WriteLine($"[noshare] {getter.FullName}");

    // b) LiveTvManager.GetChannelStream (state machine async)
    var mgr = module.GetType("Emby.LiveTV.LiveTvManager") ?? throw new Exception("No se encuentra LiveTvManager");
    var moveNext = GetMoveNext(mgr, "GetChannelStream");
    var setIds = moveNext.Body.Instructions.Where(i => i.Operand is MethodReference mr && mr.Name == "set_LiveStreamId").ToList();
    if (setIds.Count != 1) throw new Exception($"Se esperaba 1 set_LiveStreamId, hay {setIds.Count}");
    var concat = setIds[0].Previous;
    if (!(concat.OpCode == OpCodes.Call && concat.Operand is MethodReference cr && cr.Name == "Concat" && cr.Parameters.Count == 2))
        throw new Exception("Patrón IL inesperado antes de set_LiveStreamId: " + concat);
    Console.WriteLine($"[noshare] {moveNext.FullName} (IL_{setIds[0].Offset:x4})");

    return () =>
    {
        // a) return false;
        getter.Body.Instructions.Clear();
        getter.Body.ExceptionHandlers.Clear();
        getter.Body.Variables.Clear();
        var gp = getter.Body.GetILProcessor();
        gp.Append(gp.Create(OpCodes.Ldc_I4_0));
        gp.Append(gp.Create(OpCodes.Ret));

        // b) Concat(a, b) -> Concat(Concat(a, b), "_", Guid.NewGuid().ToString("N"))
        // Las referencias de tipos se toman del propio ensamblado (System.Runtime) en lugar de typeof().
        var concatRef = (MethodReference)concat.Operand;
        var stringType = concatRef.DeclaringType;
        var guidType = new TypeReference("System", "Guid", module, stringType.Scope, valueType: true);
        var newGuid = new MethodReference("NewGuid", guidType, guidType) { HasThis = false };
        var guidToString = new MethodReference("ToString", module.TypeSystem.String, guidType) { HasThis = true };
        guidToString.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
        var concat3 = new MethodReference("Concat", module.TypeSystem.String, stringType) { HasThis = false };
        for (var i = 0; i < 3; i++) concat3.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));

        var guidLocal = new VariableDefinition(guidType);
        moveNext.Body.Variables.Add(guidLocal);
        moveNext.Body.InitLocals = true;
        var p = moveNext.Body.GetILProcessor();
        var anchor = concat;
        foreach (var ins in new[]
        {
            p.Create(OpCodes.Ldstr, "_"),
            p.Create(OpCodes.Call, newGuid),
            p.Create(OpCodes.Stloc, guidLocal),
            p.Create(OpCodes.Ldloca, guidLocal),
            p.Create(OpCodes.Ldstr, "N"),
            p.Create(OpCodes.Call, guidToString),
            p.Create(OpCodes.Call, concat3),
        })
        {
            p.InsertAfter(anchor, ins);
            anchor = ins;
        }
    };
}

static Action? PrepareHlsTargetDuration(ModuleDefinition module)
{
    var service = module.GetType("Emby.LiveTV.Api.LiveStreamService") ?? throw new Exception("No se encuentra LiveStreamService");
    var moveNext = GetMoveNext(service, "GetVariantPlaylistFileText");
    var il = moveNext.Body.Instructions;

    // ... Enumerable.Max<TimeSpan>(durations.Concat(SegmentDuration + 1s)); pop   <- resultado descartado
    var maxCalls = il.Where(i => i.OpCode == OpCodes.Call && i.Operand is GenericInstanceMethod gm
        && gm.Name == "Max" && gm.DeclaringType.FullName == "System.Linq.Enumerable"
        && gm.GenericArguments.Count == 1 && gm.GenericArguments[0].FullName == "System.TimeSpan").ToList();
    if (maxCalls.Count == 0)
    {
        // Emby 4.11+: TARGETDURATION = Ceiling(segments.Max(i => i.Duration).TotalSeconds + 1)
        if (il.Any(i => i.Operand is GenericInstanceMethod gm && gm.Name == "Max" && gm.DeclaringType.FullName == "System.Linq.Enumerable"
            && gm.Parameters.Count == 2))
            return null;
        throw new Exception("No se encuentra el cálculo de TARGETDURATION");
    }
    if (maxCalls.Count != 1) throw new Exception($"Se esperaba 1 Enumerable.Max<TimeSpan>, hay {maxCalls.Count}");
    var maxCall = maxCalls[0];
    var pop = maxCall.Next;
    if (pop.OpCode != OpCodes.Pop) throw new Exception("Patrón IL inesperado tras Enumerable.Max: " + pop);

    // ldstr "#EXT-X-TARGETDURATION:" ... get_TotalSeconds; ldc.r8 1; add; call Math.Ceiling
    var label = il.SingleOrDefault(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "#EXT-X-TARGETDURATION:")
        ?? throw new Exception("No se encuentra #EXT-X-TARGETDURATION");
    var ceiling = label.Next;
    while (ceiling != null && !(ceiling.Operand is MethodReference m && m.Name == "Ceiling" && m.DeclaringType.FullName == "System.Math"))
        ceiling = ceiling.Next;
    if (ceiling == null || ceiling.Previous.OpCode != OpCodes.Add) throw new Exception("Patrón IL inesperado en TARGETDURATION");
    var totalSeconds = ceiling.Previous.Previous.Previous;
    if (!(totalSeconds.Operand is MethodReference ts && ts.Name == "get_TotalSeconds" && ts.DeclaringType.FullName == "System.TimeSpan"))
        throw new Exception("Patrón IL inesperado en TARGETDURATION: " + totalSeconds);
    // Entre el Max y TARGETDURATION no puede haber un await, o la variable local no sobreviviría
    for (var i = pop; i != label; i = i.Next)
        if (i.Operand is MethodReference mr && mr.Name.Contains("AwaitUnsafeOnCompleted"))
            throw new Exception("Hay un await entre Max y TARGETDURATION");
    Console.WriteLine($"[hls-targetduration] {moveNext.FullName} (IL_{maxCall.Offset:x4}, IL_{ceiling.Offset:x4})");

    return () =>
    {
        var timeSpanType = ((MethodReference)totalSeconds.Operand).DeclaringType;
        var maxLocal = new VariableDefinition(timeSpanType);
        moveNext.Body.Variables.Add(maxLocal);
        var p = moveNext.Body.GetILProcessor();

        // pop -> stloc maxLocal (se modifica la instrucción en sitio para no romper saltos que la apunten)
        pop.OpCode = OpCodes.Stloc;
        pop.Operand = maxLocal;

        // Math.Ceiling(SegmentDuration.TotalSeconds + 1) -> Math.Ceiling(max.TotalSeconds)
        // (max ya incluye SegmentDuration + 1, así que el valor nunca baja de 4)
        p.InsertBefore(ceiling, p.Create(OpCodes.Pop));
        p.InsertBefore(ceiling, p.Create(OpCodes.Ldloca, maxLocal));
        p.InsertBefore(ceiling, p.Create(OpCodes.Call, (MethodReference)totalSeconds.Operand));
    };
}

static Action? PrepareHlsHoldBack(ModuleDefinition module)
{
    var service = module.GetType("Emby.Server.MediaEncoding.Api.Hls.BaseHlsService") ?? throw new Exception("No se encuentra BaseHlsService");
    var method = service.Methods.SingleOrDefault(m => m.Name == "GetLivePlaylistText") ?? throw new Exception("No se encuentra GetLivePlaylistText");
    if (method.IsStatic || method.Parameters.Count < 1 || method.Parameters[0].ParameterType.Name != "StreamState")
        throw new Exception("Firma inesperada de GetLivePlaylistText");

    // <sb> ldstr "#EXT-X-TARGETDURATION:" ... callvirt StringBuilder::AppendLine(string); pop
    var il = method.Body.Instructions;
    var label = il.SingleOrDefault(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "#EXT-X-TARGETDURATION:")
        ?? throw new Exception("No se encuentra #EXT-X-TARGETDURATION");
    var loadBuilder = label.Previous;
    if (!loadBuilder.OpCode.Name.StartsWith("ldloc")) throw new Exception("Patrón IL inesperado antes de TARGETDURATION: " + loadBuilder);
    var appendLine = label.Next;
    while (appendLine != null && !(appendLine.Operand is MethodReference m && m.Name == "AppendLine" && m.DeclaringType.FullName == "System.Text.StringBuilder"))
        appendLine = appendLine.Next;
    if (appendLine == null || appendLine.Next.OpCode != OpCodes.Pop) throw new Exception("Patrón IL inesperado en TARGETDURATION");
    var afterLine = appendLine.Next;

    // StreamState hereda EncodingJobInfo.UserAgent (rellenado con el User-Agent de la petición)
    MethodReference? getUserAgent = null;
    // Solo se recorren clases del propio ensamblado (así no hace falta resolver System.Runtime)
    for (var t = method.Parameters[0].ParameterType.Resolve(); t != null && getUserAgent == null; t = t.BaseType != null && t.BaseType.Scope == module ? t.BaseType.Resolve() : null)
        getUserAgent = t.Properties.FirstOrDefault(pr => pr.Name == "UserAgent" && pr.GetMethod != null)?.GetMethod;
    if (getUserAgent == null) throw new Exception("No se encuentra StreamState.UserAgent");
    getUserAgent = module.ImportReference(getUserAgent);
    Console.WriteLine($"[hls-holdback] {method.FullName} (IL_{afterLine.Offset:x4})");

    return () =>
    {
        var appendLineRef = (MethodReference)appendLine.Operand;
        var str = module.TypeSystem.String;
        var runtime = appendLineRef.DeclaringType.Scope;
        var stringType = new TypeReference("System", "String", module, runtime, valueType: false);
        var int32 = new TypeReference("System", "Int32", module, runtime, valueType: true);
        var envType = new TypeReference("System", "Environment", module, runtime, valueType: false);

        var getEnv = new MethodReference("GetEnvironmentVariable", str, envType) { HasThis = false };
        getEnv.Parameters.Add(new ParameterDefinition(str));
        var tryParse = new MethodReference("TryParse", module.TypeSystem.Boolean, int32) { HasThis = false };
        tryParse.Parameters.Add(new ParameterDefinition(str));
        tryParse.Parameters.Add(new ParameterDefinition(new ByReferenceType(module.TypeSystem.Int32)));
        var intToString = new MethodReference("ToString", str, int32) { HasThis = true };
        var indexOf = new MethodReference("IndexOf", module.TypeSystem.Int32, stringType) { HasThis = true };
        indexOf.Parameters.Add(new ParameterDefinition(str));
        indexOf.Parameters.Add(new ParameterDefinition(new TypeReference("System", "StringComparison", module, runtime, valueType: true)));
        var concat4 = new MethodReference("Concat", str, stringType) { HasThis = false };
        for (var i = 0; i < 4; i++) concat4.Parameters.Add(new ParameterDefinition(str));

        // private static string EmbyNoShare_GetLiveHoldBackTags(string userAgent)
        // {
        //     if (userAgent == null) return null;
        //     string s = Environment.GetEnvironmentVariable("EMBY_HLS_HOLDBACK") ?? "30";
        //     if (!int.TryParse(s, out int n) || n <= 0) return null;
        //     s = n.ToString();
        //     string ua = Environment.GetEnvironmentVariable("EMBY_HLS_HOLDBACK_UA") ?? "Kepler";
        //     if (userAgent.IndexOf(ua, StringComparison.OrdinalIgnoreCase) < 0) return null;
        //     return "#EXT-X-START:TIME-OFFSET=-" + s + "\n#EXT-X-SERVER-CONTROL:HOLD-BACK=" + s;
        // }
        var helper = new MethodDefinition("EmbyNoShare_GetLiveHoldBackTags", MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, str);
        helper.Parameters.Add(new ParameterDefinition("userAgent", ParameterAttributes.None, str));
        var s = new VariableDefinition(str);
        var ua = new VariableDefinition(str);
        var n = new VariableDefinition(module.TypeSystem.Int32);
        helper.Body.Variables.Add(s);
        helper.Body.Variables.Add(ua);
        helper.Body.Variables.Add(n);
        helper.Body.InitLocals = true;
        var h = helper.Body.GetILProcessor();
        var retNull = h.Create(OpCodes.Ldnull);
        var haveS = h.Create(OpCodes.Ldloc, s);
        var haveUa = h.Create(OpCodes.Ldarg_0);
        h.Append(h.Create(OpCodes.Ldarg_0));
        h.Append(h.Create(OpCodes.Brfalse, retNull));
        h.Append(h.Create(OpCodes.Ldstr, "EMBY_HLS_HOLDBACK"));
        h.Append(h.Create(OpCodes.Call, getEnv));
        h.Append(h.Create(OpCodes.Stloc, s));
        h.Append(h.Create(OpCodes.Ldloc, s));
        h.Append(h.Create(OpCodes.Brtrue, haveS));
        h.Append(h.Create(OpCodes.Ldstr, "30"));
        h.Append(h.Create(OpCodes.Stloc, s));
        h.Append(haveS);
        h.Append(h.Create(OpCodes.Ldloca, n));
        h.Append(h.Create(OpCodes.Call, tryParse));
        h.Append(h.Create(OpCodes.Brfalse, retNull));
        h.Append(h.Create(OpCodes.Ldloc, n));
        h.Append(h.Create(OpCodes.Ldc_I4_0));
        h.Append(h.Create(OpCodes.Ble, retNull));
        h.Append(h.Create(OpCodes.Ldloca, n));
        h.Append(h.Create(OpCodes.Call, intToString));
        h.Append(h.Create(OpCodes.Stloc, s));
        h.Append(h.Create(OpCodes.Ldstr, "EMBY_HLS_HOLDBACK_UA"));
        h.Append(h.Create(OpCodes.Call, getEnv));
        h.Append(h.Create(OpCodes.Stloc, ua));
        h.Append(h.Create(OpCodes.Ldloc, ua));
        h.Append(h.Create(OpCodes.Brtrue, haveUa));
        h.Append(h.Create(OpCodes.Ldstr, "Kepler"));
        h.Append(h.Create(OpCodes.Stloc, ua));
        h.Append(haveUa);
        h.Append(h.Create(OpCodes.Ldloc, ua));
        h.Append(h.Create(OpCodes.Ldc_I4_5)); // StringComparison.OrdinalIgnoreCase
        h.Append(h.Create(OpCodes.Callvirt, indexOf));
        h.Append(h.Create(OpCodes.Ldc_I4_0));
        h.Append(h.Create(OpCodes.Blt, retNull));
        h.Append(h.Create(OpCodes.Ldstr, "#EXT-X-START:TIME-OFFSET=-"));
        h.Append(h.Create(OpCodes.Ldloc, s));
        h.Append(h.Create(OpCodes.Ldstr, "\n#EXT-X-SERVER-CONTROL:HOLD-BACK="));
        h.Append(h.Create(OpCodes.Ldloc, s));
        h.Append(h.Create(OpCodes.Call, concat4));
        h.Append(h.Create(OpCodes.Ret));
        h.Append(retNull);
        h.Append(h.Create(OpCodes.Ret));
        service.Methods.Add(helper);

        // Tras la línea de TARGETDURATION:
        //   string tags = EmbyNoShare_GetLiveHoldBackTags(state.UserAgent);
        //   if (tags != null) sb.AppendLine(tags);
        // Formas largas mientras se inserta, para que ningún salto corto existente quede fuera de rango
        method.Body.SimplifyMacros();
        var tags = new VariableDefinition(str);
        method.Body.Variables.Add(tags);
        var p = method.Body.GetILProcessor();
        foreach (var ins in new[]
        {
            p.Create(OpCodes.Ldarg_1),
            p.Create(OpCodes.Callvirt, getUserAgent),
            p.Create(OpCodes.Call, helper),
            p.Create(OpCodes.Stloc, tags),
            p.Create(OpCodes.Ldloc, tags),
            p.Create(OpCodes.Brfalse, afterLine),
            p.Create(loadBuilder.OpCode, (VariableDefinition)loadBuilder.Operand),
            p.Create(OpCodes.Ldloc, tags),
            p.Create(OpCodes.Callvirt, appendLineRef),
            p.Create(OpCodes.Pop),
        })
        {
            p.InsertBefore(afterLine, ins);
        }
        method.Body.OptimizeMacros();
        helper.Body.OptimizeMacros();
    };
}

static Action? PrepareXmlTvCacheCleanup(ModuleDefinition module)
{
    var provider = module.GetType("Emby.XmlTV.XmlTvProvider") ?? throw new Exception("No se encuentra Emby.XmlTV.XmlTvProvider");
    var method = provider.Methods.SingleOrDefault(m => m.Name == "ClearProgramCaches" && m.Parameters.Count == 0) ?? throw new Exception("No se encuentra ClearProgramCaches");
    var semaphore = provider.Fields.SingleOrDefault(f => f.Name == "_downloadSemaphore" && f.FieldType.FullName == "System.Threading.SemaphoreSlim") ?? throw new Exception("No se encuentra _downloadSemaphore");
    var fileSystem = provider.Fields.SingleOrDefault(f => f.Name == "_fileSystem" && f.FieldType.FullName == "MediaBrowser.Model.IO.IFileSystem") ?? throw new Exception("No se encuentra _fileSystem");
    var config = provider.Fields.SingleOrDefault(f => f.Name == "_config") ?? throw new Exception("No se encuentra _config");
    var getFolder = provider.Properties.SingleOrDefault(pr => pr.Name == "CachePathFolderName")?.GetMethod ?? throw new Exception("No se encuentra CachePathFolderName");
    var ret = method.Body.Instructions.Last();
    if (ret.OpCode != OpCodes.Ret) throw new Exception("ClearProgramCaches no termina en ret");
    if (!method.Body.Instructions.Any(i => i.Operand is FieldReference fr && fr.Name == "_cacheKey"))
        throw new Exception("ClearProgramCaches ya no cambia _cacheKey");

    // Referencias que el plugin ya usa (en GetXml), para heredar el ámbito correcto (netstandard, MediaBrowser.*)
    var refs = module.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody)
        .SelectMany(m => m.Body.Instructions).Select(i => i.Operand).OfType<MethodReference>().ToList();
    MethodReference Find(string type, string name, int args) =>
        refs.FirstOrDefault(r => r.DeclaringType.FullName == type && r.Name == name && r.Parameters.Count == args)
        ?? throw new Exception($"No se encuentra la referencia {type}::{name}");
    var release = Find("System.Threading.SemaphoreSlim", "Release", 0);
    var getAppPaths = Find("MediaBrowser.Controller.Configuration.IServerConfigurationManager", "get_ApplicationPaths", 0);
    var getCachePath = Find("MediaBrowser.Common.Configuration.IApplicationPaths", "get_CachePath", 0);
    var combine2 = Find("System.IO.Path", "Combine", 2);
    Console.WriteLine($"[xmltv-cache-cleanup] {method.FullName}");

    return () =>
    {
        var std = release.DeclaringType.Scope;
        var str = module.TypeSystem.String;
        var wait = new MethodReference("Wait", module.TypeSystem.Void, release.DeclaringType) { HasThis = true };
        var isNullOrEmpty = new MethodReference("IsNullOrEmpty", module.TypeSystem.Boolean, new TypeReference("System", "String", module, std)) { HasThis = false };
        isNullOrEmpty.Parameters.Add(new ParameterDefinition(str));
        var deleteDirectory = new MethodReference("DeleteDirectory", module.TypeSystem.Void, fileSystem.FieldType) { HasThis = true };
        deleteDirectory.Parameters.Add(new ParameterDefinition(str));
        deleteDirectory.Parameters.Add(new ParameterDefinition(module.TypeSystem.Boolean));
        var exceptionType = new TypeReference("System", "Exception", module, std);

        // Tras cambiar _cacheKey y cerrar los ficheros abiertos:
        //   string folder = CachePathFolderName;
        //   if (string.IsNullOrEmpty(folder)) return;
        //   _downloadSemaphore.Wait();
        //   try { _fileSystem.DeleteDirectory(Path.Combine(_config.ApplicationPaths.CachePath, folder), true); }
        //   catch (Exception) { }
        //   _downloadSemaphore.Release();
        method.Body.SimplifyMacros();
        var folder = new VariableDefinition(str);
        method.Body.Variables.Add(folder);
        var p = method.Body.GetILProcessor();
        var end = p.Create(OpCodes.Ret);
        var afterTry = p.Create(OpCodes.Ldarg_0);

        // El ret original pasa a ser la primera instrucción del bloque, así cualquier salto a él lo ejecuta
        ret.OpCode = OpCodes.Ldarg_0;
        ret.Operand = null;
        var code = new List<Instruction>
        {
            p.Create(OpCodes.Call, getFolder),
            p.Create(OpCodes.Stloc, folder),
            p.Create(OpCodes.Ldloc, folder),
            p.Create(OpCodes.Call, isNullOrEmpty),
            p.Create(OpCodes.Brtrue, end),
            p.Create(OpCodes.Ldarg_0),
            p.Create(OpCodes.Ldfld, semaphore),
            p.Create(OpCodes.Callvirt, wait),
        };
        var tryStart = p.Create(OpCodes.Ldarg_0);
        code.AddRange(new[]
        {
            tryStart,
            p.Create(OpCodes.Ldfld, fileSystem),
            p.Create(OpCodes.Ldarg_0),
            p.Create(OpCodes.Ldfld, config),
            p.Create(OpCodes.Callvirt, getAppPaths),
            p.Create(OpCodes.Callvirt, getCachePath),
            p.Create(OpCodes.Ldloc, folder),
            p.Create(OpCodes.Call, combine2),
            p.Create(OpCodes.Ldc_I4_1),
            p.Create(OpCodes.Callvirt, deleteDirectory),
            p.Create(OpCodes.Leave, afterTry),
        });
        var catchStart = p.Create(OpCodes.Pop);
        code.AddRange(new[]
        {
            catchStart,
            p.Create(OpCodes.Leave, afterTry),
            afterTry,
            p.Create(OpCodes.Ldfld, semaphore),
            p.Create(OpCodes.Callvirt, release),
            p.Create(OpCodes.Pop),
            end,
        });
        var anchor = ret;
        foreach (var ins in code)
        {
            p.InsertAfter(anchor, ins);
            anchor = ins;
        }
        method.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            TryStart = tryStart,
            TryEnd = catchStart,
            HandlerStart = catchStart,
            HandlerEnd = afterTry,
            CatchType = exceptionType,
        });
        method.Body.OptimizeMacros();
    };
}

// Referencia a un método de System.String / System.Environment con el ámbito (System.Runtime o netstandard) dado
static MethodReference Bcl(ModuleDefinition module, IMetadataScope scope, string type, string name, bool instance, TypeReference ret, params TypeReference[] args)
{
    var ns = type.Substring(0, type.LastIndexOf('.'));
    var t = new TypeReference(ns, type.Substring(ns.Length + 1), module, scope, valueType: type == "System.StringComparison");
    var m = new MethodReference(name, ret, t) { HasThis = instance };
    foreach (var a in args) m.Parameters.Add(new ParameterDefinition(a));
    return m;
}

static IMetadataScope ScopeOf(ModuleDefinition module, string typeFullName)
{
    return module.GetTypeReferences().FirstOrDefault(t => t.FullName == typeFullName)?.Scope
        ?? throw new Exception("No se encuentra la referencia a " + typeFullName);
}

static Action? PrepareLangAliases(ModuleDefinition module)
{
    var lm = module.GetType("Emby.Server.Implementations.Localization.LocalizationManager") ?? throw new Exception("No se encuentra LocalizationManager");
    var method = lm.Methods.SingleOrDefault(m => m.Name == "FindLanguageInfosInternal" && m.Parameters.Count == 1) ?? throw new Exception("No se encuentra FindLanguageInfosInternal");
    var il = method.Body.Instructions;
    // ldarg.0; ldfld _languageDictionary; ldarg.1; ldloca 0; callvirt TryGetValue; brfalse X; ldloc.0; ret; X: call Array.Empty<CultureDto>; ret
    var tryGet = il.SingleOrDefault(i => i.Operand is MethodReference m && m.Name == "TryGetValue") ?? throw new Exception("Patrón inesperado en FindLanguageInfosInternal");
    var dictField = il.Select(i => i.Operand).OfType<FieldReference>().SingleOrDefault(f => f.Name == "_languageDictionary") ?? throw new Exception("No se encuentra _languageDictionary");
    var empty = il.SingleOrDefault(i => i.Operand is MethodReference m && m.Name == "Empty" && m.DeclaringType.FullName == "System.Array") ?? throw new Exception("No se encuentra Array.Empty");
    if (empty.Next?.OpCode != OpCodes.Ret || method.Body.Variables.Count != 1) throw new Exception("Patrón inesperado en FindLanguageInfosInternal");
    var scope = ScopeOf(module, "System.String");
    Console.WriteLine($"[lang-aliases] {method.FullName}");

    return () =>
    {
        var str = module.TypeSystem.String;
        var i32 = module.TypeSystem.Int32;
        var cmp = new TypeReference("System", "StringComparison", module, scope, valueType: true);
        var getEnv = Bcl(module, scope, "System.Environment", "GetEnvironmentVariable", false, str, str);
        var replace = Bcl(module, scope, "System.String", "Replace", true, str, str, str);
        var concat3 = Bcl(module, scope, "System.String", "Concat", false, str, str, str, str);
        var indexOfStr = Bcl(module, scope, "System.String", "IndexOf", true, i32, str, cmp);
        var indexOfChar = Bcl(module, scope, "System.String", "IndexOf", true, i32, module.TypeSystem.Char, i32);
        var substring = Bcl(module, scope, "System.String", "Substring", true, str, i32, i32);
        var getLength = Bcl(module, scope, "System.String", "get_Length", true, i32);

        // private static string EmbyNoShare_ResolveLanguageAlias(string language)
        // {
        //     if (language == null) return null;
        //     string list = Environment.GetEnvironmentVariable("EMBY_LANG_ALIASES") ?? DEFAULT;
        //     string hay = "," + list.Replace(" ", "") + ",";
        //     string key = "," + language + "=";
        //     int p = hay.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        //     if (p < 0) return null;
        //     int start = p + key.Length;
        //     return hay.Substring(start, hay.IndexOf(',', start) - start);
        // }
        var helper = new MethodDefinition("EmbyNoShare_ResolveLanguageAlias", MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, str);
        helper.Parameters.Add(new ParameterDefinition("language", ParameterAttributes.None, str));
        var hay = new VariableDefinition(str); var key = new VariableDefinition(str); var p = new VariableDefinition(i32); var start = new VariableDefinition(i32);
        foreach (var v in new[] { hay, key, p, start }) helper.Body.Variables.Add(v);
        helper.Body.InitLocals = true;
        var h = helper.Body.GetILProcessor();
        var retNull = h.Create(OpCodes.Ldnull);
        var haveList = h.Create(OpCodes.Ldstr, " ");
        h.Append(h.Create(OpCodes.Ldarg_0));
        h.Append(h.Create(OpCodes.Brfalse, retNull));
        h.Append(h.Create(OpCodes.Ldstr, "EMBY_LANG_ALIASES"));
        h.Append(h.Create(OpCodes.Call, getEnv));
        h.Append(h.Create(OpCodes.Dup));
        h.Append(h.Create(OpCodes.Brtrue, haveList));
        h.Append(h.Create(OpCodes.Pop));
        h.Append(h.Create(OpCodes.Ldstr, "esp=spa,cas=spa,castellano=spa,espanol=spa,español=spa,castilian=spa"));
        h.Append(h.Create(OpCodes.Ldstr, " "));
        var afterDefault = h.Body.Instructions.Last();
        // (la rama con valor salta a haveList, que vuelve a cargar " "; la rama por defecto ya lo ha cargado)
        h.Append(h.Create(OpCodes.Ldstr, ""));
        var doReplace = h.Body.Instructions.Last();
        h.Append(h.Create(OpCodes.Callvirt, replace));
        h.Append(h.Create(OpCodes.Stloc, hay));
        h.Append(h.Create(OpCodes.Ldstr, ","));
        h.Append(h.Create(OpCodes.Ldloc, hay));
        h.Append(h.Create(OpCodes.Ldstr, ","));
        h.Append(h.Create(OpCodes.Call, concat3));
        h.Append(h.Create(OpCodes.Stloc, hay));
        h.Append(h.Create(OpCodes.Ldstr, ","));
        h.Append(h.Create(OpCodes.Ldarg_0));
        h.Append(h.Create(OpCodes.Ldstr, "="));
        h.Append(h.Create(OpCodes.Call, concat3));
        h.Append(h.Create(OpCodes.Stloc, key));
        h.Append(h.Create(OpCodes.Ldloc, hay));
        h.Append(h.Create(OpCodes.Ldloc, key));
        h.Append(h.Create(OpCodes.Ldc_I4_5));
        h.Append(h.Create(OpCodes.Callvirt, indexOfStr));
        h.Append(h.Create(OpCodes.Stloc, p));
        h.Append(h.Create(OpCodes.Ldloc, p));
        h.Append(h.Create(OpCodes.Ldc_I4_0));
        h.Append(h.Create(OpCodes.Blt, retNull));
        h.Append(h.Create(OpCodes.Ldloc, p));
        h.Append(h.Create(OpCodes.Ldloc, key));
        h.Append(h.Create(OpCodes.Callvirt, getLength));
        h.Append(h.Create(OpCodes.Add));
        h.Append(h.Create(OpCodes.Stloc, start));
        h.Append(h.Create(OpCodes.Ldloc, hay));
        h.Append(h.Create(OpCodes.Ldloc, start));
        h.Append(h.Create(OpCodes.Ldloc, hay));
        h.Append(h.Create(OpCodes.Ldc_I4_S, (sbyte)','));
        h.Append(h.Create(OpCodes.Ldloc, start));
        h.Append(h.Create(OpCodes.Callvirt, indexOfChar));
        h.Append(h.Create(OpCodes.Ldloc, start));
        h.Append(h.Create(OpCodes.Sub));
        h.Append(h.Create(OpCodes.Callvirt, substring));
        h.Append(h.Create(OpCodes.Ret));
        h.Append(retNull);
        h.Append(h.Create(OpCodes.Ret));
        // La rama "hay valor" debe llegar con la lista en la pila y continuar cargando " " y "":
        // se inserta haveList justo antes del ldstr "" de la rama común y la rama por defecto salta a doReplace.
        h.InsertBefore(doReplace, haveList);
        h.InsertAfter(afterDefault, h.Create(OpCodes.Br, doReplace));
        lm.Methods.Add(helper);

        // FindLanguageInfosInternal: antes de devolver Array.Empty, probar el alias
        //   string target = EmbyNoShare_ResolveLanguageAlias(language);
        //   if (target != null && _languageDictionary.TryGetValue(target, out value)) return value;
        method.Body.SimplifyMacros();
        var target = new VariableDefinition(str);
        method.Body.Variables.Add(target);
        var mp = method.Body.GetILProcessor();
        var value = method.Body.Variables[0];
        var newEmpty = mp.Create(OpCodes.Call, (MethodReference)empty.Operand);
        // la instrucción original (destino del salto) pasa a ser la primera del bloque
        empty.OpCode = OpCodes.Ldarg_1;
        empty.Operand = null;
        var anchor = empty;
        foreach (var ins in new[]
        {
            mp.Create(OpCodes.Call, helper),
            mp.Create(OpCodes.Stloc, target),
            mp.Create(OpCodes.Ldloc, target),
            mp.Create(OpCodes.Brfalse, newEmpty),
            mp.Create(OpCodes.Ldarg_0),
            mp.Create(OpCodes.Ldfld, dictField),
            mp.Create(OpCodes.Ldloc, target),
            mp.Create(OpCodes.Ldloca, value),
            mp.Create(OpCodes.Callvirt, (MethodReference)tryGet.Operand),
            mp.Create(OpCodes.Brfalse, newEmpty),
            mp.Create(OpCodes.Ldloc, value),
            mp.Create(OpCodes.Ret),
            newEmpty,
        })
        {
            mp.InsertAfter(anchor, ins);
            anchor = ins;
        }
        method.Body.OptimizeMacros();
        helper.Body.OptimizeMacros();
    };
}

static Action? PrepareLiveAudioLanguageFirst(ModuleDefinition module)
{
    var msm = module.GetType("Emby.Server.Implementations.Library.MediaSourceManager") ?? throw new Exception("No se encuentra MediaSourceManager");
    var method = msm.Methods.SingleOrDefault(m => m.Name == "SetDefaultAudioStreamIndex" && m.Parameters.Any(pr => pr.Name == "source")) ?? throw new Exception("No se encuentra SetDefaultAudioStreamIndex");
    var source = method.Parameters.Single(pr => pr.Name == "source");
    if (source.ParameterType.FullName != "MediaBrowser.Model.Dto.MediaSourceInfo") throw new Exception("Tipo inesperado de source");
    var calls = method.Body.Instructions.Where(i => i.Operand is MethodReference m && m.Name == "get_PlayDefaultAudioTrack").ToList();
    if (calls.Count != 1) throw new Exception($"Se esperaba 1 get_PlayDefaultAudioTrack, hay {calls.Count}");
    var call = calls[0];
    Console.WriteLine($"[live-audio-language-first] {method.FullName} (IL_{call.Offset:x4})");

    return () =>
    {
        var getInfinite = new MethodReference("get_IsInfiniteStream", module.TypeSystem.Boolean, source.ParameterType) { HasThis = true };
        // preferDefaultTrack = user.Configuration.PlayDefaultAudioTrack && !source.IsInfiniteStream
        var p = method.Body.GetILProcessor();
        var anchor = call;
        foreach (var ins in new[]
        {
            p.Create(OpCodes.Ldarg, source),
            p.Create(OpCodes.Callvirt, getInfinite),
            p.Create(OpCodes.Ldc_I4_0),
            p.Create(OpCodes.Ceq),
            p.Create(OpCodes.And),
        })
        {
            p.InsertAfter(anchor, ins);
            anchor = ins;
        }
    };
}

static Action? PrepareAudioDescriptionLast(ModuleDefinition module)
{
    var selector = module.GetType("MediaBrowser.Model.Dlna.MediaStreamSelector") ?? throw new Exception("No se encuentra MediaStreamSelector");
    var method = selector.Methods.SingleOrDefault(m => m.Name == "GetDefaultAudioStreamIndex") ?? throw new Exception("No se encuentra GetDefaultAudioStreamIndex");
    var streamsParam = method.Parameters.FirstOrDefault(pr => pr.Name == "streams") ?? throw new Exception("No se encuentra el parámetro streams");
    if (streamsParam.ParameterType is not GenericInstanceType listType || listType.ElementType.FullName != "System.Collections.Generic.List`1")
        throw new Exception("Tipo inesperado de streams");
    var sorted = method.Body.Instructions.SingleOrDefault(i => i.Operand is MethodReference m && m.Name == "GetSortedAudioStreams") ?? throw new Exception("No se encuentra GetSortedAudioStreams");
    var store = sorted.Next;
    if (!(store.OpCode.Code is Code.Starg or Code.Starg_S) || store.Operand != streamsParam) throw new Exception("Patrón inesperado tras GetSortedAudioStreams: " + store);
    var mediaStream = module.GetType("MediaBrowser.Model.Entities.MediaStream") ?? throw new Exception("No se encuentra MediaStream");
    var getLanguage = mediaStream.Properties.Single(pr => pr.Name == "Language").GetMethod;
    var getTitle = mediaStream.Properties.Single(pr => pr.Name == "Title").GetMethod;
    var scope = listType.ElementType.Scope;
    Console.WriteLine($"[audio-description-last] {method.FullName} (IL_{store.Offset:x4})");

    return () =>
    {
        var str = module.TypeSystem.String;
        var i32 = module.TypeSystem.Int32;
        var cmp = new TypeReference("System", "StringComparison", module, scope, valueType: true);
        var getEnv = Bcl(module, scope, "System.Environment", "GetEnvironmentVariable", false, str, str);
        var concat3 = Bcl(module, scope, "System.String", "Concat", false, str, str, str, str);
        var indexOf = Bcl(module, scope, "System.String", "IndexOf", true, i32, str, cmp);
        // !0 de List<T>: parámetro genérico de la referencia abierta List`1
        var open = listType.ElementType;
        if (open.GenericParameters.Count == 0) open.GenericParameters.Add(new GenericParameter("T", open));
        var t0 = open.GenericParameters[0];
        MethodReference L(string name, TypeReference ret, params TypeReference[] args)
        {
            var m = new MethodReference(name, ret, listType) { HasThis = true };
            foreach (var a in args) m.Parameters.Add(new ParameterDefinition(a));
            return m;
        }
        var ctor = L(".ctor", module.TypeSystem.Void);
        var count = L("get_Count", i32);
        var item = L("get_Item", t0, i32);
        var add = L("Add", module.TypeSystem.Void, t0);

        // private static List<MediaStream> EmbyNoShare_AudioDescriptionLast(List<MediaStream> streams):
        // misma lista con las pistas de audiodescripción al final (orden estable).
        var helper = new MethodDefinition("EmbyNoShare_AudioDescriptionLast", MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, listType);
        helper.Parameters.Add(new ParameterDefinition("streams", ParameterAttributes.None, listType));
        var res = new VariableDefinition(listType); var ad = new VariableDefinition(listType); var i = new VariableDefinition(i32);
        var m = new VariableDefinition(mediaStream); var list = new VariableDefinition(str); var lang = new VariableDefinition(str); var title = new VariableDefinition(str);
        foreach (var v in new[] { res, ad, i, m, list, lang, title }) helper.Body.Variables.Add(v);
        helper.Body.InitLocals = true;
        var h = helper.Body.GetILProcessor();
        Instruction I(OpCode op) => h.Create(op);
        var start = h.Create(OpCodes.Newobj, ctor);
        var haveList = h.Create(OpCodes.Stloc, list);
        var loop = h.Create(OpCodes.Ldarg_0);
        var cond = h.Create(OpCodes.Ldloc, i);
        var checkTitle = h.Create(OpCodes.Ldloc, m);
        var notAd = h.Create(OpCodes.Ldloc, res);
        var isAd = h.Create(OpCodes.Ldloc, ad);
        var next = h.Create(OpCodes.Ldloc, i);
        var loop2 = h.Create(OpCodes.Ldloc, res);
        var cond2 = h.Create(OpCodes.Ldloc, i);
        var seq = new List<Instruction>
        {
            I(OpCodes.Ldarg_0), h.Create(OpCodes.Brtrue, start), I(OpCodes.Ldnull), I(OpCodes.Ret),
            start, h.Create(OpCodes.Stloc, res),
            h.Create(OpCodes.Newobj, ctor), h.Create(OpCodes.Stloc, ad),
            h.Create(OpCodes.Ldstr, ","),
            h.Create(OpCodes.Ldstr, "EMBY_AUDIO_DESC_LANGS"), h.Create(OpCodes.Call, getEnv),
            I(OpCodes.Dup), h.Create(OpCodes.Brtrue, h.Create(OpCodes.Nop)), // se corrige abajo
            I(OpCodes.Pop), h.Create(OpCodes.Ldstr, "ads,qad"),
        };
        var skipDefault = seq[12];
        var afterDefault = h.Create(OpCodes.Ldstr, ",");
        skipDefault.Operand = afterDefault;
        seq.AddRange(new[]
        {
            afterDefault, h.Create(OpCodes.Call, concat3), haveList,
            h.Create(OpCodes.Ldc_I4_0), h.Create(OpCodes.Stloc, i), h.Create(OpCodes.Br, cond),
            loop, h.Create(OpCodes.Ldloc, i), h.Create(OpCodes.Callvirt, item), h.Create(OpCodes.Stloc, m),
            h.Create(OpCodes.Ldloc, m), h.Create(OpCodes.Callvirt, getLanguage), h.Create(OpCodes.Stloc, lang),
            h.Create(OpCodes.Ldloc, lang), h.Create(OpCodes.Brfalse, checkTitle),
            h.Create(OpCodes.Ldloc, list), h.Create(OpCodes.Ldstr, ","), h.Create(OpCodes.Ldloc, lang), h.Create(OpCodes.Ldstr, ","),
            h.Create(OpCodes.Call, concat3), h.Create(OpCodes.Ldc_I4_5), h.Create(OpCodes.Callvirt, indexOf),
            h.Create(OpCodes.Ldc_I4_0), h.Create(OpCodes.Bge, isAd),
            checkTitle, h.Create(OpCodes.Callvirt, getTitle), h.Create(OpCodes.Stloc, title),
            h.Create(OpCodes.Ldloc, title), h.Create(OpCodes.Brfalse, notAd),
            h.Create(OpCodes.Ldloc, title), h.Create(OpCodes.Ldstr, "audiodescrip"), h.Create(OpCodes.Ldc_I4_5), h.Create(OpCodes.Callvirt, indexOf),
            h.Create(OpCodes.Ldc_I4_0), h.Create(OpCodes.Bge, isAd),
            h.Create(OpCodes.Ldloc, title), h.Create(OpCodes.Ldstr, "audio descrip"), h.Create(OpCodes.Ldc_I4_5), h.Create(OpCodes.Callvirt, indexOf),
            h.Create(OpCodes.Ldc_I4_0), h.Create(OpCodes.Bge, isAd),
            notAd, h.Create(OpCodes.Ldloc, m), h.Create(OpCodes.Callvirt, add), h.Create(OpCodes.Br, next),
            isAd, h.Create(OpCodes.Ldloc, m), h.Create(OpCodes.Callvirt, add),
            next, h.Create(OpCodes.Ldc_I4_1), h.Create(OpCodes.Add), h.Create(OpCodes.Stloc, i),
            cond, h.Create(OpCodes.Ldarg_0), h.Create(OpCodes.Callvirt, count), h.Create(OpCodes.Blt, loop),
            h.Create(OpCodes.Ldc_I4_0), h.Create(OpCodes.Stloc, i), h.Create(OpCodes.Br, cond2),
            loop2, h.Create(OpCodes.Ldloc, ad), h.Create(OpCodes.Ldloc, i), h.Create(OpCodes.Callvirt, item), h.Create(OpCodes.Callvirt, add),
            h.Create(OpCodes.Ldloc, i), h.Create(OpCodes.Ldc_I4_1), h.Create(OpCodes.Add), h.Create(OpCodes.Stloc, i),
            cond2, h.Create(OpCodes.Ldloc, ad), h.Create(OpCodes.Callvirt, count), h.Create(OpCodes.Blt, loop2),
            h.Create(OpCodes.Ldloc, res), h.Create(OpCodes.Ret),
        });
        foreach (var ins in seq) h.Append(ins);
        selector.Methods.Add(helper);

        // GetDefaultAudioStreamIndex: streams = GetSortedAudioStreams(...); streams = EmbyNoShare_AudioDescriptionLast(streams);
        var p = method.Body.GetILProcessor();
        var anchor = store;
        foreach (var ins in new[]
        {
            p.Create(OpCodes.Ldarg, streamsParam),
            p.Create(OpCodes.Call, helper),
            p.Create(OpCodes.Starg, streamsParam),
        })
        {
            p.InsertAfter(anchor, ins);
            anchor = ins;
        }
        helper.Body.OptimizeMacros();
    };
}

static Action? PrepareLiveOpenServerAudio(ModuleDefinition module)
{
    var service = module.GetType("Emby.Server.MediaEncoding.Api.MediaInfoService") ?? throw new Exception("No se encuentra MediaInfoService");
    var moveNext = GetMoveNext(service, "OpenMediaSource");
    var gets = moveNext.Body.Instructions.Where(i => i.Operand is MethodReference m && m.Name == "get_AudioStreamIndex" && m.DeclaringType.Name == "LiveStreamRequest").ToList();
    if (gets.Count != 1) throw new Exception($"Se esperaba 1 LiveStreamRequest.get_AudioStreamIndex en OpenMediaSource, hay {gets.Count}");
    var get = gets[0];
    // Debe ser un argumento de SetDeviceSpecificData (sin otro uso entre medias)
    var next = get.Next;
    while (next != null && !(next.Operand is MethodReference mr && (mr.Name == "SetDeviceSpecificData" || mr.Name.Contains("AwaitUnsafeOnCompleted"))))
        next = next.Next;
    if (next == null || ((MethodReference)next.Operand).Name != "SetDeviceSpecificData") throw new Exception("AudioStreamIndex no va a SetDeviceSpecificData");
    Console.WriteLine($"[live-open-server-audio] {moveNext.FullName} (IL_{get.Offset:x4})");

    return () =>
    {
        // request.AudioStreamIndex -> (int?)null  (se descarta el valor de la petición)
        var nullable = ((MethodReference)get.Operand).ReturnType;
        var tmp = new VariableDefinition(nullable);
        moveNext.Body.Variables.Add(tmp);
        moveNext.Body.InitLocals = true;
        var p = moveNext.Body.GetILProcessor();
        var anchor = get;
        foreach (var ins in new[]
        {
            p.Create(OpCodes.Pop),
            p.Create(OpCodes.Ldloca, tmp),
            p.Create(OpCodes.Initobj, nullable),
            p.Create(OpCodes.Ldloc, tmp),
        })
        {
            p.InsertAfter(anchor, ins);
            anchor = ins;
        }
    };
}

static Action? PrepareLiveOpenNoGlobalLock(ModuleDefinition module)
{
    var msm = module.GetType("Emby.Server.Implementations.Library.MediaSourceManager") ?? throw new Exception("No se encuentra MediaSourceManager");
    var moveNext = GetMoveNext(msm, "OpenLiveStreamInternal2");
    var il = moveNext.Body.Instructions;
    bool IsSem(Instruction i) => i.Operand is FieldReference f && f.Name == "_liveStreamSemaphore";
    var waits = il.Where(i => i.Operand is MethodReference m && m.Name == "WaitAsync" && m.DeclaringType.FullName == "System.Threading.SemaphoreSlim").ToList();
    var releases = il.Where(i => i.Operand is MethodReference m && m.Name == "Release" && m.DeclaringType.FullName == "System.Threading.SemaphoreSlim").ToList();
    if (waits.Count != 1 || releases.Count != 1) throw new Exception($"Se esperaba 1 WaitAsync y 1 Release, hay {waits.Count} y {releases.Count}");
    var wait = waits[0]; var release = releases[0];
    var waitRef = (MethodReference)wait.Operand;
    if (waitRef.Parameters.Count != 1 || waitRef.Parameters[0].ParameterType.FullName != "System.Threading.CancellationToken")
        throw new Exception("Firma inesperada de WaitAsync");
    if (!IsSem(release.Previous) || release.Next.OpCode != OpCodes.Pop) throw new Exception("Patrón inesperado en Release");
    // El semáforo de WaitAsync tiene que ser _liveStreamSemaphore
    var j = wait.Previous; int guard = 0;
    while (j != null && !IsSem(j) && guard++ < 8) j = j.Previous;
    if (j == null || !IsSem(j)) throw new Exception("WaitAsync no es sobre _liveStreamSemaphore");
    Console.WriteLine($"[live-open-no-global-lock] {moveNext.FullName} (IL_{wait.Offset:x4}, IL_{release.Offset:x4})");

    return () =>
    {
        var task = waitRef.ReturnType; // System.Threading.Tasks.Task
        var completed = new MethodReference("get_CompletedTask", task, task) { HasThis = false };
        moveNext.Body.SimplifyMacros();
        var p = moveNext.Body.GetILProcessor();
        // semaphore.WaitAsync(token)  ->  pop token; pop semaphore; Task.CompletedTask
        wait.OpCode = OpCodes.Pop; wait.Operand = null;
        var pop2 = p.Create(OpCodes.Pop);
        p.InsertAfter(wait, pop2);
        p.InsertAfter(pop2, p.Create(OpCodes.Call, completed));
        // semaphore.Release() (resultado descartado)  ->  pop semaphore; ldc.i4.0
        release.OpCode = OpCodes.Pop; release.Operand = null;
        p.InsertAfter(release, p.Create(OpCodes.Ldc_I4_0));
        moveNext.Body.OptimizeMacros();
    };
}

static Action? PrepareLiveIdleClose(ModuleDefinition module)
{
    var ls = module.GetType("Emby.LiveTV.TunerHosts.LiveStream") ?? throw new Exception("No se encuentra LiveStream");
    var pipe = module.GetType("Emby.LiveTV.TunerHosts.SharedHttpPipelineSource") ?? throw new Exception("No se encuentra SharedHttpPipelineSource");
    var consumers = ls.Fields.SingleOrDefault(f => f.Name == "Consumers") ?? throw new Exception("No se encuentra LiveStream.Consumers");
    var logger = ls.Fields.SingleOrDefault(f => f.Name == "Logger") ?? throw new Exception("No se encuentra LiveStream.Logger");
    var getMediaSource = ls.Properties.SingleOrDefault(p => p.Name == "MediaSource")?.GetMethod ?? throw new Exception("No se encuentra LiveStream.MediaSource");
    var getSegments = pipe.Methods.SingleOrDefault(m => m.Name == "GetSegments" && m.Parameters.Count == 0 && m.HasBody) ?? throw new Exception("No se encuentra GetSegments");
    var onCompleted = pipe.Methods.SingleOrDefault(m => m.Name == "OnSegmentCompleted" && m.Parameters.Count == 1 && m.HasBody) ?? throw new Exception("No se encuentra OnSegmentCompleted");
    // Referencias que el ensamblado ya usa
    var refs = module.GetMemberReferences().OfType<MethodReference>().ToList();
    MethodReference Ref(string declaring, string name, int args) => refs.FirstOrDefault(m => m.DeclaringType.FullName == declaring && m.Name == name && m.Parameters.Count == args)
        ?? throw new Exception($"No se encuentra la referencia {declaring}::{name}");
    var getLiveStreamId = Ref("MediaBrowser.Model.Dto.MediaSourceInfo", "get_LiveStreamId", 0);
    var getMsm = Ref("MediaBrowser.Controller.Entities.BaseItem", "get_MediaSourceManager", 0);
    var info = refs.FirstOrDefault(m => m.DeclaringType.FullName == "MediaBrowser.Model.Logging.ILogger" && m.Name == "Info" && m.Parameters.Count == 2 && m.Parameters[1].ParameterType.IsArray)
        ?? throw new Exception("No se encuentra ILogger.Info(string, object[])");
    var task = refs.Select(m => m.ReturnType).FirstOrDefault(t => t.FullName == "System.Threading.Tasks.Task")
        ?? throw new Exception("No se encuentra System.Threading.Tasks.Task");
    if (ls.Fields.Any(f => f.Name == "EmbyNoShare_LastRead")) throw new Exception("Campo EmbyNoShare_LastRead ya existe");
    Console.WriteLine($"[live-idle-close] {getSegments.FullName}, {onCompleted.FullName}");

    return () =>
    {
        var ts = module.TypeSystem;
        var runtime = task.Scope;
        var closeLive = new MethodReference("CloseLiveStream", task, getMsm.ReturnType) { HasThis = true };
        closeLive.Parameters.Add(new ParameterDefinition(ts.String));
        closeLive.Parameters.Add(new ParameterDefinition(ts.String));
        var clear = new MethodReference("Clear", ts.Void, consumers.FieldType) { HasThis = true };
        var tick = Bcl(module, runtime, "System.Environment", "get_TickCount64", false, ts.Int64);
        var getEnv = Bcl(module, runtime, "System.Environment", "GetEnvironmentVariable", false, ts.String, ts.String);
        var int32 = new TypeReference("System", "Int32", module, runtime, valueType: true);
        var tryParse = new MethodReference("TryParse", ts.Boolean, int32) { HasThis = false };
        tryParse.Parameters.Add(new ParameterDefinition(ts.String));
        tryParse.Parameters.Add(new ParameterDefinition(new ByReferenceType(ts.Int32)));
        var funcOpen = new TypeReference("System", "Func`1", module, runtime, valueType: false);
        funcOpen.GenericParameters.Add(new GenericParameter("TResult", funcOpen));
        var funcTask = new GenericInstanceType(funcOpen);
        funcTask.GenericArguments.Add(task);
        var funcCtor = new MethodReference(".ctor", ts.Void, funcTask) { HasThis = true };
        funcCtor.Parameters.Add(new ParameterDefinition(ts.Object));
        funcCtor.Parameters.Add(new ParameterDefinition(ts.IntPtr));
        var run = new MethodReference("Run", task, task) { HasThis = false };
        run.Parameters.Add(new ParameterDefinition(funcTask));

        // public long EmbyNoShare_LastRead;  (Environment.TickCount64 de la última lectura)
        var last = new FieldDefinition("EmbyNoShare_LastRead", FieldAttributes.Public, ts.Int64);
        ls.Fields.Add(last);

        // public Task EmbyNoShare_CloseIdle()
        // {
        //     Consumers.Clear();
        //     return BaseItem.MediaSourceManager.CloseLiveStream(MediaSource.LiveStreamId, "EmbyNoShare-idle");
        // }
        var closeIdle = new MethodDefinition("EmbyNoShare_CloseIdle", MethodAttributes.Public | MethodAttributes.HideBySig, task);
        var c = closeIdle.Body.GetILProcessor();
        c.Append(c.Create(OpCodes.Ldarg_0));
        c.Append(c.Create(OpCodes.Ldfld, consumers));
        c.Append(c.Create(OpCodes.Callvirt, clear));
        c.Append(c.Create(OpCodes.Call, getMsm));
        c.Append(c.Create(OpCodes.Ldarg_0));
        c.Append(c.Create(OpCodes.Call, getMediaSource));
        c.Append(c.Create(OpCodes.Callvirt, getLiveStreamId));
        c.Append(c.Create(OpCodes.Ldstr, "EmbyNoShare-idle"));
        c.Append(c.Create(OpCodes.Callvirt, closeLive));
        c.Append(c.Create(OpCodes.Ret));
        ls.Methods.Add(closeIdle);

        // public void EmbyNoShare_CheckIdle()
        // {
        //     long now = Environment.TickCount64;
        //     if (EmbyNoShare_LastRead == 0) { EmbyNoShare_LastRead = now; return; }
        //     string s = Environment.GetEnvironmentVariable("EMBY_LIVE_IDLE_MINUTES") ?? "20";
        //     if (!int.TryParse(s, out int m) || m <= 0) return;
        //     if (now - EmbyNoShare_LastRead < (long)m * 60000) return;
        //     string id = MediaSource.LiveStreamId;
        //     if (id == null) return;
        //     EmbyNoShare_LastRead = now;
        //     Logger.Info("EmbyNoShare: nadie lee el stream {0} desde hace {1} min, se cierra", id, m);
        //     Task.Run(new Func<Task>(EmbyNoShare_CloseIdle));
        // }
        var check = new MethodDefinition("EmbyNoShare_CheckIdle", MethodAttributes.Public | MethodAttributes.HideBySig, ts.Void);
        var now = new VariableDefinition(ts.Int64);
        var s = new VariableDefinition(ts.String);
        var m = new VariableDefinition(ts.Int32);
        var id = new VariableDefinition(ts.String);
        foreach (var v in new[] { now, s, m, id }) check.Body.Variables.Add(v);
        check.Body.InitLocals = true;
        var h = check.Body.GetILProcessor();
        var ret = h.Create(OpCodes.Ret);
        var notFirst = h.Create(OpCodes.Ldstr, "EMBY_LIVE_IDLE_MINUTES");
        var haveS = h.Create(OpCodes.Ldloc, s);
        h.Append(h.Create(OpCodes.Call, tick));
        h.Append(h.Create(OpCodes.Stloc, now));
        h.Append(h.Create(OpCodes.Ldarg_0));
        h.Append(h.Create(OpCodes.Ldfld, last));
        h.Append(h.Create(OpCodes.Brtrue, notFirst));
        h.Append(h.Create(OpCodes.Ldarg_0));
        h.Append(h.Create(OpCodes.Ldloc, now));
        h.Append(h.Create(OpCodes.Stfld, last));
        h.Append(h.Create(OpCodes.Ret));
        h.Append(notFirst);
        h.Append(h.Create(OpCodes.Call, getEnv));
        h.Append(h.Create(OpCodes.Stloc, s));
        h.Append(h.Create(OpCodes.Ldloc, s));
        h.Append(h.Create(OpCodes.Brtrue, haveS));
        h.Append(h.Create(OpCodes.Ldstr, "20"));
        h.Append(h.Create(OpCodes.Stloc, s));
        h.Append(haveS);
        h.Append(h.Create(OpCodes.Ldloca, m));
        h.Append(h.Create(OpCodes.Call, tryParse));
        h.Append(h.Create(OpCodes.Brfalse, ret));
        h.Append(h.Create(OpCodes.Ldloc, m));
        h.Append(h.Create(OpCodes.Ldc_I4_0));
        h.Append(h.Create(OpCodes.Ble, ret));
        h.Append(h.Create(OpCodes.Ldloc, now));
        h.Append(h.Create(OpCodes.Ldarg_0));
        h.Append(h.Create(OpCodes.Ldfld, last));
        h.Append(h.Create(OpCodes.Sub));
        h.Append(h.Create(OpCodes.Ldloc, m));
        h.Append(h.Create(OpCodes.Conv_I8));
        h.Append(h.Create(OpCodes.Ldc_I4, 60000));
        h.Append(h.Create(OpCodes.Conv_I8));
        h.Append(h.Create(OpCodes.Mul));
        h.Append(h.Create(OpCodes.Blt, ret));
        h.Append(h.Create(OpCodes.Ldarg_0));
        h.Append(h.Create(OpCodes.Call, getMediaSource));
        h.Append(h.Create(OpCodes.Callvirt, getLiveStreamId));
        h.Append(h.Create(OpCodes.Stloc, id));
        h.Append(h.Create(OpCodes.Ldloc, id));
        h.Append(h.Create(OpCodes.Brfalse, ret));
        h.Append(h.Create(OpCodes.Ldarg_0));
        h.Append(h.Create(OpCodes.Ldloc, now));
        h.Append(h.Create(OpCodes.Stfld, last));
        h.Append(h.Create(OpCodes.Ldarg_0));
        h.Append(h.Create(OpCodes.Ldfld, logger));
        h.Append(h.Create(OpCodes.Ldstr, "EmbyNoShare: nadie lee el stream {0} desde hace {1} min, se cierra"));
        h.Append(h.Create(OpCodes.Ldc_I4_2));
        h.Append(h.Create(OpCodes.Newarr, ts.Object));
        h.Append(h.Create(OpCodes.Dup));
        h.Append(h.Create(OpCodes.Ldc_I4_0));
        h.Append(h.Create(OpCodes.Ldloc, id));
        h.Append(h.Create(OpCodes.Stelem_Ref));
        h.Append(h.Create(OpCodes.Dup));
        h.Append(h.Create(OpCodes.Ldc_I4_1));
        h.Append(h.Create(OpCodes.Ldloc, m));
        h.Append(h.Create(OpCodes.Box, int32));
        h.Append(h.Create(OpCodes.Stelem_Ref));
        h.Append(h.Create(OpCodes.Callvirt, info));
        h.Append(h.Create(OpCodes.Ldarg_0));
        h.Append(h.Create(OpCodes.Ldftn, closeIdle));
        h.Append(h.Create(OpCodes.Newobj, funcCtor));
        h.Append(h.Create(OpCodes.Call, run));
        h.Append(h.Create(OpCodes.Pop));
        h.Append(ret);
        check.Body.OptimizeMacros();
        ls.Methods.Add(check);

        // GetSegments() (lo usan todos los lectores: stream.ts, playlists y segmentos HLS):
        //   EmbyNoShare_LastRead = Environment.TickCount64;
        var first = getSegments.Body.Instructions[0];
        var g = getSegments.Body.GetILProcessor();
        g.InsertBefore(first, g.Create(OpCodes.Ldarg_0));
        g.InsertBefore(first, g.Create(OpCodes.Call, tick));
        g.InsertBefore(first, g.Create(OpCodes.Stfld, last));
        // OnSegmentCompleted(file): EmbyNoShare_CheckIdle(); al principio
        var o = onCompleted.Body.GetILProcessor();
        var firstO = onCompleted.Body.Instructions[0];
        o.InsertBefore(firstO, o.Create(OpCodes.Ldarg_0));
        o.InsertBefore(firstO, o.Create(OpCodes.Call, check));
    };
}

static Action? PrepareLiveReconnectFactor(ModuleDefinition module)
{
    var handler = module.GetType("Emby.LiveTV.TunerHosts.LimitingRetryHandler") ?? throw new Exception("No se encuentra LimitingRetryHandler");
    var add = handler.Methods.SingleOrDefault(m => m.Name == "Add" && m.Parameters.Count == 2 && m.Parameters[1].ParameterType.FullName == "System.Int32")
        ?? throw new Exception("No se encuentra LimitingRetryHandler.Add(TimeSpan, int)");
    // ldarg.0; ldfld allowedRetries; ldarg.1; ldarg.2; callvirt SortedDictionary::Add; ret
    var loads = add.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldarg_2).ToList();
    if (loads.Count != 1 || !(loads[0].Next.Operand is MethodReference m && m.Name == "Add"))
        throw new Exception("Patrón inesperado en LimitingRetryHandler.Add");
    var load = loads[0];
    var scope = module.TypeSystem.CoreLibrary; // System.Runtime
    Console.WriteLine($"[live-reconnect-factor] {add.FullName}");

    return () =>
    {
        var ts = module.TypeSystem;
        var getEnv = Bcl(module, scope, "System.Environment", "GetEnvironmentVariable", false, ts.String, ts.String);
        var int32 = new TypeReference("System", "Int32", module, scope, valueType: true);
        var tryParse = new MethodReference("TryParse", ts.Boolean, int32) { HasThis = false };
        tryParse.Parameters.Add(new ParameterDefinition(ts.String));
        tryParse.Parameters.Add(new ParameterDefinition(new ByReferenceType(ts.Int32)));

        // private static int EmbyNoShare_ScaleRetries(int n)
        // {
        //     string s = Environment.GetEnvironmentVariable("EMBY_LIVE_RECONNECT_FACTOR") ?? "5";
        //     if (!int.TryParse(s, out int f) || f < 1 || f > 1000) return n;
        //     return n * f;
        // }
        var helper = new MethodDefinition("EmbyNoShare_ScaleRetries", MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, ts.Int32);
        helper.Parameters.Add(new ParameterDefinition("n", ParameterAttributes.None, ts.Int32));
        var s = new VariableDefinition(ts.String);
        var f = new VariableDefinition(ts.Int32);
        helper.Body.Variables.Add(s);
        helper.Body.Variables.Add(f);
        helper.Body.InitLocals = true;
        var h = helper.Body.GetILProcessor();
        var retN = h.Create(OpCodes.Ldarg_0);
        var haveS = h.Create(OpCodes.Ldloc, s);
        h.Append(h.Create(OpCodes.Ldstr, "EMBY_LIVE_RECONNECT_FACTOR"));
        h.Append(h.Create(OpCodes.Call, getEnv));
        h.Append(h.Create(OpCodes.Stloc, s));
        h.Append(h.Create(OpCodes.Ldloc, s));
        h.Append(h.Create(OpCodes.Brtrue, haveS));
        h.Append(h.Create(OpCodes.Ldstr, "5"));
        h.Append(h.Create(OpCodes.Stloc, s));
        h.Append(haveS);
        h.Append(h.Create(OpCodes.Ldloca, f));
        h.Append(h.Create(OpCodes.Call, tryParse));
        h.Append(h.Create(OpCodes.Brfalse, retN));
        h.Append(h.Create(OpCodes.Ldloc, f));
        h.Append(h.Create(OpCodes.Ldc_I4_1));
        h.Append(h.Create(OpCodes.Blt, retN));
        h.Append(h.Create(OpCodes.Ldloc, f));
        h.Append(h.Create(OpCodes.Ldc_I4, 1000));
        h.Append(h.Create(OpCodes.Bgt, retN));
        h.Append(h.Create(OpCodes.Ldarg_0));
        h.Append(h.Create(OpCodes.Ldloc, f));
        h.Append(h.Create(OpCodes.Mul));
        h.Append(h.Create(OpCodes.Ret));
        h.Append(retN);
        h.Append(h.Create(OpCodes.Ret));
        helper.Body.OptimizeMacros();
        handler.Methods.Add(helper);

        // allowedRetries.Add(timeSpan, EmbyNoShare_ScaleRetries(maxRetryCount))
        add.Body.GetILProcessor().InsertAfter(load, Instruction.Create(OpCodes.Call, helper));
    };
}

static Action? PrepareLiveHlsWindow(ModuleDefinition module)
{
    var service = module.GetType("Emby.Server.MediaEncoding.Api.Hls.BaseHlsService") ?? throw new Exception("No se encuentra BaseHlsService");
    var method = service.Methods.SingleOrDefault(m => m.Name == "GetLivePlaylistText") ?? throw new Exception("No se encuentra GetLivePlaylistText");
    if (method.IsStatic || method.Parameters.Count != 4 || method.Parameters[1].ParameterType.Name != "TranscodingJob" || method.Parameters[3].ParameterType.FullName != "System.Int32")
        throw new Exception("Firma inesperada de GetLivePlaylistText");
    var il = method.Body.Instructions;
    bool Has(string text) => il.Any(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == text);
    if (!Has("#EXT-X-PLAYLIST-TYPE:EVENT") || !Has("#EXT-X-MEDIA-SEQUENCE:0")) throw new Exception("GetLivePlaylistText no escribe EVENT / MEDIA-SEQUENCE:0");
    // newobj StringBuilder; stloc.N
    var newSb = il.FirstOrDefault(i => i.OpCode == OpCodes.Newobj && ((MethodReference)i.Operand).DeclaringType.FullName == "System.Text.StringBuilder")
        ?? throw new Exception("No se encuentra el StringBuilder");
    var sbVar = StoredLocal(method, newSb.Next) ?? throw new Exception("Patrón inesperado al guardar el StringBuilder");
    // ... ToList<SegmentInfo>() queda en la pila hasta List<SegmentInfo>::GetEnumerator()
    var toLists = il.Where(i => i.Operand is MethodReference m && m.Name == "ToList" && m.DeclaringType.FullName == "System.Linq.Enumerable").ToList();
    var getEnums = il.Where(i => i.Operand is MethodReference m && m.Name == "GetEnumerator" && m.DeclaringType is GenericInstanceType g && g.ElementType.FullName == "System.Collections.Generic.List`1").ToList();
    if (toLists.Count != 1 || getEnums.Count != 1) throw new Exception("Patrón inesperado de la lista de segmentos");
    var toList = toLists[0];
    var listType = (GenericInstanceType)((MethodReference)getEnums[0].Operand).DeclaringType;
    if (listType.GenericArguments[0].FullName != "Emby.Server.MediaEncoding.Api.SegmentInfo") throw new Exception("La lista no es de SegmentInfo");
    var getFileSystem = il.Select(i => i.Operand).OfType<MethodReference>().FirstOrDefault(m => m.Name == "get_FileSystem") ?? throw new Exception("No se encuentra FileSystem");
    var getDirName = il.Select(i => i.Operand).OfType<MethodReference>().FirstOrDefault(m => m.Name == "GetDirectoryName" && m.DeclaringType.Name == "IFileSystem") ?? throw new Exception("No se encuentra GetDirectoryName");
    var getPath = il.Select(i => i.Operand).OfType<MethodReference>().FirstOrDefault(m => m.Name == "get_Path" && m.DeclaringType.Name == "TranscodingJob") ?? throw new Exception("No se encuentra TranscodingJob.Path");
    var getFileName = il.Select(i => i.Operand).OfType<MethodReference>().FirstOrDefault(m => m.Name == "GetFileName" && m.DeclaringType.FullName == "System.IO.Path") ?? throw new Exception("No se encuentra Path.GetFileName");
    var appendLine = il.Select(i => i.Operand).OfType<MethodReference>().First(m => m.Name == "AppendLine");
    var concat2 = il.Select(i => i.Operand).OfType<MethodReference>().FirstOrDefault(m => m.Name == "Concat" && m.Parameters.Count == 2 && m.DeclaringType.FullName == "System.String") ?? throw new Exception("No se encuentra String.Concat");
    var refs = module.GetMemberReferences().OfType<MethodReference>().ToList();
    var deleteFile = refs.Where(m => m.Name == "DeleteFile" && m.DeclaringType.FullName == "MediaBrowser.Model.IO.IFileSystem").OrderBy(m => m.Parameters.Count).FirstOrDefault()
        ?? throw new Exception("No se encuentra IFileSystem.DeleteFile");
    if (deleteFile.Parameters.Count > 1 && deleteFile.Parameters.Skip(1).Any(p => p.ParameterType.FullName != "System.Boolean")) throw new Exception("Firma inesperada de DeleteFile");
    var segInfo = module.GetType("Emby.Server.MediaEncoding.Api.SegmentInfo") ?? throw new Exception("No se encuentra SegmentInfo");
    var getFilename = segInfo.Properties.SingleOrDefault(p => p.Name == "Filename")?.GetMethod ?? throw new Exception("No se encuentra SegmentInfo.Filename");
    var exceptionScope = module.TypeSystem.CoreLibrary; // System.Runtime
    Console.WriteLine($"[live-hls-window] {method.FullName} (IL_{toList.Offset:x4})");

    return () =>
    {
        var ts = module.TypeSystem;
        var runtime = appendLine.DeclaringType.Scope;
        var sbType = appendLine.DeclaringType;
        var replace = new MethodReference("Replace", sbType, sbType) { HasThis = true };
        replace.Parameters.Add(new ParameterDefinition(ts.String));
        replace.Parameters.Add(new ParameterDefinition(ts.String));
        var getEnv = Bcl(module, runtime, "System.Environment", "GetEnvironmentVariable", false, ts.String, ts.String);
        var int32 = new TypeReference("System", "Int32", module, runtime, valueType: true);
        var tryParse = new MethodReference("TryParse", ts.Boolean, int32) { HasThis = false };
        tryParse.Parameters.Add(new ParameterDefinition(ts.String));
        tryParse.Parameters.Add(new ParameterDefinition(new ByReferenceType(ts.Int32)));
        var intToString = new MethodReference("ToString", ts.String, int32) { HasThis = true };
        var combine = new MethodReference("Combine", ts.String, getFileName.DeclaringType) { HasThis = false };
        combine.Parameters.Add(new ParameterDefinition(ts.String));
        combine.Parameters.Add(new ParameterDefinition(ts.String));
        var open = listType.ElementType;
        if (open.GenericParameters.Count == 0) open.GenericParameters.Add(new GenericParameter("T", open));
        var getCount = new MethodReference("get_Count", ts.Int32, listType) { HasThis = true };
        var getItem = new MethodReference("get_Item", open.GenericParameters[0], listType) { HasThis = true };
        getItem.Parameters.Add(new ParameterDefinition(ts.Int32));
        var removeRange = new MethodReference("RemoveRange", ts.Void, listType) { HasThis = true };
        removeRange.Parameters.Add(new ParameterDefinition(ts.Int32));
        removeRange.Parameters.Add(new ParameterDefinition(ts.Int32));
        var exception = new TypeReference("System", "Exception", module, exceptionScope, valueType: false);

        // private static List<SegmentInfo> EmbyNoShare_LiveWindow(List<SegmentInfo> list, TranscodingJob job, int segmentLength, StringBuilder sb, BaseHlsService self)
        // {
        //     string s = Environment.GetEnvironmentVariable("EMBY_LIVE_HLS_WINDOW_MINUTES") ?? "30";
        //     if (!int.TryParse(s, out int min) || min <= 0 || segmentLength <= 0) return list;
        //     sb.Replace("#EXT-X-PLAYLIST-TYPE:EVENT", "");
        //     int keep = min * 60 / segmentLength; if (keep < 10) keep = 10;
        //     int k = list.Count - keep;
        //     if (k <= 0) return list;
        //     sb.Replace("#EXT-X-MEDIA-SEQUENCE:0", "#EXT-X-MEDIA-SEQUENCE:" + k.ToString());
        //     string dir = self.FileSystem.GetDirectoryName(job.Path);
        //     for (int i = Math.Max(0, k - 20); i < k; i++)
        //         try { self.FileSystem.DeleteFile(Path.Combine(dir, list[i].Filename)); } catch (Exception) { }
        //     list.RemoveRange(0, k);
        //     return list;
        // }
        var helper = new MethodDefinition("EmbyNoShare_LiveWindow", MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, listType);
        helper.Parameters.Add(new ParameterDefinition("list", ParameterAttributes.None, listType));
        helper.Parameters.Add(new ParameterDefinition("job", ParameterAttributes.None, method.Parameters[1].ParameterType));
        helper.Parameters.Add(new ParameterDefinition("segmentLength", ParameterAttributes.None, ts.Int32));
        helper.Parameters.Add(new ParameterDefinition("sb", ParameterAttributes.None, sbType));
        helper.Parameters.Add(new ParameterDefinition("self", ParameterAttributes.None, service));
        var s = new VariableDefinition(ts.String);
        var min = new VariableDefinition(ts.Int32);
        var keep = new VariableDefinition(ts.Int32);
        var k = new VariableDefinition(ts.Int32);
        var dir = new VariableDefinition(ts.String);
        var i = new VariableDefinition(ts.Int32);
        foreach (var v in new[] { s, min, keep, k, dir, i }) helper.Body.Variables.Add(v);
        helper.Body.InitLocals = true;
        var h = helper.Body.GetILProcessor();
        var ret = h.Create(OpCodes.Ldarg_0);
        var haveS = h.Create(OpCodes.Ldloc, s);
        var keepOk = h.Create(OpCodes.Ldarg_0);
        var iOk = h.Create(OpCodes.Nop);
        var loopCond = h.Create(OpCodes.Ldloc, i);
        var next = h.Create(OpCodes.Ldloc, i);
        h.Append(h.Create(OpCodes.Ldstr, "EMBY_LIVE_HLS_WINDOW_MINUTES"));
        h.Append(h.Create(OpCodes.Call, getEnv));
        h.Append(h.Create(OpCodes.Stloc, s));
        h.Append(h.Create(OpCodes.Ldloc, s));
        h.Append(h.Create(OpCodes.Brtrue, haveS));
        h.Append(h.Create(OpCodes.Ldstr, "30"));
        h.Append(h.Create(OpCodes.Stloc, s));
        h.Append(haveS);
        h.Append(h.Create(OpCodes.Ldloca, min));
        h.Append(h.Create(OpCodes.Call, tryParse));
        h.Append(h.Create(OpCodes.Brfalse, ret));
        h.Append(h.Create(OpCodes.Ldloc, min));
        h.Append(h.Create(OpCodes.Ldc_I4_0));
        h.Append(h.Create(OpCodes.Ble, ret));
        h.Append(h.Create(OpCodes.Ldarg_2));
        h.Append(h.Create(OpCodes.Ldc_I4_0));
        h.Append(h.Create(OpCodes.Ble, ret));
        h.Append(h.Create(OpCodes.Ldarg_3));
        h.Append(h.Create(OpCodes.Ldstr, "#EXT-X-PLAYLIST-TYPE:EVENT"));
        h.Append(h.Create(OpCodes.Ldstr, ""));
        h.Append(h.Create(OpCodes.Callvirt, replace));
        h.Append(h.Create(OpCodes.Pop));
        h.Append(h.Create(OpCodes.Ldloc, min));
        h.Append(h.Create(OpCodes.Ldc_I4, 60));
        h.Append(h.Create(OpCodes.Mul));
        h.Append(h.Create(OpCodes.Ldarg_2));
        h.Append(h.Create(OpCodes.Div));
        h.Append(h.Create(OpCodes.Stloc, keep));
        h.Append(h.Create(OpCodes.Ldloc, keep));
        h.Append(h.Create(OpCodes.Ldc_I4, 10));
        h.Append(h.Create(OpCodes.Bge, keepOk));
        h.Append(h.Create(OpCodes.Ldc_I4, 10));
        h.Append(h.Create(OpCodes.Stloc, keep));
        h.Append(keepOk);
        h.Append(h.Create(OpCodes.Callvirt, getCount));
        h.Append(h.Create(OpCodes.Ldloc, keep));
        h.Append(h.Create(OpCodes.Sub));
        h.Append(h.Create(OpCodes.Stloc, k));
        h.Append(h.Create(OpCodes.Ldloc, k));
        h.Append(h.Create(OpCodes.Ldc_I4_0));
        h.Append(h.Create(OpCodes.Ble, ret));
        h.Append(h.Create(OpCodes.Ldarg_3));
        h.Append(h.Create(OpCodes.Ldstr, "#EXT-X-MEDIA-SEQUENCE:0"));
        h.Append(h.Create(OpCodes.Ldstr, "#EXT-X-MEDIA-SEQUENCE:"));
        h.Append(h.Create(OpCodes.Ldloca, k));
        h.Append(h.Create(OpCodes.Call, intToString));
        h.Append(h.Create(OpCodes.Call, concat2));
        h.Append(h.Create(OpCodes.Callvirt, replace));
        h.Append(h.Create(OpCodes.Pop));
        h.Append(h.Create(OpCodes.Ldarg, helper.Parameters[4]));
        h.Append(h.Create(OpCodes.Call, getFileSystem));
        h.Append(h.Create(OpCodes.Ldarg_1));
        h.Append(h.Create(OpCodes.Callvirt, getPath));
        h.Append(h.Create(OpCodes.Callvirt, getDirName));
        h.Append(h.Create(OpCodes.Stloc, dir));
        h.Append(h.Create(OpCodes.Ldloc, k));
        h.Append(h.Create(OpCodes.Ldc_I4, 20));
        h.Append(h.Create(OpCodes.Sub));
        h.Append(h.Create(OpCodes.Stloc, i));
        h.Append(h.Create(OpCodes.Ldloc, i));
        h.Append(h.Create(OpCodes.Ldc_I4_0));
        h.Append(h.Create(OpCodes.Bge, loopCond));
        h.Append(h.Create(OpCodes.Ldc_I4_0));
        h.Append(h.Create(OpCodes.Stloc, i));
        h.Append(h.Create(OpCodes.Br, loopCond));
        // cuerpo: try { DeleteFile } catch (Exception) { }
        var tryStart = h.Create(OpCodes.Ldarg, helper.Parameters[4]);
        h.Append(tryStart);
        h.Append(h.Create(OpCodes.Call, getFileSystem));
        h.Append(h.Create(OpCodes.Ldloc, dir));
        h.Append(h.Create(OpCodes.Ldarg_0));
        h.Append(h.Create(OpCodes.Ldloc, i));
        h.Append(h.Create(OpCodes.Callvirt, getItem));
        h.Append(h.Create(OpCodes.Callvirt, getFilename));
        h.Append(h.Create(OpCodes.Call, combine));
        for (var a = 1; a < deleteFile.Parameters.Count; a++) h.Append(h.Create(OpCodes.Ldc_I4_0));
        h.Append(h.Create(OpCodes.Callvirt, deleteFile));
        h.Append(h.Create(OpCodes.Leave, next));
        var catchStart = h.Create(OpCodes.Pop);
        h.Append(catchStart);
        h.Append(h.Create(OpCodes.Leave, next));
        h.Append(next);
        h.Append(h.Create(OpCodes.Ldc_I4_1));
        h.Append(h.Create(OpCodes.Add));
        h.Append(h.Create(OpCodes.Stloc, i));
        h.Append(loopCond);
        h.Append(h.Create(OpCodes.Ldloc, k));
        h.Append(h.Create(OpCodes.Blt, tryStart));
        h.Append(h.Create(OpCodes.Ldarg_0));
        h.Append(h.Create(OpCodes.Ldc_I4_0));
        h.Append(h.Create(OpCodes.Ldloc, k));
        h.Append(h.Create(OpCodes.Callvirt, removeRange));
        h.Append(ret);
        h.Append(h.Create(OpCodes.Ret));
        helper.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            TryStart = tryStart, TryEnd = catchStart,
            HandlerStart = catchStart, HandlerEnd = next,
            CatchType = exception,
        });
        helper.Body.OptimizeMacros();
        service.Methods.Add(helper);

        // ToList() -> EmbyNoShare_LiveWindow(ToList(), transcodingJob, segmentLength, sb, this)
        method.Body.SimplifyMacros();
        var p = method.Body.GetILProcessor();
        var anchor = toList;
        foreach (var ins in new[]
        {
            p.Create(OpCodes.Ldarg, method.Parameters[1]),
            p.Create(OpCodes.Ldarg, method.Parameters[3]),
            p.Create(OpCodes.Ldloc, sbVar),
            p.Create(OpCodes.Ldarg_0),
            p.Create(OpCodes.Call, helper),
        })
        {
            p.InsertAfter(anchor, ins);
            anchor = ins;
        }
        method.Body.OptimizeMacros();
    };
}

static VariableDefinition? StoredLocal(MethodDefinition method, Instruction ins)
{
    var vars = method.Body.Variables;
    if (ins.OpCode == OpCodes.Stloc_0) return vars[0];
    if (ins.OpCode == OpCodes.Stloc_1) return vars[1];
    if (ins.OpCode == OpCodes.Stloc_2) return vars[2];
    if (ins.OpCode == OpCodes.Stloc_3) return vars[3];
    return ins.Operand as VariableDefinition;
}

static Action? PrepareLiveFastOpen(ModuleDefinition module)
{
    var source = module.GetType("Emby.LiveTV.TunerHosts.SharedHttpPipelineSource") ?? throw new Exception("No se encuentra SharedHttpPipelineSource");
    var moveNext = GetMoveNext(source, "OpenInternal");
    var ins = moveNext.Body.Instructions;
    // SegmentDuration = SupportsPlaybackSegments ? TimeSpan.FromSeconds(3.0) : TimeSpan.FromMinutes(1.0)
    var seconds = ins.Where(i => i.OpCode == OpCodes.Ldc_R8 && (double)i.Operand == 3.0
        && i.Next.Operand is MethodReference f && f.Name == "FromSeconds"
        && i.Next.Next.Operand is MethodReference set && set.Name == "set_SegmentDuration").ToList();
    // for (numSegments = GetSegments().Count; numSegments < 2; ...)
    var count = ins.Where(i => i.OpCode == OpCodes.Ldc_I4_2
        && i.Previous.Operand is FieldReference fr && fr.Name.Contains("numSegments")
        && (i.Next.OpCode == OpCodes.Blt || i.Next.OpCode == OpCodes.Blt_S)).ToList();
    if (seconds.Count != 1 || count.Count != 1)
        throw new Exception($"Patrón inesperado en SharedHttpPipelineSource.OpenInternal ({seconds.Count}, {count.Count})");
    Console.WriteLine($"[live-fast-open] {moveNext.FullName}");

    return () =>
    {
        var segmentSeconds = EnvInt(module, source, "EmbyNoShare_SegmentSeconds", "EMBY_LIVE_SEGMENT_SECONDS", 1, 3, 1, 10);
        var openSegments = EnvInt(module, source, "EmbyNoShare_OpenSegments", "EMBY_LIVE_OPEN_SEGMENTS", 1, 2, 1, 10);
        // Se cambia la instrucción en el sitio (es destino de un salto): ldc.r8 3 -> call; conv.r8
        var s = seconds[0];
        s.OpCode = OpCodes.Call;
        s.Operand = segmentSeconds;
        moveNext.Body.GetILProcessor().InsertAfter(s, Instruction.Create(OpCodes.Conv_R8));
        var c = count[0];
        c.OpCode = OpCodes.Call;
        c.Operand = openSegments;
    };
}

/// static int Name(): entero de la variable de entorno; sin definir -> byDefault; fuera de [min, max] o no numérica -> original
static MethodDefinition EnvInt(ModuleDefinition module, TypeDefinition owner, string name, string variable, int byDefault, int original, int min, int max)
{
    var ts = module.TypeSystem;
    var getEnv = Bcl(module, ts.CoreLibrary, "System.Environment", "GetEnvironmentVariable", false, ts.String, ts.String);
    var int32 = new TypeReference("System", "Int32", module, ts.CoreLibrary, valueType: true);
    var tryParse = new MethodReference("TryParse", ts.Boolean, int32) { HasThis = false };
    tryParse.Parameters.Add(new ParameterDefinition(ts.String));
    tryParse.Parameters.Add(new ParameterDefinition(new ByReferenceType(ts.Int32)));

    var method = new MethodDefinition(name, MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, ts.Int32);
    var s = new VariableDefinition(ts.String);
    var v = new VariableDefinition(ts.Int32);
    method.Body.Variables.Add(s);
    method.Body.Variables.Add(v);
    method.Body.InitLocals = true;
    var h = method.Body.GetILProcessor();
    var useDefault = h.Create(OpCodes.Ldc_I4, byDefault);
    var useOriginal = h.Create(OpCodes.Ldc_I4, original);
    var parse = h.Create(OpCodes.Ldloc, s);
    h.Append(h.Create(OpCodes.Ldstr, variable));
    h.Append(h.Create(OpCodes.Call, getEnv));
    h.Append(h.Create(OpCodes.Stloc, s));
    h.Append(h.Create(OpCodes.Ldloc, s));
    h.Append(h.Create(OpCodes.Brtrue, parse));
    h.Append(useDefault);
    h.Append(h.Create(OpCodes.Ret));
    h.Append(parse);
    h.Append(h.Create(OpCodes.Ldloca, v));
    h.Append(h.Create(OpCodes.Call, tryParse));
    h.Append(h.Create(OpCodes.Brfalse, useOriginal));
    h.Append(h.Create(OpCodes.Ldloc, v));
    h.Append(h.Create(OpCodes.Ldc_I4, min));
    h.Append(h.Create(OpCodes.Blt, useOriginal));
    h.Append(h.Create(OpCodes.Ldloc, v));
    h.Append(h.Create(OpCodes.Ldc_I4, max));
    h.Append(h.Create(OpCodes.Bgt, useOriginal));
    h.Append(h.Create(OpCodes.Ldloc, v));
    h.Append(h.Create(OpCodes.Ret));
    h.Append(useOriginal);
    h.Append(h.Create(OpCodes.Ret));
    method.Body.OptimizeMacros();
    owner.Methods.Add(method);
    return method;
}
