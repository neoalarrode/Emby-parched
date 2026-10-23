// Copia tipos de EmbyNoShare.Runtime.dll (recurso incrustado, ver runtime/) dentro de una DLL de Emby.
// Así la lógica de los parches grandes se escribe en C# normal y el IL que se inyecta en los métodos de Emby
// se reduce a una llamada. El código copiado solo usa la biblioteca estándar (a Emby accede por reflexión).
//
// Limitaciones (comprobadas al copiar): sin tipos ni métodos genéricos propios, sin call sites (calli) y sin
// usar tipos propios como argumentos de genéricos de la biblioteca (p. ej. List<TipoPropio>): ahí se usa object.
using Mono.Cecil;
using Mono.Cecil.Cil;

static class Injector
{
    const string RuntimeNamespace = "EmbyNoShare.Runtime";
    static ModuleDefinition? runtimeModule;

    static ModuleDefinition Runtime()
    {
        if (runtimeModule != null) return runtimeModule;
        using var stream = typeof(Injector).Assembly.GetManifestResourceStream("EmbyNoShare.Runtime.dll")
            ?? throw new Exception("Falta el recurso EmbyNoShare.Runtime.dll");
        var ms = new MemoryStream();
        stream.CopyTo(ms);
        ms.Position = 0;
        return runtimeModule = ModuleDefinition.ReadModule(ms);
    }

    /// Devuelve el tipo EmbyNoShare.Runtime.<name> dentro de target, copiándolo si aún no está.
    public static TypeDefinition Ensure(ModuleDefinition target, string name)
    {
        var existing = target.Types.FirstOrDefault(t => t.Namespace == RuntimeNamespace && t.Name == name);
        if (existing != null) return existing;
        var src = Runtime().Types.FirstOrDefault(t => t.Namespace == RuntimeNamespace && t.Name == name)
            ?? throw new Exception($"No existe {RuntimeNamespace}.{name} en EmbyNoShare.Runtime.dll");
        return new Copier(src.Module, target).Copy(src);
    }

    /// Método público estático del tipo copiado
    public static MethodDefinition Method(TypeDefinition type, string name, int parameters)
        => type.Methods.Single(m => m.Name == name && m.Parameters.Count == parameters);

    sealed class Copier
    {
        readonly ModuleDefinition src, dst;
        readonly Dictionary<TypeDefinition, TypeDefinition> types = new();
        readonly Dictionary<MethodDefinition, MethodDefinition> methods = new();
        readonly Dictionary<FieldDefinition, FieldDefinition> fields = new();

        public Copier(ModuleDefinition src, ModuleDefinition dst) { this.src = src; this.dst = dst; }

        // Tipos ya copiados en el destino (por otro parche) y pendientes de rellenar
        readonly Queue<TypeDefinition> pending = new();
        readonly HashSet<MethodDefinition> existingMethods = new();

        public TypeDefinition Copy(TypeDefinition root)
        {
            var copy = Root(root);
            // Los tipos propios que use (p. ej. Reflect) se copian también, al encontrarlos
            var filled = new HashSet<MethodDefinition>();
            while (pending.Count > 0)
            {
                var from = pending.Dequeue();
                if (membered.Add(from)) Members(from, types[from]);
                foreach (var (m, nm) in methods.ToList())
                    if (!existingMethods.Contains(m) && filled.Add(m)) Body(m, nm);
            }
            return copy;
        }

        TypeDefinition Root(TypeDefinition from)
        {
            var existing = dst.Types.FirstOrDefault(t => t.FullName == from.FullName);
            if (existing != null) { MapExisting(from, existing); return existing; }
            var copy = Shell(from, null);
            dst.Types.Add(copy);
            return copy;
        }

