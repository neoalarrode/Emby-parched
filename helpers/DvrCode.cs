using System;
using System.Collections;
using System.Reflection;

namespace EmbyNoShare
{
    /// <summary>Grabaciones DVR por usuario (parche dvr-owner y siguientes). Todo por reflexión, sin tipos de Emby,
    /// para que el parcheador pueda copiar estos métodos dentro de Emby.LiveTV.dll y para que valga entre versiones.
    /// El dueño de una grabación se guarda en TimerInfo.ProviderIds["OwnerUserId"] (ProviderIds se serializa con el
    /// timer, así que sobrevive a los reinicios y no cambia el esquema de la base de datos del DVR).</summary>
    public static class DvrCode
    {
        /// <summary>Id estable del usuario (su Guid Id; si no, InternalId). null si no se puede leer.</summary>
        public static string GetUserId(object user)
        {
            if (user == null)
                return null;
            try
            {
                Type t = user.GetType();
                PropertyInfo p = t.GetProperty("Id");
                object v = p != null ? p.GetValue(user) : null;
                if (v != null)
                {
                    string s = v.ToString();
                    if (!string.IsNullOrEmpty(s) && s != Guid.Empty.ToString() && s != "0")
                        return s;
                }
                p = t.GetProperty("InternalId");
                v = p != null ? p.GetValue(user) : null;
                if (v != null)
                {
                    string s = v.ToString();
                    if (!string.IsNullOrEmpty(s) && s != "0")
                        return s;
                }
            }
            catch (Exception)
            {
            }
            return null;
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
            if (ownerId == null)
                return;
            try
            {
                IDictionary d = Providers(timer);
                if (d != null && !d.Contains("OwnerUserId"))
                    d["OwnerUserId"] = ownerId;
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
    }
}
