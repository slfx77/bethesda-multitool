# SPDX-License-Identifier: 0BSD
"""Display-blend probe: the PROBE-ONLY importer variant. Runs INSIDE Blender 5.1; the driver launches it:

    blender --background --factory-startup --python-exit-code 1 --python display_blend_variant.py -- \\
        --spec <spec.json> --out <result.json> --work <directory> --engine CYCLES|EEVEE

It is not import_model.py and changes nothing there. It builds the probe scene straight from the spec that
``probe_builders.build_display_blend_probe`` wrote: the three copies of the probe texture (PNG, BGRA8 DDS, BC3 DDS, each
loaded as sRGB with alpha mode CHANNEL_PACKED, as the importer now loads every image not declared premultiplied), one
material per recipe and one mesh of cell quads per material, emission background quads grouped by color, a sun for the
lit rows, and the orthographic camera, render settings, EXR and sampling of ``probe_render.py`` (imported from beside
this file). Each blend recipe's graph is the display-space fit the design proposes for the importer (display-blend
design 4.4), with the fit's constants taken from the spec, so a control spec with other constants draws other graphs:

    Sd = clamp(encode(S)), a = clamp(Sa), k0 = poly(E terms, Sd, a), k1 = poly(T terms, Sd, a)   per channel
    exact:    E = decode(clamp(k0)) (E = clamp(S) when k0 is Sd itself), T = k1
    one:      E = max(decode(clamp(k0 + xa)) - lin(xa), 0), T = 1
    two:      Y_i = decode(clamp(k0 + k1 x_i)), T = clamp((Y2 - Y1) / (lin(x2) - lin(x1)), 0, tmax), E = max(Y1 - T lin(x1), 0)
    anchored: as two with x2 -> max(min(x2, x1 + frac max((1 - k0) / k1 - x1, 0)), x1 + guard) (a node, decoded in the graph)
    closure = Emission(E, strength 1) + Transparent BSDF(T), BLENDED
Lit recipes draw Add(Mix(min(m, 1), None, lit), Mix(clamp(m - 1, 0, 1), None, lit)) + Transparent BSDF(tau) with
m(As), tau(As) from the spec's curve; the twin draws the lit closure alone, opaque. Sentinels draw constant E and T,
through Value nodes (``linked``) or socket defaults (``socket``). THE RULE for constants above 1 (and for the importer):
a Transparent BSDF color or Combine Color input above 1 is never a socket default; it arrives through Value nodes, the
path the ``linked`` sentinels measure (only the ``socket`` sentinels keep a default above 1, to measure it). After
building each material the variant READS ITS TREE BACK (``GraphReader``: links, operations, clamp flags and the socket
defaults Blender stored) at every texel of its cells and writes the result into the graph record, so compare_f judges
the graph Blender holds, not the recipe it was given. encode/decode are Math-node chains identical to the
importer's mt_srgb_encode / mt_srgb_decode channel functions (import_model.py _srgb_encode_channel /
_srgb_decode_channel); constants fold in Python as the importer's helpers fold them.

The graph code runs against a small backend interface: ``BpyGraph`` builds nodes; ``NumericGraph`` evaluates the same
calls in float32 without Blender, which is how ``selfcheck_display_blend.py`` proves the graphs equal the analytic
model before any Blender run.

Exit codes: 0 the result was written with no error; 4 the result was written but a step failed (``errors``); 2 usage
or a Blender older than 5.0; 1 any unhandled error (``--python-exit-code 1``).
"""

import argparse
import json
import math
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
if HERE not in sys.path:
    sys.path.insert(0, HERE)

SCHEMA = 'gate1a-blender-probe/1'
MINIMUM_BLENDER = (5, 0, 0)
ANCHOR_GUARD = 0.0625  # the constants state it per fit; 1e-4 made T jump 0 -> tmax below GPU texture noise (probe 2026-09-27)


# --------------------------------------------------------------------------------------------------------------------
# Constant folding (the importer's _add_f / _mul_f discipline: Python floats fold, sockets become Math nodes)
# --------------------------------------------------------------------------------------------------------------------


def is_socket(value):
    """A node output (anything but a Python number or a constant color tuple)."""
    return not isinstance(value, (int, float, tuple, list))


def is_constant_color(value):
    return isinstance(value, (tuple, list))


def encode_value(x):
    x = max(float(x), 0.0)
    return 12.92 * x if x < 0.0031308 else 1.055 * math.pow(x, 1.0 / 2.4) - 0.055


def decode_value(x):
    x = max(float(x), 0.0)
    return x / 12.92 if x < 0.04045 else math.pow((x + 0.055) / 1.055, 2.4)


def add(g, a, b):
    if not is_socket(a) and not is_socket(b):
        return float(a) + float(b)
    if not is_socket(b) and float(b) == 0.0:
        return a
    if not is_socket(a) and float(a) == 0.0:
        return b
    return g.math('ADD', a, b)


def sub(g, a, b):
    if not is_socket(a) and not is_socket(b):
        return float(a) - float(b)
    if not is_socket(b) and float(b) == 0.0:
        return a
    return g.math('SUBTRACT', a, b)


def mul(g, a, b):
    if not is_socket(a) and not is_socket(b):
        return float(a) * float(b)
    if (not is_socket(a) and float(a) == 0.0) or (not is_socket(b) and float(b) == 0.0):
        return 0.0
    if not is_socket(b) and float(b) == 1.0:
        return a
    if not is_socket(a) and float(a) == 1.0:
        return b
    return g.math('MULTIPLY', a, b)


