// Acceso a Emby por reflexión (este código se copia dentro de sus DLL y no se compila contra ellas)
using System;
using System.Reflection;

namespace EmbyNoShare.Runtime
{
    public static class Reflect
    {
        const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public static object Get(object target, string name)
        {
            if (target == null) return null;
            for (var t = target.GetType(); t != null; t = t.BaseType)
            {
                var p = t.GetProperty(name, All | BindingFlags.DeclaredOnly);
                if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(target);
                var f = t.GetField(name, All | BindingFlags.DeclaredOnly);
                if (f != null) return f.GetValue(target);
            }
            throw new MissingMemberException(target.GetType().FullName, name);
        }

        public static void Set(object target, string name, object value)
        {
            for (var t = target.GetType(); t != null; t = t.BaseType)
            {
                var p = t.GetProperty(name, All | BindingFlags.DeclaredOnly);
                if (p != null) { (p.GetSetMethod(true) ?? throw new MissingMethodException(t.FullName, "set_" + name)).Invoke(target, new[] { value }); return; }
                var f = t.GetField(name, All | BindingFlags.DeclaredOnly);
                if (f != null) { f.SetValue(target, value); return; }
            }
            throw new MissingMemberException(target.GetType().FullName, name);
        }

        public static object Call(object target, string name, params object[] args)
        {
            for (var t = target.GetType(); t != null; t = t.BaseType)
            {
                foreach (var m in t.GetMethods(All | BindingFlags.DeclaredOnly))
                {
                    if (m.Name != name || m.IsGenericMethodDefinition) continue;
                    var ps = m.GetParameters();
                    if (ps.Length != args.Length) continue;
                    var ok = true;
                    for (var i = 0; i < ps.Length && ok; i++)
                        ok = args[i] == null ? !ps[i].ParameterType.IsValueType || Nullable.GetUnderlyingType(ps[i].ParameterType) != null
                                             : ps[i].ParameterType.IsInstanceOfType(args[i]);
                    if (ok) return m.Invoke(target, args);
                }
            }
            throw new MissingMethodException(target.GetType().FullName, name);
        }

        /// Info en el log de Emby a través del ILogger del objeto (propiedad Logger)
        public static void Log(object owner, string message)
        {
            try
            {
                var logger = owner == null ? null : Get(owner, "Logger");
                if (logger != null) Call(logger, "Info", message, new object[0]);
            }
            catch { }
        }
    }
}
