#!/usr/bin/env python3
"""Variantes de imagen de los subtítulos DVB de una grabación, para medir con qué imagen acierta más el OCR de Emby.

Cada subtítulo se junta en una sola imagen (dvbsub.Merger, como hace el relevo) y se vuelve a dibujar según el modo:
  igual         como lo da el relevo hoy (una sola imagen, colores de la cadena)
  relleno       solo el relleno de la letra, blanco puro; borde, sombra y fondo transparentes
  negro         solo el relleno, negro puro sobre caja blanca opaca
  relleno2      'relleno' ampliado al doble
  negro2        'negro' ampliado al doble
Escribe un .ts igual al de entrada con el subtítulo cambiado (mismo PID, mismos instantes). Solo usa la grabación.

Uso: python3 ocr_variantes.py --irelay DIR entrada.ts salida.ts MODO"""
import argparse, sys

ap = argparse.ArgumentParser()
ap.add_argument("--irelay", required=True)
ap.add_argument("--umbral", type=int, default=120, help="luminancia mínima del relleno de la letra")
ap.add_argument("entrada")
ap.add_argument("salida")
ap.add_argument("modo", choices=["igual", "relleno", "negro", "relleno2", "negro2"])
a = ap.parse_args()
sys.path.insert(0, a.irelay)
from irelay import dvbsub, glyphs  # noqa: E402

PAGE, REGION, CLUT, OBJECT, DDS, END = 0x10, 0x11, 0x12, 0x13, 0x14, 0x80
data = open(a.entrada, "rb").read()
packets = [data[i:i + 188] for i in range(0, len(data) - 187, 188)]


def pid_of(p):
    return ((p[1] & 0x1F) << 8) | p[2]


def payload(p):
    afc = (p[3] >> 4) & 3
    if not afc & 1:
        return b""
    return p[4 + (1 + p[4] if afc & 2 else 0):]


# PID del subtítulo DVB: el de la PMT con descriptor 0x59
pmt_pids, sub = set(), None
for p in packets[:20000]:
    if not p[1] & 0x40:
        continue
    pid, pl = pid_of(p), payload(p)
    if pid == 0:
        t = pl[1 + pl[0]:]
        L = ((t[1] & 0xF) << 8) | t[2]
        for q in range(8, 3 + L - 4, 4):
            pmt_pids.add(((t[q + 2] & 0x1F) << 8) | t[q + 3])
    elif pid in pmt_pids:
        t = pl[1 + pl[0]:]
        L = ((t[1] & 0xF) << 8) | t[2]
        q = 12 + (((t[10] & 0xF) << 8) | t[11])
        while q < 3 + L - 4:
            ep, el = ((t[q + 1] & 0x1F) << 8) | t[q + 2], ((t[q + 3] & 0xF) << 8) | t[q + 4]
            d = t[q + 5:q + 5 + el]
            j = 0
            while j + 2 <= len(d):
                if d[j] == 0x59 and sub is None:
                    sub = ep
                j += 2 + d[j + 1]
            q += 5 + el
    if sub:
        break
if sub is None:
    sys.exit("la grabación no lleva subtítulos DVB")


def clut_body(cid, ink, bg):
    """Paleta con dos entradas de 4 bits: 0 = fondo, 1 = letra (Y, Cr, Cb, T a rango completo)."""
    out = bytes([cid, 0x00])
    for eid, (y, t) in ((0, bg), (1, ink)):
        out += bytes([eid, 0x40 | 0x01, y, 128, 128, t])
    return out


def rebuild(page_id, timeout, version, x0, y0, pix, cid, dds):
    """Datos del PES (desde data_identifier) con una región y un objeto con esos píxeles (índices 0/1)."""
    v = version & 0x0F
    segs = bytearray()
    if dds:
        segs += dvbsub._segment(DDS, page_id, dds)
    w, h = len(pix[0]), len(pix)
    segs += dvbsub._segment(PAGE, page_id, bytes([timeout, (v << 4) | 0x04, 0, 0xFF]) + x0.to_bytes(2, "big") + y0.to_bytes(2, "big"))
    region = bytes([0, (v << 4) | 0x08 | 0x07]) + w.to_bytes(2, "big") + h.to_bytes(2, "big")
    region += bytes([(2 << 5) | (2 << 2) | 0x03, cid, 0, (0 << 4) | 0x03]) + bytes([0, 0, 0, 0, 0, 0])
    segs += dvbsub._segment(REGION, page_id, region)
    ink = (16, 0) if a.modo.startswith("negro") else (235, 0)
    bg = (235, 0) if a.modo.startswith("negro") else (0, 0xFF)
    segs += dvbsub._segment(CLUT, page_id, clut_body(cid, ink, bg))
    top, bottom = dvbsub.encode_object(pix)
    obj = (0).to_bytes(2, "big") + bytes([(v << 4) | 0x01]) + len(top).to_bytes(2, "big") + len(bottom).to_bytes(2, "big") + top + bottom
    if len(obj) % 2:
        obj += b"\x00"
    segs += dvbsub._segment(OBJECT, page_id, obj)
    segs += dvbsub._segment(END, page_id, b"")
    return bytes([0x20, 0x00]) + bytes(segs) + b"\xff"