def div(g, a, b):
    """Blender's DIVIDE: a / b, 0 where b is 0."""
    if not is_socket(a) and not is_socket(b):
        return float(a) / float(b) if float(b) != 0.0 else 0.0
    if not is_socket(b) and float(b) == 1.0:
        return a
    return g.math('DIVIDE', a, b)


def power(g, a, b):
    if not is_socket(a) and not is_socket(b):
        return math.pow(max(float(a), 0.0), float(b)) if float(a) > 0.0 or float(b) > 0.0 else 1.0
    if not is_socket(b) and float(b) == 1.0:
        return a
    return g.math('POWER', a, b)


def minimum(g, a, b):
    if not is_socket(a) and not is_socket(b):
        return min(float(a), float(b))
    return g.math('MINIMUM', a, b)


def maximum(g, a, b):
    if not is_socket(a) and not is_socket(b):
        return max(float(a), float(b))
    return g.math('MAXIMUM', a, b)


def clamp(g, value, lo=0.0, hi=1.0):
    if not is_socket(value):
        return min(hi, max(lo, float(value)))
    return g.clamp(value, lo, hi)


def srgb_encode(g, x):
    """import_model._srgb_encode_channel, node for node."""
    if not is_socket(x):
        return encode_value(x)
    positive = g.math('MAXIMUM', x, 0.0)
    linear_part = g.math('MULTIPLY', positive, 12.92)
    powered = g.math('POWER', positive, 1.0 / 2.4)
    curve_part = g.math('SUBTRACT', g.math('MULTIPLY', powered, 1.055), 0.055)
    mask = g.math('LESS_THAN', positive, 0.0031308)
    return add(g, mul(g, mask, linear_part), mul(g, g.math('SUBTRACT', 1.0, mask), curve_part))


def srgb_decode(g, x):
    """import_model._srgb_decode_channel, node for node."""
    if not is_socket(x):
        return decode_value(x)
    positive = g.math('MAXIMUM', x, 0.0)
    linear_part = g.math('DIVIDE', positive, 12.92)
    curve_part = g.math('POWER', g.math('DIVIDE', g.math('ADD', positive, 0.055), 1.055), 2.4)
    mask = g.math('LESS_THAN', positive, 0.04045)
    return add(g, mul(g, mask, linear_part), mul(g, g.math('SUBTRACT', 1.0, mask), curve_part))


def poly(g, terms, s, sa):
    """c0 + c1*S + c2*Sa + c3*S*S + c4*S*Sa for one channel (import_model._poly_rgb, per channel)."""
    c0, c1, c2, c3, c4 = (float(t) for t in terms)
    result = c0
    if c1:
        result = add(g, result, mul(g, c1, s))
    if c2:
        result = add(g, result, mul(g, c2, sa))
    if c3:
        result = add(g, result, mul(g, c3, mul(g, s, s)))
    if c4:
        result = add(g, result, mul(g, c4, mul(g, s, sa)))
    return result


# --------------------------------------------------------------------------------------------------------------------
# The drawn graphs (one channel at a time; backend-agnostic)
# --------------------------------------------------------------------------------------------------------------------


def fit_channel(g, fit, e_terms, t_terms, s_linear, alpha):
    """(E, T) of one channel: the display-space fit of the design, on the compiled terms."""
    sd = clamp(g, srgb_encode(g, s_linear))
    a = clamp(g, alpha)
    k0 = poly(g, e_terms, sd, a)
    k1 = poly(g, t_terms, sd, a)
    kind = fit['kind']
    if kind == 'exact':
        if [float(t) for t in e_terms] == [0.0, 1.0, 0.0, 0.0, 0.0]:
            return clamp(g, s_linear), k1  # k0 is Sd itself: no curve round trip
        return srgb_decode(g, clamp(g, k0)), k1
    if kind == 'one':
        xa = float(fit['xa'])
        return maximum(g, sub(g, srgb_decode(g, clamp(g, add(g, k0, xa))), decode_value(xa)), 0.0), 1.0
    if kind not in ('two', 'anchored'):
        raise ValueError('unknown fit kind %r' % (kind,))
    x1, x2, tmax = float(fit['x1']), float(fit['x2']), float(fit.get('tmax', 1.0))
    big_x1 = decode_value(x1)
    y1 = srgb_decode(g, clamp(g, add(g, k0, mul(g, k1, x1))))
    if kind == 'two':
        y2 = srgb_decode(g, clamp(g, add(g, k0, mul(g, k1, x2))))
        t = clamp(g, mul(g, sub(g, y2, y1), 1.0 / (decode_value(x2) - big_x1)), 0.0, tmax)
    else:
        frac, guard = float(fit['frac']), float(fit.get('guard', ANCHOR_GUARD))
        saturation = div(g, sub(g, 1.0, k0), k1)
        xs2 = maximum(g, minimum(g, x2, add(g, x1, mul(g, frac, maximum(g, sub(g, saturation, x1), 0.0)))), x1 + guard)
        y2 = srgb_decode(g, clamp(g, add(g, k0, mul(g, k1, xs2))))
        t = clamp(g, div(g, sub(g, y2, y1), sub(g, srgb_decode(g, xs2), big_x1)), 0.0, tmax)
    e = maximum(g, sub(g, y1, mul(g, t, big_x1)), 0.0)
    return e, t


