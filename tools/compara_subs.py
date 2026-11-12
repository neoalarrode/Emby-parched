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
SPAN = {}            # texto normalizado -> {via: [inicio, fin]} en el reloj MPEG-TS de esa vía (s)
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


def secs(stamp):
    parts = stamp.strip().split(":")
    return sum(float(x) * 60 ** k for k, x in enumerate(reversed(parts)))


def cues(vtt):
    """(texto normalizado, texto, inicio, fin) de cada frase, en segundos del reloj MPEG-TS (X-TIMESTAMP-MAP)."""
    vtt = vtt.replace("\r", "")
    base = 0.0
    m = re.search(r"X-TIMESTAMP-MAP=([^\n]*)", vtt)
    if m:
        f = dict(x.split(":", 1) for x in m.group(1).replace(" ", "").split(",") if ":" in x)
        try:
            base = int(f.get("MPEGTS", "0")) / 90000 - secs(f.get("LOCAL", "0"))
        except ValueError:
            base = 0.0
    out = []
    for block in vtt.split("\n\n"):
        lines = block.strip("\n").split("\n")
        for i, l in enumerate(lines):
            if "-->" in l:
                a, b = l.split("-->")[:2]
                text = norm("\n".join(lines[i + 1:]).replace("\u200b", ""))
                if text:
                    out.append((text, " / ".join(lines[i + 1:]), base + secs(a), base + secs(b.split()[0])))
                break
    return out


def note(via, vtt):
    now = round(time.time() - T0, 1)
    with LOCK:
        for n, raw, a, b in cues(vtt):
            SEEN.setdefault(n, {}).setdefault(via, now)
            TEXT.setdefault(n, raw)
            sp = SPAN.setdefault(n, {}).get(via)
            if sp is None:
                SPAN[n][via] = [a, b]
            elif abs(a - sp[0]) < 30:          # la misma frase partida en varios segmentos: se unen
                sp[0], sp[1] = min(sp[0], a), max(sp[1], b)


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


DVB = []             # [inicio, fin] (s, PTS del TS) de cada subtítulo DVB mostrado


