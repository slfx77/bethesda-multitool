# SPDX-License-Identifier: 0BSD
"""Display-blend probe: the scalar models the probe is judged against (pure Python, no Blender, no numpy).

Gamebryo (the source; D3D9, 8-bit UNORM target, no sRGB write, the pixel shader output saturated) blends per channel on
display-encoded values:  y = clamp(k0 + k1 * Cd),  k0 = sf(Cs, As) * Cs,  k1 = df(Cs, As).  That framebuffer model is
INFERRED for the PC source and not established for the X360 and PS3 framebuffers (display-blend CHALLENGE item 3).

Blender composites a blended surface in scene-linear light, out = E + T * lin(Cd), shown through the Standard view
(display clipped at 1). A "fit" chooses E >= 0 and T in [0, tmax] from (k0, k1), both computed on display values:
    exact     E = lin(clamp(k0)), T = k1 (k1 identically 0 or 1)
    one       T = 1, E = max(lin(clamp(k0 + xa)) - lin(xa), 0)
    two       y_i = clamp(k0 + k1 x_i), Y_i = lin(y_i), X_i = lin(x_i), T = clamp((Y2 - Y1) / (X2 - X1), 0, tmax),
              E = max(Y1 - T X1, 0)
    anchored  as two, with the second node following the target's saturation point xsat = (1 - k0) / k1 (a safe divide,
              0 where k1 is 0): xs2 = max(min(x2, x1 + frac * max(xsat - x1, 0)), x1 + guard)
The lit route scales the lit closure: E = m(As) * L, T = tau(As), both blind to the lit color L. Besides the tabulated
curves (power, scaled-power, constant), the Shared writer gives every other lit-scaled equation the generic ``linear``
curve: m = (m + mAlpha * As)^p, tau = (tau + tauAlpha * As)^p, the equation's own source scale and source-alpha
transmission raised to the exponent p the writer chose by the smallest grid-measured lit bound (display-blend review
fixes 1 and 2, 2026-09-27; design 3.5, m = s(As)^p, tau = t(As)^p).
"""

import json
import math
import os

HERE = os.path.dirname(os.path.abspath(__file__))
CONSTANTS_PATH = os.path.join(HERE, 'display_blend_constants.json')
CONSTANTS_SCHEMA = 'display-blend-constants/1'
TOLERANCE255 = 1.0
ANCHOR_GUARD = 0.0625  # the constants state it per fit; 1e-4 made T jump 0 -> tmax below GPU texture noise (probe 2026-09-27)


def srgb_encode(x):
    """Linear to sRGB-encoded (import_model._srgb_encode_value), clamped below at 0."""
    x = max(float(x), 0.0)
    return 12.92 * x if x < 0.0031308 else 1.055 * math.pow(x, 1.0 / 2.4) - 0.055


def srgb_decode(x):
    """sRGB-encoded to linear (import_model._srgb_decode_value), clamped below at 0."""
    x = max(float(x), 0.0)
    return x / 12.92 if x < 0.04045 else math.pow((x + 0.055) / 1.055, 2.4)


def clamp(x, lo=0.0, hi=1.0):
    return min(hi, max(lo, float(x)))


def factor(name, cs, a):
    """One channel of a NIF AlphaFunction factor on display-encoded values."""
    return {'ONE': 1.0, 'ZERO': 0.0, 'SRC_COLOR': cs, 'INV_SRC_COLOR': 1.0 - cs, 'SRC_ALPHA': a,
            'INV_SRC_ALPHA': 1.0 - a}[name]


def k_terms(sf, df, cs, a):
    """(k0, k1) of one channel: the display blend is clamp(k0 + k1 * Cd)."""
    return factor(sf, cs, a) * cs, factor(df, cs, a)


def gamebryo(sf, df, cs, a, cd):
    """Gamebryo's display result for one channel (all display values in [0, 1])."""
    k0, k1 = k_terms(sf, df, cs, a)
    return clamp(k0 + k1 * cd)


