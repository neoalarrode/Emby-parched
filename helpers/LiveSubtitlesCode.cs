using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;

namespace EmbyNoShare
{
    /// <summary>Lista de subtítulos en directo hecha por Emby y no por ffmpeg (parche live-subtitles-list).</summary>
    public static class LiveSubtitlesCode
    {
        /// <summary>Arranca la conversión pidiendo a Emby la lista de vídeo de la misma sesión (las apps de Apple
        /// piden antes los subtítulos que el vídeo, y la conversión solo arranca con la lista de vídeo).</summary>
        public static void StartJob(int port, string rawUrl)
        {
            try
            {
                if (rawUrl == null || port <= 0)
                    return;
                string path = rawUrl;
                int scheme = path.IndexOf("://", StringComparison.Ordinal);
                if (scheme >= 0)
                {
                    int slash = path.IndexOf('/', scheme + 3);
                    path = slash >= 0 ? path.Substring(slash) : "/";
                }
                int q = path.IndexOf('?');
                string head = q >= 0 ? path.Substring(0, q) : path;
                string query = q >= 0 ? path.Substring(q) : string.Empty;
                int k = head.LastIndexOf("live_subtitles.m3u8", StringComparison.OrdinalIgnoreCase);
                if (k < 0)
                    return;
                string url = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + head.Substring(0, k) + "live.m3u8" + query;
                using (HttpClient client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(60);
                    using (HttpResponseMessage response = client.GetAsync(url).GetAwaiter().GetResult())
                    {
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>Segmentos de subtítulos que ffmpeg ya ha cerrado (completos): los que tiene su propia lista.</summary>
        public static int ClosedSegments(string subsList)
        {
            int count = 0;
            try
            {
                if (subsList == null || !File.Exists(subsList))
                    return 0;
                string text;
                using (FileStream stream = new FileStream(subsList, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader reader = new StreamReader(stream))
                    text = reader.ReadToEnd();
                int at = 0;
                while (true)
                {
                    int i = text.IndexOf(".vtt", at, StringComparison.Ordinal);
                    if (i < 0)
                        break;
                    count++;
                    at = i + 4;
                }
            }
            catch (Exception)
            {
            }
            return count;
        }

        /// <summary>Lista de subtítulos: los segmentos que ffmpeg ya ha cerrado, cada uno de `segLen` s (ffmpeg corta
        /// los subtítulos en ventanas fijas de `segLen` s desde el principio: el segmento k lleva las frases que empiezan
        /// en [k·segLen, (k+1)·segLen)); siempre al menos el primero (SegmentPath espera a que se cierre), ventana
        /// deslizante y las mismas cabeceras en cada respuesta. Sin conversión (videoList null), una lista vacía.</summary>
        public static string Build(string videoList, string subsList, string rawUrl, int segLen)
        {
            if (segLen <= 0)
                segLen = 3;
            StringBuilder sb = new StringBuilder();
            sb.Append("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:");
            sb.Append(segLen.ToString(CultureInfo.InvariantCulture));
            sb.Append('\n');
            if (videoList == null || subsList == null)
            {
                sb.Append("#EXT-X-MEDIA-SEQUENCE:0\n");
                return sb.ToString();
            }
            string query = string.Empty;
            if (rawUrl != null)
            {
                int q = rawUrl.IndexOf('?');
                if (q >= 0)
                    query = rawUrl.Substring(q) + "&";
            }
            int count = ClosedSegments(subsList);
            if (count < 1)
                count = 1;
            int first = count > 200 ? count - 200 : 0;
            string dir = Path.GetFileNameWithoutExtension(videoList);
            string name = Path.GetFileNameWithoutExtension(subsList);
            string length = segLen.ToString(CultureInfo.InvariantCulture) + ".000000,\n";
            sb.Append("#EXT-X-MEDIA-SEQUENCE:");
            sb.Append(first.ToString(CultureInfo.InvariantCulture));
            sb.Append('\n');
            for (int k = first; k < count; k++)
            {
                sb.Append("#EXTINF:");
                sb.Append(length);
                sb.Append("hls/");
                sb.Append(dir);
                sb.Append('/');
                sb.Append(name);
                sb.Append('_');
                sb.Append(k.ToString(CultureInfo.InvariantCulture));
                sb.Append(".vtt");
                sb.Append(query);
                sb.Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>Quita las marcas de los segmentos sin frases (frases que solo llevan un espacio de anchura cero,
        /// las que pone Intro Relay para que ffmpeg cierre sus segmentos mientras nadie habla): el reproductor no las
        /// recibe. Devuelve el fichero a servir (el mismo si no hay ninguna).</summary>
        public static string Clean(string path)
        {
            try
            {
                string text = File.ReadAllText(path);
                if (text.IndexOf('\u200b') < 0)
                    return path;
                string[] blocks = text.Replace("\r", "").Split(new string[] { "\n\n" }, StringSplitOptions.None);
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < blocks.Length; i++)
                {
                    string block = blocks[i];
                    int arrow = block.IndexOf("-->", StringComparison.Ordinal);
                    if (arrow >= 0)
                    {
                        int nl = block.IndexOf('\n', arrow);
                        string payload = nl >= 0 ? block.Substring(nl + 1) : string.Empty;
                        if (payload.Replace("\u200b", string.Empty).Trim().Length == 0)
                            continue;
                    }
                    if (block.Trim().Length == 0)
                        continue;
                    sb.Append(block);
                    sb.Append("\n\n");
                }
                string clean = path + ".limpio";
                File.WriteAllText(clean, sb.ToString());
                return clean;
            }
            catch (Exception)
            {
                return path;
            }
        }

        /// <summary>Fichero a leer para un segmento de subtítulos. Si ffmpeg aún no lo ha cerrado (solo pasa con el
        /// primero, que la lista da antes de tiempo) se espera hasta 12 s a que lo cierre, porque lo que lleva escrito
        /// mientras está abierto puede no estar aún en el disco; después, lo que haya, o uno vacío (Emby lo sirve en
        /// blanco).</summary>
        public static string SegmentPath(string path)
        {
            try
            {
                if (path == null || !path.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase))
                    return path;
                for (int i = 0; i < 48 && !File.Exists(path); i++)
                    System.Threading.Thread.Sleep(250);
                if (File.Exists(path))
                    return Clean(path);
                string tmp = path + ".tmp";
                if (File.Exists(tmp))
                {
                    string copy = path + ".parcial";
                    using (FileStream source = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (FileStream target = new FileStream(copy, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                        source.CopyTo(target);
                    return Clean(copy);
                }
                string empty = Path.Combine(Path.GetDirectoryName(path), "embynoshare-vacio.vtt");
                if (!File.Exists(empty))
                    File.WriteAllText(empty, string.Empty);
                return empty;
            }
            catch (Exception)
            {
                return path;
            }
        }
    }
}
