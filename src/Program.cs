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
// Uso: EmbyNoShare <ruta/Emby.LiveTV.dll | ruta/Emby.Server.MediaEncoding.dll> [--check]
//   --check: 0 = hay parches pendientes y todos los patrones coinciden, 3 = nada que hacer
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

if (args.Length < 1)
{
    Console.Error.WriteLine("Uso: EmbyNoShare <Emby.LiveTV.dll | Emby.Server.MediaEncoding.dll> [--check]");
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
