#!/usr/bin/env python3
"""Compara los subtítulos de texto de un canal por tres vías a la vez, durante unos minutos:
  fuente   la lista maestra HLS original (sus pistas WebVTT)
  dispa    la salida HLS de Dispatcharr con Intro Relay (pista irelay=t<n>)
  emby     Emby en directo, como un iPhone (live_subtitles.m3u8)
Para cada frase dice por qué vías llega y cuándo (segundos desde el inicio). No muestra claves ni tokens.

Uso: python3 compara_subs.py --fuente-fichero F --dispatcharr-canal UUID --emby-canal "Comedy" [--minutos 5]
  F: fichero con la URL de la lista maestra de la fuente (lleva credenciales: no se imprime)
  La clave de Emby se lee de /root/emby-trabajo/.emby_api_key (o --emby-clave-fichero)."""
import argparse, json, re, threading, time, urllib.parse, urllib.request, uuid

T0 = time.time()
LOCK = threading.Lock()
SEEN = {}            # texto normalizado -> {via: segundos}
TEXT = {}            # texto normalizado -> texto
ERR = {}
UA = "AppleCoreMedia/1.0.0.21A329 (iPhone; U; CPU OS 17_0 like Mac OS X; es_es)"


def norm(t):
    t = re.sub(r"<[^>]*>|\{[^}]*\}", "", t or "").replace("&amp;", "&").replace("&lt;", "<").replace("&gt;", ">")
    t = re.sub(r"^\s*-\s*", "", t, flags=re.M)
    return " ".join(t.split()).strip().lower()


def get(url, timeout=30, headers=None):
    req = urllib.request.Request(url, headers={"User-Agent": UA, **(headers or {})})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return r.read().decode("utf-8", "replace"), r.geturl()


def cues(vtt):
    out = []
    for block in vtt.replace("\r", "").split("\n\n"):
        lines = block.strip("\n").split("\n")
        for i, l in enumerate(lines):
            if "-->" in l:
                text = norm("\n".join(lines[i + 1:]))
                if text:
                    out.append((text, " / ".join(lines[i + 1:])))
                break
    return out


def note(via, vtt):
    now = round(time.time() - T0, 1)
    with LOCK:
        for n, raw in cues(vtt):
            SEEN.setdefault(n, {}).setdefault(via, now)
            TEXT.setdefault(n, raw)


def follow(via, playlist_url, end, headers=None, also=None):
    """Lee una lista de subtítulos en directo (y opcionalmente otra de vídeo, para mantener viva la sesión)."""
    done = set()
    while time.time() < end:
        try:
            text, base = get(playlist_url, headers=headers)
            for line in text.splitlines():
                line = line.strip()
                if not line or line.startswith("#"):
                    continue
                key = line.split("?")[0]
                if key in done:
                    continue
                done.add(key)
                body, _ = get(urllib.parse.urljoin(base, line), headers=headers)
                note(via, body)
            if also:
                vtext, vbase = get(also, headers=headers)
                segs = [l for l in vtext.splitlines() if l and not l.startswith("#")]
                if segs:                     # el último segmento de vídeo, para que la conversión no se pare
                    get(urllib.parse.urljoin(vbase, segs[-1]), timeout=60, headers=headers)
        except Exception as e:
            ERR.setdefault(via, []).append(f"{round(time.time() - T0)}s {type(e).__name__}: {str(e)[:120]}")
        time.sleep(2)


