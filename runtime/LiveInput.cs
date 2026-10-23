// Emby.LiveTV: que un lector del buffer de un canal empiece en un trozo concreto (lo pide LiveRestart, en
// Emby.Server.MediaEncoding, al relanzar una conversión). Ambas DLL se hablan por AppDomain.SetData, con la clave
// Key + UniqueId del LiveStream y el número de secuencia del trozo; se usa una sola vez.
using System;
using System.Collections;

namespace EmbyNoShare.Runtime
{
    public static class LiveInput
    {
        public const string Key = "EmbyNoShare.StartSequence.";
        public const string FormatKey = "EmbyNoShare.FormatSeq.";

        /// LiveStream.GetSegment(DateTimeOffset): el trozo pedido, o null para seguir con lo de Emby
        public static object PickSegment(object segments, object liveStream)
        {
            try
            {
                var uniqueId = Reflect.Get(liveStream, "UniqueId") as string;
                if (string.IsNullOrEmpty(uniqueId)) return null;
                var key = Key + uniqueId;
                if (!(segments is IList list)) return null;
                if (AppDomain.CurrentDomain.GetData(key) is int sequence)
                {
                    AppDomain.CurrentDomain.SetData(key, null);
                    foreach (var segment in list)
                        if (Reflect.Get(segment, "SequenceNumber") is int n && n == sequence) return segment;
                    return null;
                }
                // tras un cambio de formato del canal (LiveIntro), ningún lector nuevo empieza antes de él: Emby empieza
                // en el trozo de hace 10 s, que puede ser del formato anterior (la intro)
                if (AppDomain.CurrentDomain.GetData(FormatKey + uniqueId) is int format)
                {
                    object chosen = null;
                    var limit = DateTimeOffset.UtcNow.AddSeconds(-10);
                    for (var i = list.Count - 1; i >= 0; i--)
                    {
                        if (!(Reflect.Get(list[i], "WallClockStartTime") is DateTimeOffset wall)) break;
                        chosen = list[i];
                        if (wall <= limit) break;
                    }
                    if (chosen != null && Reflect.Get(chosen, "SequenceNumber") is int c && c >= format) return null;
                    foreach (var segment in list)
                        if (Reflect.Get(segment, "SequenceNumber") is int n && n >= format) return segment;
                }
            }
            catch { }
            return null;
        }
    }
}
