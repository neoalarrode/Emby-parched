// Emby.LiveTV: puente entre el origen de un canal y sus lectores, para que el directo se adapte a lo que llegue y se
// recupere solo, sin cortes para el cliente.
//
// Hoy SharedHttpPipelineSource conecta con el proveedor, espera dos trozos de 3 s, analiza el canal y deja que los
// lectores (ffmpeg de Emby, reproducción directa MPEG-TS, HLS directo) lean el buffer tal cual se escribe. Si el origen
// tarda, se corta, salta de reloj o cambia de formato, el cliente se queda en negro o se cae. Con este puente:
//   - Ingesta: cada conexión con el origen es una "generación" con su propio token y carpeta. Si el origen deja de
//     mandar datos o el procesador de Emby falla, se abre otra sin cerrar el canal. Los orígenes HLS (o mp4, mkv...)
//     entran por el ffmpeg de Emby convertidos a MPEG-TS sin recodificar.
//   - Los trozos del origen se retienen hasta estar completos y se publican reescritos sobre una línea de tiempo propia
//     y continua (PTS/DTS/PCR, contadores de continuidad, PAT/PMT con versión), así que reconexiones, saltos de reloj o
//     cambios de PID no llegan a los lectores.
//   - Puente: si a los lectores se les va a acabar lo publicado (apertura, corte, retraso del origen), entra la intro
//     codificada por el ffmpeg de Emby exactamente en el formato actual del canal (códec, parámetros, PID, PAT/PMT), con
//     el tiempo seguido. En cuanto hay trozos del origen, siguen ellos.
//   - Cambio de formato a mitad de directo (resolución, entrelazado, códec, pistas): se detecta en cada trozo, Emby
//     vuelve a analizar el canal, la intro se regenera en el formato nuevo, el trozo va con discontinuidad (HLS directo)
//     y PMT con versión nueva (MPEG-TS), y las conversiones de Emby se relanzan con una decisión nueva (LiveRestart).
//   - Apertura al momento: Emby responde ya con la intro en formato estándar (H.264 1080p25 + AAC), codificada una
//     vez y guardada en su caché; el origen se conecta aparte y, cuando llega, su formato entra como un cambio de
//     formato más (si es distinto del de la intro).
// EMBY_LIVE_BRIDGE=0 lo desactiva. EMBY_LIVE_INTRO: vídeo o imagen de la intro (si no, negro con silencio).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace EmbyNoShare.Runtime
{
    public static class LiveIntro
    {
        const long Clock = 90000;
        const long Wrap = 1L << 33;
        const int SegmentSeconds = 3;
        const int EncodedAhead = 3;             // trozos de intro codificados por delante como máximo
        const double LowLead = 1.0;             // s de margen publicado por debajo de los cuales entra la intro
        const double MaxLead = 25;              // s de margen por encima de los cuales se descartan trozos (volver al directo)
        const double Stall = 10;                // s sin datos del origen para abrir otra conexión
        const long OutStart = 10 * Clock;       // primer DTS de la línea de tiempo publicada

        static readonly object Sync = new object();
        static readonly Dictionary<object, object> States = new Dictionary<object, object>();
        static readonly HashSet<object> Bypass = new HashSet<object>();

        sealed class Gen
        {
            public int Id;
            public CancellationTokenSource Cts;
            public string Dir;
            public readonly List<Process> Remux = new List<Process>();
        }

        sealed class Encoder
        {
            public Process Process;
            public readonly Queue<object> Queue = new Queue<object>();
            public bool Done;
            public string Error = "";
        }

        sealed class State
        {
            public object Source, MediaSource;
            public string Uid, BaseDir, BasePattern, CacheDir, Intro;
            public object[] Cached;              // intro en formato estándar, guardada
            public int CachedNext;
            public Type InfoType;
            public CancellationTokenSource Main;
            public CancellationToken Token;

            // origen
            public string OriginalUrl, RemuxUrl, RemuxMap;
            public object OriginalProtocol;
            public bool Remuxed;                 // entra por el ffmpeg de Emby (HLS u otro contenedor)
            public bool Detected;                // ya se sabe si entra directo o por ffmpeg
            public Gen Gen;
            public int GenCount, GenFailures;
            public bool GenStarting;
            public DateTime NextGenRestart, LastActivity, GenStartedAt;
            public readonly object GenLock = new object();
            public string ActivityKey;

            // trozos del origen retenidos (generación actual) y los ya terminados
            public readonly List<object> Held = new List<object>();
            public readonly HashSet<object> Done = new HashSet<object>();
            public bool Replaying, PassThrough;
            public int NextSeq;

            // formato actual
            public JsonElement[] Format;
            public bool DefaultFormat;
            public string Signature, Material;
            public byte[][] Pat, Pmt;
            public int PmtPid, PcrPid, VideoPid = -1;
            public readonly HashSet<int> MediaPids = new HashSet<int>();
            public long FrameDur = Clock / 25;
            public string PatBody, PmtBody;
            public int PatVersion = -1, PmtVersion = -1;
            public readonly Dictionary<int, int> Counters = new Dictionary<int, int>();
            // audio publicado: siguiente PTS esperado por pista (para no solapar al empezar un tramo)
            public readonly Dictionary<int, long> AudioNext = new Dictionary<int, long>();
            public readonly Dictionary<int, long> AudioDelta = new Dictionary<int, long>();

            // intro
            public Encoder Enc;
            public int IntroPmtPid;
            public DateTime NextEncoderStart;
            public bool Bridging;

            // línea de tiempo publicada
            public bool OutStarted;
            public long OutNextDts, P0;
            public DateTime W0;
            public int RunKind;                  // 0 ninguno, 1 intro, 2 origen
            public long RunOffset;
            public long? RunRawNext;
            public bool Dropping;
            public int ProbeCount;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Ganchos

        /// SharedHttpPipelineSource.OpenInternal: la apertura con puente, o null para seguir con la de Emby
        public static Task Open(object source, CancellationToken cancellationToken)
        {
            try
            {
                if (Environment.GetEnvironmentVariable("EMBY_LIVE_BRIDGE") == "0") return null;
                lock (Sync) if (Bypass.Contains(source)) return null;
                var protocol = Reflect.Get(Reflect.Get(source, "MediaSource"), "Protocol")?.ToString();
                if (protocol != "Http" && protocol != "File") return null;
                var intro = Environment.GetEnvironmentVariable("EMBY_LIVE_INTRO");
                if (string.IsNullOrEmpty(intro) || !File.Exists(intro)) intro = null;
                return Task.Run(() => Run(source, intro, cancellationToken));
            }
            catch (Exception e)
            {
                Log(source, "Puente: no se usa: " + e.Message);
                return null;
            }
        }

        /// SharedHttpPipelineSource.OnSegmentStarted / OnSegmentCompleted: los trozos del origen se retienen (true)
        public static bool Hold(object source, object file, bool started)
        {
            var st = Find(source);
            if (st == null) return false;
            lock (st)
            {
                if (st.Replaying) return false;
                var path = Reflect.Get(file, "Path") as string ?? "";
                var gen = st.Gen;
                if (gen == null || !path.StartsWith(gen.Dir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    // de una conexión anterior
                    if (!started) TryDelete(path);
                    return true;
                }
                if (started) st.Held.Add(file);
                else st.Done.Add(file);
                st.LastActivity = DateTime.UtcNow;
                return true;
            }
        }

        /// SharedHttpPipelineSource.OpenStream: para orígenes que entran por ffmpeg, su salida MPEG-TS (o null)
        public static object OpenStream(object source, object context, object mediaSource, string url, CancellationToken ct)
        {
            var st = Find(source);
            if (st == null || !st.Remuxed) return null;
            var resultType = OpenStreamResultType(source);
            try
            {
                var result = Remux(st, context, ct);
                return typeof(Task).GetMethod("FromResult").MakeGenericMethod(resultType).Invoke(null, new[] { result });
            }
            catch (Exception e)
            {
                return typeof(Task).GetMethod("FromException", 1, new[] { typeof(Exception) }).MakeGenericMethod(resultType).Invoke(null, new object[] { e });
            }
        }

        /// Fallo del procesador de Emby (antes cerraba todo el directo): con puente, otra conexión
        public static void PipelineFailed(object source)
        {
            var st = Find(source);
            if (st == null)
            {
                ((CancellationTokenSource)Reflect.Get(source, "LiveStreamCancellationTokenSource")).Cancel();
                return;
            }
            // al parar una conexión vieja también puede fallar su procesador: solo cuenta el de la actual
            if ((DateTime.UtcNow - st.GenStartedAt).TotalSeconds < 5) return;
            Log(source, "Puente: el procesador del origen ha fallado, se abre otra conexión");
            RestartGen(st, 0);
        }

        static State Find(object source)
        {
            lock (Sync) return States.TryGetValue(source, out var o) ? (State)o : null;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Apertura

        static void Run(object source, string intro, CancellationToken ct)
        {
            var ms = Reflect.Get(source, "MediaSource");
            var st = new State
            {
                Source = source,
                MediaSource = ms,
                Intro = intro,
                OriginalUrl = Reflect.Get(ms, "Path") as string,
                OriginalProtocol = Reflect.Get(ms, "Protocol"),
                Main = (CancellationTokenSource)Reflect.Get(source, "LiveStreamCancellationTokenSource"),
            };
            st.Token = st.Main.Token;
            var started = DateTime.UtcNow;
            try
            {
                Prepare(st);
                // al momento: la intro en formato estándar (preparada una vez y guardada); el origen se conecta aparte
                st.Format = DefaultFormat(st);
                st.DefaultFormat = true;
                st.Material = Material(st, st.Format);
                st.FrameDur = FrameDuration(st.Format);
                st.Cached = IntroCache(st);
                ApplyTables(st, ParseTables((byte[])((object[])st.Cached[0])[0]));
                lock (Sync) States[source] = st;
                st.Token.Register(() => Cleanup(st));
                RestartGen(st, 0);

                PublishIntro(st, NextCached(st));
                PublishIntro(st, NextCached(st));
                Probe(st, LastPublishedPath(st), ct);
                Log(source, "Puente: canal listo con la intro en " + Ms(started) + " ms; el origen se conecta aparte");
                StartPublisher(st);
            }
            catch
            {
                Cleanup(st);
                try { st.Main.Cancel(); } catch { }
                throw;
            }
        }

        static void Cleanup(State st)
        {
            lock (Sync) States.Remove(st.Source);
            StopEncoder(st);
            var gen = st.Gen;
            if (gen != null) StopGen(gen);
        }

        /// Lo que hace OpenInternal antes de empezar a leer el origen
        static void Prepare(State st)
        {
            var source = st.Source;
            Reflect.Set(source, "_supportsCopyTo", true);
            Reflect.Set(source, "SupportsSegments", true);
            Reflect.Set(source, "SupportsPlaybackSegments", true);
            Reflect.Set(source, "SegmentDuration", TimeSpan.FromSeconds(SegmentSeconds));
            var limit = TimeSpan.Zero;
            try
            {
                var options = Reflect.Call(Reflect.Get(source, "_config"), "GetConfiguration", "livetv");
                if (Reflect.Get(options, "BufferLimitInMinutes") is int minutes) limit = TimeSpan.FromMinutes(minutes);
            }
            catch { }
            if (limit > TimeSpan.Zero && limit < TimeSpan.FromMinutes(10)) limit = TimeSpan.FromMinutes(10);
            Reflect.Set(source, "BufferLimit", limit);
            st.Uid = (string)Reflect.Get(source, "UniqueId");
            var paths = Reflect.Get(source, "AppPaths");
            var temp = (string)Reflect.Call(paths, "GetTranscodingTempPath");
            st.BaseDir = Path.Combine(temp, st.Uid);
            st.BasePattern = Path.Combine(st.BaseDir, "{0:0000000}.ts");
            try { st.CacheDir = Path.Combine((string)Reflect.Get(paths, "CachePath"), "emby-noshare-intro"); }
            catch { st.CacheDir = Path.Combine(temp, "emby-noshare-intro"); }
            Reflect.Set(source, "TempFilePath", st.BasePattern);
            Directory.CreateDirectory(st.BaseDir);
            st.InfoType = Reflect.Get(source, "CompletedSegmentFiles").GetType().GetGenericArguments()[0];
        }

        /// Primera conexión: decide si el origen entra directo (MPEG-TS) o por ffmpeg (HLS, mp4, mkv...)
        static object OpenFirst(State st, CancellationToken ct)
        {
            var source = st.Source;
            var opening = (Task)Reflect.Call(source, "OpenStream", null, st.MediaSource, st.OriginalUrl, st.OriginalProtocol, ct, 0);
            opening.GetAwaiter().GetResult();
            var result = Reflect.Get(opening, "Result");
            var contentType = Reflect.Get(result, "ContentType") as string ?? "";
            var responseUrl = Reflect.Get(result, "ResponseUrl") as string ?? st.OriginalUrl;
            var hls = IsHlsUrl(st.OriginalUrl) || IsHlsUrl(responseUrl) || contentType.IndexOf("mpegurl", StringComparison.OrdinalIgnoreCase) >= 0;
            st.Detected = true;
            if (!hls && !IsOtherContainer(contentType)) return result;

            st.Remuxed = true;
            st.RemuxUrl = responseUrl;
            var response = Reflect.Get(result, "ResponseInfo");
            if (hls)
            {
                try
                {
                    var content = Reflect.Get(response, "Content") as Stream;
                    if (content != null)
                    {
                        string text;
                        using (var reader = new StreamReader(content)) text = ReadLimited(reader, 1024 * 1024);
                        ChooseVariant(st, responseUrl, text);
                    }
                }
                catch (Exception e) { Log(source, "Puente: no se pudo leer la lista HLS, se deja elegir a ffmpeg: " + e.Message); }
            }
            (response as IDisposable)?.Dispose();
            (Reflect.Get(result, "ConectionContext") as IDisposable)?.Dispose();
            Log(source, "Puente: origen " + (hls ? "HLS" : contentType) + ", entra por ffmpeg desde " + Sanitize(st.RemuxUrl));
            return Remux(st, null, ct);
        }

        static bool IsHlsUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            var q = url.IndexOf('?');
            var p = q >= 0 ? url.Substring(0, q) : url;
            return p.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase);
        }

        static bool IsOtherContainer(string contentType)
        {
            foreach (var t in new[] { "matroska", "mkv", "mp4", "avi", "webm", "dash", "flv", "quicktime" })
                if (contentType.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        static string ReadLimited(StreamReader reader, int max)
        {
            var buffer = new char[8192];
            var b = new StringBuilder();
            int n;
            while (b.Length < max && (n = reader.Read(buffer, 0, buffer.Length)) > 0) b.Append(buffer, 0, n);
            return b.ToString();
        }

        /// Lista maestra HLS: la variante de más resolución y caudal (si las pistas de audio van aparte, la maestra
        /// con solo el programa de esa variante)
        static void ChooseVariant(State st, string url, string text)
        {
            if (text.IndexOf("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase) < 0) return;
            var lines = text.Replace("\r", "").Split('\n');
            string best = null;
            long bestScore = -1;
            int bestIndex = -1, index = -1;
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase)) continue;
                index++;
                long bandwidth = 0, pixels = 0;
                var m = System.Text.RegularExpressions.Regex.Match(lines[i], @"[:,]BANDWIDTH=(\d+)");
                if (m.Success) long.TryParse(m.Groups[1].Value, out bandwidth);
                m = System.Text.RegularExpressions.Regex.Match(lines[i], @"RESOLUTION=(\d+)x(\d+)");
                if (m.Success) pixels = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                var score = pixels * 1000000000L + bandwidth;
                var j = i + 1;
                while (j < lines.Length && (lines[j].Trim().Length == 0 || lines[j].StartsWith("#", StringComparison.Ordinal))) j++;
                if (j >= lines.Length) continue;
                if (score > bestScore) { bestScore = score; best = lines[j].Trim(); bestIndex = index; }
            }
            if (best == null) return;
            var separateAudio = System.Text.RegularExpressions.Regex.IsMatch(text, @"#EXT-X-MEDIA:[^\n]*TYPE=AUDIO[^\n]*URI=", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (separateAudio) st.RemuxMap = "-map 0:p:" + bestIndex.ToString(CultureInfo.InvariantCulture) + ":v? -map 0:p:" + bestIndex.ToString(CultureInfo.InvariantCulture) + ":a?";
            else st.RemuxUrl = new Uri(new Uri(url), best).ToString();
        }

        /// El origen convertido a MPEG-TS sin recodificar por el ffmpeg de Emby, como respuesta de OpenStream
        static object Remux(State st, object context, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var args = new StringBuilder("-hide_banner -nostdin -loglevel error -fflags +genpts+discardcorrupt");
            var headers = Reflect.Get(st.MediaSource, "RequiredHttpHeaders") as System.Collections.IDictionary;
            var extra = new StringBuilder();
            if (headers != null)
                foreach (System.Collections.DictionaryEntry h in headers)
                {
                    var name = h.Key as string;
                    var value = h.Value as string;
                    if (string.IsNullOrEmpty(name) || value == null) continue;
                    if (string.Equals(name, "User-Agent", StringComparison.OrdinalIgnoreCase)) args.Append(" -user_agent ").Append(Quote(value));
                    else extra.Append(name).Append(": ").Append(value).Append("\r\n");
                }
            if (extra.Length > 0) args.Append(" -headers ").Append(Quote(extra.ToString()));
            if (st.RemuxUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !IsHlsUrl(st.RemuxUrl))
                args.Append(" -reconnect 1 -reconnect_streamed 1 -reconnect_delay_max 4");
            args.Append(" -i ").Append(Quote(st.RemuxUrl));
            args.Append(' ').Append(st.RemuxMap ?? "-map 0:v? -map 0:a?");
            args.Append(" -c copy -f mpegts pipe:1");
            var info = new ProcessStartInfo(Tool("ffmpeg"), args.ToString())
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = AppDir(),
            };
            var process = Process.Start(info);
            process.ErrorDataReceived += (sender, e) => { if (!string.IsNullOrEmpty(e.Data)) Log(st.Source, "Puente: ffmpeg del origen: " + e.Data); };
            process.BeginErrorReadLine();
            var gen = st.Gen;
            if (gen != null) lock (gen.Remux) gen.Remux.Add(process);

            var resultType = OpenStreamResultType(st.Source);
            var ctor = resultType.GetConstructors()[0];
            var ps = ctor.GetParameters();
            var response = Activator.CreateInstance(ps[0].ParameterType, new object[] { new IDisposable[] { process } });
            Reflect.Set(response, "Content", process.StandardOutput.BaseStream);
            Reflect.Set(response, "ContentType", "video/mp2t");
            Reflect.Set(response, "ResponseUrl", st.RemuxUrl);
            Reflect.Set(response, "StatusCode", HttpStatusCode.OK);
            var http = Enum.Parse(ps[5].ParameterType, "Http");
            return ctor.Invoke(new[] { response, context, st.RemuxUrl, st.RemuxUrl, "video/mp2t", http });
        }

        static Type OpenStreamResultType(object source)
        {
            for (var t = source.GetType(); t != null; t = t.BaseType)
                foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.DeclaredOnly))
                    if (m.Name == "OpenStream" && m.ReturnType.IsGenericType) return m.ReturnType.GetGenericArguments()[0];
            throw new MissingMethodException(source.GetType().FullName, "OpenStream");
        }

        // ---------------------------------------------------------------------------------------------------------
        // Generaciones de la ingesta

        /// Conexión nueva con el origen en una generación nueva (carpeta y retención propias); la anterior se para
        static void Connect(State st)
        {
            var old = st.Gen;
            if (old != null) StopGen(old);
            var gen = new Gen { Cts = CancellationTokenSource.CreateLinkedTokenSource(st.Token) };
            lock (st)
            {
                gen.Id = ++st.GenCount;
                gen.Dir = Path.Combine(st.BaseDir, "g" + gen.Id.ToString(CultureInfo.InvariantCulture));
                Directory.CreateDirectory(gen.Dir);
                st.Gen = gen;
                st.GenStartedAt = DateTime.UtcNow;
                st.Held.Clear();
                st.Done.Clear();
                st.LastActivity = DateTime.UtcNow;
                st.ActivityKey = null;
            }
            if (old != null) Task.Delay(15000).ContinueWith(_ => { try { Directory.Delete(old.Dir, true); } catch { } });

            object result;
            if (!st.Detected) result = OpenFirst(st, gen.Cts.Token);
            else
            {
                var opening = (Task)Reflect.Call(st.Source, "OpenStream", null, st.MediaSource, st.Remuxed ? st.RemuxUrl : st.OriginalUrl,
                    st.OriginalProtocol, gen.Cts.Token, 0);
                opening.GetAwaiter().GetResult();
                result = Reflect.Get(opening, "Result");
            }
            // StartStreaming lee TempFilePath al arrancar su tarea (antes de completar tcs)
            lock (st.GenLock)
            {
                Reflect.Set(st.Source, "TempFilePath", Path.Combine(gen.Dir, "{0:0000000}.ts"));
                try
                {
                    var tcs = new TaskCompletionSource<bool>();
                    Reflect.Call(st.Source, "StartStreaming", st.MediaSource, Reflect.Get(result, "ResponseInfo"), Reflect.Get(result, "ConectionContext"),
                        Reflect.Get(result, "ResponseUrl"), Reflect.Get(result, "MediaProtocol"), tcs, gen.Cts.Token);
                    tcs.Task.Wait(15000);
                }
                finally { Reflect.Set(st.Source, "TempFilePath", st.BasePattern); }
            }
        }

        static void StopGen(Gen gen)
        {
            try { gen.Cts.Cancel(); } catch { }
            List<Process> remux;
            lock (gen.Remux) remux = new List<Process>(gen.Remux);
            foreach (var p in remux) { try { if (!p.HasExited) p.Kill(); } catch { } }
        }

        /// Otra conexión con el origen (en segundo plano), con espera creciente si va fallando
        static void RestartGen(State st, double delaySeconds)
        {
            lock (st)
            {
                if (st.GenStarting || st.Token.IsCancellationRequested) return;
                st.GenStarting = true;
            }
            Task.Run(() =>
            {
                try
                {
                    if (delaySeconds > 0) Thread.Sleep(TimeSpan.FromSeconds(delaySeconds));
                    var start = DateTime.UtcNow;
                    Connect(st);
                    Log(st.Source, "Puente: conexión " + st.Gen.Id.ToString(CultureInfo.InvariantCulture) + " con el origen abierta en " + Ms(start) + " ms");
                }
                catch (Exception e)
                {
                    st.GenFailures++;
                    if (!st.Token.IsCancellationRequested)
                        Log(st.Source, "Puente: no se pudo conectar con el origen (" + st.GenFailures.ToString(CultureInfo.InvariantCulture) + "): " + Inner(e).Message);
                }
                finally
                {
                    st.NextGenRestart = DateTime.UtcNow.AddSeconds(Math.Min(30, 5 * Math.Max(1, st.GenFailures)));
                    lock (st) { st.GenStarting = false; st.LastActivity = DateTime.UtcNow; }
                }
            });
        }

        static Exception Inner(Exception e)
        {
            while ((e is System.Reflection.TargetInvocationException || e is AggregateException) && e.InnerException != null) e = e.InnerException;
            return e;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Formato

        /// H.264 1080p25 + AAC estéreo: formato de la intro mientras no se conoce el del origen
        static JsonElement[] DefaultFormat(State st)
        {
            const string json = "[{\"codec_type\":\"video\",\"codec_name\":\"h264\",\"profile\":\"High\",\"level\":40,\"width\":1920,\"height\":1080,"
                + "\"pix_fmt\":\"yuv420p\",\"r_frame_rate\":\"25/1\",\"avg_frame_rate\":\"25/1\",\"field_order\":\"progressive\",\"has_b_frames\":2,"
                + "\"sample_aspect_ratio\":\"1:1\",\"id\":\"0x100\"},{\"codec_type\":\"audio\",\"codec_name\":\"aac\",\"sample_rate\":48000,\"channels\":2,\"id\":\"0x101\"}]";
            var list = new List<JsonElement>();
            using (var doc = JsonDocument.Parse(json))
                foreach (var e in doc.RootElement.EnumerateArray()) list.Add(e.Clone());
            st.VideoPid = 0x100;
            st.PcrPid = 0x100;
            st.MediaPids.Clear();
            st.MediaPids.Add(0x100);
            st.MediaPids.Add(0x101);
            return list.ToArray();
        }

        /// La intro en formato estándar, codificada una vez y guardada en la caché de Emby (se rehace si cambia el
        /// fichero de la intro): trozos {byte[] datos, long PTS inicial, long duración}
        static object[] IntroCache(State st)
        {
            var seconds = 6;
            if (st.Intro != null && !IsImage(st.Intro))
            {
                var streams = FfprobeStreams(st.Intro);
                var duration = 0.0;
                if (streams != null)
                    foreach (var s in streams)
                        if (Str(s, "codec_type") == "video" && double.TryParse(Str(s, "duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) duration = d;
                seconds = (int)Math.Max(6, Math.Min(30, Math.Round(duration / SegmentSeconds) * SegmentSeconds));
            }
            // un trozo de más: el último que corta ffmpeg queda incompleto y no se usa
            var args = EncoderArguments(st, st.Format, st.Intro, seconds + SegmentSeconds);
            var key = args + "|" + (st.Intro == null ? "" : File.GetLastWriteTimeUtc(st.Intro).Ticks + "|" + new FileInfo(st.Intro).Length);
            string hash;
            using (var sha = System.Security.Cryptography.SHA1.Create())
                hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-", "").Substring(0, 16).ToLowerInvariant();
            var dir = Path.Combine(st.CacheDir, hash);
            lock (IntroLock)
            {
                var cached = ReadIntroCache(dir);
                if (cached != null) return cached;
                Log(st.Source, "Puente: preparando la intro (solo la primera vez): ffmpeg " + args);
                var enc = new Encoder();
                StartProcess(st, enc, args, st.VideoPid, CancellationToken.None);
                var segments = new List<object>();
                object segment;
                while ((segment = TakeEncoded(enc, 60000)) != null) segments.Add(segment);
                if (segments.Count == 0) throw new Exception("Puente: no se pudo preparar la intro: " + enc.Error.Trim());
                var tmp = dir + ".tmp";
                try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
                Directory.CreateDirectory(tmp);
                var index = new StringBuilder();
                for (var i = 0; i < segments.Count; i++)
                {
                    var s = (object[])segments[i];
                    File.WriteAllBytes(Path.Combine(tmp, i.ToString("000", CultureInfo.InvariantCulture) + ".ts"), (byte[])s[0]);
                    index.Append(((long)s[1]).ToString(CultureInfo.InvariantCulture)).Append(' ').Append(((long)s[2]).ToString(CultureInfo.InvariantCulture)).Append('\n');
                }
                File.WriteAllText(Path.Combine(tmp, "index.txt"), index.ToString());
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
                Directory.Move(tmp, dir);
                return segments.ToArray();
            }
        }

        static readonly object IntroLock = new object();

        static object[] ReadIntroCache(string dir)
        {
            try
            {
                var indexPath = Path.Combine(dir, "index.txt");
                if (!File.Exists(indexPath)) return null;
                var lines = File.ReadAllLines(indexPath);
                var segments = new List<object>();
                for (var i = 0; i < lines.Length; i++)
                {
                    var parts = lines[i].Split(' ');
                    if (parts.Length != 2) continue;
                    var data = File.ReadAllBytes(Path.Combine(dir, i.ToString("000", CultureInfo.InvariantCulture) + ".ts"));
                    segments.Add(new object[] { data, long.Parse(parts[0], CultureInfo.InvariantCulture), long.Parse(parts[1], CultureInfo.InvariantCulture) });
                }
                return segments.Count > 0 ? segments.ToArray() : null;
            }
            catch { return null; }
        }

        /// Siguiente trozo de la intro guardada, en bucle
        static object[] NextCached(State st)
        {
            var segment = (object[])st.Cached[st.CachedNext % st.Cached.Length];
            st.CachedNext++;
            return segment;
        }

        static byte[] ReadShared(string file, long offset, int max)
        {
            try
            {
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (offset > 0) stream.Seek(offset, SeekOrigin.Begin);
                    var length = (int)Math.Min(max, Math.Max(0, stream.Length - offset));
                    var data = new byte[length];
                    var read = 0;
                    while (read < length) { var n = stream.Read(data, read, length - read); if (n <= 0) break; read += n; }
                    if (read < length) Array.Resize(ref data, read);
                    return data;
                }
            }
            catch { return new byte[0]; }
        }

        static long FileSize(string file)
        {
            try { return new FileInfo(file).Length; } catch { return -1; }
        }

        static void TryDelete(string path)
        {
            try { if (!string.IsNullOrEmpty(path)) File.Delete(path); } catch { }
        }

        /// PAT y PMT (paquetes exactos), su PID, PID de reloj y de vídeo, PID de las pistas y tipos:
        /// {byte[][] pat, byte[][] pmt, int pmtPid, int pcrPid, int videoPid, HashSet&lt;int&gt; pids, Dictionary&lt;int,int&gt; tipos, byte[] sección PMT}
        static object[] ParseTables(byte[] data)
        {
            byte[][] pat = null;
            int pmtPid = -1;
            for (var p = 0; p + 188 <= data.Length && pat == null; p += 188)
            {
                if (data[p] != 0x47 || PidOf(data, p) != 0 || (data[p + 1] & 0x40) == 0) continue;
                var o = PayloadOffset(data, p);
                if (o < 0) continue;
                o += 1 + data[o];
                if (o + 8 > p + 188 || data[o] != 0) continue;
                var length = ((data[o + 1] & 0x0F) << 8) | data[o + 2];
                for (var e = o + 8; e + 4 <= o + 3 + length - 4 && e + 4 <= p + 188; e += 4)
                {
                    var program = (data[e] << 8) | data[e + 1];
                    if (program == 0) continue;
                    pmtPid = ((data[e + 2] & 0x1F) << 8) | data[e + 3];
                    break;
                }
                if (pmtPid >= 0) pat = new[] { Copy(data, p) };
            }
            if (pat == null) return null;

            for (var p = 0; p + 188 <= data.Length; p += 188)
            {
                if (data[p] != 0x47 || PidOf(data, p) != pmtPid || (data[p + 1] & 0x40) == 0) continue;
                var packets = new List<byte[]>();
                var section = new MemoryStream();
                var o = PayloadOffset(data, p);
                if (o < 0) continue;
                o += 1 + data[o];
                if (o >= p + 188) continue;
                section.Write(data, o, p + 188 - o);
                packets.Add(Copy(data, p));
                var q = p + 188;
                while (section.Length >= 3 && section.Length < 3 + (((section.GetBuffer()[1] & 0x0F) << 8) | section.GetBuffer()[2]))
                {
                    while (q + 188 <= data.Length && (data[q] != 0x47 || PidOf(data, q) != pmtPid)) q += 188;
                    if (q + 188 > data.Length) break;
                    var oo = PayloadOffset(data, q);
                    if (oo >= 0) section.Write(data, oo, q + 188 - oo);
                    packets.Add(Copy(data, q));
                    q += 188;
                }
                var s = section.ToArray();
                if (s.Length < 12 || s[0] != 2) continue;
                var total = 3 + (((s[1] & 0x0F) << 8) | s[2]);
                if (s.Length < total) return null;
                var pcrPid = ((s[8] & 0x1F) << 8) | s[9];
                var infoLength = ((s[10] & 0x0F) << 8) | s[11];
                var pids = new HashSet<int>();
                var types = new Dictionary<int, int>();
                var videoPid = -1;
                for (var e = 12 + infoLength; e + 5 <= total - 4;)
                {
                    var type = s[e];
                    var pid = ((s[e + 1] & 0x1F) << 8) | s[e + 2];
                    var esLength = ((s[e + 3] & 0x0F) << 8) | s[e + 4];
                    pids.Add(pid);
                    types[pid] = type;
                    if (videoPid < 0 && (type == 0x01 || type == 0x02 || type == 0x1B || type == 0x24)) videoPid = pid;
                    e += 5 + esLength;
                }
                var sectionBytes = new byte[total];
                Array.Copy(s, sectionBytes, total);
                return new object[] { pat, packets.ToArray(), pmtPid, pcrPid, videoPid, pids, types, sectionBytes };
            }
            return null;
        }

        /// Usa las tablas del origen; si su contenido cambia, la versión publicada sube (y se recalcula el CRC)
        static void ApplyTables(State st, object[] tables)
        {
            var pat = Clone((byte[][])tables[0]);
            var pmt = Clone((byte[][])tables[1]);
            st.PmtPid = (int)tables[2];
            st.PcrPid = (int)tables[3];
            st.VideoPid = (int)tables[4];
            st.MediaPids.Clear();
            foreach (var pid in (HashSet<int>)tables[5]) st.MediaPids.Add(pid);
            var patBody = SectionBody(pat);
            if (patBody != st.PatBody) { st.PatVersion = st.PatVersion < 0 ? SectionVersion(pat) : (st.PatVersion + 1) & 31; st.PatBody = patBody; }
            var pmtBody = SectionBody(pmt);
            if (pmtBody != st.PmtBody) { st.PmtVersion = st.PmtVersion < 0 ? SectionVersion(pmt) : (st.PmtVersion + 1) & 31; st.PmtBody = pmtBody; }
            SetSectionVersion(pat, st.PatVersion);
            SetSectionVersion(pmt, st.PmtVersion);
            st.Pat = pat;
            st.Pmt = pmt;
        }

        static byte[][] Clone(byte[][] packets)
        {
            var copy = new byte[packets.Length][];
            for (var i = 0; i < packets.Length; i++) copy[i] = (byte[])packets[i].Clone();
            return copy;
        }

        /// Firma barata de lo que define el formato: PMT y cabeceras de secuencia del vídeo (SPS, VPS, MPEG-2)
        static string Signature(byte[] data, object[] tables)
        {
            if (tables == null) return null;
            var section = (byte[])tables[7];
            var b = new StringBuilder();
            for (var i = 0; i < section.Length - 4; i++) if (i != 5) b.Append(section[i].ToString("x2", CultureInfo.InvariantCulture));
            var videoPid = (int)tables[4];
            if (videoPid < 0) return b.ToString();
            var types = (Dictionary<int, int>)tables[6];
            var type = types.TryGetValue(videoPid, out var t) ? t : 0;
            // carga del primer PES de vídeo (hasta 8 KB)
            var payload = new MemoryStream();
            var started = false;
            for (var p = 0; p + 188 <= data.Length && payload.Length < 8192; p += 188)
            {
                if (data[p] != 0x47 || PidOf(data, p) != videoPid) continue;
                var pusi = (data[p + 1] & 0x40) != 0;
                if (pusi && started) break;
                if (pusi) started = true;
                if (!started) continue;
                var o = PayloadOffset(data, p);
                if (o < 0) continue;
                if (pusi && o + 9 <= p + 188 && data[o] == 0 && data[o + 1] == 0 && data[o + 2] == 1) o += 9 + data[o + 8];
                if (o < p + 188) payload.Write(data, o, p + 188 - o);
            }
            var v = payload.ToArray();
            b.Append('|');
            for (var i = 0; i + 4 < v.Length; i++)
            {
                if (v[i] != 0 || v[i + 1] != 0 || v[i + 2] != 1) continue;
                var h = v[i + 3];
                var take = 0;
                if (type == 0x1B && (h & 0x1F) == 7) take = 64;                                            // H.264 SPS
                else if (type == 0x24 && (((h >> 1) & 0x3F) == 32 || ((h >> 1) & 0x3F) == 33)) take = 64;  // HEVC VPS/SPS
                else if ((type == 0x01 || type == 0x02) && h == 0xB3) take = 8;                            // MPEG-2 secuencia
                else if ((type == 0x01 || type == 0x02) && h == 0xB5 && (v[i + 4] >> 4) == 1) take = 6;    // MPEG-2 extensión
                if (take == 0) continue;
                for (var k = i + 3; k < v.Length && k < i + 3 + take; k++)
                {
                    if (k + 2 < v.Length && v[k] == 0 && v[k + 1] == 0 && (v[k + 2] == 1 || v[k + 2] == 0)) break;
                    b.Append(v[k].ToString("x2", CultureInfo.InvariantCulture));
                }
                b.Append(',');
            }
            return b.ToString();
        }

        /// Lo que obliga a decidir de nuevo: códec, resolución, entrelazado, fps y pistas de audio
        static string Material(State st, JsonElement[] streams)
        {
            var b = new StringBuilder();
            foreach (var s in streams)
            {
                var pid = Pid(s);
                if (st.MediaPids.Count > 0 && !st.MediaPids.Contains(pid)) continue;
                var type = Str(s, "codec_type");
                // por posición, no por PID: Emby elige las pistas por su orden
                if (type == "video")
                {
                    var field = Str(s, "field_order") ?? "progressive";
                    b.Append(string.Format(CultureInfo.InvariantCulture, "v:{0}:{1}x{2}:{3}:{4}:{5};", Str(s, "codec_name"), Int(s, "width"),
                        Int(s, "height"), field == "progressive" ? "p" : "i", Rate(s), Str(s, "pix_fmt")));
                }
                else if (type == "audio")
                    b.Append(string.Format(CultureInfo.InvariantCulture, "a:{0}:{1};", Str(s, "codec_name"), Int(s, "channels")));
                else b.Append(string.Format(CultureInfo.InvariantCulture, "o:{0};", Str(s, "codec_name") ?? type));
            }
            return b.ToString();
        }

        static long FrameDuration(JsonElement[] streams)
        {
            foreach (var s in streams)
                if (Str(s, "codec_type") == "video")
                {
                    var fps = Fps(Rate(s));
                    if (fps > 0) return (long)Math.Round(Clock / fps);
                }
            return Clock / 25;
        }

        static string Describe(JsonElement[] streams)
        {
            var parts = new List<string>();
            foreach (var s in streams)
            {
                var type = Str(s, "codec_type");
                if (type == "video")
                    parts.Add(string.Format(CultureInfo.InvariantCulture, "{0} {1} {2}x{3} {4} {5} {6} pid {7}", Str(s, "codec_name"),
                        Str(s, "profile"), Int(s, "width"), Int(s, "height"), Str(s, "field_order") ?? "?", Rate(s), Str(s, "pix_fmt"), Pid(s)));
                else if (type == "audio")
                    parts.Add(string.Format(CultureInfo.InvariantCulture, "{0} {1} Hz {2} ch pid {3}", Str(s, "codec_name"),
                        Int(s, "sample_rate"), Int(s, "channels"), Pid(s)));
                else parts.Add((Str(s, "codec_name") ?? type) + " pid " + Pid(s));
            }
            return string.Join(", ", parts.ToArray());
        }

        /// Emby analiza un trozo (como en OpenInternal) y deja la fuente lista para los lectores
        static void Probe(State st, string probePath, CancellationToken ct)
        {
            var ms = st.MediaSource;
            var protocolType = Reflect.Get(ms, "Protocol").GetType();
            Reflect.Set(ms, "ProbePath", probePath);
            Reflect.Set(ms, "ProbeProtocol", Enum.Parse(protocolType, "File"));
            var local = (string)Reflect.Call(Reflect.Get(st.Source, "_appHost"), "GetLocalApiUrl", "127.0.0.1");
            Reflect.Set(ms, "Path", local + "/LiveTv/LiveStreamFiles/" + st.Uid + "/stream.ts");
            try { ((Task)Reflect.Call(st.Source, "AddMediaInfoWithProbe", false, ct)).GetAwaiter().GetResult(); }
            catch (Exception e) when (!(e is OperationCanceledException)) { Log(st.Source, "Puente: el análisis de Emby falló: " + Inner(e).Message); }
            Reflect.Set(ms, "Container", "mpegts");
            Reflect.Set(ms, "Path", local + "/LiveTv/LiveStreamFiles/" + st.Uid + "/stream.ts");
            Reflect.Set(ms, "DirectStreamUrl", "/LiveTv/LiveStreamFiles/" + st.Uid + "/hls/master.m3u8");
            Reflect.Set(ms, "AddApiKeyToDirectStreamUrl", true);
            Reflect.Set(ms, "HasMixedProtocols", true);
            Reflect.Set(ms, "TrancodeLiveStartIndex", (int?)0);
            Reflect.Set(ms, "Protocol", Enum.Parse(protocolType, "Http"));
            Reflect.Set(ms, "IsRemote", false);
            Reflect.Set(ms, "RequiresLooping", false);
            Reflect.Set(ms, "SupportsDirectPlay", false);
            Reflect.Set(ms, "SupportsDirectStream", true);
            Reflect.Set(ms, "SupportsTranscoding", true);
        }

        // ---------------------------------------------------------------------------------------------------------
        // Publicación

        static void StartPublisher(State st)
        {
            var thread = new Thread(() => Publisher(st)) { IsBackground = true, Name = "EmbyNoShare puente " + st.Uid };
            thread.Start();
        }

        static void Publisher(State st)
        {
            var errors = 0;
            while (!st.Token.IsCancellationRequested)
            {
                try
                {
                    Step(st);
                    errors = 0;
                }
                catch (Exception e)
                {
                    errors++;
                    if (errors <= 3 || errors % 200 == 0) Log(st.Source, "Puente: error (" + errors.ToString(CultureInfo.InvariantCulture) + "), se sigue: " + Inner(e));
                }
                Thread.Sleep(50);
            }
            StopEncoder(st);
        }

        static void Step(State st)
        {
            // trozos del origen terminados, en orden
            while (true)
            {
                object file = null;
                lock (st)
                    if (st.Held.Count > 0 && st.Done.Contains(st.Held[0]))
                    {
                        file = st.Held[0];
                        st.Held.RemoveAt(0);
                        st.Done.Remove(file);
                    }
                if (file == null) break;
                if (st.PassThrough) PublishRaw(st, file);
                else PublishSource(st, file);
                st.GenFailures = 0;
            }

            // ¿llegan datos del origen?
            string key = null;
            lock (st)
                if (st.Held.Count > 0)
                {
                    var last = Reflect.Get(st.Held[st.Held.Count - 1], "Path") as string;
                    key = st.Held.Count.ToString(CultureInfo.InvariantCulture) + "|" + last + "|" + FileSize(last).ToString(CultureInfo.InvariantCulture);
                }
            if (key != null && key != st.ActivityKey) { st.ActivityKey = key; st.LastActivity = DateTime.UtcNow; }
            if (st.PassThrough) { CheckStall(st); return; }

            // puente: más intro si a los lectores se les va a acabar lo publicado
            var lead = Lead(st);
            if (st.OutStarted && lead < LowLead)
            {
                if (!st.Bridging) Log(st.Source, "Puente: el origen no llega a tiempo (margen " + lead.ToString("0.0", CultureInfo.InvariantCulture) + " s), entra la intro");
                st.Bridging = true;
                // mientras no se conoce el formato del origen, la intro guardada; después, la del formato actual
                if (st.DefaultFormat) PublishIntro(st, NextCached(st));
                else if (st.Enc != null)
                {
                    var segment = TakeEncoded(st.Enc, 0) as object[];
                    if (segment != null) PublishIntro(st, segment);
                }
            }
            CheckStall(st);

            // intro lista para cuando haga falta
            if (!st.DefaultFormat && (st.Enc == null || st.Enc.Done) && DateTime.UtcNow >= st.NextEncoderStart && st.Format != null)
            {
                if (st.Enc != null) Log(st.Source, "Puente: el ffmpeg de la intro terminó (" + st.Enc.Error.Trim() + "), se relanza");
                st.NextEncoderStart = DateTime.UtcNow.AddSeconds(10);
                StartEncoder(st);
            }
        }

        static void CheckStall(State st)
        {
            if (st.GenStarting || DateTime.UtcNow < st.NextGenRestart) return;
            var idle = (DateTime.UtcNow - st.LastActivity).TotalSeconds;
            if (idle < Stall) return;
            Log(st.Source, "Puente: " + ((int)idle).ToString(CultureInfo.InvariantCulture) + " s sin datos del origen, se abre otra conexión");
            RestartGen(st, 0);
        }

        /// Segundos publicados por delante del ritmo de reproducción
        static double Lead(State st)
        {
            if (!st.OutStarted) return 0;
            return Mod(st.OutNextDts - st.P0) / (double)Clock - (DateTime.UtcNow - st.W0).TotalSeconds;
        }

        static void StartOut(State st)
        {
            if (st.OutStarted) return;
            st.OutStarted = true;
            st.P0 = OutStart;
            st.OutNextDts = OutStart;
            st.W0 = DateTime.UtcNow;
        }

        /// Un trozo del origen: si cambia el formato, Emby lo vuelve a analizar, la intro se regenera y las conversiones
        /// se relanzan; se publica reescrito sobre la línea de tiempo continua
        static void PublishSource(State st, object file)
        {
            var path = Reflect.Get(file, "Path") as string;
            var raw = ReadShared(path, 0, int.MaxValue);
            try
            {
                if (raw.Length < 188 * 4) return;
                var tables = ParseTables(raw);
                var signature = tables == null ? st.Signature : Signature(raw, tables);
                var formatChanged = false;
                if (tables != null && (signature != st.Signature || st.DefaultFormat))
                {
                    ApplyTables(st, tables);
                    var streams = FfprobeStreams(path);
                    if (streams != null)
                    {
                        var wasDefault = st.DefaultFormat;
                        var material = Material(st, streams);
                        var changed = material != st.Material;
                        if (changed || wasDefault)
                        {
                            Log(st.Source, (wasDefault ? "Puente: formato del origen: " : "Puente: el formato del origen cambia: ") + Describe(streams)
                                           + (st.Remuxed ? " (entra por ffmpeg)" : "") + (changed ? "" : " (igual que la intro)"));
                            st.Format = streams;
                            st.Material = material;
                            st.DefaultFormat = false;
                            st.FrameDur = FrameDuration(streams);
                            var probe = Path.Combine(st.BaseDir, "probe" + (++st.ProbeCount).ToString(CultureInfo.InvariantCulture) + ".ts");
                            File.WriteAllBytes(probe, raw);
                            Probe(st, probe, st.Token);
                            StopEncoder(st);
                            if (st.VideoPid < 0) st.PassThrough = true;     // sin vídeo (radio): tal cual desde ahora
                            else StartEncoder(st);
                            if (changed)
                            {
                                formatChanged = true;
                                // conversiones de Emby (Emby.Server.MediaEncoding): relanzar con una decisión nueva
                                try { (AppDomain.CurrentDomain.GetData("EmbyNoShare.LiveFormatChanged") as Action<string>)?.Invoke(st.Uid); }
                                catch (Exception e) { Log(st.Source, "Puente: aviso a las conversiones fallido: " + e.Message); }
                            }
                        }
                    }
                    st.Signature = signature;
                }
                if (st.VideoPid < 0) return;
                var firstDts = FirstVideoDts(raw, st.VideoPid);
                if (firstDts == null) return;

                // volver al directo si se ha acumulado demasiado margen
                if (!formatChanged && st.OutStarted && Lead(st) > MaxLead)
                {
                    if (!st.Dropping) Log(st.Source, "Puente: margen de " + Lead(st).ToString("0.0", CultureInfo.InvariantCulture) + " s, se descartan trozos para volver al directo");
                    st.Dropping = true;
                    st.RunKind = 0;
                    return;
                }
                st.Dropping = false;

                StartOut(st);
                var discontinuous = Reflect.Get(file, "IsDiscontinuity") is bool d && d;
                var newRun = st.RunKind != 2 || st.RunRawNext == null || discontinuous
                             || Math.Abs(Signed(firstDts.Value - st.RunRawNext.Value)) > Clock;
                if (newRun) st.RunOffset = Mod(st.OutNextDts - firstDts.Value);
                var output = Rewrite(st, raw, st.RunOffset, newRun, false, out var lastRaw, out var lastOut);
                if (lastOut < 0) return;
                long duration = 0;
                if (Reflect.Get(file, "Duration") is TimeSpan span && span > TimeSpan.Zero) duration = (long)(span.TotalSeconds * Clock);
                if (duration <= 0) duration = Mod(lastOut - Mod(firstDts.Value + st.RunOffset)) + st.FrameDur;
                Publish(st, output, duration, Mod(firstDts.Value + st.RunOffset), formatChanged);
                if (st.Bridging) Log(st.Source, "Puente: vuelve el origen" + (newRun ? " (tiempo reajustado)" : ""));
                st.Bridging = false;
                st.OutNextDts = Mod(lastOut + st.FrameDur);
                st.RunRawNext = Mod(lastRaw + st.FrameDur);
                st.RunKind = 2;
            }
            finally
            {
                TryDelete(path);
            }
        }

        /// Un trozo de la intro: tablas del canal y tiempo seguido
        static void PublishIntro(State st, object[] segment)
        {
            var data = (byte[])segment[0];
            var firstDts = FirstVideoDts(data, st.VideoPid);
            if (firstDts == null) return;
            StartOut(st);
            var newRun = st.RunKind != 1 || st.RunRawNext == null || Math.Abs(Signed(firstDts.Value - st.RunRawNext.Value)) > Clock;
            if (newRun) st.RunOffset = Mod(st.OutNextDts - firstDts.Value);
            var output = Rewrite(st, data, st.RunOffset, newRun, true, out var lastRaw, out var lastOut);
            if (lastOut < 0) return;
            Publish(st, output, (long)segment[2], Mod(firstDts.Value + st.RunOffset), false);
            st.OutNextDts = Mod(lastOut + st.FrameDur);
            st.RunRawNext = Mod(lastRaw + st.FrameDur);
            st.RunKind = 1;
        }

        /// Sin vídeo: el trozo tal cual, con la numeración seguida
        static void PublishRaw(State st, object file)
        {
            lock (st)
            {
                st.Replaying = true;
                try
                {
                    Reflect.Set(file, "SequenceNumber", st.NextSeq++);
                    Reflect.Call(st.Source, "OnSegmentStarted", file);
                    Reflect.Call(st.Source, "OnSegmentCompleted", file);
                    Reflect.Set(st.Source, "CurrentWritingFile", null);
                }
                finally { st.Replaying = false; }
            }
        }

        static void Publish(State st, byte[] data, long duration, long firstDts, bool discontinuity)
        {
            lock (st)
            {
                var seq = st.NextSeq++;
                var path = Path.Combine(st.BaseDir, "p" + seq.ToString("0000000", CultureInfo.InvariantCulture) + ".ts");
                File.WriteAllBytes(path, data);
                var info = Activator.CreateInstance(st.InfoType);
                Reflect.Set(info, "Path", path);
                Reflect.Set(info, "FileName", Path.GetFileName(path));
                Reflect.Set(info, "SequenceNumber", seq);
                Reflect.Set(info, "Duration", TimeSpan.FromSeconds(duration / (double)Clock));
                Reflect.Set(info, "IsClosed", true);
                Reflect.Set(info, "IsDiscontinuity", discontinuity);
                var wall = new DateTimeOffset(st.W0, TimeSpan.Zero).AddSeconds(Mod(firstDts - st.P0) / (double)Clock);
                Reflect.Set(info, "WallClockStartTime", (DateTimeOffset?)wall);
                st.Replaying = true;
                try
                {
                    Reflect.Call(st.Source, "OnSegmentStarted", info);
                    Reflect.Call(st.Source, "OnSegmentCompleted", info);
                    Reflect.Set(st.Source, "CurrentWritingFile", null);
                }
                finally { st.Replaying = false; }
            }
        }

        static string LastPublishedPath(State st) =>
            Path.Combine(st.BaseDir, "p" + (st.NextSeq - 1).ToString("0000000", CultureInfo.InvariantCulture) + ".ts");

        /// Reescribe un trozo sobre la línea de tiempo publicada: PAT/PMT del canal (con versión), PTS/DTS/PCR + offset,
        /// contadores de continuidad seguidos y, al empezar un tramo nuevo, indicador de discontinuidad en el primer PCR
        static byte[] Rewrite(State st, byte[] input, long offset, bool newRun, bool intro, out long lastRawDts, out long lastOutDts)
        {
            lastRawDts = -1;
            lastOutDts = -1;
            var output = new MemoryStream(input.Length + 188 * 16);
            WriteTables(st, output);
            var markPcr = newRun;
            var packet = new byte[188];
            var dropping = new Dictionary<int, bool>();
            var trimming = new HashSet<int>();          // pistas de audio aún por encajar tras el empalme
            if (newRun) foreach (var pid in st.MediaPids) if (pid != st.VideoPid) trimming.Add(pid);
            var lastAudio = new Dictionary<int, long>();
            for (var p = 0; p + 188 <= input.Length; p += 188)
            {
                if (input[p] != 0x47) continue;
                var pid = PidOf(input, p);
                if (pid == 0) { WriteTables(st, output); continue; }
                if (!st.MediaPids.Contains(pid) && pid != st.PcrPid) continue;    // tablas y SDT, relleno
                if (intro && pid == st.IntroPmtPid) continue;
                Buffer.BlockCopy(input, p, packet, 0, 188);
                if ((packet[1] & 0x40) != 0 && pid == st.VideoPid)
                {
                    var dts = PesPts(packet, 0, true) ?? PesPts(packet, 0, false);
                    if (dts != null) { lastRawDts = dts.Value; lastOutDts = Mod(dts.Value + offset); }
                }
                if ((packet[1] & 0x40) != 0)
                {
                    ShiftPes(packet, offset);
                    if (pid != st.VideoPid && PesPts(packet, 0, false) is long apts)
                    {
                        // tras un empalme, fuera el audio que se solaparía con el ya publicado
                        var drop = false;
                        if (trimming.Contains(pid))
                        {
                            if (st.AudioNext.TryGetValue(pid, out var expected) && Signed(apts - expected) < -Clock / 100) drop = true;
                            else trimming.Remove(pid);
                        }
                        dropping[pid] = drop;
                        if (!drop)
                        {
                            var delta = lastAudio.TryGetValue(pid, out var previous) ? Signed(apts - previous) : 0;
                            lastAudio[pid] = apts;
                            if (delta > 0 && delta < Clock) st.AudioDelta[pid] = delta;
                            st.AudioNext[pid] = Mod(apts + (st.AudioDelta.TryGetValue(pid, out var step) ? step : 1920));
                        }
                    }
                }
                if (dropping.TryGetValue(pid, out var dropNow) && dropNow) continue;
                var pcr = ShiftPcr(packet, offset);
                if (pcr != null && markPcr && pid == st.PcrPid) { packet[5] |= 0x80; markPcr = false; }
                WritePacket(st, output, packet);
                if (pcr != null && pid != st.PcrPid)
                {
                    WritePcr(st, output, st.PcrPid, pcr.Value, markPcr);
                    markPcr = false;
                }
            }
            return output.ToArray();
        }

        static long? FirstVideoDts(byte[] data, int videoPid)
        {
            for (var p = 0; p + 188 <= data.Length; p += 188)
            {
                if (data[p] != 0x47 || PidOf(data, p) != videoPid || (data[p + 1] & 0x40) == 0) continue;
                var dts = PesPts(data, p, true) ?? PesPts(data, p, false);
                if (dts != null) return dts;
            }
            return null;
        }

        static void WriteTables(State st, MemoryStream output)
        {
            foreach (var p in st.Pat) WritePacket(st, output, (byte[])p.Clone());
            foreach (var p in st.Pmt) WritePacket(st, output, (byte[])p.Clone());
        }

        static void WritePacket(State st, MemoryStream output, byte[] packet)
        {
            var pid = PidOf(packet, 0);
            var payload = (packet[3] & 0x10) != 0;
            if (!st.Counters.TryGetValue(pid, out var counter)) counter = 15;
            if (payload) counter = (counter + 1) & 15;
            st.Counters[pid] = counter;
            packet[3] = (byte)((packet[3] & 0xF0) | counter);
            output.Write(packet, 0, 188);
        }

        /// Paquete solo con PCR (sin carga) en el PID que el canal usa para el reloj
        static void WritePcr(State st, MemoryStream output, int pid, long pcr, bool discontinuity)
        {
            var b = new byte[188];
            for (var i = 0; i < 188; i++) b[i] = 0xFF;
            b[0] = 0x47;
            b[1] = (byte)((pid >> 8) & 0x1F);
            b[2] = (byte)pid;
            b[3] = 0x20;
            b[4] = 183;
            b[5] = (byte)(discontinuity ? 0x90 : 0x10);
            b[6] = (byte)(pcr >> 25);
            b[7] = (byte)(pcr >> 17);
            b[8] = (byte)(pcr >> 9);
            b[9] = (byte)(pcr >> 1);
            b[10] = (byte)(((pcr & 1) << 7) | 0x7E);
            b[11] = 0;
            WritePacket(st, output, b);
        }

        // ---------------------------------------------------------------------------------------------------------
        // Secciones PSI

        static byte[] Section(byte[][] packets)
        {
            var section = new MemoryStream();
            for (var i = 0; i < packets.Length; i++)
            {
                var o = PayloadOffset(packets[i], 0);
                if (o < 0) continue;
                if (i == 0) o += 1 + packets[i][o];
                if (o < 188) section.Write(packets[i], o, 188 - o);
            }
            var s = section.ToArray();
            if (s.Length < 3) return s;
            var total = 3 + (((s[1] & 0x0F) << 8) | s[2]);
            if (total < s.Length) Array.Resize(ref s, total);
            return s;
        }

        static string SectionBody(byte[][] packets)
        {
            var s = Section(packets);
            var b = new StringBuilder();
            for (var i = 0; i < s.Length - 4; i++) if (i != 5) b.Append(s[i].ToString("x2", CultureInfo.InvariantCulture));
            return b.ToString();
        }

        static int SectionVersion(byte[][] packets)
        {
            var s = Section(packets);
            return s.Length > 5 ? (s[5] >> 1) & 31 : 0;
        }

        /// Pone la versión en la sección repartida entre los paquetes y recalcula su CRC
        static void SetSectionVersion(byte[][] packets, int version)
        {
            var s = Section(packets);
            if (s.Length < 12) return;
            s[5] = (byte)((s[5] & 0xC1) | ((version & 31) << 1));
            var crc = Crc32(s, s.Length - 4);
            s[s.Length - 4] = (byte)(crc >> 24);
            s[s.Length - 3] = (byte)(crc >> 16);
            s[s.Length - 2] = (byte)(crc >> 8);
            s[s.Length - 1] = (byte)crc;
            var k = 0;
            for (var i = 0; i < packets.Length && k < s.Length; i++)
            {
                var o = PayloadOffset(packets[i], 0);
                if (o < 0) continue;
                if (i == 0) o += 1 + packets[i][o];
                for (; o < 188 && k < s.Length; o++, k++) packets[i][o] = s[k];
            }
        }

        static uint Crc32(byte[] data, int length)
        {
            uint crc = 0xFFFFFFFF;
            for (var i = 0; i < length; i++)
            {
                crc ^= (uint)data[i] << 24;
                for (var bit = 0; bit < 8; bit++) crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
            }
            return crc;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Intro

        /// Intro en directo (bucle) en el formato actual del origen, para los cortes
        static void StartEncoder(State st)
        {
            if (st.Format == null || st.VideoPid < 0 || st.DefaultFormat) return;
            var args = EncoderArguments(st, st.Format, st.Intro, null);
            if (args == null)
            {
                Log(st.Source, "Puente: no hay codificador para la intro en este formato; sin intro hasta que cambie");
                st.Enc = null;
                st.NextEncoderStart = DateTime.MaxValue;
                return;
            }
            Log(st.Source, "Puente: intro con ffmpeg " + args);
            var enc = new Encoder();
            StartProcess(st, enc, args, st.VideoPid, st.Token);
            st.Enc = enc;
            st.NextEncoderStart = DateTime.UtcNow.AddSeconds(5);
        }

        static void StartProcess(State st, Encoder enc, string args, int videoPid, CancellationToken token)
        {
            try
            {
                var info = new ProcessStartInfo(Tool("ffmpeg"), args)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = AppDir(),
                };
                enc.Process = Process.Start(info);
                enc.Process.ErrorDataReceived += (sender, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;
                    lock (enc.Queue) enc.Error = (enc.Error + " " + e.Data).Length > 2000 ? e.Data : enc.Error + " " + e.Data;
                };
                enc.Process.BeginErrorReadLine();
                var reader = new Thread(() => ReadEncoder(enc, videoPid, token)) { IsBackground = true, Name = "EmbyNoShare intro ffmpeg" };
                reader.Start();
            }
            catch (Exception e)
            {
                lock (enc.Queue)
                {
                    enc.Error = e.Message;
                    enc.Done = true;
                }
            }
        }

        static void StopEncoder(State st)
        {
            var enc = st.Enc;
            st.Enc = null;
            if (enc == null) return;
            try { if (enc.Process != null && !enc.Process.HasExited) enc.Process.Kill(); } catch { }
            lock (enc.Queue)
            {
                enc.Done = true;
                Monitor.PulseAll(enc.Queue);
            }
        }

        /// Corta la salida de ffmpeg en trozos que empiezan en un fotograma clave cada SegmentSeconds:
        /// {byte[] datos, long PTS inicial, long duración}
        static void ReadEncoder(Encoder enc, int videoPid, CancellationToken token)
        {
            var stream = enc.Process.StandardOutput.BaseStream;
            var buffer = new byte[188 * 1024];
            var filled = 0;
            MemoryStream current = null;
            long currentStart = 0;
            try
            {
                while (!token.IsCancellationRequested && !enc.Done)
                {
                    var n = stream.Read(buffer, filled, buffer.Length - filled);
                    if (n <= 0) break;
                    filled += n;
                    var p = 0;
                    for (; p + 188 <= filled; p += 188)
                    {
                        if (buffer[p] != 0x47)
                        {
                            var next = Array.IndexOf(buffer, (byte)0x47, p + 1, filled - p - 1);
                            if (next < 0) { p = filled; break; }
                            p = next - 188;
                            continue;
                        }
                        if (PidOf(buffer, p) == videoPid && (buffer[p + 1] & 0x40) != 0 && RandomAccess(buffer, p))
                        {
                            var pts = PesPts(buffer, p, false);
                            if (pts != null)
                            {
                                if (current != null && Mod(pts.Value - currentStart) >= SegmentSeconds * Clock - Clock / 30)
                                {
                                    Enqueue(enc, new object[] { current.ToArray(), currentStart, Mod(pts.Value - currentStart) }, token);
                                    current = null;
                                }
                                if (current == null) { current = new MemoryStream(); currentStart = pts.Value; }
                            }
                        }
                        current?.Write(buffer, p, 188);
                    }
                    var rest = filled - p;
                    if (rest > 0) Buffer.BlockCopy(buffer, p, buffer, 0, rest);
                    filled = Math.Max(0, rest);
                }
            }
            catch { }
            lock (enc.Queue)
            {
                enc.Done = true;
                Monitor.PulseAll(enc.Queue);
            }
        }

        static void Enqueue(Encoder enc, object segment, CancellationToken token)
        {
            lock (enc.Queue)
            {
                while (enc.Queue.Count >= EncodedAhead && !token.IsCancellationRequested && !enc.Done)
                    Monitor.Wait(enc.Queue, 500);
                enc.Queue.Enqueue(segment);
                Monitor.PulseAll(enc.Queue);
            }
        }

        static object TakeEncoded(Encoder enc, int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            lock (enc.Queue)
            {
                while (enc.Queue.Count == 0)
                {
                    if (enc.Done) return null;
                    var left = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                    if (left <= 0) return null;
                    Monitor.Wait(enc.Queue, Math.Min(left, 500));
                }
                var segment = enc.Queue.Dequeue();
                Monitor.PulseAll(enc.Queue);
                return segment;
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // Codificación de la intro

        /// Argumentos de ffmpeg para la intro en el formato del canal, o null si no hay codificador para él
        static string EncoderArguments(State st, JsonElement[] streams, string intro, double? seconds)
        {
            JsonElement? video = null;
            var audios = new List<string>();     // argumentos por pista de audio
            var audioPids = new List<int>();
            var latm = false;
            foreach (var s in streams)
            {
                var pid = Pid(s);
                if (!st.MediaPids.Contains(pid)) continue;
                var type = Str(s, "codec_type");
                if (type == "video" && pid == st.VideoPid) video = s;
                if (type != "audio") continue;
                var codec = Str(s, "codec_name");
                var rate = Int(s, "sample_rate") > 0 ? Int(s, "sample_rate") : 48000;
                var channels = Int(s, "channels") > 0 ? Int(s, "channels") : 2;
                string audioEncoder;
                int bitrate;
                switch (codec)
                {
                    case "aac": audioEncoder = "aac"; bitrate = Math.Min(64 * channels, 384); break;
                    case "aac_latm": audioEncoder = "aac"; bitrate = Math.Min(64 * channels, 384); latm = true; break;
                    case "mp2": audioEncoder = "mp2"; channels = Math.Min(channels, 2); bitrate = 192; break;
                    case "mp3": audioEncoder = "libmp3lame"; channels = Math.Min(channels, 2); bitrate = 128; break;
                    case "ac3": audioEncoder = "ac3"; bitrate = channels > 2 ? 448 : 192; break;
                    case "eac3": audioEncoder = "eac3"; bitrate = channels > 2 ? 448 : 192; break;
                    default: return null;
                }
                var i = audios.Count.ToString(CultureInfo.InvariantCulture);
                audios.Add(string.Format(CultureInfo.InvariantCulture, " -c:a:{0} {1} -ar:a:{0} {2} -ac:a:{0} {3} -b:a:{0} {4}k",
                    i, audioEncoder, rate, channels, bitrate));
                audioPids.Add(pid);
            }
            if (video == null) return null;
            var v = video.Value;
            var width = Int(v, "width");
            var height = Int(v, "height");
            var pixFmt = Str(v, "pix_fmt");
            var fps = Rate(v);
            var field = Str(v, "field_order") ?? "progressive";
            var interlaced = field == "tt" || field == "tb" || field == "bb" || field == "bt";
            var topFirst = field == "tt" || field == "tb";
            var bframes = Int(v, "has_b_frames");
            var gop = Math.Max(1, (int)Math.Round(SegmentSeconds * Fps(fps)));
            var sar = Str(v, "sample_aspect_ratio");
            int sarNum = 1, sarDen = 1;
            if (sar != null)
            {
                var parts = sar.Split(':');
                if (parts.Length == 2 && int.TryParse(parts[0], out var n) && int.TryParse(parts[1], out var d) && n > 0 && d > 0) { sarNum = n; sarDen = d; }
            }
            var displayWidth = (int)Math.Round(width * (double)sarNum / sarDen / 2) * 2;
            var kbps = height <= 576 ? 3000 : height <= 720 ? 5000 : 8000;

            var filter = string.Format(CultureInfo.InvariantCulture,
                "fps={0},scale={1}:{2}:force_original_aspect_ratio=decrease,pad={1}:{2}:(ow-iw)/2:(oh-ih)/2,scale={3}:{2},setsar={4}/{5},format={6}{7}",
                fps, displayWidth, height, width, sarNum, sarDen, pixFmt, interlaced ? ",setfield=" + (topFirst ? "tff" : "bff") : "");
            string encoder;
            switch (Str(v, "codec_name"))
            {
                case "h264":
                {
                    var profile = (Str(v, "profile") ?? "High").ToLowerInvariant();
                    profile = profile.Contains("baseline") ? "baseline" : profile.Contains("main") ? "main"
                        : profile.Contains("10") ? "high10" : profile.Contains("4:2:2") ? "high422" : profile.Contains("4:4:4") ? "high444" : "high";
                    var level = Int(v, "level");
                    var x264 = "scenecut=0:open-gop=0" + (interlaced ? (topFirst ? ":tff=1" : ":bff=1") : "");
                    encoder = string.Format(CultureInfo.InvariantCulture,
                        "-c:v libx264 -preset veryfast -profile:v {0}{1} -g {2} -keyint_min {2} -bf {3} -x264-params {4}{5} -b:v {6}k -maxrate {6}k -bufsize {7}k",
                        profile, level > 0 ? string.Format(CultureInfo.InvariantCulture, " -level:v {0}.{1}", level / 10, level % 10) : "",
                        gop, bframes <= 0 ? 0 : bframes == 1 ? 1 : 3, x264, interlaced ? " -flags +ildct+ilme" : "", kbps, kbps * 2);
                    break;
                }
                case "mpeg2video":
                    encoder = string.Format(CultureInfo.InvariantCulture,
                        "-c:v mpeg2video -g 12 -bf {0} -sc_threshold 1000000000 -b:v {1}k -maxrate {1}k -bufsize {2}{3}",
                        bframes > 0 ? 2 : 0, kbps, height > 576 ? 9781248 : 1835008, interlaced ? " -flags +ildct+ilme -top " + (topFirst ? "1" : "0") : "");
                    break;
                case "hevc":
                    if (interlaced) return null;
                    encoder = string.Format(CultureInfo.InvariantCulture,
                        "-c:v libx265 -preset ultrafast -x265-params keyint={0}:min-keyint={0}:scenecut=0:bframes={1}:repeat-headers=1:log-level=error -b:v {2}k",
                        gop, bframes > 0 ? 4 : 0, kbps / 2);
                    break;
                default:
                    return null;
            }

            // PID de la tabla de la intro: uno que el canal no use (se sustituye por la del canal)
            st.IntroPmtPid = 0x1FF0;
            while (st.MediaPids.Contains(st.IntroPmtPid) || st.IntroPmtPid == st.PmtPid || st.IntroPmtPid == st.PcrPid) st.IntroPmtPid--;

            var image = intro == null || IsImage(intro);
            var introAudio = !image && HasAudio(intro);
            var b = new StringBuilder("-hide_banner -nostdin -loglevel error ");
            if (intro == null)
                b.Append("-f lavfi -i ").Append(Quote(string.Format(CultureInfo.InvariantCulture, "color=c=black:s={0}x{1}:r={2}", width, height, fps)));
            else
                b.Append(image ? "-loop 1 -framerate " + fps + " " : "-stream_loop -1 ").Append("-i ").Append(Quote(intro));
            if (audios.Count > 0 && !introAudio) b.Append(" -f lavfi -i anullsrc=r=48000:cl=stereo");
            b.Append(" -map 0:v:0");
            foreach (var _ in audios) b.Append(introAudio ? " -map 0:a:0" : " -map 1:a:0");
            b.Append(" -vf ").Append(Quote(filter)).Append(' ').Append(encoder);
            b.Append(" -force_key_frames ").Append(Quote("expr:gte(t,n_forced*" + SegmentSeconds + ")"));
            // huecos del audio de la intro (p. ej. más corto que el vídeo, en cada vuelta) rellenos con silencio
            if (audios.Count > 0) b.Append(" -af aresample=async=1:first_pts=0");
            foreach (var a in audios) b.Append(a);
            b.Append(" -streamid 0:").Append(st.VideoPid.ToString(CultureInfo.InvariantCulture));
            for (var i = 0; i < audioPids.Count; i++)
                b.Append(" -streamid ").Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(':').Append(audioPids[i].ToString(CultureInfo.InvariantCulture));
            b.Append(" -mpegts_pmt_start_pid ").Append(st.IntroPmtPid.ToString(CultureInfo.InvariantCulture));
            if (latm) b.Append(" -mpegts_flags latm");
            if (seconds != null) b.Append(" -t ").Append(seconds.Value.ToString(CultureInfo.InvariantCulture));
            b.Append(" -muxdelay 0 -muxpreload 0 -f mpegts pipe:1");
            return b.ToString();
        }

        static bool IsImage(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".webp";
        }

        static string introChecked;
        static bool introHasAudio;

        static bool HasAudio(string intro)
        {
            var key = intro + "|" + File.GetLastWriteTimeUtc(intro).Ticks.ToString(CultureInfo.InvariantCulture);
            lock (Sync) if (introChecked == key) return introHasAudio;
            var streams = FfprobeStreams(intro);
            var audio = false;
            if (streams != null) foreach (var s in streams) if (Str(s, "codec_type") == "audio") audio = true;
            lock (Sync) { introChecked = key; introHasAudio = audio; }
            return audio;
        }


        // ---------------------------------------------------------------------------------------------------------
        // MPEG-TS

        static int PidOf(byte[] b, int p) => ((b[p + 1] & 0x1F) << 8) | b[p + 2];

        static byte[] Copy(byte[] b, int p)
        {
            var c = new byte[188];
            Buffer.BlockCopy(b, p, c, 0, 188);
            return c;
        }

        /// Comienzo de la carga útil del paquete, o -1 si no tiene
        static int PayloadOffset(byte[] b, int p)
        {
            var afc = (b[p + 3] >> 4) & 3;
            if ((afc & 1) == 0) return -1;
            var o = p + 4;
            if ((afc & 2) != 0) o += 1 + b[p + 4];
            return o < p + 188 ? o : -1;
        }

        static bool RandomAccess(byte[] b, int p) => ((b[p + 3] >> 4) & 2) != 0 && b[p + 4] > 0 && (b[p + 5] & 0x40) != 0;

        /// PTS (o DTS) de la cabecera PES que empieza en el paquete
        static long? PesPts(byte[] b, int p, bool dts)
        {
            var o = PayloadOffset(b, p);
            if (o < 0 || o + 19 > p + 188 || b[o] != 0 || b[o + 1] != 0 || b[o + 2] != 1) return null;
            var flags = b[o + 7] >> 6;
            if (dts) return flags == 3 ? ReadTs(b, o + 14) : (long?)null;
            return (flags & 2) != 0 ? ReadTs(b, o + 9) : (long?)null;
        }

        static void ShiftPes(byte[] b, long shift)
        {
            var o = PayloadOffset(b, 0);
            if (o < 0 || o + 19 > 188 || b[o] != 0 || b[o + 1] != 0 || b[o + 2] != 1) return;
            var flags = b[o + 7] >> 6;
            if ((flags & 2) != 0) WriteTs(b, o + 9, Mod(ReadTs(b, o + 9) + shift));
            if (flags == 3) WriteTs(b, o + 14, Mod(ReadTs(b, o + 14) + shift));
        }

        /// Desplaza el PCR del paquete, si lo lleva, y lo devuelve (base de 90 kHz)
        static long? ShiftPcr(byte[] b, long shift)
        {
            if (((b[3] >> 4) & 2) == 0 || b[4] < 7 || (b[5] & 0x10) == 0) return null;
            long bas = ((long)b[6] << 25) | ((long)b[7] << 17) | ((long)b[8] << 9) | ((long)b[9] << 1) | ((long)b[10] >> 7);
            bas = Mod(bas + shift);
            b[6] = (byte)(bas >> 25);
            b[7] = (byte)(bas >> 17);
            b[8] = (byte)(bas >> 9);
            b[9] = (byte)(bas >> 1);
            b[10] = (byte)(((bas & 1) << 7) | (b[10] & 0x7F));
            return bas;
        }

        static long ReadTs(byte[] b, int o) =>
            ((long)(b[o] >> 1) & 7) << 30 | (long)b[o + 1] << 22 | (long)(b[o + 2] >> 1) << 15 | (long)b[o + 3] << 7 | (long)(b[o + 4] >> 1);

        static void WriteTs(byte[] b, int o, long v)
        {
            b[o] = (byte)((b[o] & 0xF1) | (int)(((v >> 30) & 7) << 1));
            b[o + 1] = (byte)(v >> 22);
            b[o + 2] = (byte)((((v >> 15) & 0x7F) << 1) | 1);
            b[o + 3] = (byte)(v >> 7);
            b[o + 4] = (byte)(((v & 0x7F) << 1) | 1);
        }

        static long Mod(long v) => ((v % Wrap) + Wrap) % Wrap;

        static long Signed(long v)
        {
            v = Mod(v);
            return v >= Wrap / 2 ? v - Wrap : v;
        }

        static string Sanitize(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            var q = url.IndexOf('?');
            return q >= 0 ? url.Substring(0, q) + "?…" : url;
        }


        // ---------------------------------------------------------------------------------------------------------
        // ffprobe / ffmpeg de Emby

        static string AppDir()
        {
            var system = AppContext.BaseDirectory.TrimEnd('/', '\\');
            return Directory.GetParent(system)?.FullName ?? system;
        }

        static string Tool(string name)
        {
            var configured = Environment.GetEnvironmentVariable("EMBY_LIVE_INTRO_" + name.ToUpperInvariant());
            if (!string.IsNullOrEmpty(configured)) return configured;
            var path = Path.Combine(AppDir(), "bin", name);
            return File.Exists(path) ? path : name;
        }

        static JsonElement[] FfprobeStreams(string file)
        {
            try
            {
                var info = new ProcessStartInfo(Tool("ffprobe"),
                    "-v error -analyzeduration 5000000 -probesize 20000000 -show_streams -of json " + Quote(file))
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = AppDir(),
                };
                using (var process = Process.Start(info))
                {
                    process.ErrorDataReceived += (sender, e) => { };
                    process.BeginErrorReadLine();
                    var json = process.StandardOutput.ReadToEnd();
                    if (!process.WaitForExit(15000)) { try { process.Kill(); } catch { } return null; }
                    using (var doc = JsonDocument.Parse(json))
                    {
                        if (!doc.RootElement.TryGetProperty("streams", out var streams)) return null;
                        var list = new List<JsonElement>();
                        foreach (var s in streams.EnumerateArray()) list.Add(s.Clone());
                        return list.ToArray();
                    }
                }
            }
            catch { return null; }
        }

        static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() != "unknown" ? v.GetString() : null;

        static int Int(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var v)) return 0;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
            if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out i)) return i;
            return 0;
        }

        static int Pid(JsonElement e)
        {
            var id = Str(e, "id");
            if (id == null) return -1;
            return id.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(id.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var pid) ? pid : -1;
        }

        /// Fotogramas por segundo exactos ("25/1", "30000/1001"). Si r_frame_rate da el doble (campos), avg_frame_rate.
        static string Rate(JsonElement e)
        {
            var r = Str(e, "r_frame_rate");
            var avg = Str(e, "avg_frame_rate");
            var fr = Fps(r);
            var fa = Fps(avg);
            if (fr <= 0 && fa <= 0) return null;
            if (fr <= 0 || (fa > 0 && Math.Abs(fr - 2 * fa) < 0.01)) return avg;
            return r;
        }

        static double Fps(string rate)
        {
            if (rate == null) return 0;
            var parts = rate.Split('/');
            if (parts.Length != 2 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || d <= 0) return 0;
            return n / d;
        }

        static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        static string Ms(DateTime since) => ((int)(DateTime.UtcNow - since).TotalMilliseconds).ToString(CultureInfo.InvariantCulture);

        static void Log(object source, string message) => Reflect.Log(source, message);
    }
}