def follow_dvb(url, end):
    """Lee el TS del canal (salida del relevo por Dispatcharr) y apunta cuándo aparece y se borra cada subtítulo DVB
    (página con regiones = aparece; página sin regiones = se borra)."""
    r = urllib.request.urlopen(urllib.request.Request(url, headers={"User-Agent": "VLC/3.0.21 LibVLC/3.0.21"}), timeout=60)
    buf, pmt_pids, sub, pes, cur = b"", set(), None, {}, None
    def handle(data):
        nonlocal cur
        if len(data) < 14 or data[:3] != b"\x00\x00\x01" or not data[7] & 0x80:
            return
        b = data[9:14]
        pts = (((b[0] >> 1) & 7) << 30 | b[1] << 22 | (b[2] >> 1) << 15 | b[3] << 7 | b[4] >> 1) / 90000
        k = 9 + data[8] + 2                       # data_identifier, subtitle_stream_id
        regions = None
        while k + 6 <= len(data) and data[k] == 0x0F:
            seg_type, length = data[k + 1], (data[k + 4] << 8) | data[k + 5]
            if seg_type == 0x10:                  # page composition: 2 bytes (timeout, version/state) + 6 por región
                regions = max(0, (length - 2) // 6)
            k += 6 + length
        if regions is None:
            return
        with LOCK:
            if regions > 0:
                if cur is None:
                    cur = [pts, None]
                    DVB.append(cur)
            elif cur is not None:
                cur[1] = pts
                cur = None
    try:
        while time.time() < end:
            chunk = r.read(188 * 300)
            if not chunk:
                break
            buf += chunk
            n = len(buf) - len(buf) % 188
            for i in range(0, n, 188):
                p = buf[i:i + 188]
                if p[0] != 0x47:
                    continue
                pid = ((p[1] & 0x1F) << 8) | p[2]
                afc = (p[3] >> 4) & 3
                if not afc & 1:
                    continue
                o = 4 + (1 + p[4] if afc & 2 else 0)
                pusi = p[1] & 0x40
                if pid == 0 and pusi:
                    t = p[o + 1 + p[o]:]
                    L = ((t[1] & 0xF) << 8) | t[2]
                    for q in range(8, 3 + L - 4, 4):
                        pmt_pids.add(((t[q + 2] & 0x1F) << 8) | t[q + 3])
                elif pid in pmt_pids and pusi and sub is None:
                    t = p[o + 1 + p[o]:]
                    L = ((t[1] & 0xF) << 8) | t[2]
                    q = 12 + (((t[10] & 0xF) << 8) | t[11])
                    found = []
                    while q < 3 + L - 4:
                        st, ep, el = t[q], ((t[q + 1] & 0x1F) << 8) | t[q + 2], ((t[q + 3] & 0xF) << 8) | t[q + 4]
                        d = t[q + 5:q + 5 + el]
                        j = 0
                        while j + 2 <= len(d):
                            if d[j] == 0x59:
                                found.append((ep, d[j + 2:j + 5]))
                            j += 2 + d[j + 1]
                        q += 5 + el
                    if found:
                        sub = next((ep for ep, lang in found if lang == b"spa"), found[0][0])
                        print(f"[dvb] pista DVB en el PID {hex(sub)}", flush=True)
                elif pid == sub:
                    if pusi:
                        if pid in pes:
                            handle(bytes(pes[pid]))
                        pes[pid] = bytearray(p[o:])
                    elif pid in pes:
                        pes[pid] += p[o:]
            buf = buf[n:]
    except Exception as e:
        ERR.setdefault("dvb", []).append(f"{round(time.time() - T0)}s {type(e).__name__}: {str(e)[:120]}")


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
    ap.add_argument("--dvb-canal", help="UUID de Dispatcharr de un canal con subtítulos DVB (UDP): compara con el OCR de Emby")
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
    if a.dvb_canal:
        t = threading.Thread(target=follow_dvb, args=(f"{a.dispatcharr}/proxy/ts/stream/{a.dvb_canal}?output_format=ts", end), daemon=True)
        t.start()
        threads.append(t)
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

    if a.dvb_canal:
        report_dvb()
        return
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
    print("\n== duración de cada frase (s) y diferencia con la fuente")
    import statistics
    for v in ("dispa", "emby"):
        diffs, early = [], []
        for n, sp in SPAN.items():
            if "fuente" in sp and v in sp:
                d0 = sp["fuente"][1] - sp["fuente"][0]
                d1 = sp[v][1] - sp[v][0]
                diffs.append(d1 - d0)
                if d1 < d0 - 0.3:
                    early.append((d0, d1, TEXT[n]))
        if diffs:
            print(f"{v}: {len(diffs)} frases; diferencia mediana {statistics.median(diffs):+.2f} s, mínima {min(diffs):+.2f}, "
                  f"máxima {max(diffs):+.2f}; más cortas que en la fuente (>0,3 s): {len(early)}")
            for d0, d1, t in sorted(early, key=lambda x: x[1] - x[0])[:12]:
                print(f"    fuente {d0:5.2f} s  {v} {d1:5.2f} s  {t[:70]}")
    # dispa -> emby (sin fuente, p. ej. canales UDP con OCR)
    pairs = [(sp["dispa"][1] - sp["dispa"][0], sp["emby"][1] - sp["emby"][0]) for sp in SPAN.values() if "dispa" in sp and "emby" in sp]
    if pairs:
        d = [b - a for a, b in pairs]
        print(f"emby frente a dispa: {len(d)} frases; diferencia mediana {statistics.median(d):+.2f} s, mínima {min(d):+.2f}")
    for v, errs in ERR.items():
        print(f"errores {v}: {len(errs)} (primero: {errs[0]})")


def report_dvb():
    """Empareja cada subtítulo DVB con la frase de Emby (OCR) del mismo momento: el desfase entre los dos relojes es
    el que más emparejamientos da."""
    import statistics
    shows = [(a, b) for a, b in DVB if b is not None]
    emby = sorted((sp["emby"][0], sp["emby"][1], TEXT[n]) for n, sp in SPAN.items() if "emby" in sp)
    print(f"\n== DVB: {len(shows)} subtítulos mostrados y borrados | Emby (OCR): {len(emby)} frases")
    if not shows or not emby:
        return
    best = (0, 0.0)
    for a, _b in shows[:40]:
        for e0, _e1, _t in emby[:40]:
            off = e0 - a
            hits = sum(1 for x, _ in shows if any(abs(x + off - y) < 0.4 for y, _z, _w in emby))
            if hits > best[0]:
                best = (hits, off)
    hits, off = best
    print(f"desfase DVB→Emby {off:.2f} s; emparejados {hits} de {len(shows)}")
    lo, hi = emby[0][0] - off + 10, emby[-1][0] - off - 10
    miss, diffs = [], []
    print("\n DVB inicio  dura | Emby dura  dif | texto (OCR)")
    for a, b in shows:
        m = next(((y, z, w) for y, z, w in emby if abs(a + off - y) < 0.4), None)
        if m is None:
            if lo <= a <= hi:
                miss.append(a)
            print(f"{a - shows[0][0]:9.1f} {b - a:5.2f} |   ---          | (no está en Emby)")
            continue
        diffs.append((m[1] - m[0]) - (b - a))
        print(f"{a - shows[0][0]:9.1f} {b - a:5.2f} | {m[1] - m[0]:5.2f} {diffs[-1]:+5.2f} | {m[2][:70]}")
    if diffs:
        print(f"\nduración en Emby frente a DVB: mediana {statistics.median(diffs):+.2f} s, mínima {min(diffs):+.2f}; "
              f"más cortas (>0,3 s): {sum(1 for d in diffs if d < -0.3)}")
    print(f"subtítulos DVB sin frase en Emby (dentro del tramo común): {len(miss)}")


if __name__ == "__main__":
    main()
