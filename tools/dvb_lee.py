#!/usr/bin/env python3
"""Lee en directo los subtítulos DVB de un canal (salida del relevo por Dispatcharr) con la tabla de letras de
Intro Relay (irelay/glyphs.py + letras.json), sin OCR, y dice cuántos reconoce enteros. Solo lee: no cambia nada.

Uso: python3 dvb_lee.py --irelay /ruta/a/intro_relay --canal <UUID de Dispatcharr> [--minutos 3]
Para cada subtítulo: segundo en que aparece, cuánto dura, y el texto ('?' en cada letra que no está en la tabla)."""
import argparse, sys, time, urllib.request

ap = argparse.ArgumentParser()
ap.add_argument("--irelay", required=True, help="carpeta que contiene irelay/ (con glyphs.py y letras.json)")
ap.add_argument("--canal", help="UUID del canal en Dispatcharr")
ap.add_argument("--fichero", help="en vez de un canal, un .ts grabado")
ap.add_argument("--dispatcharr", default="http://127.0.0.1:9191")
ap.add_argument("--minutos", type=float, default=3)
ap.add_argument("--desconocidas", help="fichero JSON donde guardar la forma de cada letra desconocida con su frase")
a = ap.parse_args()
sys.path.insert(0, a.irelay)
from irelay import dvbsub, glyphs  # noqa: E402

exact, shapes = glyphs.table()
print(f"tabla: {len(shapes)} formas de letra", flush=True)


UNKNOWN = []          # [ancho, alto, hex, frase] de cada letra desconocida (formato de letras.json)


def pack(bits):
    w, h = len(bits[0]), len(bits)
    by = bytearray((w * h + 7) // 8)
    for y in range(h):
        for x in range(w):
            if bits[y][x]:
                by[(y * w + x) >> 3] |= 0x80 >> ((y * w + x) & 7)
    return w, h, by.hex()


def text_of(pixels, luma):
    """Texto con '?' en las letras desconocidas, y (letras reconocidas, total)."""
    lines, known, total = [], 0, 0
    unknown_here = []
    for line in glyphs.segment(pixels, luma):
        out, prev = "", None
        for bits, x, _y in line:
            ch = glyphs.char(bits)
            total += 1
            known += ch is not None
            if prev is not None and x - prev >= glyphs.SPACE:
                out += " "
            out += ch if ch is not None else "?"
            if ch is None:
                unknown_here.append(pack(bits))
            prev = x + len(bits[0])
        if out:
            lines.append(out)
    text = " / ".join(lines)
    for w, h, hx in unknown_here:
        UNKNOWN.append([w, h, hx, text])
    return text, known, total


if a.fichero:
    r = open(a.fichero, "rb")
else:
    url = f"{a.dispatcharr}/proxy/ts/stream/{a.canal}?output_format=ts"
    r = urllib.request.urlopen(urllib.request.Request(url, headers={"User-Agent": "VLC/3.0.21 LibVLC/3.0.21"}), timeout=60)
end = time.time() + a.minutos * 60
merger = dvbsub.Merger()
buf, pmt_pids, sub, pes = b"", set(), None, None
shown = None          # (pts, texto, letras conocidas, total)
stats = {"subs": 0, "enteros": 0, "letras": 0, "conocidas": 0, "errores": 0}
t0 = None


def handle(data):
    global shown, t0
    if len(data) < 14 or data[:3] != b"\x00\x00\x01" or not data[7] & 0x80:
        return
    b = data[9:14]
    pts = (((b[0] >> 1) & 7) << 30 | b[1] << 22 | (b[2] >> 1) << 15 | b[3] << 7 | b[4] >> 1) / 90000
    t0 = pts if t0 is None else t0
    try:
        if merger.process(bytes(data[9 + data[8]:])) is None:
            return
    except Exception as e:
        stats["errores"] += 1
        print(f"  (no se pudo leer un conjunto: {e})")
        return
    if shown is not None:
        st, txt, k, n = shown
        print(f"{st - t0:8.1f} {pts - st:5.2f} s  {'OK ' if k == n else f'{k}/{n}'}  {txt}", flush=True)
        shown = None
    if merger.last is not None:
        pixels, _x, _y, clut, _timeout = merger.last
        txt, k, n = text_of(pixels, glyphs.lumas(clut))
        stats["subs"] += 1
        stats["enteros"] += k == n and n > 0
        stats["letras"] += n
        stats["conocidas"] += k
        shown = (pts, txt, k, n)


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
            while q < 3 + L - 4:
                ep, el = ((t[q + 1] & 0x1F) << 8) | t[q + 2], ((t[q + 3] & 0xF) << 8) | t[q + 4]
                d = t[q + 5:q + 5 + el]
                j = 0
                while j + 2 <= len(d):
                    if d[j] == 0x59 and sub is None:
                        sub = ep
                        print(f"pista DVB en el PID {hex(ep)} ({bytes(d[j + 2:j + 5]).decode(errors='replace')})", flush=True)
                    j += 2 + d[j + 1]
                q += 5 + el
        elif pid == sub:
            if pusi:
                if pes is not None:
                    handle(pes)
                pes = bytearray(p[o:])
            elif pes is not None:
                pes += p[o:]
    buf = buf[n:]

s = stats
print(f"\nsubtítulos: {s['subs']} | leídos enteros con la tabla: {s['enteros']} "
      f"({100 * s['enteros'] / max(1, s['subs']):.0f} %) | letras reconocidas: {s['conocidas']} de {s['letras']} "
      f"({100 * s['conocidas'] / max(1, s['letras']):.1f} %) | conjuntos que no se pudieron leer: {s['errores']}")

if a.desconocidas:
    import json
    json.dump(UNKNOWN, open(a.desconocidas, "w"), ensure_ascii=False)
    print(f"{len(UNKNOWN)} letras desconocidas guardadas en {a.desconocidas}")
