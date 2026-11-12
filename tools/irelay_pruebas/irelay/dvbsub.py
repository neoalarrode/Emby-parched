"""Subtítulos DVB (EN 300 743) rehechos en una sola imagen por frase.

Algunas cadenas (La 1) mandan cada línea de texto partida en dos tiras horizontales (regiones de 42 píxeles: la
mitad de arriba y la de abajo de las letras). Emby pasa los subtítulos DVB a texto con OCR región a región, así que
lee medias letras y se inventa la frase. Aquí se pinta la página como la pintaría el reproductor (regiones, objetos y
paleta de la fuente) y se vuelve a mandar como una sola región con un solo objeto que abarca todo el texto, con la
misma paleta: se ve igual y el OCR lee la frase entera."""
from __future__ import annotations

PAGE_COMPOSITION, REGION_COMPOSITION, CLUT_DEFINITION, OBJECT_DATA = 0x10, 0x11, 0x12, 0x13
DISPLAY_DEFINITION, END_OF_DISPLAY_SET = 0x14, 0x80
MAX_SEGMENT = 0xFFFF - 16


class _Bits:
    def __init__(self, data: bytes, pos: int = 0):
        self.data, self.bit = data, pos * 8

    def read(self, n: int) -> int:
        v = 0
        for _ in range(n):
            byte = self.data[self.bit >> 3] if (self.bit >> 3) < len(self.data) else 0
            v = (v << 1) | ((byte >> (7 - (self.bit & 7))) & 1)
            self.bit += 1
        return v

    def align(self) -> None:
        self.bit = (self.bit + 7) & ~7

    @property
    def pos(self) -> int:
        return self.bit >> 3


class _Writer:
    def __init__(self):
        self.out, self.acc, self.n = bytearray(), 0, 0

    def put(self, value: int, bits: int) -> None:
        for k in range(bits - 1, -1, -1):
            self.acc = (self.acc << 1) | ((value >> k) & 1)
            self.n += 1
            if self.n == 8:
                self.out.append(self.acc)
                self.acc = self.n = 0

    def align(self) -> None:
        while self.n:
            self.put(0, 1)


def _string2(b: _Bits, line: list[int], table: list[int]) -> None:
    while True:
        code = b.read(2)
        if code:
            line.append(table[code]); continue
        if b.read(1):
            run = b.read(3) + 3; line += [table[b.read(2)]] * run; continue
        if b.read(1):
            line.append(table[0]); continue
        sw = b.read(2)
        if sw == 0:
            break
        if sw == 1:
            line += [table[0]] * 2
        elif sw == 2:
            run = b.read(4) + 12; line += [table[b.read(2)]] * run
        else:
            run = b.read(8) + 29; line += [table[b.read(2)]] * run
    b.align()


def _string4(b: _Bits, line: list[int]) -> None:
    while True:
        code = b.read(4)
        if code:
            line.append(code); continue
        if not b.read(1):
            run = b.read(3)
            if not run:
                break
            line += [0] * (run + 2); continue
        if not b.read(1):
            run = b.read(2) + 4; line += [b.read(4)] * run; continue
        sw = b.read(2)
        if sw == 0:
            line.append(0)
        elif sw == 1:
            line += [0, 0]
        elif sw == 2:
            run = b.read(4) + 9; line += [b.read(4)] * run
        else:
            run = b.read(8) + 25; line += [b.read(4)] * run
    b.align()


def _field(data: bytes, rows: dict[int, list[int]], first: int) -> None:
    """Bloque de un campo (líneas pares o impares) del objeto: filas de códigos de píxel de 4 bits."""
    b = _Bits(data)
    y, line = first, []
    table2 = [0, 7, 8, 15]
    while b.pos < len(data):
        kind = b.read(8)
        if kind == 0x10:
            _string2(b, line, table2)
        elif kind == 0x11:
            _string4(b, line)
        elif kind == 0x20:
            table2 = [b.read(4) for _ in range(4)]
        elif kind == 0xF0:
            rows[y] = line
            y, line = y + 2, []
        else:
            raise ValueError(f"datos de píxel no soportados ({kind:#x})")


