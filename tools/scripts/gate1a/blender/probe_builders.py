# SPDX-License-Identifier: 0BSD
"""Synthetic inputs for the Blender hops: Blender packages (v2), GLBs, PNG and DDS images, and the render layouts.

Everything is written from bytes here; no writer code runs. The package layout follows what the Shared package writer
emits (``manifest.json`` first, ``streams/mesh_MMMM_prim_PPPP.<name>.bin`` little-endian tuples, ``images/<sha256>.<ext>``
stored uncompressed; the member names ``import_model.py`` documents in its docstring and reads, cross-checked against a
real corpus manifest). The material blocks mirror what the NIF reader emits for a NIF material (a source declaration with
an ordered BaseColor layer and a render state whose blend factors use ``NifModelRenderStateMapping.BlendFactor``'s
terms), so the Blender probes run the same importer branches a corpus package takes.

Probe sets:
* ``build_d_synthetic`` (hop D): a package covering reversed winding, a degenerate triangle, eight-bit and float colors,
  two UV sets, tangents, welded n-gons with every attribute domain, an unrepresentable attribute, relative and
  absolute morphs, a flat-shaded primitive, a multi-primitive node and a BC1 DDS plus a PNG image.
* ``build_blend_probe`` (hop F): the 11 FNV blend pairs as unlit textured cells over emission background columns, each
  through three copies of the source texture (a PNG, an uncompressed BGRA8 DDS and a solid-block BC3 DDS), plus unlit
  OPAQUE cells that sample every source texel (alpha below 255 included) through each copy: the texel-rule cells. Every
  enabled blend carries the display record the Shared writer emits for it (``display_record_for_blend``), which the
  importer requires and draws, and each blend cell names it, so hop F judges the fit the package declares. The six
  historical sources are joined by the design 6.4 worst-case sources, so the probe exercises every declared bound.
* ``build_lit_blend_probe`` (hop F): the importer's own LIT route, one lit family per curve it draws (over: power,
  SRC_ALPHA/ONE: scaled-power, ONE/ONE: constant, premultiplied over ONE/INV_SRC_ALPHA: the generic linear curve at
  exponent 1, SRC_ALPHA/ZERO: the generic linear curve at its chosen exponent 2.07), lit
  blended cells over the five backgrounds and one opaque lit twin per texel under a sun, so the expectation
  m(As) L_twin + tau(As) Cd needs no lighting model (design 6.4 "lit twin probe").
* ``build_ramp_probe`` (hop F): the asymmetric ramp texture times per-row vertex colors, eight-bit and float storage.
* ``build_orientation_probe`` (hop F): one asymmetric pattern as a PNG, an uncompressed DDS and a BC1 DDS.
* ``build_transmission_glb`` (hop F) and ``build_custom_attribute_glb`` (the hop B' control).
* ``build_display_blend_probe`` (the display-blend probe, run by ``run_display_blend_probe.py``): the 11 FNV pairs drawn
  by display-space fits under BOTH constant sets of ``display_blend_constants.json`` (set A: T <= 1; set B: Transparent
  colors above 1 with E >= 0), through the PNG, BGRA8 and BC3 copies, plus each (set, pair)'s worst-case cell, the
  additive dark-background profile beside the black-anchored alternative K, the two-cell T > 1 sentinels, lit twins for
  the lit route with m in [0, 2], and the opaque texel-rule cells. It writes a spec for the probe-only importer variant
  ``inside_blender/display_blend_variant.py`` (never a package: ``import_model.py`` is not involved).
"""

import hashlib
import io
import json
import os
import struct
import sys
import zipfile

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from hop_b import rebuild_glb  # noqa: E402
import display_blend_math as dbm  # noqa: E402
import import_rules as rules  # noqa: E402

IDENTITY16 = [1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0]

# --------------------------------------------------------------------------------------------------------------------
# Images
# --------------------------------------------------------------------------------------------------------------------


def png_bytes(rgba):
    """An RGBA8 PNG of ``rgba`` (H, W, 4) uint8, rows top to bottom (Pillow, no ancillary chunks)."""
    from PIL import Image
    buffer = io.BytesIO()
    Image.fromarray(np.ascontiguousarray(rgba, dtype=np.uint8), 'RGBA').save(buffer, format='PNG', optimize=False)
    return buffer.getvalue()


def _dds_header(width, height, flags, pitch_or_size, pf_flags, fourcc, bit_count, masks):
    header = struct.pack('<4s7I', b'DDS ', 124, flags, height, width, pitch_or_size, 0, 0)
    header += b'\0' * 44  # dwReserved1[11]
    header += struct.pack('<2I4s5I', 32, pf_flags, fourcc, bit_count, *masks)
    header += struct.pack('<5I', 0x1000, 0, 0, 0, 0)  # DDSCAPS_TEXTURE, caps2..4, reserved2
    return header


def dds_bgra8_bytes(rgba):
    """An uncompressed 32-bit DDS (DDPF_RGB | DDPF_ALPHAPIXELS, A8R8G8B8 masks), rows stored top to bottom."""
    rgba = np.ascontiguousarray(rgba, dtype=np.uint8)
    height, width = rgba.shape[:2]
    header = _dds_header(width, height, 0x1 | 0x2 | 0x4 | 0x8 | 0x1000, width * 4, 0x41, b'\0\0\0\0', 32,
                         (0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000))
    bgra = rgba[:, :, [2, 1, 0, 3]]
    return header + bgra.tobytes()