def lit_curve(g, lit, alpha):
    """(m, tau) of the lit route at the source alpha."""
    a = clamp(g, alpha)
    form = lit['form']
    if form == 'power':
        p = float(lit['p'])
        return power(g, a, p), power(g, sub(g, 1.0, a), p)
    if form == 'scaled-power':
        m = minimum(g, float(lit['mmax']), mul(g, float(lit['s']), power(g, a, float(lit['p']))))
        tau = minimum(g, float(lit['tmax']), add(g, 1.0, mul(g, float(lit['q']), power(g, a, float(lit['r'])))))
        return m, tau
    if form == 'constant':
        return float(lit['m']), float(lit['tau'])
    raise ValueError('unknown lit form %r' % (form,))


def nonzero(value):
    return is_socket(value) or float(value) != 0.0


def scaled_closure(g, factor, closure):
    """factor * closure for a factor in [0, 1] (Mix Shader against no closure); None when the factor is 0."""
    if not is_socket(factor):
        if float(factor) == 0.0:
            return None
        if float(factor) == 1.0:
            return closure
    return g.mix_shader(factor, None, closure)


def add_closures(g, first, second):
    if first is None:
        return second
    if second is None:
        return first
    return g.add_shader(first, second)


def material_closure(g, recipe):
    """The closure of one material recipe (see the module docstring)."""
    kind = recipe['kind']
    if kind == 'sentinel':
        sentinel = recipe['sentinel']
        e, t = float(sentinel['e']), float(sentinel['t'])
        if sentinel['mode'] == 'linked':
            ev, tv = g.value(e), g.value(t)
            return g.add_shader(g.emission(g.combine(ev, ev, ev)), g.transparent(g.combine(tv, tv, tv)))
        # the 'socket' sentinels are the one place a color above 1 is deliberately a socket default: they measure what
        # Blender stores and composites for it (every drawn fit routes such a constant through a Value node instead)
        return g.add_shader(g.emission((e, e, e)), g.transparent((t, t, t), route='socket'))
    color, alpha = g.texture(recipe['image'])
    if kind == 'opaque':
        return g.emission(color)
    if kind == 'twin':
        return g.principled(color)
    if kind == 'lit':
        lit = g.principled(color)
        m, tau = lit_curve(g, recipe['lit'], alpha)
        emission = add_closures(g, scaled_closure(g, clamp(g, m), lit), scaled_closure(g, clamp(g, sub(g, m, 1.0)), lit))
        transparent = g.transparent(g.combine(tau, tau, tau)) if nonzero(tau) else None
        return add_closures(g, emission, transparent) or g.emission((0.0, 0.0, 0.0))
    if kind != 'blend':
        raise ValueError('unknown recipe kind %r' % (kind,))
    channels = g.separate(color)
    e_rgb, t_rgb = [], []
    for c in range(3):
        e, t = fit_channel(g, recipe['fit'], recipe['emissionTerms'][c], recipe['transmissionTerms'][c], channels[c], alpha)
        e_rgb.append(e)
        t_rgb.append(t)
    emission = g.emission(g.combine(*e_rgb)) if any(nonzero(v) for v in e_rgb) else None
    transparent = g.transparent(g.combine(*t_rgb)) if any(nonzero(v) for v in t_rgb) else None
    return add_closures(g, emission, transparent) or g.emission((0.0, 0.0, 0.0))


# --------------------------------------------------------------------------------------------------------------------
# Backends
# --------------------------------------------------------------------------------------------------------------------


class NumericGraph:
    """Evaluates the graph calls in float32 for one texel (no Blender). A socket is a ``Num``; a closure is a dict
    {'E': rgb, 'T': rgb, 'lit': weight}: the drawn linear result is E + lit * L + T * Cd."""

    class Num:
        def __init__(self, value):
            import numpy as np
            self.value = np.float32(value)

        def __float__(self):
            return float(self.value)

    def __init__(self, texel_linear=(0.0, 0.0, 0.0), alpha=1.0):
        self.texel = [self.Num(v) for v in texel_linear]
        self.alpha = self.Num(alpha)
        self.nodes = 0

    def _f(self, value):
        import numpy as np
        return value.value if isinstance(value, self.Num) else np.float32(value)

    def math(self, operation, a, b):
        import numpy as np
        self.nodes += 1
        x, y = self._f(a), self._f(b)
        with np.errstate(all='ignore'):
            if operation == 'ADD':
                r = x + y
            elif operation == 'SUBTRACT':
                r = x - y
            elif operation == 'MULTIPLY':
                r = x * y
            elif operation == 'DIVIDE':
                r = x / y if y != 0 else np.float32(0.0)
            elif operation == 'POWER':
                r = np.power(x, y) if (x >= 0 or float(y).is_integer()) else np.float32(0.0)
            elif operation == 'MINIMUM':
                r = min(x, y)
            elif operation == 'MAXIMUM':
                r = max(x, y)
            elif operation == 'LESS_THAN':
                r = np.float32(1.0 if x < y else 0.0)
            else:
                raise ValueError(operation)
        return self.Num(r)

    def clamp(self, value, lo, hi):
        import numpy as np
        self.nodes += 1
        return self.Num(min(np.float32(hi), max(np.float32(lo), self._f(value))))

    def value(self, v):
        self.nodes += 1
        return self.Num(v)

    def texture(self, _image):
        return tuple(self.texel), self.alpha

    def separate(self, color):
        return tuple(color)

    def combine(self, r, g, b):
        return (r, g, b)

    def _rgb(self, color):
        return [float(self._f(c)) for c in color]

    def emission(self, color):
        return {'E': self._rgb(color), 'T': [0.0, 0.0, 0.0], 'lit': 0.0}

    def transparent(self, color, route='value'):
        return {'E': [0.0, 0.0, 0.0], 'T': self._rgb(color), 'lit': 0.0}

    def principled(self, _color):
        return {'E': [0.0, 0.0, 0.0], 'T': [0.0, 0.0, 0.0], 'lit': 1.0}

    def add_shader(self, first, second):
        return {'E': [a + b for a, b in zip(first['E'], second['E'])], 'T': [a + b for a, b in zip(first['T'], second['T'])],
                'lit': first['lit'] + second['lit']}

    def mix_shader(self, factor, first, second):
        f = min(1.0, max(0.0, float(self._f(factor))))  # Cycles and EEVEE clamp the Mix Shader factor
        out = {'E': [0.0, 0.0, 0.0], 'T': [0.0, 0.0, 0.0], 'lit': 0.0}
        for weight, closure in ((1.0 - f, first), (f, second)):
            if closure is not None:
                out = {'E': [a + weight * b for a, b in zip(out['E'], closure['E'])],
                       'T': [a + weight * b for a, b in zip(out['T'], closure['T'])], 'lit': out['lit'] + weight * closure['lit']}
        return out