def _encode_line(w: _Writer, px: list[int]) -> None:
    w.put(0x11, 8)
    i = 0
    while i < len(px):
        c, run = px[i], 1
        while i + run < len(px) and px[i + run] == c and run < 280:
            run += 1
        i += run
        if c == 0:
            if run == 1:
                w.put(0, 4); w.put(0b1100, 4)
            elif run == 2:
                w.put(0, 4); w.put(0b1101, 4)
            elif run <= 9:
                w.put(0, 4); w.put(0, 1); w.put(run - 2, 3)
            elif run <= 24:
                w.put(0, 4); w.put(0b1110, 4); w.put(run - 9, 4); w.put(0, 4)
            else:
                w.put(0, 4); w.put(0b1111, 4); w.put(run - 25, 8); w.put(0, 4)
        elif run <= 3:
            for _ in range(run):
                w.put(c, 4)
        elif run <= 7:
            w.put(0, 4); w.put(0b10, 2); w.put(run - 4, 2); w.put(c, 4)
        elif run == 8:
            w.put(0, 4); w.put(0b10, 2); w.put(3, 2); w.put(c, 4); w.put(c, 4)
        elif run <= 24:
            w.put(0, 4); w.put(0b1110, 4); w.put(run - 9, 4); w.put(c, 4)
        else:
            w.put(0, 4); w.put(0b1111, 4); w.put(run - 25, 8); w.put(c, 4)
    w.put(0, 8)                                           # fin de la cadena: 0000 0 000
    w.align()
    w.put(0xF0, 8)


def encode_object(pixels: list[list[int]]) -> tuple[bytes, bytes]:
    """(campo de arriba, campo de abajo) de un objeto de píxeles de 4 bits."""
    top, bottom = _Writer(), _Writer()
    for y, row in enumerate(pixels):
        _encode_line(top if y % 2 == 0 else bottom, row)
    return bytes(top.out), bytes(bottom.out)


def decode_object(top: bytes, bottom: bytes) -> dict[int, list[int]]:
    rows: dict[int, list[int]] = {}
    _field(top, rows, 0)
    if bottom:
        _field(bottom, rows, 1)
    else:
        for y in list(rows):
            rows[y + 1] = list(rows[y])
    return rows


def _segment(kind: int, page: int, body: bytes) -> bytes:
    return bytes([0x0F, kind]) + page.to_bytes(2, "big") + len(body).to_bytes(2, "big") + body