def rgb565(r, g, b):
    return ((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3)


def rgb565_exact(rgb):
    """The nearest color whose every channel is exactly representable in RGB565 under BOTH common 5/6/5-bit expansions,
    bit replication and round(q * 255 / (2^bits - 1)), ties to the lower level. Decoders differ here: Blender 5.1's BC3
    decode missed the bit-replicated expectation by one 8-bit step on 4 of the 32 five-bit levels (hop F 2026-09-27,
    texel rule worst 1.0/255, amplified to 1.82/255 by the SRC_COLOR pairs), which measures the decoder's rounding, not
    the blend. Only levels both expansions agree on (28 of 32 five-bit, 54 of 64 six-bit) are used."""
    def nearest(value, bits):
        top = (1 << bits) - 1
        levels = [(q << (8 - bits)) | (q >> (2 * bits - 8)) for q in range(1 << bits)
                  if ((q << (8 - bits)) | (q >> (2 * bits - 8))) == int(q * 255 / top + 0.5)]
        return min(levels, key=lambda level: (abs(level - int(value)), level))
    return (nearest(rgb[0], 5), nearest(rgb[1], 6), nearest(rgb[2], 5))


def _solid_blocks(rgba, label, require_opaque):
    """(width, height, [(rgb565 value, alpha byte)] in block order) of an image of solid 4x4 blocks whose colors are
    exactly representable in RGB565; refuses anything else, so a probe never depends on a decoder's interpolation."""
    height, width = rgba.shape[:2]
    if width % 4 or height % 4:
        raise ValueError('%s probe images must be a whole number of 4x4 blocks' % label)
    out = []
    for by in range(height // 4):
        for bx in range(width // 4):
            block = rgba[by * 4:by * 4 + 4, bx * 4:bx * 4 + 4].reshape(-1, 4)
            if not (block == block[0]).all() or (require_opaque and block[0, 3] != 255):
                raise ValueError('%s probe block (%d, %d) is not one %scolor' % (label, bx, by, 'opaque ' if require_opaque else ''))
            r, g, b = (int(v) for v in block[0, :3])
            value = rgb565(r, g, b)
            expanded = ((value >> 11) << 3 | (value >> 11) >> 2, ((value >> 5) & 63) << 2 | ((value >> 5) & 63) >> 4,
                        (value & 31) << 3 | (value & 31) >> 2)
            if expanded != (r, g, b):
                raise ValueError('color %s is not exactly representable in RGB565' % ((r, g, b),))
            out.append((value, int(block[0, 3])))
    return width, height, out


def dds_bc1_bytes(rgba):
    """A DXT1 DDS of an image made of solid 4x4 blocks whose colors are exactly representable in RGB565 with alpha
    255: each block stores color0 == color1 and every selector 0, so any decoder returns the color exactly."""
    width, height, blocks = _solid_blocks(np.ascontiguousarray(rgba, dtype=np.uint8), 'BC1', True)
    data = b''.join(struct.pack('<HHI', value, value, 0) for value, _alpha in blocks)
    header = _dds_header(width, height, 0x1 | 0x2 | 0x4 | 0x1000 | 0x80000, len(data), 0x4, b'DXT1', 0, (0, 0, 0, 0))
    return header + data


def dds_bc3_bytes(rgba):
    """A DXT5 DDS of an image made of solid 4x4 blocks whose colors are exactly representable in RGB565, with ANY 8-bit
    alpha. Each alpha block stores alpha0 == alpha1 == the block's alpha with every 3-bit selector 0 (selector 0 is
    alpha0 in both the eight- and six-value modes), and each color block color0 == color1 with every selector 0 (BC3
    color blocks are always four-color), so any decoder, including a GPU that samples the blocks directly, returns the
    color and alpha bytes exactly."""
    width, height, blocks = _solid_blocks(np.ascontiguousarray(rgba, dtype=np.uint8), 'BC3', False)
    data = b''.join(struct.pack('<BB6x', alpha, alpha) + struct.pack('<HHI', value, value, 0) for value, alpha in blocks)
    header = _dds_header(width, height, 0x1 | 0x2 | 0x4 | 0x1000 | 0x80000, len(data), 0x4, b'DXT5', 0, (0, 0, 0, 0))
    return header + data


# --------------------------------------------------------------------------------------------------------------------
# Packages
# --------------------------------------------------------------------------------------------------------------------

MODULATE = {'sourceFactor': {'constant': [0, 0, 0, 0], 'scale': [1, 1, 1, 1], 'input': 'DestinationColor'},
            'destinationFactor': {'constant': [0, 0, 0, 0], 'scale': [0, 0, 0, 0], 'input': 'Zero'},
            'operation': 'Add', 'clamp': False}

# NifModelRenderStateMapping.BlendFactor (BMT Core/Modeling/Nif/NifModelRenderStateMapping.cs lines 19-35).
NIF_FACTORS = {
    'ONE': {'constant': [1, 1, 1, 1], 'scale': [0, 0, 0, 0], 'input': 'Zero'},
    'ZERO': {'constant': [0, 0, 0, 0], 'scale': [0, 0, 0, 0], 'input': 'Zero'},
    'SRC_COLOR': {'constant': [0, 0, 0, 0], 'scale': [1, 1, 1, 1], 'input': 'SourceColor'},
    'INV_SRC_COLOR': {'constant': [1, 1, 1, 1], 'scale': [-1, -1, -1, -1], 'input': 'SourceColor'},
    'SRC_ALPHA': {'constant': [0, 0, 0, 0], 'scale': [1, 1, 1, 1], 'input': 'SourceAlpha'},
    'INV_SRC_ALPHA': {'constant': [1, 1, 1, 1], 'scale': [-1, -1, -1, -1], 'input': 'SourceAlpha'},
}

# The 11 blend pairs measured in 20,542 loose FNV NIFs (design section 6.2), with their file counts.
BLEND_PAIRS = [('SRC_ALPHA', 'INV_SRC_ALPHA', 3325), ('SRC_ALPHA', 'ONE', 807), ('ZERO', 'SRC_COLOR', 726),
               ('ONE', 'ONE', 227), ('ONE', 'ZERO', 12), ('SRC_COLOR', 'ONE', 9), ('ZERO', 'INV_SRC_ALPHA', 3),
               ('SRC_COLOR', 'SRC_COLOR', 3), ('SRC_ALPHA', 'SRC_COLOR', 1), ('SRC_COLOR', 'ZERO', 1), ('ZERO', 'ZERO', 1)]


def blend_state(source_factor, destination_factor, enabled=True, with_display=True):
    """The render-state blend block the NIF reader writes: the same factors on the color and alpha equations, plus (when
    enabled and ``with_display``) the display record the Shared writer emits for it, which the importer requires."""
    equation = {'sourceFactor': NIF_FACTORS[source_factor], 'destinationFactor': NIF_FACTORS[destination_factor],
                'operation': 'Add', 'clamp': True}
    blend = {'colorEquation': json.loads(json.dumps(equation)), 'alphaEquation': json.loads(json.dumps(equation)),
             'enabled': enabled}
    display = display_record_for_blend(blend) if with_display else None
    if display is not None:
        blend['display'] = display
    return blend


# --------------------------------------------------------------------------------------------------------------------
# The writer's display-space decision, restated (Shared BlendDisplayApproximation over BlendDisplayConstants)
# --------------------------------------------------------------------------------------------------------------------
#
# Gamebryo blends display-encoded values; Blender composites E + T * lin(Cd). The Shared writer classifies each enabled
# affine render-state blend's compiled RGB terms and writes renderState.blend.display {family, fit, bound, lit}; the
# importer draws that fit. The families and constants are set B of display_blend_constants.json (owner decision D1
# taken as set B provisionally), the data the C# BlendDisplayConstants carries; compare_d holds every C#-written record of
# a NIF pair to this restatement, so a drift between the two sides fails hop D. Untabulated equations (no FNV producer in
# the slice-10 census; the E/T-swap control makes four) draw the candidate fit with the smallest grid-measured bound (the
# set-B fits, then set A's two-node fit), and every other lit-scaled equation takes the generic linear lit curve, its
# exponent chosen by the same search as the C# BlendDisplayApproximation.LinearLitRoute and its lit bound grid-measured:
# the C# BlendDisplayBound routine restated in numpy.

DISPLAY_SET = 'B'
# Owner decision D1 (the additive trade-off) is taken as set B provisionally; set K (xa = 0, exact over black, 80.1618/255
# at mid gray) is the alternative. Swapping is a data edit: 'K' here, and BlendDisplayConstants.Additive / LitSourceAlphaOne
# / LitOneOne in the Shared writer, then the re-pins BlendDisplayConstants' remarks list (hop D's constants check then
# holds both sides to set K). The untabulated candidates keep the set-B additive fit whatever D1 decides.
DISPLAY_ADDITIVE_SET = 'B'
# BlendDisplayConstants.UntabulatedCandidates, in their tie-breaking order: (set, pair whose fit it is).
DISPLAY_UNTABULATED_CANDIDATES = (('B', 'SRC_ALPHA/INV_SRC_ALPHA'), ('B', 'ONE/ONE'), ('B', 'ZERO/SRC_COLOR'),
                                  ('B', 'SRC_COLOR/SRC_COLOR'), ('B', 'SRC_ALPHA/SRC_COLOR'), ('A', 'SRC_ALPHA/INV_SRC_ALPHA'))
DISPLAY_LIT_REPLACE = {'form': 'constant', 'm': 1.0, 'tau': 0.0, 'bound': 0.0}  # BlendDisplayConstants.LitReplace
DISPLAY_LIT_FACTOR_MAXIMUM = 2.0  # BlendDisplayConstants.LitFactorMaximum (two Mix Shader factors)
DISPLAY_LINEAR_LIT_TRANSMISSION_MAXIMUM = 1.0  # BlendDisplayConstants.LinearLitTransmissionMaximum
# BlendDisplayConstants.LitExponentCoarse / LitExponentRefinements / LitExponentReach / LitExponentMinimum / Maximum: the
# generic lit curve's exponent search, in hundredths (review fixes 2, finding 4; receipt derive/review_fixes2_mirror.py)
DISPLAY_LIT_EXPONENT_COARSE = (100, 50, 75, 125, 150, 175, 200, 225, 250, 275, 300)
DISPLAY_LIT_EXPONENT_REFINEMENTS = (5, 1)
DISPLAY_LIT_EXPONENT_REACH = 4
DISPLAY_LIT_EXPONENT_RANGE = (50, 300)
DISPLAY_MARGIN255 = 0.05
_TERM_ZERO = [0.0, 0.0, 0.0, 0.0, 0.0]
_TERM_UNIT = [1.0, 0.0, 0.0, 0.0, 0.0]
_TERM_SOURCE = [0.0, 1.0, 0.0, 0.0, 0.0]
_TERM_SOURCE_SQUARED = [0.0, 0.0, 0.0, 1.0, 0.0]
_TERM_SOURCE_ALPHA = [0.0, 0.0, 0.0, 0.0, 1.0]
_TERM_INVERSE_ALPHA = [1.0, 0.0, -1.0, 0.0, 0.0]
_DISPLAY_MEMO = {}


def _term_range(term):
    """BlendGraphTerm.ColorRange: (min, max) of c0 + c1 S + c2 Sa + c3 S^2 + c4 S Sa over S and Sa in [0, 1]."""
    c0, c1, c2, c3, c4 = (float(v) for v in term)

    def quadratic(constant, linear, square):
        at_one = constant + linear + square
        low, high = min(constant, at_one), max(constant, at_one)
        if square != 0.0:
            stationary = -linear / (2.0 * square)
            if 0.0 < stationary < 1.0:
                value = constant + stationary * (linear + stationary * square)
                low, high = min(low, value), max(high, value)
        return low, high

    at_zero, at_one = quadratic(c0, c1, c3), quadratic(c0 + c2, c1 + c4, c3)
    return min(at_zero[0], at_one[0]), max(at_zero[1], at_one[1])


def _fit_of(set_name, pair):
    """A set's fit for ``pair`` as the manifest carries it (numbers as floats)."""
    fit = dbm.load_constants()['sets'][set_name]['pairs'][pair]['fit']
    return {key: (value if key == 'kind' else float(value)) for key, value in fit.items()}


def _tabulated(pair, lit, set_name=DISPLAY_SET):
    """A tabulated record from the set's entry for ``pair`` (the family's fit and pin, as BlendDisplayConstants holds them)."""
    entry = dbm.load_constants()['sets'][set_name]['pairs'][pair]
    record = {'family': entry['family'], 'fit': _fit_of(set_name, pair), 'bound': float(entry['pin255']) / 255.0}
    if lit is not None:
        record['lit'] = lit
    return record


def _lit(pair, set_name=DISPLAY_SET):
    """The set's lit curve for ``pair`` as the manifest carries it (form, parameters, bound), or None."""
    curve = dbm.load_constants()['sets'][set_name]['lit'].get(pair)
    if curve is None:
        return None
    out = {'form': curve['form']}
    for key in ('p', 's', 'mmax', 'tmax', 'q', 'r', 'm', 'tau'):
        if key in curve:
            out[key] = float(curve[key])
    out['bound'] = float(curve['pin255']) / 255.0
    return out


def _lin(x):
    x = np.maximum(np.asarray(x, dtype=np.float64), 0.0)
    return np.where(x < 0.04045, x / 12.92, np.power((x + 0.055) / 1.055, 2.4))


def _enc(x):
    x = np.maximum(np.asarray(x, dtype=np.float64), 0.0)
    return np.where(x < 0.0031308, 12.92 * x, 1.055 * np.power(x, 1.0 / 2.4) - 0.055)


def _fit_terms_array(fit, k0, k1):
    """BlendDisplayFit.Terms on arrays (display_blend_math.fit_terms, vectorized): every form the writer draws."""
    kind = fit['kind']
    if kind == 'exact':
        return _lin(np.clip(k0, 0.0, 1.0)), k1 + 0.0 * k0
    if kind == 'one':
        xa = float(fit['xa'])
        return np.maximum(_lin(np.clip(k0 + xa, 0.0, 1.0)) - float(_lin(xa)), 0.0), np.ones_like(k0 + k1)
    x1, x2, tmax = float(fit['x1']), float(fit['x2']), float(fit['tmax'])
    if kind == 'anchored':
        safe = np.where(k1 != 0.0, k1, 1.0)
        saturation = np.where(k1 != 0.0, (1.0 - k0) / safe, 0.0)
        x2 = np.maximum(np.minimum(x2, x1 + float(fit['frac']) * np.maximum(saturation - x1, 0.0)), x1 + float(fit['guard']))
    elif kind != 'two':
        raise ValueError('unknown fit kind %r' % (kind,))
    y1, y2 = _lin(np.clip(k0 + k1 * x1, 0.0, 1.0)), _lin(np.clip(k0 + k1 * x2, 0.0, 1.0))
    transmission = np.clip((y2 - y1) / (_lin(x2) - float(_lin(x1))), 0.0, tmax)
    return np.maximum(y1 - transmission * float(_lin(x1)), 0.0), transmission


def _poly_array(terms, c, a):
    return terms[0] + terms[1] * c + terms[2] * a + terms[3] * c * c + terms[4] * c * a


def _grid_error(fit, e, t):
    """Vectorized |drawn - Gamebryo| of a fit on one component's terms (BlendDisplayBound.FitError)."""
    def error(c, a, x):
        k0, k1 = _poly_array(e, c, a), _poly_array(t, c, a)
        emission, transmission = _fit_terms_array(fit, k0, k1)
        return np.abs(np.clip(_enc(emission + transmission * _lin(x)), 0.0, 1.0) - np.clip(k0 + k1 * x, 0.0, 1.0))
    return error


def _raise(base, p):
    """BlendDisplayLitCurve's max(base, 0)^p on arrays; an exponent of 1 leaves the base as it is."""
    base = np.maximum(base, 0.0)
    return base if p == 1.0 else np.power(base, p)


def _lit_grid_error(lit, e, t):
    """Vectorized lit error of the generic linear curve (BlendDisplayLitCurve.Error): the grid's first coordinate is the lit
    display color Ld, m lin(Ld) + tau lin(Cd) against clamp(k0(Ld, As) + k1(As) Cd), m = (m + mAlpha As)^p and
    tau = (tau + tauAlpha As)^p."""
    p = float(lit['p'])

    def error(c, a, x):
        m = _raise(float(lit['m']) + float(lit['mAlpha']) * a, p)
        tau = _raise(float(lit['tau']) + float(lit['tauAlpha']) * a, p)
        k0, k1 = _poly_array(e, c, a), _poly_array(t, c, a)
        return np.abs(np.clip(_enc(m * _lin(c) + tau * _lin(x)), 0.0, 1.0) - np.clip(k0 + k1 * x, 0.0, 1.0))
    return error


def _reads_alpha(e, t):
    """BlendDisplayBound.ReadsAlpha: either term has a source-alpha coefficient."""
    return any(float(term[2]) != 0.0 or float(term[4]) != 0.0 for term in (e, t))


def _grid_routine(error, reads_alpha):
    """BlendDisplayBound.Measure: (bound, found) in normalized units. Base grid 257 x 1025 (Cs, Cd), As fixed at 1, or
    33 x 17 x 129 when the terms read As; the 64 worst base cells (error descending, ties by grid order) refined three
    times by 11^3 grids at a tenth of the spacing, clamped and recentered on each level's first maximum; plus 0.05/255."""
    n = (33, 17, 129) if reads_alpha else (257, 1, 1025)
    axes = [np.linspace(0.0, 1.0, n[0]), np.linspace(0.0, 1.0, n[1]) if reads_alpha else np.array([1.0]), np.linspace(0.0, 1.0, n[2])]
    C, A, X = (v.reshape(-1) for v in np.meshgrid(*axes, indexing='ij'))
    values = error(C, A, X)
    best = float(values.max())
    order = np.argsort(-values, kind='stable')[:64]
    centers = np.stack([C[order], A[order], X[order]], 1)
    step = np.array([1.0 / (n[0] - 1), (1.0 / (n[1] - 1)) if reads_alpha else 0.0, 1.0 / (n[2] - 1)])
    offsets = np.arange(-5, 6)
    for _level in range(3):
        step = step / 10.0
        following = []
        for center in centers:
            local = [np.clip(center[k] + step[k] * offsets, 0.0, 1.0) for k in range(3)]
            if not reads_alpha:
                local[1] = np.array([1.0])
            CC, AA, XX = (v.reshape(-1) for v in np.meshgrid(*local, indexing='ij'))
            local_values = error(CC, AA, XX)
            j = int(np.argmax(local_values))
            best = max(best, float(local_values[j]))
            following.append([CC[j], AA[j], XX[j]])
        centers = np.array(following)
    return best + DISPLAY_MARGIN255 / 255.0, best


def display_grid_bound(fit, e, t):
    """BlendDisplayBound.Measure of a fit on one component: (bound, found) in normalized units."""
    e, t = [float(v) for v in e], [float(v) for v in t]
    return _grid_routine(_grid_error(fit, e, t), _reads_alpha(e, t))


def display_lit_grid_bound(lit, e, t):
    """BlendDisplayBound.Measure of the generic linear lit curve (BlendDisplayApproximation.LinearLitRoute)."""
    e, t = [float(v) for v in e], [float(v) for v in t]
    return _grid_routine(_lit_grid_error(lit, e, t), _reads_alpha(e, t))


def _untabulated_record(e, t):
    """BlendDisplayApproximation.Untabulated: every candidate measured over the distinct RGB components; the smallest
    bound wins, an equal bound keeping the earlier candidate."""
    components = [c for c in range(3) if not any(e[k] == e[c] and t[k] == t[c] for k in range(c))]
    chosen, chosen_bound = None, float('inf')
    for set_name, pair in DISPLAY_UNTABULATED_CANDIDATES:
        fit = _fit_of(set_name, pair)
        bound = max(display_grid_bound(fit, e[c], t[c])[0] for c in components)
        if bound < chosen_bound:
            chosen, chosen_bound = fit, bound
    return {'family': 'other', 'fit': chosen, 'bound': chosen_bound}


def _linear_lit(e, t):
    """BlendDisplayApproximation.LitFactors and LinearLitRoute: the generic linear lit curve with its chosen exponent and
    grid-measured bound, the exact replace curve for m 1 and tau 0, or None when the terms do not factor into a lit route
    (the source term the lit color times one factor of the source alpha, the same for R, G and B, within [0, 2] and not
    identically zero, beside a transmission of the source alpha alone, the same for R, G and B, within [0, 1]). The
    exponent search: every coarse exponent in order, then for each refinement step REACH steps on each side of the best so
    far (lower side first, the best skipped); an exponent outside the range or lifting m above 2 is skipped; a strictly
    smaller bound replaces the best, so a tie keeps the earlier exponent (exponent 1 first)."""
    if not all(e[c] == e[0] and t[c] == t[0] for c in (1, 2)):
        return None
    c0, c1, c2, c3, c4 = e[0]
    if c0 != 0.0 or c2 != 0.0 or c3 != 0.0 or (c1 == 0.0 and c4 == 0.0):
        return None
    t0, t1, t2, t3, t4 = t[0]
    if t1 != 0.0 or t3 != 0.0 or t4 != 0.0:
        return None
    if min(c1, c1 + c4) < 0.0 or max(c1, c1 + c4) > DISPLAY_LIT_FACTOR_MAXIMUM:
        return None
    if min(t0, t0 + t2) < 0.0 or max(t0, t0 + t2) > DISPLAY_LINEAR_LIT_TRANSMISSION_MAXIMUM:
        return None
    if (c1, c4, t0, t2) == (1.0, 0.0, 0.0, 0.0):
        return dict(DISPLAY_LIT_REPLACE)
    factor_maximum = max(c1, c1 + c4)
    chosen = {}

    def consider(hundredths):
        if not DISPLAY_LIT_EXPONENT_RANGE[0] <= hundredths <= DISPLAY_LIT_EXPONENT_RANGE[1]:
            return
        exponent = hundredths / 100.0
        if factor_maximum ** exponent > DISPLAY_LIT_FACTOR_MAXIMUM:
            return
        lit = {'form': 'linear', 'p': exponent, 'm': c1, 'tau': t0, 'mAlpha': c4, 'tauAlpha': t2}
        bound = display_lit_grid_bound(lit, e[0], t[0])[0]
        if not chosen or bound < chosen['lit']['bound']:
            lit['bound'] = bound
            chosen.update(hundredths=hundredths, lit=lit)

    for hundredths in DISPLAY_LIT_EXPONENT_COARSE:
        consider(hundredths)
    for step in DISPLAY_LIT_EXPONENT_REFINEMENTS:
        center = chosen['hundredths']
        for k in range(-DISPLAY_LIT_EXPONENT_REACH, DISPLAY_LIT_EXPONENT_REACH + 1):
            if k != 0:
                consider(center + k * step)
    return chosen['lit']


def display_record_and_kinds(blend):
    """(record, kinds) for a manifest blend state, or (None, None) when the state is disabled or not affine. ``record`` is
    the display record the Shared writer emits (``renderState.blend.display``); ``kinds`` says where each bound comes from,
    {'bound': 'exact' | 'pinned' | 'grid', 'lit': None | 'exact' | 'pinned' | 'grid'} (BlendDisplayBoundKind), so a
    comparison can hold a pinned bound exactly and a grid-measured one within a libm tolerance. Mirrors
    BlendDisplayApproximation.Classify: exact when every RGB component has T identically 0, or E identically 0 and T
    identically 1; otherwise the three components must share one pair of terms, which is over, additive (T identically 1,
    E nonnegative), multiplicative (E identically 0, T within [0, 1]) or a tabulated "other" pair; anything else is
    untabulated (``_untabulated_record``); an equation without a tabulated lit curve then takes the generic linear one
    where its terms factor into a lit route (``_linear_lit``)."""
    if blend is None or not bool(blend.get('enabled', True)):
        return None, None
    klass, emission, transmission = rules.compile_blend_state(blend)
    if klass != 'Affine':
        return None, None
    e = [[float(v) for v in term] for term in emission[:3]]
    t = [[float(v) for v in term] for term in transmission[:3]]
    key = (tuple(tuple(v) for v in e), tuple(tuple(v) for v in t))
    if key not in _DISPLAY_MEMO:
        if all(t[c] == _TERM_ZERO or (e[c] == _TERM_ZERO and t[c] == _TERM_UNIT) for c in range(3)):
            replace = all(e[c] == _TERM_SOURCE and t[c] == _TERM_ZERO for c in range(3))
            record, kinds = {'family': 'exact', 'fit': {'kind': 'exact'}, 'bound': 0.0}, {'bound': 'exact', 'lit': None}
            if replace:
                record['lit'] = dict(DISPLAY_LIT_REPLACE)
        else:
            shared = all(e[c] == e[0] and t[c] == t[0] for c in (1, 2))
            record = _tabulated_record(e[0], t[0]) if shared else None
            kinds = {'bound': 'pinned', 'lit': None}
            if record is None:
                record, kinds = _untabulated_record(e, t), {'bound': 'grid', 'lit': None}
        if record.get('lit') is not None:
            kinds['lit'] = 'exact' if record['lit']['bound'] == 0.0 else 'pinned'
        else:
            lit = _linear_lit(e, t)
            if lit is not None:
                record['lit'] = lit
                kinds['lit'] = 'exact' if lit['form'] == 'constant' else 'grid'
        _DISPLAY_MEMO[key] = (record, kinds)
    record, kinds = _DISPLAY_MEMO[key]
    return json.loads(json.dumps(record)), dict(kinds)


def display_record_for_blend(blend):
    """The display record the Shared writer emits for a manifest blend state, or None (``display_record_and_kinds``)."""
    return display_record_and_kinds(blend)[0]


def _tabulated_record(e, t):
    """The tabulated record for one shared pair of RGB terms, or None (see ``display_record_and_kinds``)."""
    if e == _TERM_SOURCE_ALPHA and t == _TERM_INVERSE_ALPHA:
        return _tabulated('SRC_ALPHA/INV_SRC_ALPHA', _lit('SRC_ALPHA/INV_SRC_ALPHA'))
    if t == _TERM_UNIT and _term_range(e)[0] >= 0.0:
        lit = (_lit('SRC_ALPHA/ONE', DISPLAY_ADDITIVE_SET) if e == _TERM_SOURCE_ALPHA
               else _lit('ONE/ONE', DISPLAY_ADDITIVE_SET) if e == _TERM_SOURCE else None)
        return _tabulated('ONE/ONE', lit, DISPLAY_ADDITIVE_SET)
    if e == _TERM_ZERO and 0.0 <= _term_range(t)[0] and _term_range(t)[1] <= 1.0:
        return _tabulated('ZERO/SRC_COLOR', None)
    if e == _TERM_SOURCE_SQUARED and t == _TERM_SOURCE:
        return _tabulated('SRC_COLOR/SRC_COLOR', None)
    if e == _TERM_SOURCE_ALPHA and t == _TERM_SOURCE:
        return _tabulated('SRC_ALPHA/SRC_COLOR', None)
    return None


def display_record(source_factor, destination_factor):
    """The display record of a NIF factor pair (``display_record_for_blend`` on its blend state)."""
    return display_record_for_blend(blend_state(source_factor, destination_factor, with_display=False))


def binding(image_index, sampler=0, uv_set=0):
    return {'image': image_index, 'sampler': sampler, 'textureCoordinateSet': uv_set, 'effectiveTextureCoordinateSet': uv_set}


def layered_material(name, image_index, unlit=True, blend=None, cull=None, double_sided=True, sampler=0):
    """A NIF-shaped material: portable summary, a source declaration with one BaseColor layer, and a render state."""
    layer = {'role': 'BaseColor', 'binding': binding(image_index, sampler), 'swizzle': ['Red', 'Green', 'Blue', 'Alpha'],
             'colorOp': MODULATE, 'alphaOp': MODULATE, 'resultScale': [1, 1, 1, 1], 'constant': [1, 1, 1, 1],
             'clampResult': False}
    state = {'blend': blend if blend is not None else blend_state('SRC_ALPHA', 'INV_SRC_ALPHA', enabled=False),
             'alphaTest': {'compare': 'Greater', 'reference': 0, 'rawReference': 0, 'enabled': False},
             'depth': {'test': True, 'write': True, 'compare': 'LessEqual', 'constantBias': 0, 'slopeBias': 0},
             'drawOrder': {'priority': 0, 'sort': 'BackToFront'},
             'vertexColorUse': {'source': 'AmbientDiffuse', 'lighting': 'EmissiveAmbientDiffuse',
                                'neutralScale': [1, 1, 1, 1], 'useAlphaForOpacity': True}}
    if cull is not None:
        state['cull'] = cull
    blended = blend is not None and blend.get('enabled', True)
    return {'name': name, 'baseColor': [1, 1, 1, 1], 'texture': binding(image_index, sampler),
            'alphaMode': 'Blend' if blended else 'Opaque', 'alphaCutoff': 0.5, 'doubleSided': double_sided,
            'unlit': unlit, 'lightingModel': 'MetallicRoughness', 'normalScale': 1, 'metallicFactor': 0,
            'roughnessFactor': 1, 'indexOfRefraction': 1.5, 'specularFactor': 1, 'specularColorFactor': [0, 0, 0],
            'occlusionStrength': 1, 'emissiveFactor': [0, 0, 0], 'emissiveStrength': 1,
            'source': {'baseColor': [1, 1, 1, 1], 'unlit': unlit, 'lightingModel': 'BlinnPhong', 'layers': [layer],
                       'ambientColor': [1, 1, 1], 'specularColor': [0, 0, 0], 'glossiness': 0,
                       'emissiveColor': [0, 0, 0], 'emissiveMultiplier': 1},
            'renderState': state, 'transmissionFactor': 0}


def plain_material(name, double_sided=True, lit=True):
    """A material with no source declaration and no texture (the portable path)."""
    return {'name': name, 'baseColor': [0.8, 0.8, 0.8, 1], 'alphaMode': 'Opaque', 'alphaCutoff': 0.5,
            'doubleSided': double_sided, 'unlit': not lit, 'lightingModel': 'MetallicRoughness', 'normalScale': 1,
            'metallicFactor': 0, 'roughnessFactor': 1, 'indexOfRefraction': 1.5, 'specularFactor': 1,
            'specularColorFactor': [1, 1, 1], 'occlusionStrength': 1, 'emissiveFactor': [0, 0, 0], 'emissiveStrength': 1,
            'transmissionFactor': 0}


class PackageBuilder:
    """Accumulates streams, images, meshes and nodes, and writes a version-2 package."""

    def __init__(self, name):
        self.name = name
        self.entries = []
        self.images = []
        self.materials = []
        self.meshes = []
        self.nodes = []
        self.samplers = [{'wrapU': 'ClampToEdge', 'wrapV': 'ClampToEdge', 'minFilter': 'Nearest', 'magFilter': 'Nearest',
                          'usesMipmaps': False}]

    def stream(self, mesh_index, primitive_index, stream_name, values, component_type, components):
        dtype = {'Float32': '<f4', 'Int32': '<i4', 'UInt32': '<u4', 'UInt8': '<u1', 'Int8': '<i1', 'Int16': '<i2',
                 'UInt16': '<u2'}[component_type]
        array = np.ascontiguousarray(np.asarray(values, dtype=dtype).reshape(-1, components))
        data = array.tobytes()
        path = 'streams/mesh_%04d_prim_%04d.%s.bin' % (mesh_index, primitive_index, stream_name)
        self.entries.append((path, data))
        return {'path': path, 'componentType': component_type, 'components': components, 'count': int(array.shape[0]),
                'byteStride': array.dtype.itemsize * components, 'byteLength': len(data)}

    def image(self, name, data, container, color_space='sRGB', width=None, height=None):
        sha = hashlib.sha256(data).hexdigest()
        path = 'images/%s.%s' % (sha, container)
        if all(entry[0] != path for entry in self.entries):
            self.entries.append((path, data))
        descriptor = {'mipLevels': [], 'channels': 'Rgba', 'colorSpace': 'Unknown', 'colorSpaceProvenance': 'Unknown',
                      'alphaMeaning': 'Unknown'}
        if width is not None:
            descriptor.update(width=width, height=height, isCube=False)
        self.images.append({'name': name, 'packing': 'Original', 'path': path, 'sha256': sha, 'byteLength': len(data),
                            'container': container, 'colorSpace': color_space, 'originalContainer': container,
                            'descriptor': descriptor, 'origin': 'SourceReference', 'displayTransforms': [],
                            'legacyMipLevelCount': 0})
        return len(self.images) - 1

    def material(self, entry):
        self.materials.append(entry)
        return len(self.materials) - 1

    def primitive(self, mesh_index, primitive_index, name, positions, normals, colors, uvs, indices=None, material=None,
                  color_encoding='FloatingPoint', color_space='Srgb', tangents=None, faces=None, points=None,
                  attributes=(), morphs=(), normal_mode='Vertex'):
        m, p = mesh_index, primitive_index
        positions = np.asarray(positions, dtype=np.float32).reshape(-1, 3)
        entry = {'name': name, 'material': material, 'purpose': 'Render', 'normalMode': normal_mode,
                 'normalProvenance': {'kind': 'Authored'}, 'vertexCount': int(len(positions))}
        if indices is not None:
            entry.update(indexCount=int(len(indices)), triangleCount=int(len(indices) // 3))
        entry.update(colorEncoding=color_encoding, colorSpace=color_space, colorSpaceProvenance='Assumed',
                     position=self.stream(m, p, 'position', positions, 'Float32', 3),
                     normal=self.stream(m, p, 'normal', normals, 'Float32', 3),
                     color=self.stream(m, p, 'color', colors, 'Float32', 4),
                     uv=[self.stream(m, p, 'uv%d' % k, values, 'Float32', 2) for k, values in enumerate(uvs)])
        if tangents is not None:
            entry['tangent'] = self.stream(m, p, 'tangent', tangents, 'Float32', 4)
        if indices is not None:
            entry['indices'] = self.stream(m, p, 'indices', indices, 'Int32', 1)
        if faces is not None:
            sizes, corners = faces
            entry['faces'] = {'faceCount': len(sizes), 'cornerCount': len(corners),
                              'sizes': self.stream(m, p, 'faces.sizes', sizes, 'Int32', 1),
                              'corners': self.stream(m, p, 'faces.corners', corners, 'Int32', 1)}
            if indices is None:
                raise ValueError('a faced primitive still carries its triangulated indices')
        if points is not None:
            point_of_vertex, point_count = points
            entry['points'] = {'pointCount': point_count, 'indices': self.stream(m, p, 'points', point_of_vertex, 'Int32', 1)}
        entry['morphs'] = []
        for t, (morph_name, form, values) in enumerate(morphs):
            key = 'absolutePositions' if form == 'absolute' else 'positionDeltas'
            suffix = 'positions' if form == 'absolute' else 'deltas'
            entry['morphs'].append({'name': morph_name, 'form': form,
                                    key: self.stream(m, p, 'morph_%04d.%s' % (t, suffix), values, 'Float32', 3)})
        entry['attributes'] = []
        for a, (attr_name, domain, component_type, components, normalized, values) in enumerate(attributes):
            stream = self.stream(m, p, 'attr_%04d' % a, values, component_type, components)
            entry['attributes'].append({'name': attr_name, 'semantic': 'gate1a.synthetic', 'domain': domain,
                                        'componentType': component_type, 'components': components, 'count': stream['count'],
                                        'byteStride': stream['byteStride'], 'normalized': normalized, 'stream': stream})
        while len(self.meshes) <= m:
            self.meshes.append({'name': None, 'primitives': []})
        self.meshes[m]['primitives'].append(entry)
        return entry

    def mesh_name(self, mesh_index, name, morph_weights=None):
        while len(self.meshes) <= mesh_index:
            self.meshes.append({'name': None, 'primitives': []})
        self.meshes[mesh_index]['name'] = name
        if morph_weights is not None:
            self.meshes[mesh_index]['morphWeights'] = morph_weights

    def node(self, name, mesh=None, parent=None, translation=(0.0, 0.0, 0.0)):
        matrix = list(IDENTITY16)
        matrix[12], matrix[13], matrix[14] = (float(t) for t in translation)
        entry = {'name': name, 'children': [], 'localMatrix': matrix,
                 'trs': {'translation': [float(t) for t in translation], 'rotation': [0, 0, 0, 1], 'scale': [1, 1, 1]},
                 'role': 'Transform'}
        if parent is not None:
            entry['parent'] = parent
            self.nodes[parent]['children'].append(len(self.nodes))
        if mesh is not None:
            entry['mesh'] = mesh
        self.nodes.append(entry)
        return len(self.nodes) - 1

    def manifest(self):
        roots = [index for index, node in enumerate(self.nodes) if node.get('parent') is None]
        return {'packageVersion': 2, 'name': self.name, 'sourceFormat': 'gate1a.synthetic',
                'sourceIdentity': 'gate1a synthetic %s' % self.name,
                'units': {'metersPerUnit': 1.0, 'provenance': 'Declared', 'evidence': 'synthetic probe', 'declared': True},
                'basis': {'up': [0, 0, 1], 'forward': [0, 1, 0], 'handedness': 'RightHanded', 'normalizedByReader': False,
                          'provenance': 'Declared', 'evidence': 'synthetic probe', 'declared': True},
                'root': {'matrix': list(IDENTITY16), 'scale': 1.0, 'metersPerUnit': 1.0, 'optionScale': 1, 'rotation': 'identity'},
                'layerSelection': {'mode': 'Default', 'ids': []}, 'defaultScene': 0,
                'scenes': [{'name': self.name, 'rootNodes': roots}], 'nodes': self.nodes, 'meshes': self.meshes,
                'materials': self.materials, 'images': self.images, 'samplers': self.samplers, 'skins': [],
                'layerSets': [], 'animationCount': 0, 'fidelity': []}

    def write(self, path, manifest=None):
        os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
        manifest = manifest if manifest is not None else self.manifest()
        with zipfile.ZipFile(path, 'w', compression=zipfile.ZIP_STORED) as package:
            package.writestr(zipfile.ZipInfo('manifest.json', date_time=(2026, 9, 25, 0, 0, 0)), json.dumps(manifest, indent=1))
            for entry_path, data in self.entries:
                package.writestr(zipfile.ZipInfo(entry_path, date_time=(2026, 9, 25, 0, 0, 0)), data)
        return path


def quad(x0, y0, x1, y1, uv=None, uv_rect=None, z=0.0):
    """Four vertices (counter-clockwise seen from +Z) and two triangles; every corner takes ``uv`` when given, else the
    source-convention rectangle ``uv_rect`` (u0, v0 at the top-left corner, v growing downward)."""
    positions = [[x0, y0, z], [x1, y0, z], [x1, y1, z], [x0, y1, z]]
    if uv is not None:
        uvs = [list(uv)] * 4
    else:
        u0, v0, u1, v1 = uv_rect
        uvs = [[u0, v1], [u1, v1], [u1, v0], [u0, v0]]
    return positions, uvs, [0, 1, 2, 0, 2, 3]


def cell_mesh(cells):
    """Concatenate cell quads: ``cells`` is a list of (rect, uv, color) -> positions, normals, colors, uvs, indices."""
    positions, normals, colors, uvs, indices = [], [], [], [], []
    for (x0, y0, x1, y1), uv, color in cells:
        p, u, i = quad(x0, y0, x1, y1, uv=uv)
        base = len(positions)
        positions += p
        uvs += u
        normals += [[0.0, 0.0, 1.0]] * 4
        colors += [list(color)] * 4
        indices += [base + k for k in i]
    return positions, normals, colors, uvs, indices


# --------------------------------------------------------------------------------------------------------------------
# Render layouts
# --------------------------------------------------------------------------------------------------------------------


class Layout:
    """A world rectangle rendered by an orthographic camera looking down -Z at ``pixels_per_unit``; cells are unit
    squares addressed by (row, column) from the top-left, sampled at their center pixel."""

    def __init__(self, columns, rows, pixels_per_unit=12, origin=(0.0, 0.0)):
        self.columns = columns
        self.rows = rows
        self.ppu = pixels_per_unit
        self.x0, self.y1 = origin  # top-left corner
        self.x1 = self.x0 + columns
        self.y0 = self.y1 - rows

    def cell_rect(self, row, column):
        return (self.x0 + column, self.y1 - row - 1, self.x0 + column + 1, self.y1 - row)

    def cell_pixel(self, row, column):
        return [column * self.ppu + self.ppu // 2, row * self.ppu + self.ppu // 2]

    def render(self, engine='CYCLES', samples=16):
        width, height = self.columns * self.ppu, self.rows * self.ppu
        return {'engine': engine, 'samples': samples, 'resolution': [width, height],
                'camera': {'center': [(self.x0 + self.x1) / 2.0, (self.y0 + self.y1) / 2.0], 'height': 10.0,
                           'orthoScale': float(max(self.columns, self.rows))},
                'clip': [0.1, 100.0], 'world': [0.0, 0.0, 0.0]}


def spec_document(probe, layout, cells, background=(), image_pixels=None, engine='CYCLES', samples=16):
    """The JSON the in-Blender probe script reads. ``cells`` is a list of dicts with ``id``, ``row``, ``column`` and any
    metadata the comparator needs (the script reads only id and pixel)."""
    samples_list = []
    cell_table = {}
    for cell in cells:
        pixel = layout.cell_pixel(cell['row'], cell['column'])
        samples_list.append({'id': cell['id'], 'pixel': pixel})
        cell_table[cell['id']] = dict(cell, pixel=pixel)
    return {'schema': 'gate1a-blender-probe-spec/1', 'probe': probe,
            'render': layout.render(engine, samples) if cells else None,
            'background': [dict(b) for b in background], 'samples': samples_list,
            'imagePixels': image_pixels, 'cells': cell_table}


# --------------------------------------------------------------------------------------------------------------------
# Hop D: the synthetic package
# --------------------------------------------------------------------------------------------------------------------

ORIENTATION_BLOCKS = [  # 4 x 4 blocks, top row first; every channel 0 or 255 (exact in RGB565), no row equals its mirror
    [(0, 0, 0), (255, 0, 0), (0, 255, 0), (0, 0, 255)],
    [(0, 255, 255), (255, 0, 255), (255, 255, 0), (255, 255, 255)],
    [(255, 0, 0), (0, 0, 0), (0, 0, 255), (0, 255, 0)],
    [(255, 255, 255), (255, 255, 0), (255, 0, 255), (0, 255, 255)],
]


def orientation_pattern(flip=False):
    """The 16 x 16 RGBA8 asymmetric pattern (rows top to bottom); ``flip`` stores it upside down."""
    image = np.zeros((16, 16, 4), dtype=np.uint8)
    for by, row in enumerate(ORIENTATION_BLOCKS):
        for bx, color in enumerate(row):
            image[by * 4:by * 4 + 4, bx * 4:bx * 4 + 4, :3] = color
            image[by * 4:by * 4 + 4, bx * 4:bx * 4 + 4, 3] = 255
    return image[::-1].copy() if flip else image


def build_d_synthetic(path):
    """The hop-D synthetic package; returns ``path``. See the module docstring for what it covers."""
    builder = PackageBuilder('gate1a_d_synthetic')
    dds = builder.image('textures\\synthetic\\pattern.dds', dds_bc1_bytes(orientation_pattern()), 'dds', 'sRGB', 16, 16)
    normal = np.zeros((4, 4, 4), dtype=np.uint8)
    normal[:, :, 0], normal[:, :, 1], normal[:, :, 2], normal[:, :, 3] = 128, 128, 255, 200
    normal[0, 0] = (140, 120, 250, 17)
    png = builder.image('textures\\synthetic\\normal.png', png_bytes(normal), 'png', 'Non-Color', 4, 4)
    reversed_material = builder.material(layered_material('reversed', dds, unlit=False, cull='Front', double_sided=False))
    flat_material = builder.material(plain_material('flat'))
    builder.materials[flat_material]['normalTexture'] = binding(png)

    # mesh 0: triangles with a degenerate one, eight-bit colors, two UV sets, tangents, one relative morph.
    builder.mesh_name(0, 'synthetic_triangles', morph_weights=[0.25])
    positions = [[0, 0, 0], [1, 0, 0], [1, 1, 0], [0, 1, 0]]
    builder.primitive(0, 0, 'tri', positions, [[0, 0, 1], [0, 0.6, 0.8], [0, 0, 2], [0.1, 0.2, 0.97467943]],
                      [[k / 255.0 for k in c] for c in ((255, 0, 0, 255), (0, 128, 0, 200), (1, 2, 3, 4), (250, 251, 252, 253))],
                      [[[0, 0], [1, 0], [1, 1], [0, 1]], [[0.25, 0.5], [0.75, 0.5], [0.75, 0.125], [0.25, 0.125]]],
                      indices=[0, 1, 2, 0, 2, 3, 1, 1, 2], material=reversed_material,
                      color_encoding='UnsignedByteNormalized',
                      tangents=[[1, 0, 0, 1], [1, 0, 0, -1], [0, 1, 0, 1], [0.6, 0.8, 0, 1]],
                      morphs=[('bend', 'relative', [[0, 0, 0.5], [0, 0, 0.25], [0.125, 0, 0], [-0.0, 0, 1]])])

    # mesh 1, primitive 0: a quad and a triangle over 7 vertices welded to 5 points, float colors, every attribute
    # domain, an unrepresentable UInt32 x 2 stream, an absolute morph consistent across welded vertices.
    builder.mesh_name(1, 'synthetic_ngons')
    point_of_vertex = [0, 1, 2, 3, 1, 4, 3]
    point_positions = [[2, 0, 0], [3, 0, 0], [3, 1, 0], [2, 1, 0], [4, 0.5, 0]]
    ngon_positions = [point_positions[p] for p in point_of_vertex]
    absolute = [[x, y, z + 0.5 * p] for p, (x, y, z) in zip(point_of_vertex, ngon_positions)]
    builder.primitive(1, 0, 'ngon', ngon_positions, [[0, 0, 1]] * 7,
                      [[0.1 * v, 0.5, 1.0 - 0.1 * v, 1.0] for v in range(7)],
                      [[[0, 0], [1, 0], [1, 1], [0, 1], [0.2, 0.3], [0.9, 0.5], [0.4, 0.6]]],
                      indices=[0, 1, 2, 0, 2, 3, 4, 5, 6], material=flat_material,
                      faces=([4, 3], [0, 1, 2, 3, 4, 5, 6]), points=(point_of_vertex, 5),
                      attributes=[('test.vec3', 'Vertex', 'Float32', 3, False, [[v, v + 0.5, -v] for v in range(7)]),
                                  ('test.rgba8', 'Vertex', 'UInt8', 4, True, [[v * 30, 255 - v, 7, 200] for v in range(7)]),
                                  ('test.faceid', 'Face', 'Int32', 1, False, [[11], [-22]]),
                                  ('test.corner', 'FaceCorner', 'Float32', 1, False, [[c * 0.5] for c in range(7)]),
                                  ('test.pointuv', 'Point', 'Float32', 2, False, [[p, -p] for p in range(5)]),
                                  ('test.raw', 'Vertex', 'UInt32', 2, False, [[v, v * 3] for v in range(7)])],
                      morphs=[('lift', 'absolute', absolute)])
    # mesh 1, primitive 1: a flat-shaded triangle without colors of interest.
    builder.primitive(1, 1, 'flat', [[5, 0, 0], [6, 0, 0], [5, 1, 0]], [[0, 0, 1]] * 3, [[1, 1, 1, 1]] * 3,
                      [[[0, 0], [1, 0], [0, 1]]], indices=[0, 1, 2], material=flat_material, normal_mode='Flat')
    root = builder.node('root')
    builder.node('triangles', mesh=0, parent=root)
    ngons = builder.node('ngons', mesh=1, parent=root, translation=(0.0, 2.0, 0.0))
    builder.nodes[ngons]['extras'] = {'note': 'synthetic node extras', 'weight': 1.5, 'flags': [1, 0, -1]}
    # a node with no TRS: a quarter turn about Z with a non-uniform scale, decomposed by the importer
    rotated = builder.node('rotated', parent=root)
    del builder.nodes[rotated]['trs']
    builder.nodes[rotated]['localMatrix'] = [0.0, 2.0, 0.0, 0.0, -1.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.5, 0.0, 1.0, 2.0, 3.0, 1.0]
    builder.materials[flat_material]['extras'] = {'note': 'synthetic material extras'}
    builder.images[png]['extras'] = {'note': 'synthetic image extras'}
    manifest = builder.manifest()
    payload = {'raw': [1, 2.5, -0.25], 'text': 'synthetic native payload'}
    manifest['nativeStates'] = [
        {'target': {'kind': 'Document'}, 'kind': 'gate1a.synthetic', 'version': 1, 'payload': payload},
        {'target': {'kind': 'Scene', 'index': 0}, 'kind': 'gate1a.synthetic', 'version': 1, 'payload': payload},
        {'target': {'kind': 'Node', 'index': ngons}, 'kind': 'gate1a.synthetic', 'version': 1, 'payload': payload},
        {'target': {'kind': 'Material', 'index': flat_material}, 'kind': 'gate1a.synthetic', 'version': 1, 'payload': payload},
        {'target': {'kind': 'Image', 'index': png}, 'kind': 'gate1a.synthetic', 'version': 1, 'payload': payload},
        {'target': {'kind': 'Mesh', 'index': 1}, 'kind': 'gate1a.synthetic', 'version': 1, 'payload': payload},
        {'target': {'kind': 'Primitive', 'index': 1, 'primitiveIndex': 1}, 'kind': 'gate1a.synthetic', 'version': 1, 'payload': payload},
        {'target': {'kind': 'MorphTarget', 'index': 0, 'primitiveIndex': 0}, 'kind': 'gate1a.synthetic', 'version': 1, 'payload': payload},
        {'target': {'kind': 'Node', 'index': ngons}, 'kind': 'gate1a.synthetic.second', 'version': 2, 'payload': None}]
    return builder.write(path, manifest)


# --------------------------------------------------------------------------------------------------------------------
# Hop F: blend pairs, color ramp, DDS orientation
# --------------------------------------------------------------------------------------------------------------------

# The design 6.4 worst-case sources (display-blend DESIGN.md; the display-blend probe's DB_DESIGN_WORST_SOURCES): with them
# every non-exact pair's declared set-B fit reaches its bound within 0.62/255 on some cell (compare_f.check_blend_exercise;
# the six historical sources alone leave over 0.58/255 short, with them 0.13).
BLEND_WORST_SOURCES = [(227, 227, 227, 139), (211, 0, 211, 243), (12, 203, 227, 255), (231, 231, 231, 255), (255, 255, 255, 199)]
BLEND_SOURCES = [(255, 255, 255, 255), (128, 128, 128, 128), (204, 77, 26, 64), (51, 153, 230, 191),
                 (89, 191, 140, 153), (230, 115, 38, 255)] + BLEND_WORST_SOURCES  # display-referred RGBA bytes: Cs and source alpha
BLEND_BACKGROUNDS = [(0, 0, 0), (255, 255, 255), (128, 128, 128), (64, 153, 230), (230, 102, 51)]  # Cd, display bytes
# The BC3 copy carries each source's nearest RGB565-exact color with the source alpha unchanged (BC3 stores 8-bit alpha
# endpoints exactly); its cells state these bytes as their sourceBytes.
BLEND_SOURCES_RGB565 = [rgb565_exact(rgba[:3]) + (rgba[3],) for rgba in BLEND_SOURCES]
# (container kind, file name, cell-id tag, sources). The PNG keeps the historical cell ids (tag None), so the recorded
# 2026-09-25 render still names its cells; the BGRA8 DDS is a byte copy of the PNG's texels; the BC3 DDS is 4 x 4 solid
# blocks (display-blend CHALLENGE item 5: the corpus ships DDS, and the earlier probe tested only a PNG).
BLEND_CONTAINERS = (('png', 'blend_sources.png', None, BLEND_SOURCES),
                    ('dds-bgra8', 'blend_sources_bgra8.dds', 'bgra8', BLEND_SOURCES),
                    ('dds-bc3', 'blend_sources_bc3.dds', 'bc3', BLEND_SOURCES_RGB565))


def texel_uv(column, width, row=0, height=1):
    """The source-convention UV of a texel center (v grows downward from the top row)."""
    return [(column + 0.5) / width, (row + 0.5) / height]


def blend_source_image(kind, sources):
    """(payload bytes, container, width, height, texel(k) -> [x, y]) of one copy of the blend-source texture."""
    if kind == 'dds-bc3':
        texture = np.zeros((4, 4 * len(sources), 4), dtype=np.uint8)
        for k, rgba in enumerate(sources):
            texture[:, 4 * k:4 * k + 4] = rgba
        return dds_bc3_bytes(texture), 'dds', 4 * len(sources), 4, lambda k: [4 * k + 1, 1]
    texture = np.zeros((1, len(sources), 4), dtype=np.uint8)
    for k, rgba in enumerate(sources):
        texture[0, k] = rgba
    if kind == 'dds-bgra8':
        return dds_bgra8_bytes(texture), 'dds', len(sources), 1, lambda k: [k, 0]
    return png_bytes(texture), 'png', len(sources), 1, lambda k: [k, 0]


def build_blend_probe(package_path, spec_path, swap_factors=False, engine='CYCLES', samples=16):
    """The 11 pairs as bands of cells, once per copy of the source texture (``BLEND_CONTAINERS``): rows = container x pair
    x source, columns = background. Below them, one row per source of unlit OPAQUE cells (no blend, no alpha test), one
    column per container: the importer never links the Alpha output there, so these cells show the texel exactly as
    Blender's alpha mode delivers it (``compare_f.check_texels``). ``swap_factors`` exchanges the source and destination
    factors of every pair (the E/T-swap control); it leaves the opaque cells unchanged. Returns the spec (also written)."""
    builder = PackageBuilder('gate1a_f_blend_pairs')
    per_container = len(BLEND_PAIRS) * len(BLEND_SOURCES)
    blend_rows = len(BLEND_CONTAINERS) * per_container
    layout = Layout(len(BLEND_BACKGROUNDS), blend_rows + len(BLEND_SOURCES))
    cells = []
    containers = []
    mesh = 0
    for q, (kind, file_name, tag, sources) in enumerate(BLEND_CONTAINERS):
        data, container, width, height, texel_of = blend_source_image(kind, sources)
        image = builder.image('textures\\probe\\' + file_name, data, container, 'sRGB', width, height)
        containers.append({'kind': kind, 'image': image, 'sha256': hashlib.sha256(data).hexdigest(),
                           'sources': [list(source) for source in sources]})
        suffix = '' if tag is None else '_' + tag
        for p, (sf, df, files) in enumerate(BLEND_PAIRS):
            drawn_sf, drawn_df = (df, sf) if swap_factors else (sf, df)
            material = builder.material(layered_material('pair_%02d_%s_%s%s' % (p, sf, df, suffix), image, unlit=True,
                                                         blend=blend_state(drawn_sf, drawn_df)))
            display = display_record(sf, df)  # what the pristine package declares: every cell is judged against it
            quads = []
            for k in range(len(sources)):
                row = q * per_container + p * len(BLEND_SOURCES) + k
                texel = texel_of(k)
                for j, background in enumerate(BLEND_BACKGROUNDS):
                    quads.append((layout.cell_rect(row, j), texel_uv(texel[0], width, texel[1], height), (1.0, 1.0, 1.0, 1.0)))
                    cell_id = 'pair%02d_src%d_bg%d' % (p, k, j) if tag is None else 'pair%02d_%s_src%d_bg%d' % (p, tag, k, j)
                    cells.append({'id': cell_id, 'kind': 'blend', 'container': kind, 'row': row, 'column': j,
                                  'pair': '%s/%s' % (sf, df), 'texel': texel, 'vertex': 4 * (len(quads) - 1),
                                  'pairIndex': p, 'sourceFactor': sf, 'destinationFactor': df, 'fnvFiles': files,
                                  'drawnSourceFactor': drawn_sf, 'drawnDestinationFactor': drawn_df, 'mesh': mesh,
                                  'sourceBytes': list(sources[k]), 'backgroundBytes': list(background),
                                  'display': display})
            positions, normals, colors, uvs, indices = cell_mesh(quads)
            builder.mesh_name(mesh, 'pair_%02d%s' % (p, suffix))
            builder.primitive(mesh, 0, 'cells', positions, normals, colors, [uvs], indices=indices, material=material)
            builder.node('pair_%02d%s' % (p, suffix), mesh=mesh)
            mesh += 1
    for q, (kind, _file_name, tag, sources) in enumerate(BLEND_CONTAINERS):
        _data, _container, width, height, texel_of = blend_source_image(kind, sources)
        material = builder.material(layered_material('opaque_%s' % kind, containers[q]['image'], unlit=True))
        quads = []
        for k in range(len(sources)):
            row = blend_rows + k
            texel = texel_of(k)
            quads.append((layout.cell_rect(row, q), texel_uv(texel[0], width, texel[1], height), (1.0, 1.0, 1.0, 1.0)))
            cells.append({'id': 'opaque_%s_src%d' % (tag or 'png', k), 'kind': 'opaque', 'container': kind, 'row': row,
                          'column': q, 'texel': texel, 'vertex': 4 * (len(quads) - 1), 'mesh': mesh,
                          'sourceBytes': list(sources[k])})
        positions, normals, colors, uvs, indices = cell_mesh(quads)
        builder.mesh_name(mesh, 'opaque_%s' % kind)
        builder.primitive(mesh, 0, 'cells', positions, normals, colors, [uvs], indices=indices, material=material)
        builder.node('opaque_%s' % kind, mesh=mesh)
        mesh += 1
    background = []
    for j, cd in enumerate(BLEND_BACKGROUNDS):
        x0, _, x1, _ = layout.cell_rect(0, j)
        background.append({'id': 'bg%d' % j, 'rect': [x0, layout.y0, x1, layout.y1], 'z': -1.0,
                           'linear': [rules.srgb_decode(c / 255.0) for c in cd], 'displayBytes': list(cd)})
    builder.write(package_path)
    spec = spec_document('blend-pairs', layout, cells, background, engine=engine, samples=samples)
    spec['containers'] = containers
    spec['control'] = 'factors swapped (E and T exchanged)' if swap_factors else None
    _write_json(spec_path, spec)
    return spec


# The lit families the importer draws a lit route for, one per lit curve form: over (power), SRC_ALPHA/ONE (scaled-power),
# ONE/ONE (constant), premultiplied over (the generic linear curve at exponent 1) and SRC_ALPHA/ZERO (the generic linear
# curve at its chosen exponent 2.07, so the render draws the POWER nodes the exponent adds; review fixes 2, finding 4).
LIT_BLEND_PAIRS = (('SRC_ALPHA', 'INV_SRC_ALPHA'), ('SRC_ALPHA', 'ONE'), ('ONE', 'ONE'), ('ONE', 'INV_SRC_ALPHA'),
                   ('SRC_ALPHA', 'ZERO'))
# The design's alphas 64, 128 and 191 on colored texels, lit over's worst case (a white texel at As 85/255, about 0.33,
# over black: its lit display color is 1) and an opaque white texel.
LIT_BLEND_SOURCES = [(204, 77, 26, 64), (51, 153, 230, 128), (128, 128, 128, 191), (255, 255, 255, 85), (255, 255, 255, 255)]
LIT_SUN_STRENGTH = float(np.pi)  # W/m^2: a Lambert surface facing the sun then reflects exactly its linear albedo
LIT_SAMPLES = 4096  # Cycles samples of the lit cells' border render (its closure pick makes a lit blended cell noisy)
LIT_CELL_MEAN_INSET = 2  # a lit or twin cell is measured over its inner (ppu - 2 * inset)^2 pixels


def build_lit_blend_probe(package_path, spec_path, engine='CYCLES', samples=16):
    """The importer's own lit route (review finding 3, design 6.4 "lit twin probe"): per pair of ``LIT_BLEND_PAIRS`` one
    LIT blended material (the NIF-shaped layered material, lit, with its render-state blend and the display record the
    writer emits, lit curve included), one row per source texel over the five background columns; below them one LIT twin
    per texel drawn with a replace blend (ONE/ZERO: lit curve m 1, tau 0, exact), which shows the lit color L the blended
    cells scale through the SAME forward (blended) path, and below that one OPAQUE lit twin per texel (the same material
    without a blend, ``path`` deferred) as a cross-check: in Cycles the two twins must agree (``compare_f.check_lit_twins``,
    which proves the replace route draws L); EEVEE's deferred path quantizes a dark albedo (measured 2026-09-27: blue
    0.00977 against lin(26/255) = 0.01033), so there the opaque twin is a recorded diagnostic, not the reference. A sun of
    strength pi at normal incidence (no shadow, specular factor 0, no indirect light) lights them. The expectation needs
    no lighting model: m(As) L_twin + tau(As) lin(Cd) (``compare_f.check_lit_blend``). Lit and twin cells are measured
    as inner-block means (``inside_blender/probe_render.py`` runs the display-blend variant's lit pass). Returns the spec
    (also written)."""
    builder = PackageBuilder('gate1a_f_lit_blend')
    count = len(LIT_BLEND_SOURCES)
    texture = np.zeros((1, count, 4), dtype=np.uint8)
    for k, rgba in enumerate(LIT_BLEND_SOURCES):
        texture[0, k] = rgba
    data = png_bytes(texture)
    image = builder.image('textures\\probe\\lit_sources.png', data, 'png', 'sRGB', count, 1)
    rows = len(LIT_BLEND_PAIRS) * count
    layout = Layout(max(len(BLEND_BACKGROUNDS), count), rows + 2)
    cells = []
    mesh = 0
    for p, (sf, df) in enumerate(LIT_BLEND_PAIRS):
        material = builder.material(layered_material('lit_%02d_%s_%s' % (p, sf, df), image, unlit=False, blend=blend_state(sf, df)))
        display = display_record(sf, df)
        quads = []
        for k, source in enumerate(LIT_BLEND_SOURCES):
            row = p * count + k
            for j, background in enumerate(BLEND_BACKGROUNDS):
                quads.append((layout.cell_rect(row, j), texel_uv(k, count), (1.0, 1.0, 1.0, 1.0)))
                cells.append({'id': 'lit%02d_src%d_bg%d' % (p, k, j), 'kind': 'lit', 'row': row, 'column': j,
                              'pair': '%s/%s' % (sf, df), 'pairIndex': p, 'sourceFactor': sf, 'destinationFactor': df,
                              'texel': [k, 0], 'vertex': 4 * (len(quads) - 1), 'mesh': mesh, 'sourceBytes': list(source),
                              'backgroundBytes': list(background), 'twin': 'twin_src%d' % k, 'display': display})
        positions, normals, colors, uvs, indices = cell_mesh(quads)
        builder.mesh_name(mesh, 'lit_%02d' % p)
        builder.primitive(mesh, 0, 'cells', positions, normals, colors, [uvs], indices=indices, material=material)
        builder.node('lit_%02d' % p, mesh=mesh)
        mesh += 1
    for twin_row, (prefix, path, name, blend) in enumerate((('twin_src', 'forward', 'lit_twin', blend_state('ONE', 'ZERO')),
                                                            ('twinopaque_src', 'deferred', 'lit_twin_opaque', None))):
        twin = builder.material(layered_material(name, image, unlit=False, blend=blend))
        quads = []
        for k, source in enumerate(LIT_BLEND_SOURCES):
            quads.append((layout.cell_rect(rows + twin_row, k), texel_uv(k, count), (1.0, 1.0, 1.0, 1.0)))
            cells.append({'id': '%s%d' % (prefix, k), 'kind': 'twin', 'path': path, 'row': rows + twin_row, 'column': k,
                          'texel': [k, 0], 'vertex': 4 * (len(quads) - 1), 'mesh': mesh, 'sourceBytes': list(source)})
        positions, normals, colors, uvs, indices = cell_mesh(quads)
        builder.mesh_name(mesh, name + 's')
        builder.primitive(mesh, 0, 'cells', positions, normals, colors, [uvs], indices=indices, material=twin)
        builder.node(name + 's', mesh=mesh)
        mesh += 1
    background = []
    for j, cd in enumerate(BLEND_BACKGROUNDS):
        x0, _, x1, _ = layout.cell_rect(0, j)
        _, y0, _, _ = layout.cell_rect(rows - 1, j)
        background.append({'id': 'bg%d' % j, 'rect': [x0, y0, x1, layout.y1], 'z': -1.0,
                           'linear': [rules.srgb_decode(c / 255.0) for c in cd], 'displayBytes': list(cd)})
    builder.write(package_path)
    spec = spec_document('lit-blend', layout, cells, background, engine=engine, samples=samples)
    spec['render']['lighting'] = {'sun': {'strength': LIT_SUN_STRENGTH, 'angle': 0.0, 'rotationEuler': [0.0, 0.0, 0.0],
                                          'castShadow': False, 'specularFactor': 0.0}}
    spec['render'].update(pixelsPerUnit=layout.ppu, litSamples=LIT_SAMPLES, cellMeanInset=LIT_CELL_MEAN_INSET)
    spec['control'] = None
    _write_json(spec_path, spec)
    return spec


RAMP_TEXELS = [(round(255 * i / 15), round(255 * (15 - i) / 15), ((i * 7) % 16) * 17, 255) for i in range(16)]
RAMP_VERTEX_COLORS = [(255, 255, 255), (128, 128, 128), (255, 128, 64), (64, 200, 255), (32, 32, 32), (200, 64, 160),
                      (180, 220, 90), (100, 50, 230)]
RAMP_STORAGES = (('ramp_byte', 'UnsignedByteNormalized'), ('ramp_float', 'FloatingPoint'))


def build_ramp_probe(package_path, spec_path, color_space='Srgb', engine='CYCLES', samples=16):
    """The asymmetric ramp: 16 texel columns times 8 vertex-color rows, once with eight-bit and once with float vertex
    color storage. ``color_space='Linear'`` is the control (the gamma-space step omitted). Returns the spec."""
    builder = PackageBuilder('gate1a_f_color_ramp')
    texture = np.array([RAMP_TEXELS], dtype=np.uint8)
    image = builder.image('textures\\probe\\ramp.png', png_bytes(texture), 'png', 'sRGB', 16, 1)
    material = builder.material(layered_material('ramp', image, unlit=True))
    layout = Layout(len(RAMP_TEXELS), len(RAMP_VERTEX_COLORS) * len(RAMP_STORAGES))
    cells = []
    for m, (mesh_name, encoding) in enumerate(RAMP_STORAGES):
        quads = []
        for r, vc in enumerate(RAMP_VERTEX_COLORS):
            row = m * len(RAMP_VERTEX_COLORS) + r
            for c, texel in enumerate(RAMP_TEXELS):
                quads.append((layout.cell_rect(row, c), texel_uv(c, len(RAMP_TEXELS)), [v / 255.0 for v in vc] + [1.0]))
                cells.append({'id': '%s_vc%d_t%02d' % (mesh_name, r, c), 'row': row, 'column': c, 'mesh': m,
                              'texel': [c, 0], 'vertex': 4 * (len(quads) - 1),
                              'storage': encoding, 'texelBytes': list(texel), 'vertexColorBytes': list(vc),
                              'declaredColorSpace': color_space})
        positions, normals, colors, uvs, indices = cell_mesh(quads)
        builder.mesh_name(m, mesh_name)
        builder.primitive(m, 0, 'cells', positions, normals, colors, [uvs], indices=indices, material=material,
                          color_encoding=encoding, color_space=color_space)
        builder.node(mesh_name, mesh=m)
    builder.write(package_path)
    spec = spec_document('color-ramp', layout, cells, engine=engine, samples=samples)
    spec['control'] = 'vertex colors declared Linear (the gamma-space step omitted)' if color_space == 'Linear' else None
    _write_json(spec_path, spec)
    return spec


ORIENTATION_CONTAINERS = (('png', 'orientation.png'), ('dds-bgra8', 'orientation_bgra8.dds'), ('dds-bc1', 'orientation_bc1.dds'))


def build_orientation_probe(package_path, spec_path, flip_dds=False, engine='CYCLES', samples=16):
    """One asymmetric 16 x 16 pattern as a PNG, an uncompressed DDS and a BC1 DDS, each on its own unlit quad of 4 x 4
    cells (one per 4 x 4 block). ``flip_dds`` stores both DDS images upside down (the control). Returns the spec."""
    builder = PackageBuilder('gate1a_f_dds_orientation')
    pattern = orientation_pattern()
    dds_pattern = orientation_pattern(flip=flip_dds)
    payloads = {'png': (png_bytes(pattern), 'png'), 'dds-bgra8': (dds_bgra8_bytes(dds_pattern), 'dds'),
                'dds-bc1': (dds_bc1_bytes(dds_pattern), 'dds')}
    layout = Layout(4 * len(ORIENTATION_CONTAINERS) + len(ORIENTATION_CONTAINERS) - 1, 4)
    cells = []
    images = []
    for q, (kind, file_name) in enumerate(ORIENTATION_CONTAINERS):
        data, container = payloads[kind]
        image = builder.image('textures\\probe\\' + file_name, data, container, 'sRGB', 16, 16)
        images.append({'index': image, 'kind': kind, 'flipped': bool(flip_dds and kind != 'png'),
                       'sha256': hashlib.sha256(data).hexdigest()})
        material = builder.material(layered_material('orientation_%s' % kind, image, unlit=True))
        column0 = q * 5
        x0, _, _, _ = layout.cell_rect(0, column0)
        positions, uvs, indices = quad(x0, layout.y0, x0 + 4, layout.y1, uv_rect=(0.0, 0.0, 1.0, 1.0))
        builder.mesh_name(q, 'orientation_%s' % kind)
        builder.primitive(q, 0, 'quad', positions, [[0, 0, 1]] * 4, [[1, 1, 1, 1]] * 4, [uvs], indices=indices,
                          material=material)
        builder.node('orientation_%s' % kind, mesh=q)
        for by in range(4):
            for bx in range(4):
                cells.append({'id': '%s_b%d%d' % (kind, by, bx), 'row': by, 'column': column0 + bx, 'mesh': q,
                              'texel': [bx * 4 + 2, by * 4 + 2],
                              'kind': kind, 'blockRow': by, 'blockColumn': bx,
                              'expectedBytes': list(ORIENTATION_BLOCKS[by][bx])})
    builder.write(package_path)
    spec = spec_document('dds-orientation', layout, cells, engine=engine, samples=samples,
                         image_pixels={'maxTexels': 4096})
    spec['images'] = images
    spec['pattern'] = [[list(c) for c in row] for row in ORIENTATION_BLOCKS]
    spec['control'] = 'both DDS images stored upside down' if flip_dds else None
    _write_json(spec_path, spec)
    return spec


def _write_json(path, document):
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    with open(path, 'w', encoding='utf-8') as f:
        json.dump(document, f, indent=1)
    return path


# --------------------------------------------------------------------------------------------------------------------
# GLBs: the transmission route (hop F) and the custom-attribute control (hop B')
# --------------------------------------------------------------------------------------------------------------------


class GlbBuilder:
    def __init__(self):
        self.bin = b''
        self.views = []
        self.accessors = []

    def _view(self, data):
        self.bin += b'\0' * ((4 - len(self.bin) % 4) % 4)
        self.views.append({'buffer': 0, 'byteOffset': len(self.bin), 'byteLength': len(data)})
        self.bin += data
        return len(self.views) - 1

    def accessor(self, values, dtype, kind, with_bounds=False):
        array = np.ascontiguousarray(np.asarray(values, dtype=dtype))
        view = self._view(array.tobytes())
        component = {np.float32: 5126, np.uint16: 5123, np.uint32: 5125}[dtype]
        accessor = {'bufferView': view, 'componentType': component, 'count': int(array.shape[0]), 'type': kind}
        if with_bounds:
            accessor['min'] = [float(v) for v in array.min(axis=0)]
            accessor['max'] = [float(v) for v in array.max(axis=0)]
        self.accessors.append(accessor)
        return len(self.accessors) - 1

    def image_view(self, data):
        return self._view(data)

    def document(self, **members):
        doc = {'asset': {'version': '2.0', 'generator': 'gate1a blender harness (synthetic)'},
               'buffers': [{'byteLength': len(self.bin) + (4 - len(self.bin) % 4) % 4}],
               'bufferViews': self.views, 'accessors': self.accessors}
        doc.update(members)
        return doc

    def glb(self, **members):
        return rebuild_glb(self.document(**members), self.bin)


def _quad_mesh_accessors(builder):
    positions = [[0, 0, 0], [1, 0, 0], [1, 1, 0], [0, 1, 0]]
    return {'POSITION': builder.accessor(positions, np.float32, 'VEC3', with_bounds=True),
            'NORMAL': builder.accessor([[0, 0, 1]] * 4, np.float32, 'VEC3'),
            'TEXCOORD_0': builder.accessor([[0, 1], [1, 1], [1, 0], [0, 0]], np.float32, 'VEC2')}, \
        builder.accessor([0, 1, 2, 0, 2, 3], np.uint16, 'SCALAR')


TRANSMISSION_MATERIAL = 'transmission_route'
TRANSMISSION_EXTENSIONS = ('KHR_materials_transmission', 'KHR_materials_ior', 'KHR_materials_emissive_strength')


TRANSMISSION_VERTEX_COLORS = [[1.0, 1.0, 1.0, 1.0], [1.0, 0.5, 0.25, 1.0], [0.5, 1.0, 0.75, 1.0], [0.25, 0.5, 1.0, 1.0]]


def build_transmission_glb(path):
    """A GLB whose one material takes the design's transmission route (section 6.2, R22): transmission 1, IOR 1.0,
    roughness 0, metallic 0, a base-color texture and a per-vertex COLOR_0 (T = baseColor x COLOR_0) and an emissive
    texture times factor and strength (E). T and E are distinct images, so an exchange of the two is visible. The pinned
    writer does not emit the route yet (``material.transmission-unimplemented``), so this is the hop-F input."""
    builder = GlbBuilder()
    attributes, indices = _quad_mesh_accessors(builder)
    attributes['COLOR_0'] = builder.accessor(TRANSMISSION_VERTEX_COLORS, np.float32, 'VEC4')
    base = np.zeros((4, 4, 4), dtype=np.uint8)
    base[:, :, 0], base[:, :, 1], base[:, :, 2], base[:, :, 3] = 200, 150, 90, 255
    glow = np.zeros((4, 4, 4), dtype=np.uint8)
    glow[:, :, 0], glow[:, :, 1], glow[:, :, 2], glow[:, :, 3] = 20, 60, 120, 255
    images = [{'bufferView': builder.image_view(png_bytes(base)), 'mimeType': 'image/png', 'name': 'transmission_T'},
              {'bufferView': builder.image_view(png_bytes(glow)), 'mimeType': 'image/png', 'name': 'emission_E'}]
    material = {'name': TRANSMISSION_MATERIAL, 'alphaMode': 'OPAQUE', 'doubleSided': True,
                'pbrMetallicRoughness': {'baseColorTexture': {'index': 0}, 'baseColorFactor': [1, 1, 1, 1],
                                         'metallicFactor': 0.0, 'roughnessFactor': 0.0},
                'emissiveTexture': {'index': 1}, 'emissiveFactor': [1.0, 1.0, 1.0],
                'extensions': {'KHR_materials_transmission': {'transmissionFactor': 1.0},
                               'KHR_materials_ior': {'ior': 1.0},
                               'KHR_materials_emissive_strength': {'emissiveStrength': 1.0}}}
    data = builder.glb(extensionsUsed=list(TRANSMISSION_EXTENSIONS), images=images,
                       samplers=[{'magFilter': 9728, 'minFilter': 9728, 'wrapS': 33071, 'wrapT': 33071}],
                       textures=[{'source': 0, 'sampler': 0}, {'source': 1, 'sampler': 0}], materials=[material],
                       meshes=[{'name': 'transmission_quad', 'primitives': [{'attributes': attributes, 'indices': indices,
                                                                              'material': 0}]}],
                       nodes=[{'name': 'transmission_quad', 'mesh': 0}], scenes=[{'nodes': [0]}], scene=0)
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    with open(path, 'wb') as f:
        f.write(data)
    return path


def transmission_bindings(glb_path):
    """What the route's material must bind in Blender, from the GLB itself: the SHA-256 (and name) of the image behind
    baseColorTexture (T) and behind emissiveTexture (E), and whether a primitive drawing the material carries COLOR_0
    (glTF multiplies COLOR_0 into the base color, never into the emission)."""
    from glb_reader import Glb
    with open(glb_path, 'rb') as f:
        glb = Glb(f.read())
    document = glb.json
    index = next(i for i, m in enumerate(document['materials']) if m.get('name') == TRANSMISSION_MATERIAL)
    material = document['materials'][index]

    def image_of(info):
        if info is None:
            return None, None
        source = document['textures'][info['index']]['source']
        data, _mime = glb.image_bytes(source)
        return hashlib.sha256(data).hexdigest(), document['images'][source].get('name') or ('Image_%d' % source)

    base_sha, base_name = image_of((material.get('pbrMetallicRoughness') or {}).get('baseColorTexture'))
    emission_sha, emission_name = image_of(material.get('emissiveTexture'))
    vertex_color = any(primitive.get('material') == index and 'COLOR_0' in primitive.get('attributes', {})
                       for mesh in document.get('meshes') or [] for primitive in mesh.get('primitives') or [])
    return {'material': TRANSMISSION_MATERIAL, 'baseColorImageSha256': base_sha, 'baseColorImage': base_name,
            'emissionImageSha256': emission_sha, 'emissionImage': emission_name, 'baseColorVertexColor': vertex_color}


def transmission_expectations(glb_json):
    """The readback.py --expect-* arguments the transmission route implies for its material in ``glb_json``: the four
    numeric values of design section 7.2 row F plus the base-color and emission bindings the material declares."""
    material = next(m for m in glb_json['materials'] if m.get('name') == TRANSMISSION_MATERIAL)
    args = ['--expect-material', TRANSMISSION_MATERIAL, '--expect-transmission', '1', '--expect-ior', '1.0',
            '--expect-roughness', '0', '--expect-metallic', '0']
    if (material.get('pbrMetallicRoughness') or {}).get('baseColorTexture') is not None:
        args.append('--expect-base-color-image')
    if material.get('emissiveTexture') is not None:
        args.append('--expect-emission-image')
    return args


CUSTOM_ATTRIBUTES = (('_PSX_COLOR_0', 'VEC4', 4), ('_PSX_FLAGS_0', 'VEC3', 3))


def build_custom_attribute_glb(path):
    """The hop-B' control: one triangle carrying a VEC4 and a VEC3 underscore custom attribute, the pair NMT measured
    failing in 5 of 12 fresh Blender 5.1 processes (NeversoftMultitool docs/backlog/mesh-fidelity.md, 'VEC4/VEC3
    concatenation error'): io_scene_gltf2's mesh importer names custom attributes from a Python set but keeps their
    arrays in discovery order (blender/imp/mesh.py lines 108-118 and 270-279 of the 5.1 add-on), so a process whose
    hash seed orders the set differently concatenates a VEC3 array onto a VEC4 one."""
    builder = GlbBuilder()
    positions = [[0, 0, 0], [1, 0, 0], [0, 1, 0]]
    attributes = {'POSITION': builder.accessor(positions, np.float32, 'VEC3', with_bounds=True),
                  'NORMAL': builder.accessor([[0, 0, 1]] * 3, np.float32, 'VEC3')}
    for name, kind, components in CUSTOM_ATTRIBUTES:
        attributes[name] = builder.accessor([[0.25 * (c + 1) for c in range(components)]] * 3, np.float32, kind)
    indices = builder.accessor([0, 1, 2], np.uint16, 'SCALAR')
    data = builder.glb(meshes=[{'name': 'custom_attributes', 'primitives': [{'attributes': attributes, 'indices': indices}]}],
                       nodes=[{'name': 'custom_attributes', 'mesh': 0}], scenes=[{'nodes': [0]}], scene=0)
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    with open(path, 'wb') as f:
        f.write(data)
    return path


# --------------------------------------------------------------------------------------------------------------------
# An exact dump (schema 1) for a synthetic package, so hop D compares the synthetic set with a dump like any sample
# --------------------------------------------------------------------------------------------------------------------


def _floats(array):
    return [[float(v) for v in row] for row in np.asarray(array, dtype=np.float32).reshape(len(array), -1)]


def write_synthetic_dump(package_path, dump_path):
    """Write the exact-dump document (``shared/Multitool.Shared/docs/model-dump-json.md`` schema 1, the members
    ``dump_reader`` reads) holding the same values as a package this module wrote; returns ``dump_path``."""
    import base64
    from package_reader import Package
    package = Package(package_path)
    meshes = []
    try:
        for mesh in package.manifest.get('meshes') or []:
            primitives = []
            for primitive in mesh.get('primitives') or []:
                positions = package.stream(primitive['position'])
                normals = package.stream(primitive['normal'])
                colors = package.stream(primitive['color'])
                uvs = [package.stream(stream) for stream in primitive.get('uv') or []]
                vertices = [{'position': p, 'normal': n, 'color': c, 'texCoord': t}
                            for p, n, c, t in zip(_floats(positions), _floats(normals), _floats(colors), _floats(uvs[0]))]
                entry = {'name': primitive.get('name'), 'vertices': vertices,
                         'indices': [int(v) for v in package.stream(primitive['indices']).reshape(-1)],
                         'materialIndex': primitive.get('material'), 'colorEncoding': primitive.get('colorEncoding'),
                         'colorSpace': primitive.get('colorSpace'), 'normalMode': primitive.get('normalMode'),
                         'purpose': primitive.get('purpose'),
                         'tangents': {'values': _floats(package.stream(primitive['tangent']))} if primitive.get('tangent') else None,
                         'additionalTextureCoordinates': [{'values': _floats(values)} for values in uvs[1:]],
                         'skinInfluences': None, 'primaryColorAttributeIndex': None}
                faces = primitive.get('faces')
                entry['faces'] = None if faces is None else {
                    'faceSizes': [int(v) for v in package.stream(faces['sizes']).reshape(-1)],
                    'cornerIndices': [int(v) for v in package.stream(faces['corners']).reshape(-1)],
                    'faceCount': faces['faceCount'], 'cornerCount': faces['cornerCount']}
                points = primitive.get('points')
                entry['pointIndices'] = None if points is None else {
                    'values': [int(v) for v in package.stream(points['indices']).reshape(-1)], 'pointCount': points['pointCount']}
                entry['attributes'] = []
                for declared in primitive.get('attributes') or []:
                    data = package.entry(declared['stream']['path'])
                    entry['attributes'].append({key: declared.get(key) for key in ('name', 'semantic', 'domain', 'componentType',
                                                                                  'components', 'count', 'byteStride', 'normalized')}
                                               | {'colorSpace': None, 'colorSpaceProvenance': None,
                                                  'data': base64.b64encode(data).decode('ascii')})
                targets = []
                for target in primitive.get('morphs') or []:
                    if target.get('form') == 'absolute':
                        absolute = package.stream(target['absolutePositions'])
                        targets.append({'name': target.get('name'), 'absolutePositions': _floats(absolute),
                                        'positionDeltas': _floats(np.subtract(absolute, positions, dtype=np.float32)),
                                        'normalDeltas': None, 'tangentDeltas': None})
                    else:
                        targets.append({'name': target.get('name'), 'positionDeltas': _floats(package.stream(target['positionDeltas'])),
                                        'normalDeltas': None, 'tangentDeltas': None})
                entry['morphTargets'] = targets
                primitives.append(entry)
            meshes.append({'name': mesh.get('name'), 'primitives': primitives, 'morphWeights': mesh.get('morphWeights')})
        document = {'schemaVersion': 1, 'document': {'name': package.manifest.get('name'), 'sourceFormat': 'gate1a.synthetic',
                                                     'units': None, 'sourceBasis': None, 'nodes': [], 'scenes': [],
                                                     'materials': [], 'samplers': [], 'skins': [], 'animations': [],
                                                     'meshes': meshes, 'images': []}}
    finally:
        package.close()
    _write_json(dump_path, document)
    return dump_path


# --------------------------------------------------------------------------------------------------------------------
# The display-blend probe (display-blend design sections 3 and 7, CHALLENGE items 1, 2, 4, 5 and 7): both constant
# sets of display_blend_constants.json drawn by the probe-only importer variant, never through import_model.py
# --------------------------------------------------------------------------------------------------------------------

DB_SETS = ('A', 'B')  # every pair under both constant sets; K (xa = 0) only in the additive profile and worst cells
DB_STANDARD_BACKGROUNDS = BLEND_BACKGROUNDS
DB_PROFILE_GREYS = [0, 8, 16, 24, 32, 48, 64, 96, 128, 160, 192, 224, 255]
DB_SENTINEL_GREYS = [0, 16, 32, 64, 128, 192, 255]
DB_SENTINEL_T = [0.5, 1.0, 1.5, 2.0, 4.0, 8.0, 16.0]
DB_SENTINEL_E = [0.0, 0.05]
DB_SENTINEL_MODES = ('linked', 'socket')  # T and E through Value nodes (as the fits draw them), or as socket defaults
DB_DESIGN_WORST_SOURCES = [(227, 227, 227, 139), (211, 0, 211, 243), (12, 203, 227, 255), (231, 231, 231, 255),
                           (255, 255, 255, 199)]  # display-blend design 6.4
DB_LIT_PAIRS = ('SRC_ALPHA/INV_SRC_ALPHA', 'SRC_ALPHA/ONE', 'ONE/ONE')
DB_LIT_SOURCES = [(204, 77, 26, 64), (51, 153, 230, 191), (128, 128, 128, 128), (255, 255, 255, 255)]
DB_ADDITIVE_PAIRS = ('SRC_ALPHA/ONE', 'ONE/ONE', 'SRC_COLOR/ONE')
DB_SUN_STRENGTH = float(np.pi)  # W/m^2: a Lambert surface facing the sun then reflects exactly its linear albedo
DB_SIDE_COLUMN = 10  # the pair block takes columns 0-9 (set A, then set B, over the five standard backgrounds)
DB_SIDE_WIDTH = 13  # the side block: the dark profile's grey ramp is the widest section
DB_ALPHA_FACTORS = ('SRC_ALPHA', 'INV_SRC_ALPHA')
DB_LIT_SAMPLES = 4096  # Cycles samples of the lit-row border render
DB_CELL_MEAN_INSET = 2  # a lit or twin cell is measured over its inner (ppu - 2 * inset)^2 pixels


def db_is_exact(fit):
    """Whether a fit is exact by construction (no nodes, no bound)."""
    return fit['kind'] == 'exact'


def db_control_fit(set_name, fit):
    """The control's fit for one pristine fit (``constants['controls']``): set A's nodes -> (0, 1) and xa -> 0; set B's
    tmax -> 1. Exact fits and set K are unchanged."""
    fit = dict(fit)
    if set_name == 'A':
        if fit['kind'] == 'two':
            fit.update(x1=0.0, x2=1.0)
        elif fit['kind'] == 'one':
            fit['xa'] = 0.0
    elif set_name == 'B' and fit['kind'] in ('two', 'anchored'):
        fit['tmax'] = 1.0
    return fit


def db_control_lit(pair):
    """Today's lit graph (the lit control): m = the source scale, tau = the destination factor."""
    if pair == 'SRC_ALPHA/INV_SRC_ALPHA':
        return {'form': 'power', 'p': 1.0}
    if pair == 'SRC_ALPHA/ONE':
        return {'form': 'scaled-power', 'mmax': 1.0, 'tmax': 1.0, 's': 1.0, 'p': 1.0, 'q': 0.0, 'r': 1.0}
    return {'form': 'constant', 'm': 1.0, 'tau': 1.0}


DB_LIT_DATA_KEYS = ('bound255', 'found255', 'argmax', 'note', 'pin255', 'analyticLower255', 'analyticLowerAt', 'analyticLowerHow',
                    'unseededFound255', 'polished255', 'clampSensitivity255', 'clampSensitiveAt')


def db_lit_parameters(entry):
    """The drawable part of a lit constants entry (its bound, arg-max, pins and notes are not drawn)."""
    return {key: value for key, value in entry.items() if key not in DB_LIT_DATA_KEYS}


def db_cell_error255(fit, sf, df, cs_byte, as_byte, cd_byte):
    """|drawn - Gamebryo| of one grey channel, in 8-bit steps."""
    cs, a, cd = cs_byte / 255.0, as_byte / 255.0, cd_byte / 255.0
    return abs(dbm.drawn(fit, sf, df, cs, a, cd) - dbm.gamebryo(sf, df, cs, a, cd)) * 255.0


def db_worst_bytes(fit, sf, df, argmax, reach=3):
    """(Cs, As, Cd, error255): the grey byte cell within +-reach steps of the continuous arg-max that maximizes the
    analytic error (ties to the smallest bytes), so the probe exercises the reported bound."""
    reads_alpha = sf in DB_ALPHA_FACTORS or df in DB_ALPHA_FACTORS

    def around(value):
        center = int(round(float(value) * 255.0))
        return sorted({min(255, max(0, center + d)) for d in range(-reach, reach + 1)})

    best = None
    for cs in around(argmax[0]):
        for alpha in (around(argmax[1]) if reads_alpha else [255]):
            for cd in around(argmax[2]):
                error = db_cell_error255(fit, sf, df, cs, alpha, cd)
                if best is None or error > best[3] + 1e-12:
                    best = (cs, alpha, cd, error)
    return best


def db_lit_cell_error255(lit, sf, df, ld_byte, as_byte, cd_byte):
    """|drawn - Gamebryo| of one grey lit channel, in 8-bit steps, with the lit color L the texel's linear value (the
    probe's sun of strength pi makes a Lambert twin reflect exactly its linear albedo): drawn m(As) L + tau(As) lin(Cd),
    Gamebryo blends the lit display color Ld = the texel byte."""
    a, cd = as_byte / 255.0, cd_byte / 255.0
    lit_linear = dbm.srgb_decode(ld_byte / 255.0)
    m, tau = dbm.lit_terms(lit, a)
    drawn = dbm.display(m * lit_linear + tau * dbm.srgb_decode(cd))
    return abs(drawn - dbm.gamebryo(sf, df, ld_byte / 255.0, a, cd)) * 255.0


def db_lit_worst_bytes(lit, sf, df, point, reach=3):
    """(Ld, As, Cd, error255): the grey byte cell within +-reach steps of a continuous (As, Ld, Cd) point that maximizes
    the analytic lit error (ties to the smallest bytes). ONE/ONE ignores As (alpha 255)."""
    reads_alpha = sf in DB_ALPHA_FACTORS or df in DB_ALPHA_FACTORS

    def around(value):
        center = int(round(float(value) * 255.0))
        return sorted({min(255, max(0, center + d)) for d in range(-reach, reach + 1)})

    best = None
    for ld in around(point[1]):
        for alpha in (around(point[0]) if reads_alpha else [255]):
            for cd in around(point[2]):
                error = db_lit_cell_error255(lit, sf, df, ld, alpha, cd)
                if best is None or error > best[3] + 1e-12:
                    best = (ld, alpha, cd, error)
    return best


def db_clamp_error255(fit, sf, df, cs_byte, as_byte, cd_byte):
    """How far one grey channel's drawn value moves when the engine clamps the fit's T at 1, in 8-bit steps."""
    cs, a, cd = cs_byte / 255.0, as_byte / 255.0, cd_byte / 255.0
    e, t = dbm.fit_terms(fit, *dbm.k_terms(sf, df, cs, a))
    return abs(dbm.display(dbm.drawn_linear(e, t, cd)) - dbm.display(dbm.drawn_linear(e, min(t, 1.0), cd))) * 255.0


def db_lit_clamp_error255(lit, ld_byte, as_byte, cd_byte):
    """How far one grey lit channel's drawn value moves when the engine clamps tau at 1 (L the texel's linear value)."""
    m, tau = dbm.lit_terms(lit, as_byte / 255.0)
    base, cd = m * dbm.srgb_decode(ld_byte / 255.0), dbm.srgb_decode(cd_byte / 255.0)
    return abs(dbm.display(base + tau * cd) - dbm.display(base + min(tau, 1.0) * cd)) * 255.0


def db_best_bytes(error, point, reads_alpha, reach=3):
    """(first, alpha, background, value): the grey byte cell within +-reach steps of a continuous point (first, alpha,
    background) that maximizes ``error(first, alpha, background)`` (ties to the smallest bytes)."""
    def around(value):
        center = int(round(float(value) * 255.0))
        return sorted({min(255, max(0, center + d)) for d in range(-reach, reach + 1)})

    best = None
    for first in around(point[0]):
        for alpha in (around(point[1]) if reads_alpha else [255]):
            for cd in around(point[2]):
                value = error(first, alpha, cd)
                if best is None or value > best[3] + 1e-12:
                    best = (first, alpha, cd, value)
    return best


def db_clamp_cases(constants):
    """(blend, lit): for every set-B fit or lit curve that can draw T above 1, the byte-realized point where a clamp of
    T at 1 moves the drawn value most (constants' clampSensitiveAt), as dicts {set, pair, source, backgroundByte,
    clamp255}. The probe draws a cell there so a clamping engine shows on EVERY such pair."""
    blend, lit = [], []
    for pair, entry in constants['sets']['B']['pairs'].items():
        if entry.get('clampSensitiveAt') is None:
            continue
        sf, df = pair.split('/')
        fit = entry['fit']
        cs, alpha, cd, value = db_best_bytes(lambda c, a, x: db_clamp_error255(fit, sf, df, c, a, x), entry['clampSensitiveAt'],
                                             sf in DB_ALPHA_FACTORS or df in DB_ALPHA_FACTORS)
        blend.append({'set': 'B', 'pair': pair, 'source': (cs, cs, cs, alpha), 'backgroundByte': cd, 'clamp255': round(value, 4)})
    for pair, entry in constants['sets']['B'].get('lit', {}).items():
        if entry.get('clampSensitiveAt') is None:
            continue
        sf, df = pair.split('/')
        curve = db_lit_parameters(entry)
        a, ld, cd0 = entry['clampSensitiveAt']
        ld_byte, alpha, cd, value = db_best_bytes(lambda l, al, x: db_lit_clamp_error255(curve, l, al, x), (ld, a, cd0),
                                                  sf in DB_ALPHA_FACTORS or df in DB_ALPHA_FACTORS)
        lit.append({'set': 'B', 'pair': pair, 'locus': 'clamp', 'source': (ld_byte, ld_byte, ld_byte, alpha), 'backgroundByte': cd,
                    'clamp255': round(value, 4)})
    return blend, lit


def db_profile_k0_bytes(constants):
    """The k0 levels the additive dark-background profile renders, per set (K, A, B): the k0 of its worst error over
    the profile's greys, the k0 of its worst error over black when that error is not zero, and the k0 of its global
    arg-max (ONE/ONE, where k0 = Cs)."""
    levels = set()
    for name, rows in constants['darkProfile'].items():
        worst = max(rows, key=lambda r: r['worst255'])
        levels.add(int(round(worst['atK0_255'])))
        if rows[0]['worst255'] > 0.5:
            levels.add(int(round(rows[0]['atK0_255'])))
        levels.add(int(round(constants['sets'][name]['pairs']['ONE/ONE']['argmax'][0] * 255.0)))
    return sorted(level for level in levels if 0 < level < 255)


def db_profile_source(pair, k0_byte):
    """The texel realizing a k0 level for an additive pair (k0 = As for SRC_ALPHA/ONE, Cs for ONE/ONE, Cs^2 for
    SRC_COLOR/ONE, the last to the nearest byte)."""
    if pair == 'SRC_ALPHA/ONE':
        return (255, 255, 255, k0_byte)
    if pair == 'ONE/ONE':
        return (k0_byte, k0_byte, k0_byte, 255)
    c = int(round(np.sqrt(k0_byte / 255.0) * 255.0))
    return (c, c, c, 255)


def db_worst_cases(constants):
    """(blend, lit): [(set, pair, fit, source rgba, background byte, attained255, bound255)] for every non-exact
    (set, pair), set K included; and for every lit curve of sets A and B a list of dicts {set, pair, locus, source,
    backgroundByte, attained255, target255}: the byte-realized SEEDED arg-max (locus 'argmax', target its bound), plus
    the closed-form pin's locus (locus 'pin', target the pin) where its bytes differ from the arg-max's by more than 3
    steps. A lit worst case's texel is the grey Ld with alpha As: the probe's sun makes the lit color the texel."""
    blend, lit = [], []
    for set_name in ('A', 'B', 'K'):
        for pair, entry in constants['sets'][set_name]['pairs'].items():
            fit = entry['fit']
            if db_is_exact(fit):
                continue
            sf, df = pair.split('/')
            cs, alpha, cd, error = db_worst_bytes(fit, sf, df, entry['argmax'])
            blend.append((set_name, pair, fit, (cs, cs, cs, alpha), cd, error, entry['bound255']))
        if set_name not in DB_SETS:
            continue
        for pair, entry in (constants['sets'][set_name].get('lit') or {}).items():
            sf, df = pair.split('/')
            curve = db_lit_parameters(entry)
            loci = [('argmax', entry['argmax'], entry['bound255'])]
            if entry.get('analyticLowerAt') is not None:
                loci.append(('pin', entry['analyticLowerAt'], entry['analyticLower255']))
            seen = []
            for locus, point, target in loci:
                ld, alpha, cd, error = db_lit_worst_bytes(curve, sf, df, point)
                if any(max(abs(ld - s[0]), abs(alpha - s[1]), abs(cd - s[2])) <= 3 for s in seen):
                    continue
                seen.append((ld, alpha, cd))
                lit.append({'set': set_name, 'pair': pair, 'locus': locus, 'source': (ld, ld, ld, alpha), 'backgroundByte': cd,
                            'attained255': round(error, 4), 'target255': target, 'bound255': entry['bound255'],
                            'point': [round(float(v), 6) for v in point]})
    return blend, lit


def db_sources(constants):
    """Every texel the probe samples, in a fixed order, deduplicated: the standard and design worst-case sources, each
    (set, pair) worst case, the profile levels and the lit sources."""
    ordered = list(BLEND_SOURCES) + list(DB_DESIGN_WORST_SOURCES)
    blend, lit = db_worst_cases(constants)
    ordered += [case[3] for case in blend] + [case['source'] for case in lit]
    clamp_blend, clamp_lit = db_clamp_cases(constants)
    ordered += [case['source'] for case in clamp_blend + clamp_lit]
    for pair in DB_ADDITIVE_PAIRS:
        ordered += [db_profile_source(pair, level) for level in db_profile_k0_bytes(constants)]
    ordered += list(DB_LIT_SOURCES)
    out = []
    for source in ordered:
        source = tuple(int(v) for v in source)
        if source not in out:
            out.append(source)
    return out


def db_texel_discriminates(rgba, separation255=2.0):
    """Whether an opaque cell of this texel can show the texel rule: alpha 255 or a black color (every reading agrees,
    nothing to discriminate but nothing to fail), or both premultiplied readings (Cycles floor(c * a / 255), EEVEE
    lin(c) * a) farther than ``separation255`` from the straight texel on some channel."""
    color, alpha = [int(v) for v in rgba[:3]], int(rgba[3])
    if alpha == 255 or max(color) == 0:
        return True
    cycles = max(abs(c - (c * alpha) // 255) for c in color)
    eevee = max(abs(c - dbm.srgb_encode(dbm.srgb_decode(c / 255.0) * alpha / 255.0) * 255.0) for c in color)
    return min(cycles, eevee) > separation255


def db_compiled_terms(sf, df):
    """The RGB E and T terms ([1, S, Sa, S^2, S*Sa] per channel) the importer compiles for the NIF pair."""
    klass, emission, transmission = rules.compile_blend_state(blend_state(sf, df, with_display=False))
    if klass != 'Affine':
        raise ValueError('%s/%s does not compile to an affine blend' % (sf, df))
    return [list(t) for t in emission[:3]], [list(t) for t in transmission[:3]]


def db_write_images(work_dir, sources):
    """Write the PNG, BGRA8 DDS and BC3 DDS copies of the probe texture; returns their spec entries. The BC3 copy
    carries each source's nearest RGB565-exact color with its alpha unchanged."""
    os.makedirs(os.path.join(work_dir, 'images'), exist_ok=True)
    containers = []
    for kind, file_name, _tag, _unused in BLEND_CONTAINERS:
        texel_sources = sources if kind != 'dds-bc3' else [rgb565_exact(rgba[:3]) + (rgba[3],) for rgba in sources]
        data, container, width, height, _texel_of = blend_source_image(kind, texel_sources)
        with open(os.path.join(work_dir, 'images', file_name), 'wb') as f:
            f.write(data)
        containers.append({'index': len(containers), 'kind': kind, 'file': 'images/' + file_name, 'container': container,
                           'width': width, 'height': height, 'sha256': hashlib.sha256(data).hexdigest(),
                           'sources': [list(s) for s in texel_sources]})
    return containers


class DisplayBlendProbe:
    """Accumulates the display-blend probe's materials and cells (``build_display_blend_probe`` drives it)."""

    def __init__(self, constants, containers, sources, control):
        self.constants = constants
        self.containers = containers
        self.sources = sources
        self.source_index = {tuple(s): k for k, s in enumerate(sources)}
        self.control = control
        self.materials = []
        self.cells = []
        self.side_row = 0
        self.blend_materials = {}
        self.lit_materials = {}

    def uv_of(self, k):
        """Every copy is len(sources) texels (or 4 x 4 blocks) wide and one texel (or block) high, so the texel center
        is ((k + 0.5) / N, 0.5) in Blender's convention for all three, independent of any row flip."""
        return [(k + 0.5) / len(self.sources), 0.5]

    def material(self, recipe):
        self.materials.append(dict(recipe, index=len(self.materials), quads=[]))
        return self.materials[-1]

    def add(self, material, row, column, background, cell, uv=(0.5, 0.5)):
        material['quads'].append({'cell': cell['id'], 'uv': list(uv)})
        cell = dict(cell, material=material['index'], row=row, column=column,
                    backgroundBytes=None if background is None else [int(v) for v in background])
        self.cells.append(cell)
        return cell

    def side_rows(self, count):
        """The first of ``count`` fresh rows in the side block (one empty row between sections)."""
        start = self.side_row
        self.side_row += count + 1
        return start

    def blend_material(self, set_name, pair, q):
        key = (set_name, pair, q)
        if key not in self.blend_materials:
            entry = self.constants['sets'][set_name]['pairs'][pair]
            sf, df = pair.split('/')
            e_terms, t_terms = db_compiled_terms(sf, df)
            fit = db_control_fit(set_name, entry['fit']) if self.control else dict(entry['fit'])
            self.blend_materials[key] = self.material({
                'name': 'db_%s_%s_%s_%s' % (set_name, sf, df, self.containers[q]['kind']), 'kind': 'blend', 'image': q,
                'set': set_name, 'pair': pair, 'sourceFactor': sf, 'destinationFactor': df, 'family': entry['family'],
                'fit': fit, 'pristineFit': dict(entry['fit']), 'bound255': entry['bound255'], 'emissionTerms': e_terms,
                'transmissionTerms': t_terms, 'renderMethod': 'BLENDED'})
        return self.blend_materials[key]

    def blend_cell(self, cell_id, section, set_name, pair, q, source, background, row, column):
        material = self.blend_material(set_name, pair, q)
        k = self.source_index[tuple(source)]
        self.add(material, row, column, background,
                 {'id': cell_id, 'kind': 'blend', 'section': section, 'set': set_name, 'pair': pair,
                  'sourceFactor': material['sourceFactor'], 'destinationFactor': material['destinationFactor'],
                  'container': self.containers[q]['kind'], 'sourceBytes': list(self.containers[q]['sources'][k]),
                  'fit': material['fit'], 'pristineFit': material['pristineFit'], 'bound255': material['bound255'],
                  'family': material['family']}, self.uv_of(k))

    def lit_material(self, set_name, pair):
        key = (set_name, pair)
        if key not in self.lit_materials:
            entry = self.constants['sets'][set_name]['lit'][pair]
            sf, df = pair.split('/')
            lit = db_control_lit(pair) if self.control else db_lit_parameters(entry)
            self.lit_materials[key] = self.material({
                'name': 'db_lit_%s_%s_%s' % (set_name, sf, df), 'kind': 'lit', 'image': 0, 'set': set_name, 'pair': pair,
                'sourceFactor': sf, 'destinationFactor': df, 'lit': lit, 'pristineLit': db_lit_parameters(entry),
                'bound255': entry['bound255'], 'renderMethod': 'BLENDED'})
        return self.lit_materials[key]

    # sections -------------------------------------------------------------------------------------------------------

    def pair_block(self):
        """Container x pair x source rows; set A then set B over the five standard backgrounds. Returns its row count."""
        sources = list(BLEND_SOURCES) + list(DB_DESIGN_WORST_SOURCES)
        names = ['%s/%s' % (sf, df) for sf, df, _files in BLEND_PAIRS]
        for q in range(len(self.containers)):
            for p, pair in enumerate(names):
                for k, source in enumerate(sources):
                    row = (q * len(names) + p) * len(sources) + k
                    for s, set_name in enumerate(DB_SETS):
                        for j, background in enumerate(DB_STANDARD_BACKGROUNDS):
                            self.blend_cell('db_%s_%s_p%02d_s%02d_bg%d' % (set_name, self.containers[q]['kind'], p, k, j), 'pairs',
                                            set_name, pair, q, source, background, row, s * len(DB_STANDARD_BACKGROUNDS) + j)
        return len(self.containers) * len(names) * len(sources)

    def profile(self):
        """The additive dark-background profile (PNG): pair x set (K, A, B) x k0 level over the grey ramp."""
        levels = db_profile_k0_bytes(self.constants)
        row = self.side_rows(len(DB_ADDITIVE_PAIRS) * 3 * len(levels))
        for pair in DB_ADDITIVE_PAIRS:
            for set_name in ('K', 'A', 'B'):
                for level in levels:
                    source = db_profile_source(pair, level)
                    for j, grey in enumerate(DB_PROFILE_GREYS):
                        self.blend_cell('db_profile_%s_%s_k%03d_cd%03d' % (set_name, pair.replace('/', '_'), level, grey), 'profile',
                                        set_name, pair, 0, source, (grey, grey, grey), row, DB_SIDE_COLUMN + j)
                    row += 1
        return levels

    def sentinels(self):
        """The two-cell T > 1 probe, widened: constant E and T, no texture, over greys; linked and socket variants."""
        row = self.side_rows(len(DB_SENTINEL_MODES) * len(DB_SENTINEL_E) * len(DB_SENTINEL_T))
        for mode in DB_SENTINEL_MODES:
            for e in DB_SENTINEL_E:
                for t in DB_SENTINEL_T:
                    material = self.material({'name': 'db_sentinel_%s_e%g_t%g' % (mode, e, t), 'kind': 'sentinel', 'image': None,
                                              'sentinel': {'e': e, 't': t, 'mode': mode}, 'renderMethod': 'BLENDED'})
                    for j, grey in enumerate(DB_SENTINEL_GREYS):
                        self.add(material, row, DB_SIDE_COLUMN + j, (grey, grey, grey),
                                 {'id': 'db_sentinel_%s_e%g_t%g_cd%03d' % (mode, e, t, grey), 'kind': 'sentinel',
                                  'section': 'sentinel', 'e': e, 't': t, 'mode': mode})
                    row += 1

    def lit_rows(self, lit_cases, clamp_cases=()):
        """A lit opaque twin, then the lit blended cells: the lit route's E = m(As) * L is judged against the twin's
        rendered L, so no lighting model is assumed. Worst-case rows carry one cell over their own background. Returns
        the lit worst-case and lit clamp tables (spec['litWorstCases'], and the 'clamp' rows of spec['clampCases'])."""
        twin = self.material({'name': 'db_lit_twin', 'kind': 'twin', 'image': 0, 'renderMethod': 'DITHERED'})
        rows = [(set_name, pair, source, None, None) for set_name in DB_SETS for pair in DB_LIT_PAIRS for source in DB_LIT_SOURCES]
        rows += [(case['set'], case['pair'], case['source'], case['backgroundByte'], case) for case in list(lit_cases) + list(clamp_cases)]
        row = self.side_rows(len(rows))
        table = []
        for n, (set_name, pair, source, worst_cd, case) in enumerate(rows):
            material = self.lit_material(set_name, pair)
            k = self.source_index[tuple(source)]
            twin_id = 'db_lit_twin_%s_%s_r%02d' % (set_name, pair.replace('/', '_'), n)
            self.add(twin, row, DB_SIDE_COLUMN, None, {'id': twin_id, 'kind': 'twin', 'section': 'lit', 'sourceBytes': list(source)},
                     self.uv_of(k))
            backgrounds = list(DB_STANDARD_BACKGROUNDS) if worst_cd is None else [(worst_cd, worst_cd, worst_cd)]
            for j, background in enumerate(backgrounds):
                cell_id = 'db_lit_%s_%s_r%02d_bg%d' % (set_name, pair.replace('/', '_'), n, j)
                self.add(material, row, DB_SIDE_COLUMN + 1 + j, background,
                         {'id': cell_id, 'kind': 'lit', 'section': 'lit',
                          'set': set_name, 'pair': pair, 'sourceFactor': material['sourceFactor'],
                          'destinationFactor': material['destinationFactor'], 'sourceBytes': list(source), 'twin': twin_id,
                          'lit': material['lit'], 'pristineLit': material['pristineLit'], 'bound255': material['bound255'],
                          'worstCase': worst_cd is not None}, self.uv_of(k))
                if case is not None:
                    table.append(dict(case, source=list(case['source']), cell=cell_id, twin=twin_id, row=row))
            row += 1
        return [t for t in table if t['locus'] != 'clamp'], [dict(t, kind='lit') for t in table if t['locus'] == 'clamp']

    def worst_cells(self, blend_cases):
        """Each non-exact (set, pair) at its byte-realized arg-max, through every container. Returns the table."""
        row = self.side_rows(len(blend_cases))
        table = []
        for set_name, pair, _fit, source, cd, attained, bound in blend_cases:
            for q in range(len(self.containers)):
                self.blend_cell('db_worst_%s_%s_%s' % (set_name, pair.replace('/', '_'), self.containers[q]['kind']), 'worst',
                                set_name, pair, q, source, (cd, cd, cd), row, DB_SIDE_COLUMN + q)
            table.append({'set': set_name, 'pair': pair, 'sourceBytes': list(source), 'backgroundByte': cd,
                          'attained255': round(attained, 4), 'bound255': bound, 'row': row})
            row += 1
        return table

    def clamp_cells(self, clamp_cases):
        """Each set-B fit that can draw T above 1 at its byte-realized clamp-sensitive point (PNG). Returns the table."""
        row = self.side_rows(len(clamp_cases))
        table = []
        for n, case in enumerate(clamp_cases):
            cell_id = 'db_clamp_B_%s_png' % case['pair'].replace('/', '_')
            cd = case['backgroundByte']
            self.blend_cell(cell_id, 'clamp', 'B', case['pair'], 0, case['source'], (cd, cd, cd), row + n, DB_SIDE_COLUMN)
            table.append(dict(case, kind='blend', source=list(case['source']), cell=cell_id, row=row + n))
        return table

    def opaque_cells(self):
        """Every source that can show the texel rule, through every container, unlit, no blend, the Alpha output
        never linked: opaque texels, and partial-alpha texels whose premultiplied readings sit more than 2/255 from the
        straight texel (compare_f.check_texels fails a partial-alpha cell that cannot discriminate)."""
        shown = [k for k in range(len(self.sources))
                 if all(db_texel_discriminates(info['sources'][k]) for info in self.containers)]
        row = self.side_rows(len(shown))
        for q, info in enumerate(self.containers):
            material = self.material({'name': 'db_opaque_%s' % info['kind'], 'kind': 'opaque', 'image': q, 'renderMethod': 'DITHERED'})
            for n, k in enumerate(shown):
                self.add(material, row + n, DB_SIDE_COLUMN + q, None,
                         {'id': 'db_opaque_%s_src%02d' % (info['kind'], k), 'kind': 'opaque', 'container': info['kind'],
                          'sourceBytes': list(info['sources'][k])}, self.uv_of(k))


def build_display_blend_probe(work_dir, constants=None, control=False, samples=16):
    """Write the images (``<work>/images``) and the spec (``<work>/spec.json``, or ``spec-control.json``) of the
    display-blend probe; returns the spec. ``control`` builds the control copy: the same cells, images and layout,
    with every set-A and set-B fit and every lit curve replaced as ``constants['controls']`` says."""
    constants = constants or dbm.load_constants()
    sources = db_sources(constants)
    containers = db_write_images(work_dir, sources)
    probe = DisplayBlendProbe(constants, containers, sources, control)
    pair_rows = probe.pair_block()
    levels = probe.profile()
    probe.sentinels()
    blend_cases, lit_cases = db_worst_cases(constants)
    clamp_blend, clamp_lit = db_clamp_cases(constants)
    lit_worst_table, lit_clamp_table = probe.lit_rows(lit_cases, clamp_lit)
    worst_table = probe.worst_cells(blend_cases)
    clamp_table = probe.clamp_cells(clamp_blend) + lit_clamp_table
    probe.opaque_cells()

    grid = Layout(DB_SIDE_COLUMN + DB_SIDE_WIDTH, max(pair_rows, probe.side_row))
    cells = {}
    rects = {}
    occupied = {}
    for cell in probe.cells:
        where = (cell['row'], cell['column'])
        if where in occupied:
            raise ValueError('cells %s and %s share row %d column %d' % (occupied[where], cell['id'], where[0], where[1]))
        occupied[where] = cell['id']
        if cell['id'] in cells:
            raise ValueError('duplicate display-blend cell id %s' % cell['id'])
        cell['rect'] = list(grid.cell_rect(cell['row'], cell['column']))
        cell['pixel'] = grid.cell_pixel(cell['row'], cell['column'])
        cells[cell['id']] = cell
        if cell['backgroundBytes'] is not None:
            rects.setdefault(tuple(cell['backgroundBytes']), []).append(cell['rect'])
    for material in probe.materials:
        for quad in material['quads']:
            quad['rect'] = cells[quad['cell']]['rect']
    background = [{'id': 'bg_%03d_%03d_%03d' % color, 'displayBytes': list(color),
                   'linear': [dbm.srgb_decode(c / 255.0) for c in color], 'rects': rect_list, 'z': -1.0}
                  for color, rect_list in sorted(rects.items())]
    render = grid.render('CYCLES', samples)
    render['lighting'] = {'sun': {'strength': DB_SUN_STRENGTH, 'angle': 0.0, 'rotationEuler': [0.0, 0.0, 0.0],
                                  'castShadow': False, 'specularFactor': 0.0}}
    # Cycles picks ONE closure per path vertex, so a lit blended cell (a diffuse BSDF beside a Transparent BSDF) passes
    # the destination through only on the samples that pick the Transparent closure: unbiased but noisy. The variant
    # renders the lit rows again at litSamples inside a border, and every lit or twin cell is measured as the mean of
    # its inner block (cellMeanInset pixels in from each edge).
    render.update(pixelsPerUnit=grid.ppu, litSamples=DB_LIT_SAMPLES, cellMeanInset=DB_CELL_MEAN_INSET)
    spec = {'schema': 'gate1a-display-blend-spec/1', 'probe': 'display-blend', 'control': bool(control),
            'controlChange': constants['controls'] if control else None, 'render': render, 'background': background,
            'images': containers, 'materials': probe.materials,
            'samples': [{'id': cell_id, 'pixel': cell['pixel']} for cell_id, cell in cells.items()],
            'cells': cells, 'worstCases': worst_table, 'litWorstCases': lit_worst_table, 'clampCases': clamp_table,
            'profileLevels': levels, 'profileGreys': DB_PROFILE_GREYS,
            'sentinelGreys': DB_SENTINEL_GREYS, 'sources': [list(s) for s in sources],
            'constantsSha256': hashlib.sha256(json.dumps(constants, sort_keys=True).encode('utf-8')).hexdigest(),
            'constants': constants}
    _write_json(os.path.join(work_dir, 'spec-control.json' if control else 'spec.json'), spec)
    return spec