class BpyGraph:
    """Builds the graph calls as shader nodes in one material's node tree."""

    def __init__(self, bpy, tree, images):
        self.bpy = bpy
        self.tree = tree
        self.images = images
        self.nodes = 0
        self._texture = {}
        self.socket_defaults = []

    def node(self, bl_idname):
        self.nodes += 1
        return self.tree.nodes.new(bl_idname)

    def plug(self, value, socket):
        if is_socket(value):
            self.tree.links.new(value, socket)
        else:
            socket.default_value = float(value)

    def math(self, operation, a, b):
        node = self.node('ShaderNodeMath')
        node.operation = operation
        node.use_clamp = False
        self.plug(a, node.inputs[0])
        self.plug(b, node.inputs[1])
        return node.outputs[0]

    def clamp(self, value, lo, hi):
        node = self.node('ShaderNodeClamp')
        node.clamp_type = 'MINMAX'
        self.plug(value, node.inputs['Value'])
        node.inputs['Min'].default_value = float(lo)
        node.inputs['Max'].default_value = float(hi)
        return node.outputs['Result']

    def value(self, v):
        node = self.node('ShaderNodeValue')
        node.outputs[0].default_value = float(v)
        return node.outputs[0]

    def texture(self, image_index):
        if image_index not in self._texture:
            uv = self.node('ShaderNodeUVMap')
            uv.uv_map = 'UVMap'
            tex = self.node('ShaderNodeTexImage')
            tex.image = self.images[image_index]
            tex.interpolation = 'Closest'
            tex.extension = 'EXTEND'
            self.tree.links.new(uv.outputs['UV'], tex.inputs['Vector'])
            self._texture[image_index] = (tex.outputs['Color'], tex.outputs['Alpha'])
        return self._texture[image_index]

    def separate(self, color):
        node = self.node('ShaderNodeSeparateColor')
        node.mode = 'RGB'
        self.tree.links.new(color, node.inputs['Color'])
        return node.outputs['Red'], node.outputs['Green'], node.outputs['Blue']

    def combine(self, r, g, b):
        if not any(is_socket(v) for v in (r, g, b)):
            return (float(r), float(g), float(b))
        node = self.node('ShaderNodeCombineColor')
        node.mode = 'RGB'
        for value, name in ((r, 'Red'), (g, 'Green'), (b, 'Blue')):
            if not is_socket(value) and not 0.0 <= float(value) <= 1.0:
                value = self.value(value)  # never a Combine Color default outside [0, 1] (its declared range)
            self.plug(value, node.inputs[name])
        return node.outputs['Color']

    def _color(self, color, socket):
        if is_constant_color(color):
            socket.default_value = (float(color[0]), float(color[1]), float(color[2]), 1.0)
        else:
            self.tree.links.new(color, socket)

    def emission(self, color):
        node = self.node('ShaderNodeEmission')
        self._color(color, node.inputs['Color'])
        node.inputs['Strength'].default_value = 1.0
        return node.outputs['Emission']

    def transparent(self, color, route='value'):
        """A Transparent BSDF. THE RULE (review 2026-09-27 item 9), for this variant and for the importer: a constant
        color with a component above 1 (a constant T > 1, e.g. set B's lit ONE/ONE tau) is never folded into the
        socket default; it arrives through Value nodes and a Combine Color, the path the 'linked' sentinels measure,
        because whether Blender stores and composites a socket default above 1 is not established. ``route='socket'``
        (the 'socket' sentinels only) keeps the default, to measure exactly that."""
        if is_constant_color(color) and route != 'socket' and any(not 0.0 <= float(c) <= 1.0 for c in color):
            color = self.combine(*(self.value(c) for c in color))
        node = self.node('ShaderNodeBsdfTransparent')
        self._color(color, node.inputs['Color'])
        if is_constant_color(color):
            # read back what Blender stored for a socket default (an RNA range may clamp it)
            self.socket_defaults.append({'socket': 'Transparent BSDF Color', 'route': route, 'set': [float(c) for c in color],
                                         'stored': [float(v) for v in node.inputs['Color'].default_value][:3]})
        return node.outputs['BSDF']

    def principled(self, color):
        node = self.node('ShaderNodeBsdfPrincipled')
        self.tree.links.new(color, node.inputs['Base Color'])
        for name, value in (('Metallic', 0.0), ('Roughness', 1.0), ('IOR', 1.5), ('Alpha', 1.0), ('Specular IOR Level', 0.0),
                            ('Coat Weight', 0.0), ('Sheen Weight', 0.0), ('Transmission Weight', 0.0),
                            ('Subsurface Weight', 0.0), ('Emission Strength', 0.0), ('Diffuse Roughness', 0.0)):
            if name in node.inputs:
                node.inputs[name].default_value = value
        return node.outputs['BSDF']

    def add_shader(self, first, second):
        node = self.node('ShaderNodeAddShader')
        self.tree.links.new(first, node.inputs[0])
        self.tree.links.new(second, node.inputs[1])
        return node.outputs['Shader']

    def mix_shader(self, factor, first, second):
        node = self.node('ShaderNodeMixShader')
        self.plug(factor, node.inputs['Fac'])
        if first is not None:
            self.tree.links.new(first, node.inputs[1])
        if second is not None:
            self.tree.links.new(second, node.inputs[2])
        return node.outputs['Shader']


