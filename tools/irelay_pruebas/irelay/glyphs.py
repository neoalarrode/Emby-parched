"""Texto de un subtítulo DVB leyendo sus letras con una tabla, sin OCR.

Cada cadena dibuja sus subtítulos siempre con la misma letra, así que la misma letra sale siempre con los mismos
píxeles (o casi: varía un píxel con el suavizado). El subtítulo (ya en una sola imagen, dvbsub.Merger) se corta en
letras: el relleno claro de cada una (el borde negro las une), sus tildes y puntos con ella y las dos marcas de unas
comillas juntas; a media resolución, para quitar el ruido del suavizado. Cada letra se busca en la tabla de letras
conocidas (letras.json, sacada de grabaciones de la cadena): igual, o la más parecida si se parece bastante. Si
alguna no se reconoce, no se da texto (y queda el subtítulo DVB, que Emby pasa a texto con su OCR)."""
from __future__ import annotations

import json
from functools import lru_cache
from pathlib import Path

THRESHOLD = 120            # luminancia mínima del relleno de la letra
TOLERANCE = 0.3            # diferencia máxima con la letra de la tabla (fracción de píxeles)
SPACE = 7                  # hueco (media resolución) a partir del cual hay un espacio entre palabras


def _unpack(w: int, h: int, hx: str) -> tuple:
    by = bytes.fromhex(hx)
    return tuple(tuple(1 if by[(y * w + x) >> 3] & (0x80 >> ((y * w + x) & 7)) else 0 for x in range(w))
                 for y in range(h))


@lru_cache(maxsize=1)
def table() -> tuple[dict, list]:
    """(forma -> carácter, [(forma, carácter)] para buscar la más parecida)"""
    path = Path(__file__).with_name("letras.json")
    exact, shapes = {}, []
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return exact, shapes
    for w, h, hx, ch, *_n in data.get("letras", []):
        bits = _unpack(w, h, hx)
        exact[bits] = ch
        shapes.append((bits, ch))
    return exact, shapes


def lumas(clut_body: bytes) -> dict[int, int]:
    """Luminancia de cada entrada de la paleta de 4 bits (0 si es transparente)."""
    out, k = {}, 2
    while k + 1 < len(clut_body):
        entry, flags = clut_body[k], clut_body[k + 1]
        if flags & 1:
            y, t = clut_body[k + 2], clut_body[k + 5]
            k += 6
        else:
            v = (clut_body[k + 2] << 8) | clut_body[k + 3]
            y, t = (v >> 10) << 2, (v & 3) << 6
            k += 4
        if flags & 0x40:
            out[entry] = y if t < 0xC0 else 0
    return out


def _components(ink: list[list[bool]]) -> list[list[tuple[int, int]]]:
    h, w = len(ink), len(ink[0])
    seen = [[False] * w for _ in range(h)]
    comps = []
    for y in range(h):
        for x in range(w):
            if ink[y][x] and not seen[y][x]:
                stack, pts = [(y, x)], []
                seen[y][x] = True
                while stack:
                    a, b = stack.pop()
                    pts.append((a, b))
                    for c in (a - 1, a, a + 1):
                        for d in (b - 1, b, b + 1):
                            if 0 <= c < h and 0 <= d < w and ink[c][d] and not seen[c][d]:
                                seen[c][d] = True
                                stack.append((c, d))
                if len(pts) > 4:
                    comps.append(pts)
    return comps


