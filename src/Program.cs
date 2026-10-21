// Parche para Emby.LiveTV.dll: desactiva la compartición de streams de Live TV.
//
// 1) Emby.LiveTV.TunerHosts.LiveStream::get_EnableStreamSharing -> siempre false.
//    EmbyTV.GetChannelStreamWithDirectStreamProvider deja de reutilizar un
//    ILiveStream abierto del mismo canal y abre una conexión nueva al proveedor
//    (con su propio buffer en disco/HLS, ya que cada LiveStream tiene UniqueId).
//
// 2) Emby.LiveTV.LiveTvManager::GetChannelStream -> LiveStreamId único por stream.
//    Originalmente LiveStreamId = md5(provider) + "_" + mediaSource.Id, igual para
//    todos los clientes del mismo canal; sin compartición el segundo stream pisaría
//    al primero en MediaSourceManager._openStreams. Se añade "_" + Guid.
//
// Uso: EmbyNoShare <ruta/Emby.LiveTV.dll> [--check]   (--check: 0 = parcheable, 3 = ya parcheado)
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

if (module.Types.Any(t => t.FullName == "EmbyNoShare.Patched"))
{
    Console.WriteLine("Ya parcheado: " + path);
    return checkOnly ? 3 : 0;
}

// --- 1) LiveStream.EnableStreamSharing ---
var liveStream = module.GetType("Emby.LiveTV.TunerHosts.LiveStream") ?? throw new Exception("No se encuentra LiveStream");
var getter = liveStream.Properties.Single(p => p.Name == "EnableStreamSharing").GetMethod;
Console.WriteLine($"[1] {getter.FullName}");

// --- 2) LiveTvManager.GetChannelStream (state machine async) ---
var mgr = module.GetType("Emby.LiveTV.LiveTvManager") ?? throw new Exception("No se encuentra LiveTvManager");
var sm = mgr.NestedTypes.Where(t => t.Name.StartsWith("<GetChannelStream>")).ToList();
if (sm.Count != 1) throw new Exception($"Se esperaba 1 state machine GetChannelStream, hay {sm.Count}");
var moveNext = sm[0].Methods.Single(m => m.Name == "MoveNext");
var setIds = moveNext.Body.Instructions.Where(i => i.Operand is MethodReference mr && mr.Name == "set_LiveStreamId").ToList();
if (setIds.Count != 1) throw new Exception($"Se esperaba 1 set_LiveStreamId, hay {setIds.Count}");
var concat = setIds[0].Previous;
if (!(concat.OpCode == OpCodes.Call && concat.Operand is MethodReference cr && cr.Name == "Concat" && cr.Parameters.Count == 2))
    throw new Exception("Patrón IL inesperado antes de set_LiveStreamId: " + concat);
Console.WriteLine($"[2] {moveNext.FullName} (IL_{setIds[0].Offset:x4})");

if (checkOnly)
{
    Console.WriteLine("Comprobación OK, el patrón coincide.");
    return 0;
}

// Aplicar 1: return false;
getter.Body.Instructions.Clear();
getter.Body.ExceptionHandlers.Clear();
getter.Body.Variables.Clear();
var gp = getter.Body.GetILProcessor();
gp.Append(gp.Create(OpCodes.Ldc_I4_0));
gp.Append(gp.Create(OpCodes.Ret));

// Aplicar 2: Concat(a, b) -> Concat(Concat(a, b), "_", Guid.NewGuid().ToString("N"))
// Las referencias de tipos se toman del propio ensamblado (System.Runtime) en lugar de typeof().
var concatRef = (MethodReference)concat.Operand;
var stringType = concatRef.DeclaringType;
var scope = stringType.Scope;
var guidType = new TypeReference("System", "Guid", module, scope, valueType: true);
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

// Marcador para no parchear dos veces
module.Types.Add(new TypeDefinition("EmbyNoShare", "Patched", TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed, module.TypeSystem.Object));

asm.Write(path);
Console.WriteLine("Parche aplicado: " + path);
return 0;