# --------------------------------------------------------------------------------------------------------------------
# Graph readback (review 2026-09-27 item 8): evaluate the node tree a material HOLDS, not the recipe it was built from
# --------------------------------------------------------------------------------------------------------------------


def texel_key(cell):
    """The readback key of a cell: its source texel bytes, or 'constant' for a sentinel (compare_f.db_texel_key)."""
    if cell.get('kind') == 'sentinel' or cell.get('sourceBytes') is None:
        return 'constant'
    return ','.join(str(int(v)) for v in cell['sourceBytes'])


def cell_texel(cell):
    """(straight linear RGB, alpha) the graph is read back at: the authored texel (the texel rule is checked apart)."""
    if cell.get('kind') == 'sentinel' or cell.get('sourceBytes') is None:
        return [0.0, 0.0, 0.0], 1.0
    return [decode_value(b / 255.0) for b in cell['sourceBytes'][:3]], cell['sourceBytes'][3] / 255.0


class GraphReader:
    """Evaluates a material's node tree for one straight texel in Python floats, reading every link, operation, clamp
    flag and stored socket default back from the tree (never from the recipe), so what Blender kept shows in the
    result: a default stored clamped by an RNA range, a missing link, another operation. A closure is
    {'E': rgb, 'T': rgb, 'lit': weight}; a cell composites E + lit * L + T * lin(Cd), L the lit color. The Image Texture
    returns the straight texel and its alpha. Works on bpy node trees and on mock_node_tree's (the self-check)."""

    MATH = {'ADD': lambda a, b: a + b, 'SUBTRACT': lambda a, b: a - b, 'MULTIPLY': lambda a, b: a * b,
            'DIVIDE': lambda a, b: a / b if b != 0.0 else 0.0,
            'POWER': lambda a, b: math.pow(a, b) if (a > 0.0 or (a == 0.0 and b > 0.0) or (a < 0.0 and float(b).is_integer())) else (1.0 if b == 0.0 else 0.0),
            'MINIMUM': min, 'MAXIMUM': max, 'LESS_THAN': lambda a, b: 1.0 if a < b else 0.0}

    def __init__(self, texel_linear, alpha):
        self.texel = [float(v) for v in texel_linear]
        self.alpha = float(alpha)
        self._memo = {}

    @staticmethod
    def source(socket):
        """The output socket feeding an input, or None."""
        if not socket.is_linked:
            return None
        links = list(socket.links)
        return links[0].from_socket if links else None

    def input(self, node, key):
        socket = node.inputs[key]
        feeding = self.source(socket)
        if feeding is not None:
            return self.output(feeding)
        value = socket.default_value
        if value is None:
            return None
        try:
            return [float(v) for v in value][:3]
        except TypeError:
            return float(value)

    @staticmethod
    def scalar(value):
        if isinstance(value, (list, tuple)):
            raise TypeError('a color reached a scalar input')
        return float(value)

    @staticmethod
    def color(value):
        return [float(v) for v in value[:3]] if isinstance(value, (list, tuple)) else [float(value)] * 3

    def output(self, socket):
        node = socket.node
        key = (node.name, getattr(socket, 'identifier', socket.name))
        if key not in self._memo:
            self._memo[key] = self._evaluate(node, socket.name)
        return self._memo[key]

    def _closure(self, e=None, t=None, lit=0.0):
        return {'E': e or [0.0, 0.0, 0.0], 'T': t or [0.0, 0.0, 0.0], 'lit': float(lit)}

    def _evaluate(self, node, name):
        kind = node.bl_idname
        if kind == 'ShaderNodeMath':
            a, b = self.scalar(self.input(node, 0)), self.scalar(self.input(node, 1))
            r = self.MATH[node.operation](a, b)
            return min(1.0, max(0.0, r)) if node.use_clamp else r
        if kind == 'ShaderNodeClamp':
            if getattr(node, 'clamp_type', 'MINMAX') != 'MINMAX':
                raise ValueError('clamp type %s' % node.clamp_type)
            v, lo, hi = (self.scalar(self.input(node, k)) for k in ('Value', 'Min', 'Max'))
            return min(hi, max(lo, v))
        if kind == 'ShaderNodeValue':
            return float(node.outputs[0].default_value)
        if kind == 'ShaderNodeTexImage':
            return list(self.texel) if name == 'Color' else self.alpha
        if kind == 'ShaderNodeSeparateColor':
            return self.color(self.input(node, 'Color'))[['Red', 'Green', 'Blue'].index(name)]
        if kind == 'ShaderNodeCombineColor':
            return [self.scalar(self.input(node, k)) for k in ('Red', 'Green', 'Blue')]
        if kind == 'ShaderNodeEmission':
            strength = self.scalar(self.input(node, 'Strength'))
            return self._closure(e=[c * strength for c in self.color(self.input(node, 'Color'))])
        if kind == 'ShaderNodeBsdfTransparent':
            return self._closure(t=self.color(self.input(node, 'Color')))
        if kind == 'ShaderNodeBsdfPrincipled':
            return self._closure(lit=1.0)
        if kind == 'ShaderNodeAddShader':
            first, second = (self.source(node.inputs[k]) for k in (0, 1))
            if first is None or second is None:
                raise ValueError('an Add Shader with an empty input')
            a, b = self.output(first), self.output(second)
            return self._closure([x + y for x, y in zip(a['E'], b['E'])], [x + y for x, y in zip(a['T'], b['T'])], a['lit'] + b['lit'])
        if kind == 'ShaderNodeMixShader':
            f = min(1.0, max(0.0, self.scalar(self.input(node, 'Fac'))))
            out = self._closure()
            for weight, k in ((1.0 - f, 1), (f, 2)):
                feeding = self.source(node.inputs[k])
                if feeding is not None:
                    c = self.output(feeding)
                    out = self._closure([x + weight * y for x, y in zip(out['E'], c['E'])],
                                        [x + weight * y for x, y in zip(out['T'], c['T'])], out['lit'] + weight * c['lit'])
            return out
        raise KeyError('the readback cannot evaluate %s' % kind)

    def surface(self, tree):
        outputs = [n for n in tree.nodes if n.bl_idname == 'ShaderNodeOutputMaterial']
        if len(outputs) != 1:
            raise ValueError('%d Material Outputs' % len(outputs))
        feeding = self.source(outputs[0].inputs['Surface'])
        if feeding is None:
            raise ValueError('the Material Output has no Surface link')
        return self.output(feeding)


