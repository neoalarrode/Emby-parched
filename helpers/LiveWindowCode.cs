using System;

namespace EmbyNoShare
{
    /// <summary>Ventana de la lista HLS de las conversiones en directo (parche live-hls-window, Emby 4.11.0.19 y
    /// posteriores: Emby ya numera la lista desde el primer segmento que da, solo hay que quedarse con los últimos).</summary>
    public static class LiveWindowCode
    {
        /// <summary>Cuántos segmentos dejar en la lista: EMBY_LIVE_HLS_WINDOW_MINUTES (por defecto 30) en segmentos de
        /// segmentLength s, como mínimo 10; int.MaxValue (todos) con 0 o un valor no válido.</summary>
        public static int Keep(int segmentLength)
        {
            string s = Environment.GetEnvironmentVariable("EMBY_LIVE_HLS_WINDOW_MINUTES");
            if (s == null)
                s = "30";
            int minutes;
            if (!int.TryParse(s, out minutes) || minutes <= 0 || segmentLength <= 0)
                return int.MaxValue;
            int keep = minutes * 60 / segmentLength;
            return keep < 10 ? 10 : keep;
        }
    }
}
