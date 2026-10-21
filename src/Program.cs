// Parches para Emby.LiveTV.dll. Cada parche lleva su propio marcador en el ensamblado,
// así que el parcheador solo aplica los que falten (por ejemplo, sobre una DLL que ya
// tenía el parche de compartición se aplica únicamente el de HLS).
//
// noshare (marcador EmbyNoShare.Patched): desactiva la compartición de streams de Live TV.
//   a) Emby.LiveTV.TunerHosts.LiveStream::get_EnableStreamSharing -> siempre false.
//      EmbyTV.GetChannelStreamWithDirectStreamProvider deja de reutilizar un
//      ILiveStream abierto del mismo canal y abre una conexión nueva al proveedor
//      (con su propio buffer en disco/HLS, ya que cada LiveStream tiene UniqueId).
//   b) Emby.LiveTV.LiveTvManager::GetChannelStream -> LiveStreamId único por stream.
//      Originalmente LiveStreamId = md5(provider) + "_" + mediaSource.Id, igual para
//      todos los clientes del mismo canal; sin compartición el segundo stream pisaría
//      al primero en MediaSourceManager._openStreams. Se añade "_" + Guid.
//
// hls-targetduration (marcador EmbyNoShare.HlsTargetDuration):
//   Emby.LiveTV.Api.LiveStreamService::GetVariantPlaylistFileText escribe siempre
//   #EXT-X-TARGETDURATION:4 (SegmentDuration 3 s + 1), pero el segmentador solo corta en
//   keyframes, así que con GOPs largos los segmentos duran 5, 6, 10 s... El método ya
//   calcula max(duración de los segmentos, SegmentDuration + 1) y descarta el resultado;
//   el parche lo guarda y lo usa para TARGETDURATION, como exige RFC 8216 (4.3.3.1).
//   Los reproductores HLS (ExoPlayer/Media3 en Fire TV) usan TARGETDURATION para la
//   distancia al directo (3x), la frecuencia de recarga y la detección de playlist atascada.
//
// Uso: EmbyNoShare <ruta/Emby.LiveTV.dll> [--check]
//   --check: 0 = hay parches pendientes y todos los patrones coinciden, 3 = ya parcheado
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length < 1)
{
    Console.Error.WriteLine("Uso: EmbyNoShare <Emby.LiveTV.dll> [--check]");
    return 2;
}

var path = Path.GetFullPath(args[0]);
var checkOnly = args.Contains("--check");
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(path)!);

using var asm = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { AssemblyResolver = resolver, InMemory = true });
var module = asm.MainModule;

var patches = new (string Marker, string Name, Func<ModuleDefinition, Action> Prepare)[]
{
    ("Patched", "noshare", PrepareNoShare),
    ("HlsTargetDuration", "hls-targetduration", PrepareHlsTargetDuration),
};

// Localiza y valida todos los parches pendientes antes de tocar nada
var pending = new List<(string Marker, string Name, Action Apply)>();
foreach (var (marker, name, prepare) in patches)
{
    if (module.Types.Any(t => t.FullName == "EmbyNoShare." + marker))
    {
        Console.WriteLine($"[{name}] ya aplicado");
        continue;
    }
    pending.Add((marker, name, prepare(module)));
}

if (pending.Count == 0)
{
    Console.WriteLine("Ya parcheado: " + path);
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

static Action PrepareNoShare(ModuleDefinition module)
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

static Action PrepareHlsTargetDuration(ModuleDefinition module)
{
    var service = module.GetType("Emby.LiveTV.Api.LiveStreamService") ?? throw new Exception("No se encuentra LiveStreamService");
    var moveNext = GetMoveNext(service, "GetVariantPlaylistFileText");
    var il = moveNext.Body.Instructions;

    // ... Enumerable.Max<TimeSpan>(durations.Concat(SegmentDuration + 1s)); pop   <- resultado descartado
    var maxCalls = il.Where(i => i.OpCode == OpCodes.Call && i.Operand is GenericInstanceMethod gm
        && gm.Name == "Max" && gm.DeclaringType.FullName == "System.Linq.Enumerable"
        && gm.GenericArguments.Count == 1 && gm.GenericArguments[0].FullName == "System.TimeSpan").ToList();
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