def read_back(tree, recipe, cells):
    """({texel key: closure}, error or None): the tree evaluated at each distinct texel among the recipe's cells."""
    readback = {}
    try:
        for quad in recipe['quads']:
            cell = cells[quad['cell']]
            key = texel_key(cell)
            if key not in readback:
                readback[key] = GraphReader(*cell_texel(cell)).surface(tree)
    except Exception as failure:  # noqa: BLE001 - recorded; compare_f fails the graphs check on it
        return readback, '%s: %s' % (type(failure).__name__, failure)
    return readback, None


# --------------------------------------------------------------------------------------------------------------------
# Scene assembly (Blender only)
# --------------------------------------------------------------------------------------------------------------------


def _set_if_exposed(owner, name, value, label, settings, errors):
    """Set an optional render setting when this Blender exposes it; record what was applied or refused."""
    if owner is None or name not in owner.bl_rna.properties:
        settings['%s.%s' % (label, name)] = 'not exposed'
        return False
    try:
        setattr(owner, name, value)
        settings['%s.%s' % (label, name)] = value
        return True
    except Exception as failure:  # noqa: BLE001 - recorded, never hidden
        errors.append('%s.%s: %s: %s' % (label, name, type(failure).__name__, failure))
        return False


def load_images(bpy, spec, errors):
    """Load the three copies of the probe texture (paths relative to the spec) as sRGB, CHANNEL_PACKED."""
    images = {}
    for entry in spec['images']:
        path = os.path.join(spec['_specDirectory'], entry['file'])
        image = bpy.data.images.load(path, check_existing=False)
        image.name = 'db_image_%s' % entry['kind']
        image.colorspace_settings.name = 'sRGB'
        image.alpha_mode = 'CHANNEL_PACKED'
        image['mt_image_index'] = int(entry['index'])
        if list(image.size) != [int(entry['width']), int(entry['height'])]:
            errors.append('image %s loaded as %s, the spec says %s' % (entry['kind'], list(image.size), [entry['width'], entry['height']]))
        images[int(entry['index'])] = image
    return images


def quads_mesh(bpy, name, rects, z, uvs=None):
    vertices, faces = [], []
    for rect in rects:
        x0, y0, x1, y1 = (float(v) for v in rect)
        base = len(vertices)
        vertices += [(x0, y0, z), (x1, y0, z), (x1, y1, z), (x0, y1, z)]
        faces.append((base, base + 1, base + 2, base + 3))
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(vertices, [], faces)
    if uvs is not None:
        layer = mesh.uv_layers.new(name='UVMap')
        flat = []
        for polygon in mesh.polygons:
            u, v = uvs[polygon.index]
            flat += [float(u), float(v)] * polygon.loop_total
        layer.data.foreach_set('uv', flat)
    mesh.update()
    return mesh