def master_subs(master_text, base):
    """URLs de las pistas de subtítulos de una lista maestra, con su idioma, y la primera variante de vídeo."""
    subs = [(m.group(2) or "?", urllib.parse.urljoin(base, m.group(1)))
            for m in re.finditer(r'#EXT-X-MEDIA:[^\n]*TYPE=SUBTITLES[^\n]*?URI="([^"]*)"(?:[^\n]*LANGUAGE="([^"]*)")?', master_text)]
    for m in re.finditer(r'#EXT-X-MEDIA:[^\n]*TYPE=SUBTITLES[^\n]*?LANGUAGE="([^"]*)"[^\n]*?URI="([^"]*)"', master_text):
        u = urllib.parse.urljoin(base, m.group(2))
        subs = [(m.group(1) if s[1] == u else s[0], s[1]) for s in subs]
    video = next((urllib.parse.urljoin(base, l.strip()) for l in master_text.splitlines() if l.strip() and not l.startswith("#")), None)
    return subs, video


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--fuente-fichero")
    ap.add_argument("--dispatcharr-canal")
    ap.add_argument("--dispatcharr", default="http://127.0.0.1:9191")
    ap.add_argument("--emby", default="http://127.0.0.1:8096")
    ap.add_argument("--emby-clave-fichero", default="/root/emby-trabajo/.emby_api_key")
    ap.add_argument("--emby-canal")
    ap.add_argument("--idioma", default="spa")
    ap.add_argument("--minutos", type=float, default=5)
    a = ap.parse_args()
    end = T0 + a.minutos * 60 + 20
    threads, emby_stop = [], None

    def start(via, url, **kw):
        t = threading.Thread(target=follow, args=(via, url, end), kwargs=kw, daemon=True)
        t.start()
        threads.append(t)

    def pick(subs, label):
        if not subs:
            print(f"[{label}] sin pistas de subtítulos en su lista maestra")
            return None
        chosen = next((s for s in subs if s[0] == a.idioma), subs[0])
        print(f"[{label}] pistas: {[s[0] for s in subs]} -> uso {chosen[0]}")
        return chosen[1]

    if a.fuente_fichero:
        try:
            src = open(a.fuente_fichero).read().strip()
            text, base = get(src)
            subs, _ = master_subs(text, base)
            u = pick(subs, "fuente")
            if u:
                start("fuente", u)
        except Exception as e:
            print(f"[fuente] error: {type(e).__name__}")
    if a.dispatcharr_canal:
        try:
            text, base = get(f"{a.dispatcharr}/proxy/ts/stream/{a.dispatcharr_canal}", timeout=80)
            subs, video = master_subs(text, base)
            u = pick(subs, "dispa")
            if u:
                start("dispa", u, also=video)
        except Exception as e:
            print(f"[dispa] error: {type(e).__name__}: {str(e)[:100]}")
    if a.emby_canal:
        try:
            key = open(a.emby_clave_fichero).read().strip()
            H = {"X-Emby-Token": key}
            users = json.loads(get(f"{a.emby}/Users", headers=H)[0])
            uid = next(u["Id"] for u in users if u.get("Policy", {}).get("IsAdministrator")) if users else None
            chans = json.loads(get(f"{a.emby}/LiveTv/Channels?UserId={uid}&Limit=2000", headers=H)[0])["Items"]
            ch = next(c for c in chans if a.emby_canal.lower() in c["Name"].lower())
            print(f"[emby] canal: {ch['Name']}")
            dev = "comparador-" + uuid.uuid4().hex[:8]
            profile = {"MaxStreamingBitrate": 40000000, "MaxStaticBitrate": 100000000,
                       "DirectPlayProfiles": [{"Container": "mp4,m4v,mov", "Type": "Video", "VideoCodec": "h264,hevc", "AudioCodec": "aac,ac3,eac3"}],
                       "TranscodingProfiles": [{"Container": "ts", "Type": "Video", "AudioCodec": "aac,ac3,eac3", "VideoCodec": "h264", "Context": "Streaming",
                                                "Protocol": "hls", "MaxAudioChannels": "6", "MinSegments": "1", "BreakOnNonKeyFrames": True, "ManifestSubtitles": "vtt"}],
                       "ContainerProfiles": [], "CodecProfiles": [],
                       "SubtitleProfiles": [{"Format": "vtt", "Method": "Hls"}, {"Format": "vtt", "Method": "External"}]}
            hdr = {**H, "Content-Type": "application/json", "X-Emby-Device-Id": dev, "X-Emby-Device-Name": "comparador", "X-Emby-Client": "comparador"}
            q = f"{a.emby}/Items/{ch['Id']}/PlaybackInfo?UserId={uid}&StartTimeTicks=0&IsPlayback=true&AutoOpenLiveStream=true&MaxStreamingBitrate=40000000"
            req = urllib.request.Request(q, method="POST", data=json.dumps({"DeviceProfile": profile}).encode(), headers=hdr)
            pi = json.loads(urllib.request.urlopen(req, timeout=120).read())
            ms = pi["MediaSources"][0]
            st = [s for s in ms["MediaStreams"] if s["Type"] == "Subtitle"]
            print(f"[emby] subtítulos del canal: {[(s['Index'], s.get('Codec'), s.get('Language')) for s in st]}")
            sub = next((s for s in st if s.get("Language") == a.idioma), st[0] if st else None)
            if sub is None:
                print("[emby] el canal no tiene subtítulos")
            else:
                req = urllib.request.Request(q + f"&SubtitleStreamIndex={sub['Index']}", method="POST",
                                             data=json.dumps({"DeviceProfile": profile}).encode(), headers=hdr)
                pi = json.loads(urllib.request.urlopen(req, timeout=120).read())
                ms = pi["MediaSources"][0]
                turl = ms.get("TranscodingUrl")
                if not turl:
                    print("[emby] sin TranscodingUrl")
                else:
                    master, mbase = get(f"{a.emby}{turl}", headers=H)
                    subs, video = master_subs(master, mbase)
                    u = pick(subs, "emby")
                    if u:
                        start("emby", u, also=video)
                    emby_stop = (pi.get("PlaySessionId"), dev, H)
        except Exception as e:
            print(f"[emby] error: {type(e).__name__}: {str(e)[:100]}")

    for t in threads:
        t.join()
    if emby_stop:
        try:
            ps, dev, H = emby_stop
            urllib.request.urlopen(urllib.request.Request(f"{a.emby}/Videos/ActiveEncodings?DeviceId={dev}&PlaySessionId={ps}",
                                                          method="DELETE", headers=H), timeout=20)
        except Exception:
            pass

    vias = ["fuente", "dispa", "emby"]
    print("\n== frases (segundo en que llegan por cada vía; '-' = no llega)")
    order = sorted(SEEN, key=lambda n: min(SEEN[n].values()))
    for n in order:
        cols = "  ".join(f"{v}={SEEN[n][v]:6.1f}" if v in SEEN[n] else f"{v}=     -" for v in vias)
        print(f"{cols}  {TEXT[n][:70]}")
    print("\n== resumen")
    for v in vias:
        print(f"{v}: {sum(1 for n in SEEN if v in SEEN[n])} frases")
    # Las que pierde cada vía respecto a la anterior, dentro del tramo en que las dos estaban leyendo
    for a_, b_ in (("fuente", "dispa"), ("dispa", "emby"), ("fuente", "emby")):
        both = [n for n in SEEN if a_ in SEEN[n] or b_ in SEEN[n]]
        ta = [SEEN[n][a_] for n in SEEN if a_ in SEEN[n]]
        tb = [SEEN[n][b_] for n in SEEN if b_ in SEEN[n]]
        if not ta or not tb:
            continue
        lo, hi = max(min(ta), min(tb)) + 15, min(max(ta), max(tb)) - 15
        lost = [n for n in both if a_ in SEEN[n] and b_ not in SEEN[n] and lo <= SEEN[n][a_] <= hi]
        extra = [n for n in both if b_ in SEEN[n] and a_ not in SEEN[n] and lo <= SEEN[n][b_] <= hi]
        delays = sorted(SEEN[n][b_] - SEEN[n][a_] for n in SEEN if a_ in SEEN[n] and b_ in SEEN[n])
        med = delays[len(delays) // 2] if delays else None
        print(f"{a_} -> {b_}: tramo {lo:.0f}-{hi:.0f}s, perdidas {len(lost)}, nuevas {len(extra)}, retraso mediano {med}")
        for n in lost[:15]:
            print(f"    perdida: {TEXT[n][:80]}")
    for v, errs in ERR.items():
        print(f"errores {v}: {len(errs)} (primero: {errs[0]})")


if __name__ == "__main__":
    main()
