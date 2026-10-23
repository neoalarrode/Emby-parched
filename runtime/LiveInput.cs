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

        /// LiveStream.GetSegment(DateTimeOffset): el trozo pedido, o null para seguir con lo de Emby
        public static object PickSegment(object segments, object liveStream)
        {
            try
            {
                var uniqueId = Reflect.Get(liveStream, "UniqueId") as string;
                if (string.IsNullOrEmpty(uniqueId)) return null;
                var key = Key + uniqueId;
                if (!(AppDomain.CurrentDomain.GetData(key) is int sequence)) return null;
                AppDomain.CurrentDomain.SetData(key, null);
                if (!(segments is IList list)) return null;
                foreach (var segment in list)
                    if (Reflect.Get(segment, "SequenceNumber") is int n && n == sequence) return segment;
            }
            catch { }
            return null;
        }
    }
}