def build_material(bpy, recipe, images, graphs, cells):
    """One material from its recipe; its graph record carries the tree read back at every texel of its cells."""
    material = bpy.data.materials.new(recipe['name'])
    if material.node_tree is None or ('use_nodes' in material.bl_rna.properties and not material.use_nodes):
        material.use_nodes = True
    tree = material.node_tree
    for node in list(tree.nodes):
        tree.nodes.remove(node)
    g = BpyGraph(bpy, tree, images)
    closure = material_closure(g, recipe)
    output = tree.nodes.new('ShaderNodeOutputMaterial')
    output.target = 'ALL'
    tree.links.new(closure, output.inputs['Surface'])
    material.surface_render_method = recipe['renderMethod']
    material.use_backface_culling = False
    drawn = {key: recipe.get(key) for key in ('kind', 'set', 'pair', 'fit', 'lit', 'sentinel') if recipe.get(key) is not None}
    material['db_drawn'] = json.dumps(drawn, sort_keys=True)
    readback, readback_error = read_back(tree, recipe, cells)
    graphs.append(dict(drawn, name=recipe['name'], index=recipe['index'], nodes=g.nodes + 1,
                       renderMethod=material.surface_render_method, socketDefaults=g.socket_defaults,
                       readback=readback, readbackError=readback_error))
    return material


def build_scene(bpy, scene, spec, errors, settings, graphs):
    images = load_images(bpy, spec, errors)
    for recipe in spec['materials']:
        material = build_material(bpy, recipe, images, graphs, spec['cells'])
        mesh = quads_mesh(bpy, recipe['name'], [q['rect'] for q in recipe['quads']], 0.0, [q['uv'] for q in recipe['quads']])
        mesh.materials.append(material)
        obj = bpy.data.objects.new(recipe['name'], mesh)
        scene.collection.objects.link(obj)
    for plane in spec['background']:
        mesh = quads_mesh(bpy, plane['id'], plane['rects'], float(plane['z']))
        material = bpy.data.materials.new(plane['id'])
        if material.node_tree is None or ('use_nodes' in material.bl_rna.properties and not material.use_nodes):
            material.use_nodes = True
        tree = material.node_tree
        for node in list(tree.nodes):
            tree.nodes.remove(node)
        emission = tree.nodes.new('ShaderNodeEmission')
        emission.inputs['Color'].default_value = tuple(float(v) for v in plane['linear']) + (1.0,)
        emission.inputs['Strength'].default_value = 1.0
        output = tree.nodes.new('ShaderNodeOutputMaterial')
        tree.links.new(emission.outputs['Emission'], output.inputs['Surface'])
        mesh.materials.append(material)
        obj = bpy.data.objects.new(plane['id'], mesh)
        scene.collection.objects.link(obj)
    settings['backgroundColors'] = len(spec['background'])
    sun = (spec['render'].get('lighting') or {}).get('sun')
    if sun:
        light = bpy.data.lights.new('db_sun', type='SUN')
        light.energy = float(sun['strength'])
        _set_if_exposed(light, 'angle', float(sun['angle']), 'sun', settings, errors)
        _set_if_exposed(light, 'use_shadow', bool(sun['castShadow']), 'sun', settings, errors)
        _set_if_exposed(light, 'specular_factor', float(sun['specularFactor']), 'sun', settings, errors)
        obj = bpy.data.objects.new('db_sun', light)
        obj.rotation_euler = tuple(float(v) for v in sun['rotationEuler'])
        scene.collection.objects.link(obj)
        settings['sun.energy'] = light.energy


def engine_settings(scene, engine, samples, settings, errors):
    """What probe_render.setup_render does not set: no indirect diffuse or glossy light in Cycles (the lit rows are
    direct Lambert only), and EEVEE's sample count with every screen-space or ray-traced effect off."""
    if engine == 'CYCLES':
        for name, value in (('diffuse_bounces', 0), ('glossy_bounces', 0), ('transmission_bounces', 0), ('volume_bounces', 0)):
            _set_if_exposed(scene.cycles, name, value, 'cycles', settings, errors)
        return
    eevee = scene.eevee
    for name, value in (('taa_render_samples', int(samples)), ('use_raytracing', False), ('use_fast_gi', False),
                        ('use_gtao', False), ('use_bloom', False), ('use_ssr', False), ('use_motion_blur', False),
                        ('use_volumetric_shadows', False), ('use_shadows', False)):
        _set_if_exposed(eevee, name, value, 'eevee', settings, errors)


def cell_means(buffer, width, height, cells, ppu, inset):
    """{id: {'linear': mean RGB, 'std': per-channel standard deviation, 'pixels': n}} over each cell's inner block."""
    import probe_render
    out = {}
    for cell in cells:
        cx, cy = cell['pixel']
        x0, y0 = cx - ppu // 2 + inset, cy - ppu // 2 + inset
        values = []
        for y in range(y0, y0 + ppu - 2 * inset):
            for x in range(x0, x0 + ppu - 2 * inset):
                if 0 <= x < width and 0 <= y < height:
                    offset = probe_render.pixel_offset(width, height, x, y)
                    values.append([float(buffer[offset + c]) for c in range(3)])
        if not values:
            continue
        n = len(values)
        mean = [sum(v[c] for v in values) / n for c in range(3)]
        std = [math.sqrt(sum((v[c] - mean[c]) ** 2 for v in values) / n) for c in range(3)]
        out[cell['id']] = {'linear': mean, 'std': std, 'pixels': n}
    return out


def read_exr(bpy, path):
    """(RGBA float buffer, width, height) of a saved EXR, rows bottom to top as Blender stores them."""
    import array
    loaded = bpy.data.images.load(path, check_existing=False)
    width, height = int(loaded.size[0]), int(loaded.size[1])
    buffer = array.array('f', [0.0]) * len(loaded.pixels)
    loaded.pixels.foreach_get(buffer)
    return buffer, width, height