class Merger:
    """Lleva el estado de la página (regiones, objetos, paletas) y rehace cada conjunto de visualización."""

    def __init__(self):
        self.reset()

    def reset(self) -> None:
        self.regions: dict[int, dict] = {}
        self.cluts: dict[int, bytes] = {}            # cuerpo del segmento tal cual
        self.transparent: dict[int, set[int]] = {}   # CLUT -> códigos de 4 bits transparentes
        self.dds: bytes | None = None
        self.version = 0
        self.last = None              # (píxeles, x, y, cuerpo de la paleta, segundos máx.) de la última pantalla

    def _clut(self, body: bytes) -> None:
        cid = body[0]
        self.cluts[cid] = body
        clear = {0} if cid not in self.transparent else set(self.transparent[cid])
        k = 2
        while k + 1 < len(body):
            eid, fl = body[k], body[k + 1]
            if fl & 1:
                y, t = body[k + 2], body[k + 5]; k += 6
            else:
                v = (body[k + 2] << 8) | body[k + 3]; y, t = (v >> 10) << 2, (v & 3) << 6; k += 4
            if fl & 0x40:                              # entrada de la paleta de 4 bits
                (clear.add if y == 0 or t == 0xFF else clear.discard)(eid)
        self.transparent[cid] = clear

    def _region(self, body: bytes) -> None:
        rid = body[0]
        fill = (body[1] >> 3) & 1
        w, h = (body[2] << 8) | body[3], (body[4] << 8) | body[5]
        depth = (body[6] >> 2) & 7
        clut = body[7]
        bg4 = body[9] >> 4
        objs, k = [], 10
        while k + 5 < len(body):
            oid = (body[k] << 8) | body[k + 1]
            otype = body[k + 2] >> 6
            x, y = ((body[k + 2] & 0x0F) << 8) | body[k + 3], ((body[k + 4] & 0x0F) << 8) | body[k + 5]
            objs.append((oid, otype, x, y))
            k += 6 + (2 if otype in (1, 2) else 0)
        old = self.regions.get(rid)
        if depth != 2:
            raise ValueError(f"región de {depth} (solo 4 bits)")
        if any(t != 0 for _, t, _, _ in objs):
            raise ValueError("objetos de caracteres")
        if old is None or fill or (old["w"], old["h"]) != (w, h):
            pixels = [[bg4] * w for _ in range(h)]
        else:
            pixels = old["pixels"]
        self.regions[rid] = {"w": w, "h": h, "clut": clut, "objects": objs, "pixels": pixels}

    def _object(self, body: bytes) -> None:
        oid = (body[0] << 8) | body[1]
        if (body[2] >> 2) & 3 != 0:
            raise ValueError("objeto sin píxeles")
        tl, bl = (body[3] << 8) | body[4], (body[5] << 8) | body[6]
        rows = decode_object(body[7:7 + tl], body[7 + tl:7 + tl + bl])
        for region in self.regions.values():
            for o, _, ox, oy in region["objects"]:
                if o != oid:
                    continue
                for y, line in rows.items():
                    ry = oy + y
                    if 0 <= ry < region["h"]:
                        row = region["pixels"][ry]
                        for x, c in enumerate(line):
                            if 0 <= ox + x < region["w"]:
                                row[ox + x] = c

    def process(self, data: bytes) -> bytes | None:
        """Datos del PES (desde data_identifier) -> los mismos datos con la página en una sola región, o None si
        no hay que tocarlo (sin composición de página, o algo que no se sabe rehacer)."""
        if len(data) < 2 or data[0] != 0x20:
            return None
        j, page_id, page = 2, None, None
        while j + 6 <= len(data) and data[j] == 0x0F:
            kind = data[j + 1]
            pid = (data[j + 2] << 8) | data[j + 3]
            body = data[j + 6:j + 6 + ((data[j + 4] << 8) | data[j + 5])]
            j += 6 + len(body)
            if kind == DISPLAY_DEFINITION:
                self.dds = bytes(body)
            elif kind == PAGE_COMPOSITION:
                page_id = pid
                page = (body[0], (body[1] >> 2) & 3,
                        [(body[k], (body[k + 2] << 8) | body[k + 3], (body[k + 4] << 8) | body[k + 5])
                         for k in range(2, len(body) - 5, 6)])
                if page[1]:                                # adquisición o cambio de modo: se empieza de cero
                    self.regions = {}
            elif kind == REGION_COMPOSITION:
                self._region(body)
            elif kind == CLUT_DEFINITION:
                self._clut(body)
            elif kind == OBJECT_DATA:
                self._object(body)
        if page is None:
            return None
        timeout, state, visible = page
        shown = [(self.regions[r], x, y) for r, x, y in visible if r in self.regions]
        cluts = {reg["clut"] for reg, _, _ in shown}
        if len(cluts) > 1:
            raise ValueError("regiones con paletas distintas")
        self.version = (self.version + 1) & 0x0F
        v = self.version
        segs = bytearray()
        if self.dds:
            segs += _segment(DISPLAY_DEFINITION, page_id, self.dds)
        # lo que se ve: píxeles no transparentes de las regiones, en coordenadas de pantalla
        clut = next(iter(cluts), 0)
        clear = self.transparent.get(clut, {0})
        dots: dict[tuple[int, int], int] = {}
        for reg, rx, ry in shown:
            for y, row in enumerate(reg["pixels"]):
                for x, c in enumerate(row):
                    if c not in clear:
                        dots[(rx + x, ry + y)] = c
        self.last = None
        if not dots:
            segs += _segment(PAGE_COMPOSITION, page_id, bytes([timeout, (v << 4) | (state << 2)]))
        else:
            margin = 4
            x0 = max(0, min(x for x, _ in dots) - margin); x1 = max(x for x, _ in dots) + margin + 1
            y0 = max(0, min(y for _, y in dots) - margin); y1 = max(y for _, y in dots) + margin + 1
            if (y1 - y0) % 2:
                y1 += 1
            bg = min(clear)
            pixels = [[dots.get((x, y), bg) for x in range(x0, x1)] for y in range(y0, y1)]
            self.last = (pixels, x0, y0, self.cluts.get(clut, b""), timeout)
            top, bottom = encode_object(pixels)
            if 7 + len(top) + len(bottom) > MAX_SEGMENT:
                raise ValueError("imagen demasiado grande")
            w, h = x1 - x0, y1 - y0
            segs += _segment(PAGE_COMPOSITION, page_id,
                             bytes([timeout, (v << 4) | (state << 2), 0, 0xFF]) + x0.to_bytes(2, "big") + y0.to_bytes(2, "big"))
            region = bytes([0, (v << 4) | 0x08 | 0x07]) + w.to_bytes(2, "big") + h.to_bytes(2, "big")
            region += bytes([(2 << 5) | (2 << 2) | 0x03, clut, 0, (bg << 4) | 0x03])
            region += bytes([0, 0, 0, 0, 0, 0])            # objeto 0, mapa de bits, en (0, 0)
            segs += _segment(REGION_COMPOSITION, page_id, region)
            if clut in self.cluts:
                segs += _segment(CLUT_DEFINITION, page_id, self.cluts[clut])
            obj = (0).to_bytes(2, "big") + bytes([(v << 4) | 0x01]) + len(top).to_bytes(2, "big")
            obj += len(bottom).to_bytes(2, "big") + top + bottom
            if len(obj) % 2:
                obj += b"\x00"
            segs += _segment(OBJECT_DATA, page_id, obj)
        segs += _segment(END_OF_DISPLAY_SET, page_id, b"")
        return bytes([0x20, 0x00]) + bytes(segs) + b"\xff"