        // Tipo ya presente en el destino: se usan sus miembros tal cual
        void MapExisting(TypeDefinition from, TypeDefinition to)
        {
            types[from] = to;
            membered.Add(from);
            foreach (var f in from.Fields) fields[f] = to.Fields.Single(x => x.Name == f.Name);
            foreach (var m in from.Methods)
                existingMethods.Add(m);
            foreach (var m in from.Methods)
                methods[m] = to.Methods.Single(x => x.Name == m.Name && x.Parameters.Count == m.Parameters.Count
                    && x.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(m.Parameters.Select(p => p.ParameterType.FullName)));
            foreach (var n in from.NestedTypes) MapExisting(n, to.NestedTypes.Single(x => x.Name == n.Name));
        }

        TypeDefinition Shell(TypeDefinition from, TypeDefinition? parent)
        {
            if (from.HasGenericParameters) throw new Exception("Tipo genérico no soportado: " + from.FullName);
            var to = new TypeDefinition(parent == null ? from.Namespace : "", from.Name, from.Attributes);
            types[from] = to;
            pending.Enqueue(from);
            parent?.NestedTypes.Add(to);
            foreach (var nested in from.NestedTypes) Shell(nested, to);
            return to;
        }

        void Members(TypeDefinition from, TypeDefinition to)
        {
            if (from.BaseType != null) to.BaseType = Type(from.BaseType);
            foreach (var i in from.Interfaces) to.Interfaces.Add(new InterfaceImplementation(Type(i.InterfaceType)));
            foreach (var f in from.Fields)
            {
                var nf = new FieldDefinition(f.Name, f.Attributes, Type(f.FieldType));
                if (f.HasConstant) nf.Constant = f.Constant;
                if (f.InitialValue is { Length: > 0 }) nf.InitialValue = f.InitialValue;
                to.Fields.Add(nf);
                fields[f] = nf;
            }
            foreach (var m in from.Methods)
            {
                if (m.HasGenericParameters) throw new Exception("Método genérico no soportado: " + m.FullName);
                var nm = new MethodDefinition(m.Name, m.Attributes, dst.TypeSystem.Void) { ImplAttributes = m.ImplAttributes };
                to.Methods.Add(nm);
                nm.ReturnType = Type(m.ReturnType);
                foreach (var p in m.Parameters)
                    nm.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, Type(p.ParameterType)));
                foreach (var o in m.Overrides) nm.Overrides.Add(Method(o));
                methods[m] = nm;
            }
            foreach (var pr in from.Properties)
            {
                var np = new PropertyDefinition(pr.Name, pr.Attributes, Type(pr.PropertyType));
                if (pr.GetMethod != null) np.GetMethod = methods[pr.GetMethod];
                if (pr.SetMethod != null) np.SetMethod = methods[pr.SetMethod];
                to.Properties.Add(np);
            }
        }

        void Body(MethodDefinition from, MethodDefinition to)
        {
            if (!from.HasBody) return;
            var body = to.Body;
            body.InitLocals = from.Body.InitLocals;
            body.MaxStackSize = from.Body.MaxStackSize;
            var vars = new Dictionary<VariableDefinition, VariableDefinition>();
            foreach (var v in from.Body.Variables)
            {
                var nv = new VariableDefinition(Type(v.VariableType));
                body.Variables.Add(nv);
                vars[v] = nv;
            }
            var ins = new Dictionary<Instruction, Instruction>();
            foreach (var i in from.Body.Instructions)
            {
                var ni = Instruction.Create(OpCodes.Nop);
                ni.OpCode = i.OpCode;
                ni.Operand = i.Operand switch
                {
                    TypeReference t => Type(t),
                    MethodReference m => Method(m),
                    FieldReference f => Field(f),
                    VariableDefinition v => vars[v],
                    ParameterDefinition p => p.Index < 0 ? body.ThisParameter : to.Parameters[p.Index],
                    CallSite => throw new Exception("calli no soportado en " + from.FullName),
                    _ => i.Operand,
                };
                body.Instructions.Add(ni);
                ins[i] = ni;
            }
            foreach (var ni in body.Instructions)
            {
                if (ni.Operand is Instruction target) ni.Operand = ins[target];
                else if (ni.Operand is Instruction[] targets) ni.Operand = targets.Select(t => ins[t]).ToArray();
            }
            foreach (var h in from.Body.ExceptionHandlers)
            {
                body.ExceptionHandlers.Add(new ExceptionHandler(h.HandlerType)
                {
                    TryStart = ins[h.TryStart],
                    TryEnd = h.TryEnd == null ? null : ins[h.TryEnd],
                    HandlerStart = ins[h.HandlerStart],
                    HandlerEnd = h.HandlerEnd == null ? null : ins[h.HandlerEnd],
                    FilterStart = h.FilterStart == null ? null : ins[h.FilterStart],
                    CatchType = h.CatchType == null ? null : Type(h.CatchType),
                });
            }
        }

        // Asegura que el tipo propio está copiado y con sus miembros creados
        readonly HashSet<TypeDefinition> membered = new();
        void Ready(TypeDefinition def)
        {
            OwnType(def);
            if (membered.Add(def)) Members(def, types[def]);
        }

        bool Own(TypeReference t) => t.Scope == src || t.Scope?.Name == src.Assembly.Name.Name
                                     || (t is TypeDefinition td && td.Module == src);

        TypeDefinition OwnType(TypeReference t)
        {
            var def = t as TypeDefinition ?? t.Resolve() ?? throw new Exception("No se resuelve " + t.FullName);
            if (types.TryGetValue(def, out var to)) return to;
            var top = def;
            while (top.DeclaringType != null) top = top.DeclaringType;
            Root(top);
            return types[def];
        }

        TypeReference Type(TypeReference t)
        {
            switch (t)
            {
                case GenericParameter:
                    throw new Exception("Parámetro genérico no soportado: " + t.FullName);
                case ArrayType a:
                    return a.IsVector ? new ArrayType(Type(a.ElementType)) : new ArrayType(Type(a.ElementType), a.Rank);
                case ByReferenceType b:
                    return new ByReferenceType(Type(b.ElementType));
                case PointerType p:
                    return new PointerType(Type(p.ElementType));
                case GenericInstanceType g:
                    var ng = new GenericInstanceType(Type(g.ElementType));
                    foreach (var arg in g.GenericArguments)
                    {
                        if (ContainsOwn(arg)) throw new Exception("Tipo propio como argumento genérico: " + g.FullName);
                        ng.GenericArguments.Add(dst.ImportReference(arg));
                    }
                    return ng;
            }
            return Own(t) ? OwnType(t) : dst.ImportReference(t);
        }

        bool ContainsOwn(TypeReference t) => t switch
        {
            GenericInstanceType g => ContainsOwn(g.ElementType) || g.GenericArguments.Any(ContainsOwn),
            TypeSpecification s => ContainsOwn(s.ElementType),
            GenericParameter => false,
            _ => Own(t),
        };

        MethodReference Method(MethodReference m)
        {
            if (m is GenericInstanceMethod gm)
            {
                if (ContainsOwn(gm.DeclaringType) || gm.GenericArguments.Any(ContainsOwn))
                    throw new Exception("Genérico con tipo propio no soportado: " + gm.FullName);
                return dst.ImportReference(gm);
            }
            if (Own(m.DeclaringType))
            {
                var def = m.Resolve() ?? throw new Exception("No se resuelve " + m.FullName);
                Ready(def.DeclaringType);
                return methods.TryGetValue(def, out var to) ? to : throw new Exception("Método propio no copiado: " + m.FullName);
            }
            if (ContainsOwn(m.DeclaringType)) throw new Exception("Genérico con tipo propio no soportado: " + m.FullName);
            return dst.ImportReference(m);
        }

        FieldReference Field(FieldReference f)
        {
            if (Own(f.DeclaringType))
            {
                var def = f.Resolve() ?? throw new Exception("No se resuelve " + f.FullName);
                Ready(def.DeclaringType);
                return fields.TryGetValue(def, out var to) ? to : throw new Exception("Campo propio no copiado: " + f.FullName);
            }
            if (ContainsOwn(f.DeclaringType)) throw new Exception("Genérico con tipo propio no soportado: " + f.FullName);
            return dst.ImportReference(f);
        }
    }
}
