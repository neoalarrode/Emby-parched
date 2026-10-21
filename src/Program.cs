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