merger = dvbsub.Merger()
count = {"subtítulos": 0, "borrados": 0, "sin tocar": 0}


def transform(pes):
    """PES completo -> PES con la imagen cambiada (o el mismo si no se sabe rehacer)."""
    if pes[:4] != b"\x00\x00\x01\xbd" or len(pes) < 14:
        return pes
    head = 9 + pes[8]
    try:
        merged = merger.process(pes[head:])
    except Exception:
        merger.reset()
        count["sin tocar"] += 1
        return pes
    if merged is None:
        return pes
    if a.modo == "igual":
        new = merged
    else:
        # página, versión, tiempo y DDS del resultado de Merger
        j, page_id, timeout, dds = 2, 0, 0, None
        while j + 6 <= len(merged) and merged[j] == 0x0F:
            kind, pg = merged[j + 1], (merged[j + 2] << 8) | merged[j + 3]
            body = merged[j + 6:j + 6 + ((merged[j + 4] << 8) | merged[j + 5])]
            if kind == PAGE:
                page_id, timeout = pg, body[0]
            elif kind == DDS:
                dds = bytes(body)
            j += 6 + len(body)
        if merger.last is None:
            count["borrados"] += 1
            v = merger.version
            segs = bytearray()
            if dds:
                segs += dvbsub._segment(DDS, page_id, dds)
            segs += dvbsub._segment(PAGE, page_id, bytes([timeout, ((v & 15) << 4) | 0x04]))
            segs += dvbsub._segment(END, page_id, b"")
            new = bytes([0x20, 0x00]) + bytes(segs) + b"\xff"
        else:
            pixels, x0, y0, clut, _t = merger.last
            luma = glyphs.lumas(clut)
            pix = [[1 if luma.get(c, 0) >= a.umbral else 0 for c in row] for row in pixels]
            if a.modo.endswith("2"):
                pix = [[c for c in row for _ in (0, 1)] for row in pix for _ in (0, 1)]
                x0, y0 = x0 // 2, y0 // 2
            count["subtítulos"] += 1
            new = rebuild(page_id, timeout, merger.version, x0, y0, pix, 0, dds)
    body = pes[6:head] + new
    return pes[:4] + len(body).to_bytes(2, "big") + body


def packetize(pes, first_header):
    out, cc = bytearray(), first_header[3] & 0x0F
    for n, i in enumerate(range(0, len(pes), 184)):
        chunk = pes[i:i + 184]
        start = 0x40 if n == 0 else 0
        flags = 0x10 | ((cc + n) & 0x0F)
        if len(chunk) < 184:
            pad = 184 - len(chunk)
            adapt = bytes([pad - 1]) + (b"\x00" + b"\xff" * (pad - 2) if pad > 1 else b"")
            out += bytes([0x47, start | (sub >> 8), sub & 0xFF, 0x20 | flags]) + adapt + chunk
        else:
            out += bytes([0x47, start | (sub >> 8), sub & 0xFF, flags]) + chunk
    return bytes(out)


out = bytearray()
cur, cur_first = None, None
for p in packets:
    if pid_of(p) != sub:
        out += p
        continue
    if p[1] & 0x40:
        if cur is not None:
            out += packetize(transform(bytes(cur)), cur_first)
        cur, cur_first = bytearray(payload(p)), p
    elif cur is not None:
        cur += payload(p)
if cur is not None:
    out += packetize(transform(bytes(cur)), cur_first)
open(a.salida, "wb").write(out)
print(f"{a.modo}: PID {hex(sub)}, {count}")