def saturation_point(k0, k1):
    """(1 - k0) / k1 with Blender's safe divide (0 where k1 is 0)."""
    return (1.0 - k0) / k1 if k1 != 0.0 else 0.0


def fit_terms(fit, k0, k1):
    """(E, T) of one channel for a fit (see the module docstring)."""
    kind = fit['kind']
    if kind == 'exact':
        return srgb_decode(clamp(k0)), float(k1)
    if kind == 'one':
        xa = float(fit['xa'])
        return max(srgb_decode(clamp(k0 + xa)) - srgb_decode(xa), 0.0), 1.0
    if kind in ('two', 'anchored'):
        x1, x2, tmax = float(fit['x1']), float(fit['x2']), float(fit.get('tmax', 1.0))
        if kind == 'anchored':
            frac, guard = float(fit['frac']), float(fit.get('guard', ANCHOR_GUARD))
            x2 = max(min(x2, x1 + frac * max(saturation_point(k0, k1) - x1, 0.0)), x1 + guard)
        big_x1, big_x2 = srgb_decode(x1), srgb_decode(x2)
        y1, y2 = srgb_decode(clamp(k0 + k1 * x1)), srgb_decode(clamp(k0 + k1 * x2))
        t = clamp((y2 - y1) / (big_x2 - big_x1), 0.0, tmax)
        return max(y1 - t * big_x1, 0.0), t
    raise ValueError('unknown fit kind %r' % (kind,))


def drawn_linear(e, t, cd_display):
    """Blender's scene-linear composite E + T * lin(Cd)."""
    return e + t * srgb_decode(cd_display)


def display(linear):
    """The Standard view: sRGB-encoded, clipped to [0, 1]."""
    return clamp(srgb_encode(linear))


def drawn(fit, sf, df, cs, a, cd):
    """What Blender should show for one channel of one cell under a fit."""
    k0, k1 = k_terms(sf, df, cs, a)
    e, t = fit_terms(fit, k0, k1)
    return display(drawn_linear(e, t, cd))


def lit_terms(lit, a):
    """(m, tau) of a lit-route curve at source alpha ``a``.

    forms: 'power' m = a^p, tau = (1 - a)^p (SRC_ALPHA/INV_SRC_ALPHA);
           'scaled-power' m = min(mmax, s a^p), tau = min(tmax, 1 + q a^r) (SRC_ALPHA/ONE);
           'constant' m, tau (ONE/ONE);
           'linear' (m + mAlpha a)^p, (tau + tauAlpha a)^p (every other lit-scaled equation; the bases clamped below at
           0, an exponent of 1 leaving them as they are, as import_model._pow_f does)."""
    form = lit['form']
    a = clamp(a)
    if form == 'power':
        p = float(lit['p'])
        return math.pow(a, p), math.pow(1.0 - a, p)
    if form == 'scaled-power':
        m = min(float(lit['mmax']), float(lit['s']) * math.pow(a, float(lit['p'])))
        tau = min(float(lit['tmax']), 1.0 + float(lit['q']) * math.pow(a, float(lit['r'])))
        return m, tau
    if form == 'constant':
        return float(lit['m']), float(lit['tau'])
    if form == 'linear':
        p = float(lit['p'])
        bases = (max(float(lit['m']) + float(lit['mAlpha']) * a, 0.0), max(float(lit['tau']) + float(lit['tauAlpha']) * a, 0.0))
        return tuple(base if p == 1.0 else math.pow(base, p) for base in bases)
    raise ValueError('unknown lit form %r' % (form,))


def mix_factors(m):
    """The two Mix Shader factors that realize m in [0, 2]: Add(Mix(f1, None, lit), Mix(f2, None, lit))."""
    return clamp(m), clamp(m - 1.0)


def load_constants(path=None):
    with open(path or CONSTANTS_PATH, 'r', encoding='utf-8') as f:
        document = json.load(f)
    if document.get('schema') != CONSTANTS_SCHEMA:
        raise ValueError('display-blend constants schema %r' % document.get('schema'))
    return document
