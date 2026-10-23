// Relanzar la conversión HLS de TV en directo (Emby.Server.MediaEncoding) cuando su ffmpeg muere solo.
//
// Hoy, si el ffmpeg de /videos/<id>/live.m3u8 muere (p. ej. QuickSync no soporta un cambio de formato a mitad de
// la emisión), Emby marca la conversión como terminada pero la deja registrada: la lista HLS se queda congelada
// para siempre y el cliente se queda en negro. Aquí se relanza al momento, con el mismo estado de la reproducción:
//   - la nueva conversión lee el buffer del canal desde el trozo donde murió la anterior (LiveInput, en
//     Emby.LiveTV) y descarta lo ya convertido (-ss de salida), así que no se repite ni se salta nada;
//   - continúa la numeración de segmentos y conserva los ya hechos en la lista;
//   - la lista HLS lleva #EXT-X-DISCONTINUITY en el primer segmento nuevo, para que el cliente reinicie su
//     decodificador.
// EMBY_LIVE_RESTART=0 lo desactiva. A Emby se accede por reflexión (este código se copia dentro de su DLL).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace EmbyNoShare.Runtime
{
    public static class LiveRestart
    {
                const int MaxRestarts = 5;               // por reproducción, en la ventana de abajo
        static readonly TimeSpan Window = TimeSpan.FromMinutes(2);

        static readonly object Sync = new object();
        // estado de la reproducción (StreamState) -> datos del relanzamiento en curso (Pending)
        static readonly Dictionary<object, object> Pendings = new Dictionary<object, object>();
        // lista HLS (ruta del .m3u8 de la conversión) -> segmentos con discontinuidad delante
        static readonly Dictionary<string, List<int>> Discontinuities = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, List<DateTime>> Restarts = new Dictionary<string, List<DateTime>>(StringComparer.OrdinalIgnoreCase);

        sealed class Pending
        {
            public int Next;
            public double? Seek;
        }

        /// BaseStreamingService.OnFfMpegProcessExited: true si se encarga del relanzamiento (el original no sigue,
        /// porque desecharía el estado de la reproducción, que se reutiliza).
        public static bool OnExited(object service, object runner, object job, object state)
        {
            try
            {
                if (service == null || runner == null || job == null || state == null) return false;
                if (Environment.GetEnvironmentVariable("EMBY_LIVE_RESTART") == "0") return false;
                if (Reflect.Get(state, "LiveStream") == null) return false;
                var url = Reflect.Get(state, "RequestedUrl") as string ?? "";
                if (url.IndexOf("live.m3u8", StringComparison.OrdinalIgnoreCase) < 0) return false;
                var manager = Reflect.Get(service, "EncodingManager");
                var playSession = Reflect.Get(job, "PlaySessionId") as string;
                // Emby quita la conversión de su lista antes de pararla a propósito: si sigue, ha muerto sola
                if (manager == null || playSession == null || !ReferenceEquals(Reflect.Call(manager, "GetTranscodingJob", playSession), job)) return false;
                var exitCode = Reflect.Get(runner, "LastExitCode") as int?;
                if (exitCode == 0) return false;   // terminó normalmente (fin del directo)
                var liveStreamId = Reflect.Get(job, "LiveStreamId") as string;
                var sources = Reflect.Get(service, "MediaSourceManager");
                if (string.IsNullOrEmpty(liveStreamId) || sources == null || Reflect.Call(sources, "GetLiveStreamInfo", liveStreamId) == null) return false;
                var path = Reflect.Get(job, "Path") as string;
                if (path == null || !AllowRestart(path)) { Log(job, "Relanzar conversión: demasiados relanzamientos seguidos, se deja parada " + path); return false; }

                Log(job, "Relanzar conversión: ffmpeg terminó con código " + (exitCode?.ToString(CultureInfo.InvariantCulture) ?? "?") + ", se relanza " + path);
                Reflect.Call(job, "OnExited");
                try { Reflect.Call(runner, "Dispose"); } catch { }
                ThreadPool.QueueUserWorkItem(Restart, new[] { service, runner, job, state });
                return true;
            }
            catch (Exception e)
            {
                Log(job, "Relanzar conversión: error, no se relanza: " + e);
                return false;
            }
        }

        static bool AllowRestart(string path)
        {
            lock (Sync)
            {
                if (!Restarts.TryGetValue(path, out var list)) Restarts[path] = list = new List<DateTime>();
                var now = DateTime.UtcNow;
                list.RemoveAll(t => now - t > Window);
                if (list.Count >= MaxRestarts) return false;
                list.Add(now);
                return true;
            }
        }

        static void Restart(object arg)
        {
            var a = (object[])arg;
            object service = a[0], runner = a[1], job = a[2], state = a[3];
            try
            {
                var path = (string)Reflect.Get(job, "Path");
                var completed = (Array)Reflect.Call(job, "GetCompletedSegments");
                int next = 0;
                double end = 0;
                foreach (var item in completed)
                {
                    var info = Reflect.Get(item, "Value");
                    var index = (int)Reflect.Get(info, "Index");
                    if (index + 1 > next) { next = index + 1; end = (Reflect.Get(info, "EndSeconds") as double?) ?? end; }
                }
                var pending = new Pending { Next = next };
                if (next > 0) Position(service, runner, job, state, end, pending);
                lock (Sync)
                {
                    Pendings[state] = pending;
                    if (next > 0)
                    {
                        if (!Discontinuities.TryGetValue(path, out var list)) Discontinuities[path] = list = new List<int>();
                        if (!list.Contains(next)) list.Add(next);
                        if (Discontinuities.Count > 500) Discontinuities.Clear();
                    }
                }
                Reflect.Set(state, "Id", Guid.NewGuid());
                try { Reflect.Call(Reflect.Get(state, "TranscodeLog"), "Clear"); } catch { }

                var task = (Task)Reflect.Call(service, "StartFfMpeg", state, path, CancellationToken.None, false);
                task.GetAwaiter().GetResult();
                var newJob = Reflect.Get(task, "Result");
                if (completed.Length > 0) Reflect.Call(newJob, "AddCompletedSegments", completed);
                Reflect.Set(newJob, "StartingSegmentIndex", (int?)next);
                RemoveJob(Reflect.Get(service, "EncodingManager"), job);
                Log(job, "Relanzar conversión: relanzada desde el segmento " + next.ToString(CultureInfo.InvariantCulture)
                         + (pending.Seek.HasValue ? ", saltando " + pending.Seek.Value.ToString("0.000", CultureInfo.InvariantCulture) + " s ya convertidos" : ""));
            }
            catch (Exception e)
            {
                Log(job, "Relanzar conversión: no se pudo relanzar: " + e);
                try { Reflect.Call(state, "Dispose", false, true); } catch { }
            }
            finally
            {
                lock (Sync) Pendings.Remove(state);
            }
        }

        /// Dónde debe empezar la conversión nueva: el trozo del buffer del canal que contiene el final de lo ya
        /// convertido (LiveInput lo hace leer desde ahí) y cuántos segundos de ese trozo descartar.
        static void Position(object service, object runner, object job, object state, double end, Pending pending)
        {
            var liveStream = Reflect.Get(state, "LiveStream");
            var logPath = Reflect.Get(runner, "LogFilePath") as string;
            var start = InputStart(logPath);
            if (start == null) return;
            var target = start.Value + end;            // instante (en la marca de tiempo de la fuente) donde seguir
            if (!(Reflect.Call(liveStream, "GetSegments") is IList segments)) return;
            object best = null;
            double bestStart = 0;
            var files = new List<string>();
            foreach (var segment in segments)
            {
                var file = Reflect.Get(segment, "Path") as string;
                var pts = FirstPts(file);
                if (pts == null) continue;
                files.Add(file);
                if (pts.Value <= target + 0.001 && (best == null || pts.Value >= bestStart)) { best = segment; bestStart = pts.Value; }
            }
            if (best == null) return;
            // Copiando, ffmpeg recorta por el instante de decodificación (DTS), que en el fotograma clave donde hay que
            // seguir va antes que su imagen (fotogramas B, entrelazado): con el de imagen se descartaba ese fotograma y
            // se saltaba un GOP entero. Transcodificando recorta por la imagen (PTS) y vale el instante de la imagen.
            var cut = target - 0.05;
            if (string.Equals(Reflect.Get(state, "OutputVideoCodec") as string, "copy", StringComparison.OrdinalIgnoreCase))
            {
                // El fotograma suele ser el primero del trozo siguiente, que puede estar aún escribiéndose (GetSegments
                // solo da los terminados): se miran también los dos siguientes por su nombre y se espera un poco
                var bestPath = Reflect.Get(best, "Path") as string;
                var candidates = new List<string>();
                var at = files.IndexOf(bestPath);
                for (var i = Math.Max(0, at); i < files.Count && i <= at + 2; i++) candidates.Add(files[i]);
                if (Reflect.Get(best, "SequenceNumber") is int seq && bestPath != null)
                    for (var n = 1; n <= 2; n++)
                    {
                        var next = Path.Combine(Path.GetDirectoryName(bestPath) ?? "", (seq + n).ToString("0000000", CultureInfo.InvariantCulture) + Path.GetExtension(bestPath));
                        if (!candidates.Contains(next)) candidates.Add(next);
                    }
                double? dts = null;
                var deadline = DateTime.UtcNow.AddSeconds(4);
                while (dts == null)
                {
                    foreach (var file in candidates) { dts = KeyframeDts(file, target); if (dts != null) break; }
                    if (dts != null || DateTime.UtcNow > deadline) break;
                    Thread.Sleep(100);
                }
                // si no aparece, mejor repetir medio segundo que perder un GOP entero
                cut = dts != null ? dts.Value - 0.001 : target - 0.5;
            }
            var uniqueId = Reflect.Get(liveStream, "UniqueId") as string;
            if (string.IsNullOrEmpty(uniqueId)) return;
            AppDomain.CurrentDomain.SetData(LiveInput.Key + uniqueId, Reflect.Get(best, "SequenceNumber"));
            pending.Seek = Math.Max(0, cut - bestStart);
        }

        /// "start: 3.021333" de la línea Duration del log de ffmpeg: instante inicial de su entrada
        static double? InputStart(string logPath)
        {
            if (string.IsNullOrEmpty(logPath) || !File.Exists(logPath)) return null;
            using (var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
            {
                for (var i = 0; i < 400; i++)
                {
                    var line = reader.ReadLine();
                    if (line == null) break;
                    var m = Regex.Match(line, @"Duration:.*?start:\s*(-?\d+(?:\.\d+)?)");
                    if (m.Success) return double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                }
            }
            return null;
        }

        /// DTS (o PTS si no lleva) del PES de vídeo cuya imagen (PTS) cae en `pts` (±30 ms), en segundos
        static double? KeyframeDts(string file, double pts)
        {
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) return null;
            byte[] data;
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                data = new byte[Math.Min(stream.Length, 64L * 1024 * 1024)];
                var read = 0;
                while (read < data.Length) { var n = stream.Read(data, read, data.Length - read); if (n <= 0) break; read += n; }
            }
            for (var p = 0; p + 188 <= data.Length; p += 188)
            {
                if (data[p] != 0x47 || (data[p + 1] & 0x40) == 0) continue;
                var afc = (data[p + 3] >> 4) & 3;
                var o = p + 4;
                if ((afc & 2) != 0) o += 1 + data[p + 4];
                if ((afc & 1) == 0 || o + 19 > p + 188) continue;
                if (data[o] != 0 || data[o + 1] != 0 || data[o + 2] != 1 || (data[o + 3] & 0xF0) != 0xE0) continue;
                var flags = data[o + 7] >> 6;
                if ((flags & 2) == 0) continue;
                var framePts = ReadTs(data, o + 9) / 90000.0;
                if (Math.Abs(framePts - pts) > 0.03) continue;
                return (flags == 3 ? ReadTs(data, o + 14) : ReadTs(data, o + 9)) / 90000.0;
            }
            return null;
        }

        static long ReadTs(byte[] b, int o) =>
            ((long)(b[o] >> 1) & 7) << 30 | (long)b[o + 1] << 22 | (long)(b[o + 2] >> 1) << 15 | (long)b[o + 3] << 7 | (long)(b[o + 4] >> 1);

        /// Menor PTS de los primeros PES de un trozo MPEG-TS del buffer, en segundos
        internal static double? FirstPts(string file)
        {
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) return null;
            var buffer = new byte[188 * 2000];
            int read;
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                read = stream.Read(buffer, 0, buffer.Length);
            long? min = null;
            var seen = new HashSet<int>();
            for (var p = 0; p + 188 <= read; p += 188)
            {
                if (buffer[p] != 0x47 || (buffer[p + 1] & 0x40) == 0) continue;
                var pid = ((buffer[p + 1] & 0x1F) << 8) | buffer[p + 2];
                if (!seen.Add(pid)) continue;
                var afc = (buffer[p + 3] >> 4) & 3;
                var o = p + 4;
                if ((afc & 2) != 0) o += 1 + buffer[p + 4];
                if ((afc & 1) == 0 || o + 14 > p + 188) continue;
                if (buffer[o] != 0 || buffer[o + 1] != 0 || buffer[o + 2] != 1 || (buffer[o + 7] & 0x80) == 0) continue;
                long pts = ((long)(buffer[o + 9] >> 1) & 7) << 30 | (long)buffer[o + 10] << 22 | (long)(buffer[o + 11] >> 1) << 15
                           | (long)buffer[o + 12] << 7 | (long)(buffer[o + 13] >> 1);
                if (min == null || pts < min) min = pts;
            }
            return min / 90000.0;
        }

        static void RemoveJob(object manager, object job)
        {
            var field = manager.GetType().GetField("_activeTranscodingJobs", BindingFlags.Instance | BindingFlags.NonPublic);
            if (!(field?.GetValue(manager) is IList list)) return;
            lock (list) list.Remove(job);
        }

        /// BaseHlsService.GetStartNumber: en un relanzamiento, la numeración sigue donde iba
        public static int StartNumber(int original, object state)
        {
            lock (Sync) return state != null && Pendings.TryGetValue(state, out var p) ? ((Pending)p).Next : original;
        }

        /// VideoHlsService.GetCommandLineArguments: en un relanzamiento, descartar lo ya convertido (-ss de salida)
        public static object AdjustCommand(object command, object state)
        {
            try
            {
                Pending pending;
                lock (Sync) pending = state != null && Pendings.TryGetValue(state, out var p) ? (Pending)p : null;
                if (pending?.Seek == null || command == null) return command;
                var options = Reflect.Get(Reflect.Get(command, "Output0"), "Options");
                Reflect.Set(options, "ss", (TimeSpan?)TimeSpan.FromSeconds(pending.Seek.Value));
            }
            catch { }
            return command;
        }

        /// BaseHlsService.GetLivePlaylistText: #EXT-X-DISCONTINUITY delante del primer segmento de cada relanzamiento
        public static string Playlist(string text, object job)
        {
            try
            {
                var path = job == null ? null : Reflect.Get(job, "Path") as string;
                List<int> marks;
                lock (Sync)
                {
                    if (path == null || text == null || !Discontinuities.TryGetValue(path, out var list) || list.Count == 0) return text;
                    marks = new List<int>(list);
                }
                var lines = text.Replace("\r\n", "\n").Split('\n');
                var output = new List<string>(lines.Length + 4);
                int? first = null;
                var extinf = -1;
                foreach (var line in lines)
                {
                    if (line.StartsWith("#EXTINF", StringComparison.Ordinal)) { extinf = output.Count; output.Add(line); continue; }
                    if (line.Length > 0 && line[0] != '#')
                    {
                        var m = Regex.Match(line, @"_(\d+)\.[A-Za-z0-9]+(\?|$)");
                        if (m.Success)
                        {
                            var index = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                            if (first == null) first = index;
                            if (marks.Contains(index) && index != first && extinf >= 0) output.Insert(extinf, "#EXT-X-DISCONTINUITY");
                        }
                        extinf = -1;
                    }
                    output.Add(line);
                }
                if (first != null)
                {
                    // discontinuidades que ya salieron de la ventana de la lista (RFC 8216, 4.3.3.3)
                    var before = 0;
                    foreach (var mark in marks) if (mark <= first.Value) before++;
                    if (before > 0)
                    {
                        var at = output.FindIndex(l => l.StartsWith("#EXT-X-MEDIA-SEQUENCE", StringComparison.Ordinal));
                        if (at < 0) at = output.FindIndex(l => l.StartsWith("#EXT-X-TARGETDURATION", StringComparison.Ordinal));
                        output.Insert(at + 1, "#EXT-X-DISCONTINUITY-SEQUENCE:" + before.ToString(CultureInfo.InvariantCulture));
                    }
                }
                return string.Join("\n", output);
            }
            catch
            {
                return text;
            }
        }

        static void Log(object job, string message) => Reflect.Log(job, message);
    }
}