def lit_pass(bpy, scene, spec, work, errors, settings, result):
    """The lit and twin cells measured as inner-block means: from the main render in EEVEE (deterministic), from a
    border render at litSamples in Cycles, whose closure pick makes a diffuse-plus-transparent surface noisy."""
    cells = [c for c in spec['cells'].values() if c['kind'] in ('lit', 'twin')]
    if not cells:
        return
    render = spec['render']
    ppu, inset = int(render['pixelsPerUnit']), int(render['cellMeanInset'])
    width, height = (int(v) for v in render['resolution'])
    path = result.get('exr')
    if scene.render.engine == 'CYCLES':
        xs = [c['pixel'][0] for c in cells]
        ys = [c['pixel'][1] for c in cells]
        x_min, x_max = max(0, min(xs) - ppu), min(width, max(xs) + ppu)
        y_min, y_max = max(0, min(ys) - ppu), min(height, max(ys) + ppu)
        scene.render.use_border = True
        scene.render.use_crop_to_border = False
        scene.render.border_min_x, scene.render.border_max_x = x_min / width, x_max / width
        scene.render.border_min_y, scene.render.border_max_y = 1.0 - y_max / height, 1.0 - y_min / height
        main_samples = scene.cycles.samples
        scene.cycles.samples = int(render['litSamples'])
        settings['litPass'] = {'samples': scene.cycles.samples, 'borderPixels': [x_min, y_min, x_max, y_max]}
        bpy.ops.render.render(write_still=False)
        path = os.path.join(work, 'render-lit.exr')
        image_settings = scene.render.image_settings
        for name, value in (('file_format', 'OPEN_EXR'), ('color_depth', '32'), ('color_mode', 'RGBA'), ('exr_codec', 'ZIP')):
            try:
                setattr(image_settings, name, value)
            except Exception as failure:  # noqa: BLE001 - recorded, never hidden
                errors.append('lit exr %s: %s' % (name, failure))
        bpy.data.images.get('Render Result').save_render(filepath=path, scene=scene)
        scene.cycles.samples = main_samples
        scene.render.use_border = False
        result['litExr'] = path
    if not path or not os.path.isfile(path):
        errors.append('lit pass: no EXR to measure')
        return
    buffer, w, h = read_exr(bpy, path)
    if [w, h] != [width, height]:
        errors.append('lit pass: the EXR is %s, expected %s' % ([w, h], [width, height]))
        return
    result['cellMeans'] = cell_means(buffer, w, h, cells, ppu, inset)


def main():
    import bpy
    import probe_render
    if tuple(bpy.app.version) < MINIMUM_BLENDER:
        sys.stderr.write('display_blend_variant: Blender 5.0 or newer is required; running %s\n' % bpy.app.version_string)
        return 2
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    parser = argparse.ArgumentParser(prog='display_blend_variant.py')
    parser.add_argument('--spec', required=True)
    parser.add_argument('--out', required=True)
    parser.add_argument('--work', required=True)
    parser.add_argument('--engine', required=True, choices=('CYCLES', 'EEVEE'))
    args = parser.parse_args(argv)
    with open(args.spec, 'r', encoding='utf-8') as f:
        spec = json.load(f)
    spec['_specDirectory'] = os.path.dirname(os.path.abspath(args.spec))
    os.makedirs(args.work, exist_ok=True)
    errors, settings, graphs = [], {}, []
    result = {'schema': SCHEMA, 'probe': spec.get('probe'), 'control': spec.get('control'), 'blenderVersion': bpy.app.version_string,
              'requestedEngine': args.engine, 'spec': os.path.abspath(args.spec), 'constantsSha256': spec.get('constantsSha256'),
              'errors': errors, 'settings': settings, 'graphs': graphs, 'samples': [], 'exr': None, 'png': None}
    scene = bpy.context.scene
    for obj in list(bpy.data.objects):  # the factory scene's cube, light and camera
        bpy.data.objects.remove(obj, do_unlink=True)
    try:
        render_spec = dict(spec, background=[])  # probe_render's planes are single rectangles; ours are built here
        render_spec['render'] = dict(spec['render'], engine=args.engine)
        probe_render.setup_render(bpy, scene, render_spec, args.work, errors, settings)
        if args.engine == 'EEVEE' and scene.render.engine not in ('BLENDER_EEVEE', 'BLENDER_EEVEE_NEXT'):
            errors.append('EEVEE was requested but the engine is %s' % scene.render.engine)
        engine_settings(scene, 'CYCLES' if scene.render.engine == 'CYCLES' else 'EEVEE', spec['render'].get('samples') or 16,
                        settings, errors)
        build_scene(bpy, scene, spec, errors, settings, graphs)
        result['images'] = probe_render.dump_images(bpy, 4096, errors)
        probe_render.render_and_sample(bpy, scene, spec, args.work, errors, result)
        lit_pass(bpy, scene, spec, args.work, errors, settings, result)
    except Exception as failure:  # noqa: BLE001 - the result records the failure; the exit code says so
        import traceback
        errors.append('display-blend variant: %s: %s' % (type(failure).__name__, failure))
        result['traceback'] = traceback.format_exc()
    result['engine'] = scene.render.engine
    with open(args.out, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(result, f, indent=1)
    sys.stderr.write('display_blend_variant: wrote %s (%s, %d samples, %d errors)\n'
                     % (args.out, scene.render.engine, len(result['samples']), len(errors)))
    return 4 if errors else 0


if __name__ == '__main__':
    sys.exit(main())
