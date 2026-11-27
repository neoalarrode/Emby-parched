using System;
using System.Collections;
using System.IO;
using System.Reflection;

namespace EmbyNoShare
{
    /// <summary>Grabaciones DVR por usuario (parche dvr-owner y siguientes). Todo por reflexión, sin tipos de Emby,
    /// para que el parcheador pueda copiar estos métodos dentro de Emby.LiveTV.dll y para que valga entre versiones.
    /// El dueño de una grabación se guarda en TimerInfo.ProviderIds["OwnerUserId"] (ProviderIds se serializa con el
    /// timer, así que sobrevive a los reinicios y no cambia el esquema de la base de datos del DVR).</summary>
    public static class DvrCode
    {
        /// <summary>Identidad del usuario para el DVR: su nombre (el usuario no lo cambia, y así la carpeta de
        /// grabaciones es «Grabaciones/&lt;nombre&gt;»). Si no se puede leer el nombre, cae al Id como respaldo.</summary>
        public static string GetUserId(object user)
        {
            if (user == null)
                return null;
            try
            {
                Type t = user.GetType();
                PropertyInfo p = t.GetProperty("Name");
                object v = p != null ? p.GetValue(user) : null;
                if (v != null)
                {
                    string s = v.ToString();
                    if (!string.IsNullOrEmpty(s))
                        return s;
                }
                p = t.GetProperty("Id");
                v = p != null ? p.GetValue(user) : null;
                if (v != null)
                {
                    string s = v.ToString();
                    if (!string.IsNullOrEmpty(s) && s != Guid.Empty.ToString() && s != "0")
                        return s;
                }
                Trace("GetUserId", user == null ? "user=null" : "sin nombre ni id");
            }
            catch (Exception e)
            {
                Trace("GetUserId", "excepcion: " + e.Message);
            }
            return null;
        }

        /// <summary>Traza de diagnóstico a un fichero, solo si está puesta la variable EMBY_DVR_DEBUG (su valor es la
        /// ruta del fichero). Sin la variable no hace nada. Para localizar por dónde se pierde el dueño en pruebas.</summary>
        public static void Trace(string tag, string msg)
        {
            try
            {
                string path = Environment.GetEnvironmentVariable("EMBY_DVR_DEBUG");
                if (string.IsNullOrEmpty(path))
                    return;
                File.AppendAllText(path, DateTime.UtcNow.ToString("HH:mm:ss.fff") + " " + tag + ": " + (msg ?? "<null>") + "\n");
            }
            catch (Exception)
            {
            }
        }

        static IDictionary Providers(object timer)
        {
            if (timer == null)
                return null;
            PropertyInfo p = timer.GetType().GetProperty("ProviderIds");
            return p != null ? p.GetValue(timer) as IDictionary : null;
        }

        /// <summary>Marca el dueño del timer si aún no tiene (no pisa un dueño ya puesto).</summary>
        public static void SetOwnerId(object timer, string ownerId)
        {
            IDictionary d = Providers(timer);
            Trace("SetOwnerId", "owner=" + (ownerId ?? "null") + " providers=" + (d == null ? "null" : "ok"));
            if (ownerId == null)
                return;
            try
            {
                if (d != null && !d.Contains("OwnerUserId"))
                    d["OwnerUserId"] = ownerId;
            }
            catch (Exception)
            {
            }
        }

        /// <summary>Estampa el dueño en un ProviderIds PROPIO del timer (clona el actual en uno nuevo del mismo tipo y
        /// le añade OwnerUserId), para que no se comparta con el del programa (CopyProgramInfoToTimerInfo hace
        /// timer.ProviderIds = programa.ProviderIds). Se llama justo antes de guardar, tras la copia del programa.</summary>
        public static void SetOwnerFresh(object timer, string ownerId)
        {
            Trace("SetOwnerFresh", "owner=" + (ownerId ?? "null"));
            if (ownerId == null || timer == null)
                return;
            try
            {
                PropertyInfo prop = timer.GetType().GetProperty("ProviderIds");
                if (prop == null)
                    return;
                object current = prop.GetValue(timer);
                Type dictType = current != null ? current.GetType() : prop.PropertyType;
                IDictionary fresh = Activator.CreateInstance(dictType) as IDictionary;
                if (fresh == null)
                    return;
                if (current is IDictionary cur)
                    foreach (DictionaryEntry e in cur)
                        fresh[e.Key] = e.Value;
                fresh["OwnerUserId"] = ownerId;
                prop.SetValue(timer, fresh);
            }
            catch (Exception)
            {
            }
        }

