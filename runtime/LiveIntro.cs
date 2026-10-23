// Emby.LiveTV: abrir un canal MPEG-TS sin esperar a llenar el buffer, con una intro codificada al vuelo exactamente en
// el formato del canal.
//
// Hoy SharedHttpPipelineSource.OpenInternal conecta con el proveedor, espera dos trozos de 3 s del buffer y analiza el
// canal antes de responder al cliente. Con EMBY_LIVE_INTRO (ruta de un vídeo o imagen) ocurre esto:
//   1. Se conecta igual y el buffer empieza a escribirse, pero los trozos del canal se retienen (Hold): los lectores
//      todavía no los ven.
//   2. En cuanto llega la tabla de pistas y el primer fotograma clave, se mira el formato real con ffprobe (códec,
//      resolución, entrelazado, fps, audio, PID) y Emby analiza ese trozo como siempre: responde con el formato real.
//   3. El ffmpeg de Emby codifica la intro en ese mismo formato: mismo códec y parámetros, mismos PID, y la tabla de
//      pistas (PAT/PMT) del canal copiada byte a byte, así que las pistas tienen los mismos índices y descriptores.
//   4. La intro va al buffer como trozos normales, con marcas de tiempo justo antes de las del canal, al ritmo al que
//      llega el canal. Al empezar un trozo del canal posterior al final de la intro, se recorta la intro en ese
//      instante y se publican los trozos del canal: el tiempo sigue continuo y el formato es el mismo, así que ni el
//      cliente (directo MPEG-TS o HLS) ni el ffmpeg de Emby cambian nada.
// Si algo no encaja (fuente HLS, formato sin codificador, la intro no arranca) se sigue sin intro como Emby.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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
        const int EncodedAhead = 4;          // trozos de intro codificados por delante como máximo

        static readonly object Sync = new object();
        static readonly Dictionary<object, object> States = new Dictionary<object, object>();
        static readonly HashSet<object> Bypass = new HashSet<object>();

        sealed class State
        {
            public object Source, MediaSource;
            public string Uid, Dir;
            public Type InfoType;
            public CancellationToken Token;
            // trozos del canal empezados (en orden) y los ya terminados, retenidos hasta el cambio
            public readonly List<object> Held = new List<object>();
            public readonly HashSet<object> Done = new HashSet<object>();
            public bool Switched, Replaying;
            public int NextSeq, LastSeq = -1;

            // formato del canal
            public byte[][] Pat, Pmt;
            public int PmtPid, PcrPid, VideoPid;
            public readonly HashSet<int> MediaPids = new HashSet<int>();
            public readonly Dictionary<int, int> Counters = new Dictionary<int, int>();

            // intro codificada (trozos {byte[] datos, long inicio, long duración} con marcas de ffmpeg)
            public Process Encoder;
            public readonly Queue<object> Encoded = new Queue<object>();
            public bool EncoderDone;
            public string EncoderError = "";
            public int IntroPmtPid;
            public long Shift;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Ganchos

        /// SharedHttpPipelineSource.OpenInternal: la apertura con intro, o null para seguir con la de Emby
        public static Task Open(object source, CancellationToken cancellationToken)
        {
            try
            {
                var intro = Environment.GetEnvironmentVariable("EMBY_LIVE_INTRO");
                if (string.IsNullOrEmpty(intro) || !File.Exists(intro)) return null;
                lock (Sync) if (Bypass.Contains(source)) return null;
                var protocol = Reflect.Get(Reflect.Get(source, "MediaSource"), "Protocol")?.ToString();
                if (protocol != "Http" && protocol != "File") return null;
                return Task.Run(() => Run(source, intro, cancellationToken));
            }
            catch (Exception e)
            {
                Log(source, "Intro: no se usa: " + e.Message);
                return null;
            }
        }

        /// SharedHttpPipelineSource.OnSegmentStarted / OnSegmentCompleted: true si el trozo del canal se retiene
        public static bool Hold(object source, object file, bool started)
        {
            State st;
            lock (Sync)
            {
                if (!States.TryGetValue(source, out var o)) return false;
                st = (State)o;
            }
            lock (st)
            {
                if (st.Replaying) return false;
                if (!st.Switched)
                {
                    if (started) st.Held.Add(file);
                    else st.Done.Add(file);
                    return true;
                }
                // ya publicado: la numeración sigue a la de la intro
                if (started)
                {
                    if (!(Reflect.Get(file, "SequenceNumber") is int seq) || seq != st.LastSeq + 1)
                        Reflect.Set(file, "SequenceNumber", st.LastSeq + 1);
                    st.LastSeq++;
                }
                return false;
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // Apertura

        static void Run(object source, string intro, CancellationToken ct)
        {
            var ms = Reflect.Get(source, "MediaSource");
            var url = Reflect.Get(ms, "Path") as string;
            var protocol = Reflect.Get(ms, "Protocol");
            var opening = (Task)Reflect.Call(source, "OpenStream", null, ms, url, protocol, ct, 0);
            opening.GetAwaiter().GetResult();
            var result = Reflect.Get(opening, "Result");
            var response = Reflect.Get(result, "ResponseInfo");
            var context = Reflect.Get(result, "ConectionContext");
            if (!(Reflect.Call(source, "IsBufferingSupported", result) is bool buffering) || !buffering
                || Reflect.Call(source, "IsHls", result, ms) is bool hls && hls)
            {
                // HLS u otro formato que Emby no guarda en su buffer: su apertura de siempre
                (response as IDisposable)?.Dispose();
                (context as IDisposable)?.Dispose();
                Log(source, "Intro: la fuente no es MPEG-TS, apertura normal");
                Original(source, ct);
                return;
            }

            var st = new State { Source = source, MediaSource = ms };
            var streamCts = (CancellationTokenSource)Reflect.Get(source, "LiveStreamCancellationTokenSource");
            try
            {
                Prepare(st);
                st.Token = streamCts.Token;
                lock (Sync) States[source] = st;
                st.Token.Register(() => Forget(st));

                var tcs = new TaskCompletionSource<bool>();
                Reflect.Call(source, "StartStreaming", ms, response, context, Reflect.Get(result, "ResponseUrl"),
                    Reflect.Get(result, "MediaProtocol"), tcs, st.Token);
                tcs.Task.GetAwaiter().GetResult();

                var started = DateTime.UtcNow;
                var format = WaitFormat(st, ct);
                if (format == null)
                {
                    Log(source, "Intro: no llega la tabla de pistas y un fotograma clave en 20 s, se sigue sin intro");
                    WithoutIntro(st, ct, null);
                    return;
                }
                var probePath = Path.Combine(st.Dir, "probe.ts");
                Probe(st, probePath, ct);
                Log(source, "Intro: formato del canal listo en " + Ms(started) + " ms: " + Describe(format));

                var args = EncoderArguments(st, format, intro);
                if (args == null) { WithoutIntro(st, ct, "formato sin codificador para la intro"); return; }
                if (!StartEncoder(st, args)) { WithoutIntro(st, ct, "no arranca ffmpeg: " + st.EncoderError); return; }
                var first = TakeEncoded(st, 15000);
                var second = first == null ? null : TakeEncoded(st, 15000);
                var latest = second == null ? null : LatestPts(st);
                if (latest == null)
                {
                    StopEncoder(st);
                    WithoutIntro(st, ct, "la intro no se codifica a tiempo: " + st.EncoderError);
                    return;
                }
                var d0 = (long)((object[])first)[2];
                var d1 = (long)((object[])second)[2];
                var start = Mod(latest.Value - d0 - d1);
                st.Shift = Mod(start - (long)((object[])first)[1]);
                PublishIntro(st, Emit(st, (byte[])((object[])first)[0], null, null), d0);
                PublishIntro(st, Emit(st, (byte[])((object[])second)[0], null, null), d1);
                Log(source, "Intro: emitiendo desde " + Ms(started) + " ms");
                var feeder = new Thread(() => Feed(st, Mod(start + d0 + d1))) { IsBackground = true, Name = "EmbyNoShare intro" };
                feeder.Start();
            }
            catch
            {
                StopEncoder(st);
                Forget(st);
                try { streamCts.Cancel(); } catch { }
                throw;
            }
        }

        /// La apertura original de Emby (el gancho la deja pasar)
        static void Original(object source, CancellationToken ct)
        {
            lock (Sync) Bypass.Add(source);
            try { ((Task)Reflect.Call(source, "OpenInternal", ct)).GetAwaiter().GetResult(); }
            finally { lock (Sync) Bypass.Remove(source); }
        }

        static void Forget(State st)
        {
            lock (Sync) States.Remove(st.Source);
            StopEncoder(st);
        }

        /// Lo que hace OpenInternal antes de empezar a leer la fuente
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
            if (limit > TimeSpan.Zero)
            {
                if (limit < TimeSpan.FromMinutes(10)) limit = TimeSpan.FromMinutes(10);
            }
            Reflect.Set(source, "BufferLimit", limit);
            st.Uid = (string)Reflect.Get(source, "UniqueId");
            var temp = (string)Reflect.Call(Reflect.Get(source, "AppPaths"), "GetTranscodingTempPath");
            st.Dir = Path.Combine(temp, st.Uid);
            Reflect.Set(source, "TempFilePath", Path.Combine(st.Dir, "{0:0000000}.ts"));
            Directory.CreateDirectory(st.Dir);
            st.InfoType = Reflect.Get(source, "CompletedSegmentFiles").GetType().GetGenericArguments()[0];
        }

        /// Emby analiza lo recibido del canal (como en OpenInternal) y deja la fuente lista para los lectores
        static void Probe(State st, string probePath, CancellationToken ct)
        {
            var ms = st.MediaSource;
            var protocolType = Reflect.Get(ms, "Protocol").GetType();
            Reflect.Set(ms, "ProbePath", probePath);
            Reflect.Set(ms, "ProbeProtocol", Enum.Parse(protocolType, "File"));
            var local = (string)Reflect.Call(Reflect.Get(st.Source, "_appHost"), "GetLocalApiUrl", "127.0.0.1");
            Reflect.Set(ms, "Path", local + "/LiveTv/LiveStreamFiles/" + st.Uid + "/stream.ts");
            ((Task)Reflect.Call(st.Source, "AddMediaInfoWithProbe", false, ct)).GetAwaiter().GetResult();
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

        /// Sin intro: se publica el canal tal cual y se espera a dos trozos, como Emby
        static void WithoutIntro(State st, CancellationToken ct, string reason)
        {
            if (reason != null) Log(st.Source, "Intro: se sigue sin intro: " + reason);
            Switch(st, 0, false);
            var probed = reason != null;    // con motivo, Emby ya analizó el canal
            var start = DateTime.UtcNow;
            while (Count(Reflect.Call(st.Source, "GetSegments")) < 2 && (DateTime.UtcNow - start).TotalSeconds < 8)
            {
                ct.ThrowIfCancellationRequested();
                Thread.Sleep(50);
            }
            if (!probed)
            {
                var files = new List<string>();
                if (Reflect.Call(st.Source, "GetStreamFiles") is System.Collections.IList list)
                    foreach (var f in list) files.Add(Reflect.Get(f, "Path") as string);
                var path = string.Join("|", files.ToArray());
                Probe(st, path, ct);
            }
        }

        static int Count(object list) => list is System.Collections.ICollection c ? c.Count : 0;

        // ---------------------------------------------------------------------------------------------------------
        // Formato del canal

        /// Espera a que lo retenido del canal tenga la tabla de pistas y el formato de todas ellas (ffprobe)
        static JsonElement[] WaitFormat(State st, CancellationToken ct)
        {
            var start = DateTime.UtcNow;
            DateTime? videoReady = null;
            var probePath = Path.Combine(st.Dir, "probe.ts");
            while ((DateTime.UtcNow - start).TotalSeconds < 20)
            {
                ct.ThrowIfCancellationRequested();
                Thread.Sleep(videoReady == null ? 150 : 250);
                var data = Snapshot(st, 12 * 1024 * 1024);
                if (data.Length < 188 * 20 || !ParseTables(st, data)) continue;
                File.WriteAllBytes(probePath, data);
                var streams = FfprobeStreams(probePath);
                if (streams == null) continue;
                JsonElement? video = null;
                var audioMissing = false;
                foreach (var s in streams)
                {
                    var type = Str(s, "codec_type");
                    if (type == "video" && video == null && Int(s, "width") > 0 && Int(s, "height") > 0
                        && Str(s, "pix_fmt") != null && Rate(s) != null && Pid(s) == st.VideoPid)
                        video = s;
                    if (type == "audio" && (Int(s, "sample_rate") <= 0 || Int(s, "channels") <= 0)) audioMissing = true;
                }
                if (st.VideoPid < 0 || video == null) continue;
                if (videoReady == null) videoReady = DateTime.UtcNow;
                var waited = (DateTime.UtcNow - videoReady.Value).TotalSeconds;
                // el orden de campos sale al decodificar algún fotograma; el audio inactivo puede no llegar nunca
                if (Str(video.Value, "field_order") == null && waited < 1.5) continue;
                if (audioMissing && waited < 2) continue;
                return streams;
            }
            return null;
        }

        /// Lo retenido del canal hasta ahora, seguido
        static byte[] Snapshot(State st, int max)
        {
            var files = new List<string>();
            lock (st) foreach (var f in st.Held) files.Add(Reflect.Get(f, "Path") as string);
            var output = new MemoryStream();
            foreach (var file in files)
            {
                if (output.Length >= max) break;
                var bytes = ReadShared(file, 0, max - (int)output.Length);
                output.Write(bytes, 0, bytes.Length - bytes.Length % 188);
            }
            return output.ToArray();
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

        /// PAT y PMT del canal (los paquetes exactos, para repetirlos en la intro) y sus PID
        static bool ParseTables(State st, byte[] data)
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
            if (pat == null) return false;

            // PMT: desde un paquete con inicio de sección hasta completarla
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
                if (s.Length < total) return false;
                st.PcrPid = ((s[8] & 0x1F) << 8) | s[9];
                var infoLength = ((s[10] & 0x0F) << 8) | s[11];
                st.MediaPids.Clear();
                st.VideoPid = -1;
                for (var e = 12 + infoLength; e + 5 <= total - 4;)
                {
                    var type = s[e];
                    var pid = ((s[e + 1] & 0x1F) << 8) | s[e + 2];
                    var esLength = ((s[e + 3] & 0x0F) << 8) | s[e + 4];
                    st.MediaPids.Add(pid);
                    if (st.VideoPid < 0 && (type == 0x01 || type == 0x02 || type == 0x1B || type == 0x24)) st.VideoPid = pid;
                    e += 5 + esLength;
                }
                st.Pat = pat;
                st.Pmt = packets.ToArray();
                st.PmtPid = pmtPid;
                return true;
            }
            return false;
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

        // ---------------------------------------------------------------------------------------------------------
        // Codificación de la intro

        /// Argumentos de ffmpeg para la intro en el formato del canal, o null si no hay codificador para él
        static string EncoderArguments(State st, JsonElement[] streams, string intro)
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

            var image = IsImage(intro);
            var introAudio = !image && HasAudio(intro);
            var b = new StringBuilder("-hide_banner -nostdin -loglevel error ");
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

        static bool StartEncoder(State st, string args)
        {
            try
            {
                var info = new ProcessStartInfo(Tool("ffmpeg"), args)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = false,
                    CreateNoWindow = true,
                    WorkingDirectory = AppDir(),
                };
                Log(st.Source, "Intro: ffmpeg " + args);
                var process = Process.Start(info);
                st.Encoder = process;
                process.ErrorDataReceived += (sender, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;
                    lock (st.Encoded) st.EncoderError = (st.EncoderError + " " + e.Data).Length > 2000 ? e.Data : st.EncoderError + " " + e.Data;
                };
                process.BeginErrorReadLine();
                var reader = new Thread(() => ReadEncoder(st, process)) { IsBackground = true, Name = "EmbyNoShare intro ffmpeg" };
                reader.Start();
                return true;
            }
            catch (Exception e)
            {
                st.EncoderError = e.Message;
                return false;
            }
        }

        static void StopEncoder(State st)
        {
            var process = st.Encoder;
            if (process == null) return;
            try { if (!process.HasExited) process.Kill(); } catch { }
            lock (st.Encoded) Monitor.PulseAll(st.Encoded);
        }

        /// Corta la salida de ffmpeg en trozos que empiezan en un fotograma clave cada SegmentSeconds
        static void ReadEncoder(State st, Process process)
        {
            var stream = process.StandardOutput.BaseStream;
            var buffer = new byte[188 * 1024];
            var filled = 0;
            MemoryStream current = null;
            long currentStart = 0;
            try
            {
                while (!st.Token.IsCancellationRequested)
                {
                    var n = stream.Read(buffer, filled, buffer.Length - filled);
                    if (n <= 0) break;
                    filled += n;
                    var p = 0;
                    for (; p + 188 <= filled; p += 188)
                    {
                        if (buffer[p] != 0x47)
                        {
                            // resincronizar
                            var next = Array.IndexOf(buffer, (byte)0x47, p + 1, filled - p - 1);
                            if (next < 0) { p = filled; break; }
                            p = next - 188;
                            continue;
                        }
                        if (PidOf(buffer, p) == st.VideoPid && (buffer[p + 1] & 0x40) != 0 && RandomAccess(buffer, p))
                        {
                            var pts = PesPts(buffer, p, false);
                            if (pts != null)
                            {
                                if (current != null && Mod(pts.Value - currentStart) >= SegmentSeconds * Clock - Clock / 30)
                                {
                                    Enqueue(st, new object[] { current.ToArray(), currentStart, Mod(pts.Value - currentStart) });
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
            lock (st.Encoded)
            {
                st.EncoderDone = true;
                Monitor.PulseAll(st.Encoded);
            }
        }

        static void Enqueue(State st, object segment)
        {
            lock (st.Encoded)
            {
                while (st.Encoded.Count >= EncodedAhead && !st.Token.IsCancellationRequested && !st.EncoderDone)
                    Monitor.Wait(st.Encoded, 500);
                st.Encoded.Enqueue(segment);
                Monitor.PulseAll(st.Encoded);
            }
        }

        static object TakeEncoded(State st, int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            lock (st.Encoded)
            {
                while (st.Encoded.Count == 0)
                {
                    if (st.EncoderDone || st.Token.IsCancellationRequested) return null;
                    var left = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                    if (left <= 0) return null;
                    Monitor.Wait(st.Encoded, Math.Min(left, 500));
                }
                var segment = st.Encoded.Dequeue();
                Monitor.PulseAll(st.Encoded);
                return segment;
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // Emisión

        /// Mientras no haya un trozo del canal que empiece después del final de la intro, más intro al ritmo del canal;
        /// cuando lo hay, la intro se recorta justo ahí y se publica el canal
        static void Feed(State st, long end)
        {
            var lastLatest = (long?)null;
            var lastProgress = DateTime.UtcNow;
            try
            {
                while (!st.Token.IsCancellationRequested)
                {
                    var target = FindStart(st, end);
                    if (target != null)
                    {
                        var index = (int)target[0];
                        var pts = (long)target[1];
                        var dts = (long)target[2];
                        var audioStarts = (Dictionary<int, long>)target[3];
                        // cubrir con intro hasta el instante del canal (hasta medio fotograma)
                        while (Mod(pts - end) > Clock / 100 && Mod(pts - end) < Wrap / 2)
                        {
                            var segment = (object[])TakeEncoded(st, 5000);
                            if (segment == null) break;
                            var duration = (long)segment[2];
                            if (Mod(end + duration - pts) < Wrap / 2 && Mod(end + duration - pts) > 0)
                            {
                                PublishIntro(st, Emit(st, (byte[])segment[0], dts, pts, audioStarts), Mod(pts - end));
                                end = pts;
                                break;
                            }
                            PublishIntro(st, Emit(st, (byte[])segment[0], null, null), duration);
                            end = Mod(end + duration);
                        }
                        Switch(st, index, true);
                        Log(st.Source, "Intro: cambio al canal en el instante " + (pts / (double)Clock).ToString("0.000", CultureInfo.InvariantCulture)
                                       + " s (intro hasta " + (end / (double)Clock).ToString("0.000", CultureInfo.InvariantCulture) + " s)");
                        return;
                    }

                    // los trozos del canal anteriores al final de la intro ya no se van a publicar
                    lock (st)
                        while (st.Held.Count > 2 && st.Done.Contains(st.Held[0]))
                        {
                            try { File.Delete(Reflect.Get(st.Held[0], "Path") as string); } catch { }
                            st.Done.Remove(st.Held[0]);
                            st.Held.RemoveAt(0);
                        }

                    var latest = LatestPts(st);
                    if (latest != null && latest != lastLatest) { lastLatest = latest; lastProgress = DateTime.UtcNow; }
                    if ((DateTime.UtcNow - lastProgress).TotalSeconds > 60)
                    {
                        Log(st.Source, "Intro: el canal no envía nada desde hace 60 s, se cierra");
                        Close(st);
                        return;
                    }
                    // más intro solo cuando el canal ya ha llegado al final de la emitida
                    if (latest != null && Mod(latest.Value - end) < Wrap / 2)
                    {
                        var segment = (object[])TakeEncoded(st, 0);
                        if (segment != null)
                        {
                            PublishIntro(st, Emit(st, (byte[])segment[0], null, null), (long)segment[2]);
                            end = Mod(end + (long)segment[2]);
                        }
                        else if (st.EncoderDone)
                        {
                            Log(st.Source, "Intro: ffmpeg terminó (" + st.EncoderError.Trim() + "), se pasa al canal");
                            int last;
                            lock (st) last = Math.Max(0, st.Held.Count - 1);
                            Switch(st, last, true);
                            return;
                        }
                    }
                    Thread.Sleep(100);
                }
            }
            catch (Exception e)
            {
                Log(st.Source, "Intro: error, se pasa al canal: " + e);
                try { if (!st.Switched) Switch(st, 0, true); } catch { }
            }
            finally
            {
                StopEncoder(st);
            }
        }

        /// Primer trozo retenido del canal cuyo fotograma clave inicial va en `end` o después:
        /// {índice, PTS, DTS, primer PTS de cada pista de audio}
        static object[] FindStart(State st, long end)
        {
            var files = new List<object>();
            lock (st) files.AddRange(st.Held);
            for (var i = 0; i < files.Count; i++)
            {
                var path = Reflect.Get(files[i], "Path") as string;
                var data = ReadShared(path, 0, 512 * 1024);
                for (var p = 0; p + 188 <= data.Length; p += 188)
                {
                    if (data[p] != 0x47 || PidOf(data, p) != st.VideoPid || (data[p + 1] & 0x40) == 0) continue;
                    var pts = PesPts(data, p, false);
                    if (pts == null) break;
                    var dts = PesPts(data, p, true) ?? pts.Value;
                    if (Mod(pts.Value - end) >= Wrap / 2) break;
                    var audio = new Dictionary<int, long>();
                    for (var q = 0; q + 188 <= data.Length; q += 188)
                    {
                        var pid = PidOf(data, q);
                        if (data[q] != 0x47 || pid == st.VideoPid || !st.MediaPids.Contains(pid) || (data[q + 1] & 0x40) == 0
                            || audio.ContainsKey(pid)) continue;
                        var a = PesPts(data, q, false);
                        if (a != null) audio[pid] = a.Value;
                    }
                    return new object[] { i, pts.Value, dts, audio };
                }
            }
            return null;
        }

        /// Mayor PTS de vídeo recibido del canal (final del último trozo retenido)
        static long? LatestPts(State st)
        {
            string path;
            lock (st) path = st.Held.Count == 0 ? null : Reflect.Get(st.Held[st.Held.Count - 1], "Path") as string;
            if (path == null) return null;
            long length;
            try { length = new FileInfo(path).Length; } catch { return null; }
            var offset = Math.Max(0, length - 1024 * 1024);
            offset -= offset % 188;
            var data = ReadShared(path, offset, 1024 * 1024);
            long? latest = null;
            for (var p = 0; p + 188 <= data.Length; p += 188)
            {
                if (data[p] != 0x47 || PidOf(data, p) != st.VideoPid || (data[p + 1] & 0x40) == 0) continue;
                var pts = PesPts(data, p, false);
                if (pts != null && (latest == null || Mod(pts.Value - latest.Value) < Wrap / 2)) latest = pts;
            }
            return latest;
        }

        /// Un trozo de la intro listo para el buffer: tabla de pistas del canal, marcas de tiempo desplazadas y contadores
        /// de continuidad seguidos. Con corte, se descarta el vídeo desde `cutDts` (orden de decodificación) y cada audio
        /// desde donde empieza el del canal (`audioStarts`, o `cutPts`).
        static byte[] Emit(State st, byte[] input, long? cutDts, long? cutPts, Dictionary<int, long> audioStarts = null)
        {
            var output = new MemoryStream(input.Length + 188 * 16);
            WriteTables(st, output);
            var dropping = new Dictionary<int, bool>();
            var packet = new byte[188];
            for (var p = 0; p + 188 <= input.Length; p += 188)
            {
                if (input[p] != 0x47) continue;
                var pid = PidOf(input, p);
                if (pid == 0) { WriteTables(st, output); continue; }
                if (!st.MediaPids.Contains(pid)) continue;     // tabla y SDT de la intro, relleno
                Buffer.BlockCopy(input, p, packet, 0, 188);
                if ((packet[1] & 0x40) != 0)
                {
                    ShiftPes(packet, st.Shift);
                    if (cutPts != null)
                    {
                        var pts = PesPts(packet, 0, false);
                        var dts = PesPts(packet, 0, true) ?? pts;
                        if (pts != null)
                            dropping[pid] = pid == st.VideoPid
                                ? Mod(dts.Value - cutDts.Value) < Wrap / 2
                                : Mod(pts.Value - (audioStarts != null && audioStarts.TryGetValue(pid, out var a) ? a : cutPts.Value)) < Wrap / 2;
                    }
                }
                if (dropping.TryGetValue(pid, out var drop) && drop) continue;
                var pcr = ShiftPcr(packet, st.Shift);
                WritePacket(st, output, packet);
                if (pcr != null && pid != st.PcrPid) WritePcr(st, output, st.PcrPid, pcr.Value);
            }
            return output.ToArray();
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
        static void WritePcr(State st, MemoryStream output, int pid, long pcr)
        {
            var b = new byte[188];
            for (var i = 0; i < 188; i++) b[i] = 0xFF;
            b[0] = 0x47;
            b[1] = (byte)((pid >> 8) & 0x1F);
            b[2] = (byte)pid;
            b[3] = 0x20;
            b[4] = 183;
            b[5] = 0x10;
            var bas = pcr;
            b[6] = (byte)(bas >> 25);
            b[7] = (byte)(bas >> 17);
            b[8] = (byte)(bas >> 9);
            b[9] = (byte)(bas >> 1);
            b[10] = (byte)(((bas & 1) << 7) | 0x7E);
            b[11] = 0;
            WritePacket(st, output, b);
        }

        static void PublishIntro(State st, byte[] data, long duration)
        {
            lock (st)
            {
                var seq = st.NextSeq++;
                var path = Path.Combine(st.Dir, "intro_" + seq.ToString("0000000", CultureInfo.InvariantCulture) + ".ts");
                File.WriteAllBytes(path, data);
                var info = Activator.CreateInstance(st.InfoType);
                Reflect.Set(info, "Path", path);
                Reflect.Set(info, "FileName", Path.GetFileName(path));
                Reflect.Set(info, "SequenceNumber", seq);
                Reflect.Set(info, "Duration", TimeSpan.FromSeconds(duration / (double)Clock));
                Reflect.Set(info, "IsClosed", true);
                Reflect.Set(info, "WallClockStartTime", (DateTimeOffset?)DateTimeOffset.UtcNow);
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

        /// Publica los trozos retenidos del canal desde `from` (los anteriores se borran) y deja pasar los siguientes
        static void Switch(State st, int from, bool discontinuity)
        {
            lock (st)
            {
                if (st.Switched) return;
                st.Replaying = true;
                try
                {
                    object last = null;
                    for (var i = 0; i < st.Held.Count; i++)
                    {
                        var file = st.Held[i];
                        if (i < from)
                        {
                            try { File.Delete(Reflect.Get(file, "Path") as string); } catch { }
                            continue;
                        }
                        Reflect.Set(file, "SequenceNumber", st.NextSeq++);
                        if (discontinuity && i == from) Reflect.Set(file, "IsDiscontinuity", true);
                        Reflect.Call(st.Source, "OnSegmentStarted", file);
                        if (st.Done.Contains(file)) Reflect.Call(st.Source, "OnSegmentCompleted", file);
                        last = file;
                    }
                    if (last == null || st.Done.Contains(last)) Reflect.Set(st.Source, "CurrentWritingFile", null);
                    st.LastSeq = st.NextSeq - 1;
                    st.Switched = true;
                    st.Held.Clear();
                    st.Done.Clear();
                }
                finally { st.Replaying = false; }
            }
        }

        static void Close(State st)
        {
            try { Reflect.Call(st.Source, "EmbyNoShare_CloseIdle"); }
            catch
            {
                try { ((CancellationTokenSource)Reflect.Get(st.Source, "LiveStreamCancellationTokenSource")).Cancel(); } catch { }
            }
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
