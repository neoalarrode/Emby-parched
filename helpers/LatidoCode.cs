using System;
using System.Net.Http;
using System.Reflection;
using System.Threading;

namespace EmbyNoShare
{
    /// <summary>Latido hacia Dispatcharr (parche live-heartbeat): mientras un canal en directo de Emby lee la salida HLS
    /// de Dispatcharr (…/proxy/hls/&lt;sesión&gt;/index.m3u8), le avisa cada medio segundo de que sigue ahí, y al
    /// cerrarse le dice adiós, para que Dispatcharr cierre esa sesión al momento y libere la fuente (las DRM solo
    /// admiten una). Sin campos ni tipos propios: el estado se lee del propio LiveStream por reflexión (la ruta de su
    /// MediaSource y su LiveStreamCancellationTokenSource, que Close() cancela).</summary>
    public static class LatidoCode
    {
        public static void Start(object live)
        {
            try
            {
                Thread thread = new Thread(new ParameterizedThreadStart(Loop));
                thread.IsBackground = true;
                thread.Name = "EmbyNoShare-latido";
                thread.Start(live);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>0: aún sin abrir; 1: abierto; 2: cerrado (Close() cancela LiveStreamCancellationTokenSource).</summary>
        static int State(object live)
        {
            try
            {
                for (Type t = live.GetType(); t != null; t = t.BaseType)
                {
                    FieldInfo field = t.GetField("LiveStreamCancellationTokenSource", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                    if (field == null)
                        continue;
                    CancellationTokenSource cts = field.GetValue(live) as CancellationTokenSource;
                    if (cts == null)
                        return 0;
                    return cts.IsCancellationRequested ? 2 : 1;
                }
            }
            catch (ObjectDisposedException)
            {
                return 2;
            }
            catch (Exception)
            {
            }
            return 2;
        }

        static string HlsUrl(object live)
        {
            try
            {
                PropertyInfo prop = live.GetType().GetProperty("MediaSource", BindingFlags.Public | BindingFlags.Instance);
                object source = prop != null ? prop.GetValue(live) : null;
                PropertyInfo pathProp = source != null ? source.GetType().GetProperty("Path", BindingFlags.Public | BindingFlags.Instance) : null;
                string path = pathProp != null ? pathProp.GetValue(source) as string : null;
                if (path != null && path.IndexOf("/proxy/hls/", StringComparison.OrdinalIgnoreCase) >= 0)
                    return path;
            }
            catch (Exception)
            {
            }
            return null;
        }

        /// <summary>Petición a la lista de la sesión con ?irelay=&lt;que&gt;; devuelve el código HTTP (0 si falla).</summary>
        static int Send(HttpClient client, string url, string what)
        {
            try
            {
                string full = url + (url.IndexOf('?') >= 0 ? "&" : "?") + "irelay=" + what;
                using (HttpResponseMessage response = client.GetAsync(full).GetAwaiter().GetResult())
                    return (int)response.StatusCode;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        public static void Loop(object live)
        {
            HttpClient client = null;
            try
            {
                client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(2);
                string last = null;
                int idle = 0;
                while (true)
                {
                    int state = State(live);
                    if (state == 2)
                    {
                        if (last != null)
                            Send(client, last, "adios");
                        return;
                    }
                    string url = state == 1 ? HlsUrl(live) : null;
                    if (url != null)
                    {
                        last = url;
                        idle = 0;
                        int code = Send(client, url, "latido");
                        if (code == 404 || code == 410)
                            return;                     // la sesión ya no existe en Dispatcharr
                    }
                    else if (++idle > 1200)
                    {
                        return;                         // 10 min sin salida HLS de Dispatcharr: no es para nosotros
                    }
                    Thread.Sleep(500);
                }
            }
            catch (Exception)
            {
            }
            finally
            {
                if (client != null)
                    client.Dispose();
            }
        }
    }
}
