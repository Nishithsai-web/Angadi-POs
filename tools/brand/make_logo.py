#!/usr/bin/env python3
"""
Angadi logo generator. The mark: a kirana shop's striped awning over అ, the first letter of అంగడి ("shop").
Writes the app icon layers, the splash and the Play icon SVG from the Noto Sans Telugu Bold outline (OFL), so the
icon needs no font at build time. PNGs for the Play listing: node tools/brand/render.js (Chromium via Playwright).
Usage (repo root): python3 tools/brand/make_logo.py   (needs: pip install fonttools)
"""
import json, os
from fontTools.ttLib import TTFont
from fontTools.pens.svgPathPen import SVGPathPen
from fontTools.pens.boundsPen import BoundsPen

FONT = "src/Angadi.Maui/Resources/Fonts/NotoSansTelugu-Bold.ttf"

def outline(path, ch):
    font = TTFont(path); gs = font.getGlyphSet(); name = font.getBestCmap()[ord(ch)]
    pen = SVGPathPen(gs); gs[name].draw(pen)
    bp = BoundsPen(gs); gs[name].draw(bp)
    return {"d": pen.getCommands(), "bounds": bp.bounds}

g = outline(FONT, 'అ')
x0, y0, x1, y1 = g['bounds']
GREEN, GREEN2, AMBER, CREAM, DEEP = "#0F5132", "#1B7A4B", "#F4A300", "#FFF6E0", "#0A3D25"

def glyph(cx, top, width, fill):
    s = width / (x1 - x0)
    h = (y1 - y0) * s
    # font units are y-up: flip, then place the glyph's box at (cx - width/2, top)
    tx = cx - width / 2 - x0 * s
    ty = top + y1 * s
    return f'<path transform="translate({tx:.2f} {ty:.2f}) scale({s:.5f} {-s:.5f})" d="{g["d"]}" fill="{fill}"/>', h

def awning(cx, top, width, n=6, band=0.2):
    """Striped shop awning with a scalloped edge: the universal 'shop' sign of an Indian kirana."""
    w = width / n; h = width * band; r = w / 2
    parts = []
    for i in range(n):
        x = cx - width / 2 + i * w
        col = AMBER if i % 2 == 0 else CREAM
        # stripe + its scallop (half circle below)
        parts.append(f'<path d="M{x:.2f} {top:.2f} h{w:.2f} v{h:.2f} a{r:.2f} {r:.2f} 0 0 1 {-w:.2f} 0 Z" fill="{col}"/>')
    # roof cap: a slightly wider rounded bar on top
    cap = f'<rect x="{cx - width/2 - 10:.2f}" y="{top - 18:.2f}" width="{width + 20:.2f}" height="24" rx="12" fill="{CREAM}"/>'
    return cap + "".join(parts), h + r

def mark(size, scale=1.0):
    """The mark centred in a size x size box. scale shrinks it for the adaptive-icon safe zone."""
    cx = size / 2
    aw = 300 * scale * size / 512
    top = size / 2 - 114 * scale * size / 512
    a, ah = awning(cx, top, aw)
    gw = 210 * scale * size / 512
    gl, gh = glyph(cx, top + ah + 26 * scale * size / 512, gw, CREAM)
    return a + gl

def svg(size, body, bg=True, gradient=True):
    defs = ""
    back = ""
    if bg:
        if gradient:
            defs = f'<defs><radialGradient id="bg" cx="50%" cy="38%" r="75%"><stop offset="0" stop-color="{GREEN2}"/><stop offset="1" stop-color="{DEEP}"/></radialGradient></defs>'
            back = f'<rect width="{size}" height="{size}" fill="url(#bg)"/>'
        else:
            back = f'<rect width="{size}" height="{size}" fill="{GREEN}"/>'
    return f'<?xml version="1.0" encoding="UTF-8"?>\n<svg xmlns="http://www.w3.org/2000/svg" width="{size}" height="{size}" viewBox="0 0 {size} {size}">{defs}{back}{body}</svg>\n'

open('store/play-icon.svg', 'w').write(svg(512, mark(512)))
# MAUI adaptive icon: background = plain colour layer, foreground = the mark inside the safe zone (~61% circle)
open('src/Angadi.Maui/Resources/AppIcon/appiconfg.svg', 'w').write('<?xml version="1.0" encoding="UTF-8"?>\n<!-- Angadi mark: a kirana awning over అ (first letter of అంగడి, "shop"). Outline from Noto Sans Telugu Bold (OFL). -->\n' + svg(456, mark(456, 0.78), bg=False).split('\n',1)[1])
open('src/Angadi.Maui/Resources/AppIcon/appicon.svg', 'w').write(svg(456, '', bg=True, gradient=True))
open('src/Angadi.Maui/Resources/Splash/splash.svg', 'w').write(svg(456, mark(456, 0.9), bg=False))
print("ok")
