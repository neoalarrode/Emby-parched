#!/usr/bin/env python3
"""Puntúa un .vtt del OCR contra la referencia de dvb_lee (subtítulos leídos con la tabla de letras, casi exactos).

Uso: python3 puntua_ocr.py referencia.txt ocr.vtt
  referencia.txt: salida de dvb_lee.py sobre la misma grabación (líneas «inicio dura OK texto», con ' / ' entre líneas)
Empareja cada subtítulo de la referencia leído entero (OK) con la frase del OCR que empieza a la vez (el desfase entre
los dos relojes se calcula solo) y da: frases que faltan, frases exactas y la tasa de error por carácter (CER)."""
import difflib, re, sys, unicodedata


def norm(t):
    t = re.sub(r"<[^>]*>", "", t).replace("\n", " / ")
    t = t.replace("“", '"').replace("”", '"').replace("’", "'").replace("‘", "'")
    return " ".join(t.split())


def secs(s):
    p = s.split(":")
    return sum(float(x) * 60 ** k for k, x in enumerate(reversed(p)))


ref = []
for line in open(sys.argv[1], encoding="utf-8"):
    m = re.match(r"\s*([\d.]+)\s+([\d.]+) s\s+OK\s+(.*)$", line.rstrip("\n"))
    if m:
        ref.append((float(m.group(1)), float(m.group(2)), norm(m.group(3))))
vtt = open(sys.argv[2], encoding="utf-8").read().replace("\r", "")
ocr = []
for block in vtt.split("\n\n"):
    lines = block.strip("\n").split("\n")
    for i, l in enumerate(lines):
        if "-->" in l:
            text = norm("\n".join(lines[i + 1:]))
            if text:
                ocr.append((secs(l.split("-->")[0]), text))
            break
if not ref or not ocr:
    sys.exit(f"sin datos: referencia {len(ref)}, ocr {len(ocr)}")
# Emparejamiento por texto, en orden (sin tiempos: el ffmpeg suelto, sin vídeo, no da bien las duraciones): para cada
# frase de la referencia, la más parecida entre las siguientes del OCR
off = 0.0
missing = exact = 0
errs = chars = 0
worst = []
j = 0
for r0, _d, rt in ref:
    cand = [(difflib.SequenceMatcher(None, rt, ocr[k][1], autojunk=False).ratio(), k) for k in range(j, min(len(ocr), j + 6))]
    best_ratio, k = max(cand) if cand else (0, None)
    if k is None or best_ratio < 0.5:
        missing += 1
        errs += len(rt)
        chars += len(rt)
        worst.append((len(rt), rt, "(no está)"))
        continue
    j = k + 1
    m = ocr[k]
    sm = difflib.SequenceMatcher(None, rt, m[1], autojunk=False)
    e = sum(max(i2 - i1, j2 - j1) for tag, i1, i2, j1, j2 in sm.get_opcodes() if tag != "equal")
    errs += e
    chars += len(rt)
    exact += e == 0
    if e:
        worst.append((e, rt, m[1]))
print(f"referencia {len(ref)} | ocr {len(ocr)} | desfase {off:+.2f} s | faltan {missing} | exactas {exact} "
      f"({100 * exact / len(ref):.0f} %) | CER {100 * errs / max(1, chars):.2f} %")
for e, rt, ot in sorted(worst, reverse=True)[:int(sys.argv[3]) if len(sys.argv) > 3 else 8]:
    print(f"   [{e}] {rt}\n       {ot}")