def segment(pixels: list[list[int]], luma: dict[int, int]) -> list[list[tuple]]:
    """Líneas de letras [(forma a media resolución, x, y)] de la imagen del subtítulo."""
    if not pixels or not pixels[0]:
        return []
    ink = [[luma.get(c, 0) >= THRESHOLD for c in row] for row in pixels]
    comps = _components(ink)
    rows = sorted({p[0] for c in comps for p in c})
    bands: list[list[int]] = []
    for y in rows:
        if bands and y <= bands[-1][1] + 6:
            bands[-1][1] = y
        else:
            bands.append([y, y])
    lines = []
    for y0, y1 in bands:
        cs = [c for c in comps if y0 <= sum(p[0] for p in c) / len(c) <= y1]
        boxes = sorted((min(p[1] for p in c), max(p[1] for p in c), c) for c in cs)
        groups: list[tuple] = []
        for x0, x1, c in boxes:
            if groups:
                gx0, gx1, gc = groups[-1]
                if min(x1, gx1) - max(x0, gx0) > 0.6 * min(x1 - x0, gx1 - gx0):
                    groups[-1] = (min(x0, gx0), max(x1, gx1), gc + c)
                    continue
            groups.append((x0, x1, c))
        line = []
        for x0, x1, c in groups:
            ys = [p[0] for p in c]
            gy0, gy1 = min(ys), max(ys)
            s = set(c)
            full = [[1 if (y, x) in s else 0 for x in range(x0, x1 + 1)] for y in range(gy0, gy1 + 1)]
            hh, ww = (len(full) + 1) // 2, (len(full[0]) + 1) // 2
            bits = tuple(tuple(1 if sum(full[r][q] for r in (2 * yy, 2 * yy + 1) if r < len(full)
                                        for q in (2 * xx, 2 * xx + 1) if q < len(full[0])) >= 2 else 0
                               for xx in range(ww)) for yy in range(hh))
            line.append((bits, x0 // 2, (gy0 - y0) // 2))
        lines.append(_merge(line))
    return lines


def _union(parts: list[tuple]) -> tuple:
    x0 = min(x for _b, x, _y in parts)
    y0 = min(y for _b, _x, y in parts)
    x1 = max(x + len(b[0]) for b, x, _y in parts)
    y1 = max(y + len(b) for b, _x, y in parts)
    grid = [[0] * (x1 - x0) for _ in range(y1 - y0)]
    for b, x, y in parts:
        for yy, row in enumerate(b):
            for xx, v in enumerate(row):
                if v:
                    grid[y - y0 + yy][x - x0 + xx] = 1
    return tuple(tuple(r) for r in grid), x0, y0


def _merge(items: list[tuple]) -> list[tuple]:
    """Tildes y puntos con su letra, y las dos marcas de unas comillas en una."""
    items = sorted(items, key=lambda t: t[1])
    used = [False] * len(items)
    for k, (b, x, y) in enumerate(items):
        h, w = len(b), len(b[0])
        if h <= 8:
            cx = x + w / 2
            for j, (b2, x2, y2) in enumerate(items):
                if j != k and len(b2) > 8 and y + h <= y2 + 3 and x2 - 2 <= cx <= x2 + len(b2[0]) + 2:
                    used[k] = True
                    items[j] = _union([items[j], (b, x, y)])
                    break
    items = sorted((it for k, it in enumerate(items) if not used[k]), key=lambda t: t[1])
    out: list[tuple] = []
    for b, x, y in items:
        if out:
            pb, px, py = out[-1]
            small = lambda bb, yy: len(bb) <= 11 and yy + len(bb) <= 13
            if small(pb, py) and small(b, y) and x - (px + len(pb[0])) <= 6:
                out[-1] = _union([out[-1], (b, x, y)])
                continue
        out.append((b, x, y))
    return out


def _distance(a: tuple, b: tuple) -> float:
    ha, wa, hb, wb = len(a), len(a[0]), len(b), len(b[0])
    if abs(ha - hb) > 2 or abs(wa - wb) > 2:
        return 1.0
    hh, ww = max(ha, hb) + 2, max(wa, wb) + 2
    best = 1.0
    for dy in (-1, 0, 1):
        for dx in (-1, 0, 1):
            diff = tot = 0
            for y in range(-1, hh):
                for x in range(-1, ww):
                    va = a[y][x] if 0 <= y < ha and 0 <= x < wa else 0
                    yb, xb = y + dy, x + dx
                    vb = b[yb][xb] if 0 <= yb < hb and 0 <= xb < wb else 0
                    if va or vb:
                        tot += 1
                        diff += va != vb
            best = min(best, diff / max(tot, 1))
    return best


def char(bits: tuple) -> str | None:
    exact, shapes = table()
    if bits in exact:
        return exact[bits]
    best, found = TOLERANCE, None
    for shape, ch in shapes:
        d = _distance(bits, shape)
        if d <= best:
            best, found = d, ch
    return found


def read(pixels: list[list[int]], luma: dict[int, int]) -> str | None:
    """Texto del subtítulo (líneas separadas por saltos), o None si alguna letra no se reconoce."""
    out = []
    for line in segment(pixels, luma):
        text, prev_end = "", None
        for bits, x, _y in line:
            ch = char(bits)
            if ch is None:
                return None
            if prev_end is not None and x - prev_end >= SPACE:
                text += " "
            text += ch
            prev_end = x + len(bits[0])
        if text:
            out.append(text)
    return "\n".join(out) if out else None