        public static string GetOwnerId(object timer)
        {
            try
            {
                IDictionary d = Providers(timer);
                if (d != null && d.Contains("OwnerUserId"))
                    return d["OwnerUserId"] as string;
            }
            catch (Exception)
            {
            }
            return null;
        }

        /// <summary>Copia el dueño de 'series' a 'timer' (para los timers que nacen de una serie).</summary>
        public static void CopyOwner(object timer, object series)
        {
            try
            {
                string o = GetOwnerId(series);
                if (o != null)
                {
                    IDictionary d = Providers(timer);
                    if (d != null)
                        d["OwnerUserId"] = o;
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>Raíz de grabación por usuario: «&lt;root&gt;/&lt;owner&gt;», creando la carpeta. Si el timer no tiene
        /// dueño o root es nulo, devuelve root sin tocar. owner = nombre de usuario (ProviderIds["OwnerUserId"]).</summary>
        public static string UserBase(string root, object timer)
        {
            string owner = GetOwnerId(timer);
            Trace("UserBase", "root=" + (root ?? "null") + " user=" + (owner ?? "null"));
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(owner))
                return root;
            try
            {
                string dir = Path.Combine(root, SafeName(owner));
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                return dir;
            }
            catch (Exception e)
            {
                Trace("UserBase", "excepcion: " + e.Message);
                return root;
            }
        }

        /// <summary>Nombre de carpeta válido a partir del nombre de usuario.</summary>
        static string SafeName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "_";
            char[] bad = Path.GetInvalidFileNameChars();
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
            {
                bool ok = true;
                for (int i = 0; i < bad.Length; i++)
                    if (bad[i] == c) { ok = false; break; }
                sb.Append(ok ? c : '_');
            }
            string s = sb.ToString().Trim().TrimEnd('.');
            return s.Length == 0 ? "_" : s;
        }

        /// <summary>Si 'actor' (el usuario que pide la acción) no es el dueño del timer, lanza excepción. Si el timer
        /// no tiene dueño (grabaciones antiguas) o no hay actor (acción interna de Emby, no de una persona), deja pasar.</summary>
        public static void AssertOwner(object timer, string actor)
        {
            if (actor == null || timer == null)
                return;
            string owner = GetOwnerId(timer);
            if (owner != null && !string.Equals(owner, actor, StringComparison.Ordinal))
                throw new InvalidOperationException("Esta grabacion es de otro usuario.");
        }

        /// <summary>Al editar un timer: comprueba que 'actor' es el dueño del timer existente (si no, lanza), y
        /// conserva el dueño en el timer actualizado (el DTO que llega de la app no trae el dueño y se perdería al
        /// reconstruir el timer). 'existing' puede ser null (timer no encontrado): no hace nada.</summary>
        public static void UpdateGuard(object existing, object updated, string actor)
        {
            if (existing == null)
                return;
            AssertOwner(existing, actor);
            string owner = GetOwnerId(existing);
            if (owner != null)
            {
                try
                {
                    IDictionary d = Providers(updated);
                    if (d != null)
                        d["OwnerUserId"] = owner;
                }
                catch (Exception)
                {
                }
            }
        }

        static object Policy(object user)
        {
            if (user == null)
                return null;
            PropertyInfo p = user.GetType().GetProperty("Policy");
            return p != null ? p.GetValue(user) : null;
        }

        /// <summary>Límite de reproducciones simultáneas del usuario (UserPolicy.SimultaneousStreamLimit). 0 = ilimitado.</summary>
        public static int SimultaneousLimit(object user)
        {
            try
            {
                object p = Policy(user);
                if (p == null)
                    return 0;
                PropertyInfo lim = p.GetType().GetProperty("SimultaneousStreamLimit");
                object v = lim != null ? lim.GetValue(p) : null;
                return v is int ? (int)v : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        static DateTimeOffset ReadDate(object o, string prop)
        {
            try
            {
                PropertyInfo p = o.GetType().GetProperty(prop);
                object v = p != null ? p.GetValue(o) : null;
                return v is DateTimeOffset ? (DateTimeOffset)v : DateTimeOffset.MinValue;
            }
            catch (Exception)
            {
                return DateTimeOffset.MinValue;
            }
        }

        static int ReadInt(object o, string prop)
        {
            try
            {
                PropertyInfo p = o.GetType().GetProperty(prop);
                object v = p != null ? p.GetValue(o) : null;
                return v is int ? (int)v : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        static string ReadStr(object o, string prop)
        {
            try
            {
                PropertyInfo p = o.GetType().GetProperty(prop);
                object v = p != null ? p.GetValue(o) : null;
                return v != null ? v.ToString() : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Ventana del timer con sus márgenes: [inicio - prePadding, fin + postPadding].</summary>
        static bool Window(object timer, out DateTimeOffset start, out DateTimeOffset end)
        {
            start = ReadDate(timer, "StartDate");
            end = ReadDate(timer, "EndDate");
            if (start == DateTimeOffset.MinValue || end == DateTimeOffset.MinValue || end <= start)
                return false;
            start = start.AddSeconds(-ReadInt(timer, "PrePaddingSeconds"));
            end = end.AddSeconds(ReadInt(timer, "PostPaddingSeconds"));
            return true;
        }

        /// <summary>Límite de grabaciones a la vez: si el dueño del timer nuevo ya tiene 'limit' grabaciones que
        /// solapan en el tiempo con esta, lanza una excepción (la app muestra el error y no se programa). 'existing'
        /// son todos los timers actuales. limit 0 o negativo = sin límite.</summary>
        public static void CheckLimit(IEnumerable existing, object newTimer, string owner, int limit)
        {
            if (limit <= 0 || newTimer == null || existing == null || owner == null)
                return;
            DateTimeOffset ns, ne;
            if (!Window(newTimer, out ns, out ne))
                return;
            string newId = ReadStr(newTimer, "Id");
            int count = 0;
            foreach (object t in existing)
            {
                if (t == null)
                    continue;
                string id = ReadStr(t, "Id");
                if (newId != null && string.Equals(id, newId, StringComparison.Ordinal))
                    continue;                                   // el propio timer, si ya estuviera en la lista
                if (!string.Equals(GetOwnerId(t), owner, StringComparison.Ordinal))
                    continue;                                   // de otro usuario
                DateTimeOffset s, e;
                if (!Window(t, out s, out e))
                    continue;
                if (ns < e && s < ne)                            // se solapan
                    count++;
            }
            if (count + 1 > limit)
                throw new InvalidOperationException(
                    "Has alcanzado tu limite de " + limit + " grabacion(es) a la vez. Cancela otra o espera a que termine para programar esta.");
        }

        public static bool IsAdmin(object user)
        {
            try
            {
                object p = Policy(user);
                if (p == null)
                    return false;
                PropertyInfo a = p.GetType().GetProperty("IsAdministrator");
                object v = a != null ? a.GetValue(p) : null;
                return v is bool && (bool)v;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Id del usuario que actúa, para ver/cancelar/editar; null si es administrador (null = ver y poder
        /// con todo, sin filtro ni candado). Así el admin gestiona el DVR de todos.</summary>
        public static string GetActorId(object user)
        {
            return IsAdmin(user) ? null : GetUserId(user);
        }

        /// <summary>Quita de una lista (IList de timers) los que NO son del viewer (deja los suyos y los sin dueño).
        /// Si viewer es null (admin o acción interna) no toca nada. Modifica la lista en el sitio.</summary>
        public static void RemoveNotOwned(object list, string viewer)
        {
            if (viewer == null || !(list is IList l))
                return;
            try
            {
                for (int i = l.Count - 1; i >= 0; i--)
                {
                    string owner = GetOwnerId(l[i]);
                    if (owner != null && !string.Equals(owner, viewer, StringComparison.Ordinal))
                        l.RemoveAt(i);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>Filtra un QueryResult de timers para dejar solo los del actor (y los sin dueño, grabaciones
        /// antiguas). Si actor es null (administrador o acción interna), no cambia nada. Devuelve el mismo objeto.</summary>
        public static object FilterTimers(object queryResult, string actor)
        {
            if (queryResult == null || actor == null)
                return queryResult;
            try
            {
                PropertyInfo itemsProp = queryResult.GetType().GetProperty("Items");
                Array items = itemsProp != null ? itemsProp.GetValue(queryResult) as Array : null;
                if (items == null)
                    return queryResult;
                Type element = items.GetType().GetElementType();
                int kept = 0;
                for (int i = 0; i < items.Length; i++)
                {
                    object t = items.GetValue(i);
                    string owner = GetOwnerId(t);
                    if (owner == null || string.Equals(owner, actor, StringComparison.Ordinal))
                        kept++;
                }
                if (kept == items.Length)
                    return queryResult;
                Array dst = Array.CreateInstance(element, kept);
                int j = 0;
                for (int i = 0; i < items.Length; i++)
                {
                    object t = items.GetValue(i);
                    string owner = GetOwnerId(t);
                    if (owner == null || string.Equals(owner, actor, StringComparison.Ordinal))
                        dst.SetValue(t, j++);
                }
                itemsProp.SetValue(queryResult, dst);
                PropertyInfo countProp = queryResult.GetType().GetProperty("TotalRecordCount");
                if (countProp != null && countProp.CanWrite)
                    countProp.SetValue(queryResult, kept);
            }
            catch (Exception)
            {
            }
            return queryResult;
        }
    }
}
