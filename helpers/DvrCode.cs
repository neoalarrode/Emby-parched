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
