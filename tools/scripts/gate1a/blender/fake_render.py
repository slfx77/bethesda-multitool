# SPDX-License-Identifier: 0BSD
"""The fake Blender's analytic renderer for the hop-F probes; FOR THE SELF-CHECK ONLY.

It stands in for ``inside_blender/probe_render.py`` inside ``fake_blender.py``: it reads the package the fake .blend was
imported from and returns what a render would sample, using the REAL importer's constant-folding graph helpers
(``_material_blend``, ``_display_record``, ``_display_fit_channel``, ``_poly_rgb``, ``_max_rgb``, ``_clamp_rgb``,
``_vertex_color_key``, loaded under ``fake_bpy``) so the comparator is checked against the importer's own blend algebra
rather than against ``import_rules`` or ``display_blend_math``. Every helper folds constant inputs in Python, so the
display graph evaluates for one texel without a node tree.

The texel the graph receives follows Cycles' image loader, as MEASURED on the 2026-09-25 hop-F render (Blender 5.1.1,
330 of 330 cells to 0.000/255; ``fixtures/hop_f_blend_pairs_20260925.json`` and the self-check hold the fake to it):
* ``STRAIGHT`` (Blender's default): the 8-bit texel is associated at load in byte space with truncation,
  ``floor(c * a / 255)``. Where the Alpha output reaches the material output the color is unassociated (divided by
  alpha) BEFORE the sRGB decode; otherwise the graph receives the premultiplied color.
* ``PREMUL``: NOT MODELED; ``delivered_texel`` refuses it. Both recalled readings are unverified: "nothing happens at
  load, divide by alpha where linked" and "ImBuf unpremultiplies a PREMUL byte buffer at load, then Cycles re-associates
  it with truncation" (so an unlinked texel would be ``floor(unpremul(c) * a / 255)``, not ``c``). No gate probe or
  corpus image declares premultiplied alpha, so nothing renders it; hop F must measure it before a fake verdict (the
  design's C3 control, for instance) may rest on it.
* ``CHANNEL_PACKED``: the authored straight byte, linked or not (recalled; hop F's texel check measures it).
The alpha mode is the one the importer STORED on the image in the fake .blend (``image_alpha_modes``), so the fake fails
the way Blender does when the importer leaves an image at STRAIGHT. EEVEE's STRAIGHT rule (the decoded linear color
times alpha, recalled) is not modeled: the fake is Cycles.

Scenario switches (see ``fake_blender``): ``renderModel`` ``drawn`` (default: Blender composites E + T * Cd in
scene-linear space with E and T the display-space fit the package declares, which is what the importer's Transparent
BSDF plus Emission draws for a render-state blend, on the texel above), ``linear`` (the retired graph: E + clamp(T) * Cd
from the compiled terms on linear values, what the importer drew before the display-blend change; a render-state blend
must now FAIL under it) or ``display`` (an ideal Gamebryo framebuffer: sf * Cs + df * Cd on display-encoded values, on
straight texels; it holds every declared bound but misses the drawn fit); ``flipDdsOnLoad`` (DDS rows arrive upside
down); ``imageAlphaMode`` (every image rendered with this alpha mode instead of the stored one: the importer before the
straight-texel fix, which left color images at STRAIGHT).

The lit-blend probe (``probe_builders.build_lit_blend_probe``): the fake sun makes a lit surface's closure show its
linear albedo L (a Lambert surface at normal incidence under a sun of strength pi; base and vertex colors white), so a
twin cell shows L and a blended lit cell, under ``drawn``, the importer's OWN lit route evaluated through its pure
functions (``_display_blend_route``, ``_display_record``, ``_display_lit_factors``, constants folded): m(As) L +
tau(As) Cd, or the unlit display fit where the route falls back. ``linear`` is the retired lit route (the lit closure
times the uniform source scale S(k0 + k1 Sa) in [0, 1], T the clamped polynomial of the unlit color), ``display`` the
ideal Gamebryo framebuffer blending the lit display color display(L).
"""

import io
import json
import math
import os
import sys
import zipfile

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

IMPORT_MODEL = os.path.normpath(os.path.join(HERE, '..', '..', '..', '..', 'shared', 'Multitool.Shared', 'src',
                                             'Slfx77.Multitool.Media.Blender', 'Python', 'import_model.py'))
_IMPORTER = None
ALPHA_MODES = ('STRAIGHT', 'PREMUL', 'CHANNEL_PACKED')


def importer():
    global _IMPORTER
    if _IMPORTER is None:
        import fake_bpy
        fake_bpy.install()
        _IMPORTER = fake_bpy.load_module(IMPORT_MODEL, 'import_model_for_fake_render')
    return _IMPORTER


def decode(x):
    x = max(float(x), 0.0)
    return x / 12.92 if x < 0.04045 else ((x + 0.055) / 1.055) ** 2.4


def encode(x):
    x = min(max(float(x), 0.0), 1.0)
    return 12.92 * x if x < 0.0031308 else 1.055 * x ** (1.0 / 2.4) - 0.055


def delivered_texel(rgba_bytes, alpha_mode, alpha_linked):
    """The display-referred RGB (before the sRGB decode) that Cycles hands the graph for one 8-bit texel, per the module
    docstring. ``alpha_linked``: the image node's Alpha output reaches the material output. ``PREMUL`` raises
    ValueError: its texel is unverified on both readings the module docstring names and must back no verdict."""
    if alpha_mode not in ALPHA_MODES:
        raise ValueError('unknown alpha mode %r' % (alpha_mode,))
    if alpha_mode == 'PREMUL':
        raise ValueError('the fake does not model PREMUL: its texel is unverified (recalled only) and must back no '
                         'verdict until hop F measures it')
    color = [int(v) for v in rgba_bytes[:3]]
    alpha = int(rgba_bytes[3])
    if alpha_mode == 'CHANNEL_PACKED':
        return [v / 255.0 for v in color]
    stored = [(v * alpha) // 255 for v in color]  # STRAIGHT: associated at load in byte space, truncated
    if alpha_linked and 0 < alpha < 255:
        return [(v / 255.0) / (alpha / 255.0) for v in stored]
    return [v / 255.0 for v in stored]


def alpha_linked(module, material):
    """Whether the importer's graph links the base texture's Alpha output to the material output: an enabled alpha test
    (or portable Mask), or a drawn blend whose RGB E or T polynomial reads Sa or S*Sa (import_model._build_material)."""
    emission, transmission, record = module._material_blend(material)
    test = (material.get('renderState') or {}).get('alphaTest')
    if test is not None and bool(test.get('enabled', True)):
        return True
    if test is None and record.get('source') == 'alphaMode' and record.get('alphaMode') == 'Mask':
        return True
    if emission is None:
        return False
    return any(float(terms[c][k]) != 0.0 for terms in (emission, transmission) for c in range(3) for k in (2, 4))


class PackageView:
    def __init__(self, path):
        self.zip = zipfile.ZipFile(path)
        self.manifest = json.loads(self.zip.read('manifest.json').decode('utf-8'))
        self._images = {}

    def primitive(self, mesh_index):
        return self.manifest['meshes'][mesh_index]['primitives'][0]

    def material(self, mesh_index):
        return self.manifest['materials'][int(self.primitive(mesh_index)['material'])]

    def stream(self, manifest, components):
        return np.frombuffer(self.zip.read(manifest['path']), dtype='<f4').reshape(-1, components)

    def image_topdown(self, index, flip_dds):
        """RGBA8 (H, W, 4), rows top to bottom as Blender would see them (DDS flipped when the scenario says so)."""
        key = (index, flip_dds)
        if key not in self._images:
            from dds_decode import decode as decode_dds
            from PIL import Image
            entry = self.manifest['images'][index]
            data = self.zip.read(entry['path'])
            if data[:4] == b'DDS ':
                rgba, _ = decode_dds(data)
                if flip_dds:
                    rgba = rgba[::-1]
            else:
                rgba = np.asarray(Image.open(io.BytesIO(data)).convert('RGBA'), dtype=np.uint8)
            self._images[key] = np.ascontiguousarray(rgba)
        return self._images[key]

    def base_image(self, mesh_index):
        layers = (self.material(mesh_index).get('source') or {}).get('layers') or []
        return int(layers[0]['binding']['image'])


def _term(term, s_rgb, s_a):
    constant, scale, selector = term.get('constant') or [0] * 4, term.get('scale') or [0] * 4, term.get('input') or 'Zero'
    source = {'Zero': [0.0, 0.0, 0.0], 'SourceColor': list(s_rgb), 'SourceAlpha': [s_a] * 3}[selector]
    return [constant[c] + scale[c] * source[c] for c in range(3)]


def _texel(view, cell):
    mesh = cell['mesh']
    image = view.base_image(mesh)
    return image, view.image_topdown(image, False)[cell['texel'][1], cell['texel'][0]]


def blend_sample(view, cell, background_linear, config, image_alpha_modes):
    module = importer()
    material = view.material(cell['mesh'])
    image, texel = _texel(view, cell)
    s_a = texel[3] / 255.0
    if config.get('renderModel') == 'display':
        s_display = [texel[c] / 255.0 for c in range(3)]  # the ideal source samples straight texels
        equation = material['renderState']['blend']['colorEquation']
        cd_display = [b / 255.0 for b in cell['backgroundBytes']]
        sf = _term(equation['sourceFactor'], s_display, s_a)
        df = _term(equation['destinationFactor'], s_display, s_a)
        out = [min(1.0, max(0.0, s_display[c] * sf[c] + cd_display[c] * df[c])) for c in range(3)]
        return [decode(v) for v in out]
    delivered = delivered_texel(texel, image_alpha_modes[image], alpha_linked(module, material))
    emission_terms, transmission_terms, record = module._material_blend(material)
    s_linear = tuple(decode(v) for v in delivered)
    if config.get('renderModel', 'drawn') == 'drawn' and record.get('display') is not None:
        fit = module._display_record(record['display'], material.get('name'))['fit']
        terms = [module._display_fit_channel(None, fit, emission_terms[c], transmission_terms[c], s_linear[c], s_a) for c in range(3)]
        return [float(terms[c][0]) + float(terms[c][1]) * background_linear[c] for c in range(3)]
    e = module._max_rgb(None, module._poly_rgb(None, emission_terms[:3], s_linear, s_a), (0.0, 0.0, 0.0))
    t = module._clamp_rgb(None, module._poly_rgb(None, transmission_terms[:3], s_linear, s_a))
    return [e[c] + t[c] * background_linear[c] for c in range(3)]


def _straight_or_delivered(view, cell, config, image_alpha_modes):
    """The display-referred texel RGB a cell's graph receives: straight for the ideal source (``display``), else as
    Cycles delivers it for the stored alpha mode (``delivered_texel``)."""
    module = importer()
    image, texel = _texel(view, cell)
    if config.get('renderModel') == 'display':
        return [texel[c] / 255.0 for c in range(3)], texel
    return delivered_texel(texel, image_alpha_modes[image], alpha_linked(module, view.material(cell['mesh']))), texel


def lit_sample(view, cell, background_linear, config, image_alpha_modes):
    """A lit-blend probe cell (see the module docstring): a twin shows the lit color L; a blended lit cell the render
    model's lit composite."""
    module = importer()
    delivered, texel = _straight_or_delivered(view, cell, config, image_alpha_modes)
    lit_color = [decode(v) for v in delivered]  # the fake sun: the closure shows the linear albedo
    if cell['kind'] == 'twin':
        return lit_color
    material = view.material(cell['mesh'])
    s_a = texel[3] / 255.0
    emission_terms, transmission_terms, record = module._material_blend(material)
    model = config.get('renderModel', 'drawn')
    if model == 'display':
        lit_display = [encode(v) for v in lit_color]
        equation = material['renderState']['blend']['colorEquation']
        cd_display = [b / 255.0 for b in cell['backgroundBytes']]
        sf = _term(equation['sourceFactor'], lit_display, s_a)
        df = _term(equation['destinationFactor'], lit_display, s_a)
        return [decode(min(1.0, max(0.0, lit_display[c] * sf[c] + cd_display[c] * df[c]))) for c in range(3)]
    s_linear = tuple(lit_color)
    if model == 'linear':
        scale = module._uniform_source_scale(emission_terms)
        t = module._clamp_rgb(None, module._poly_rgb(None, transmission_terms[:3], s_linear, s_a))
        if scale is not None and 0.0 <= min(scale[0], scale[0] + scale[1]) and max(scale[0], scale[0] + scale[1]) <= 1.0:
            m = scale[0] + scale[1] * s_a
            return [m * lit_color[c] + t[c] * background_linear[c] for c in range(3)]
        e = module._max_rgb(None, module._poly_rgb(None, emission_terms[:3], s_linear, s_a), (0.0, 0.0, 0.0))
        return [e[c] + t[c] * background_linear[c] for c in range(3)]
    display = module._display_record(record['display'], material.get('name'))
    if module._display_blend_route(True, display) == 'display-lit':
        m, tau = module._display_lit_factors(None, display['lit'], s_a)
        return [float(m) * lit_color[c] + float(tau) * background_linear[c] for c in range(3)]
    terms = [module._display_fit_channel(None, display['fit'], emission_terms[c], transmission_terms[c], s_linear[c], s_a) for c in range(3)]
    return [float(terms[c][0]) + float(terms[c][1]) * background_linear[c] for c in range(3)]


def opaque_sample(view, cell, config, image_alpha_modes):
    """An unlit opaque cell (white vertex color, base color 1): the emission of the delivered texel. The ``display``
    scenario is the ideal source, which draws the straight texel."""
    module = importer()
    image, texel = _texel(view, cell)
    if config.get('renderModel') == 'display':
        delivered = [texel[c] / 255.0 for c in range(3)]
    else:
        delivered = delivered_texel(texel, image_alpha_modes[image], alpha_linked(module, view.material(cell['mesh'])))
    return [decode(v) for v in delivered]


def ramp_sample(view, cell):
    module = importer()
    mesh = cell['mesh']
    primitive = view.primitive(mesh)
    texel = view.image_topdown(view.base_image(mesh), False)[cell['texel'][1], cell['texel'][0]]
    colors = view.stream(primitive['color'], 4)
    raw = [float(v) for v in colors[cell['vertex'], :3]]
    domain, storage = module._vertex_color_key(primitive)
    if storage == 'byte':
        raw = [round(v * 255.0) / 255.0 for v in raw]
    tex_display = [texel[c] / 255.0 for c in range(3)]
    if domain == 'gamma':
        return [decode(tex_display[c] * raw[c]) for c in range(3)]
    return [decode(tex_display[c]) * raw[c] for c in range(3)]


def orientation_sample(view, cell, config):
    mesh = cell['mesh']
    image = view.image_topdown(view.base_image(mesh), bool(config.get('flipDdsOnLoad')))
    texel = image[cell['texel'][1], cell['texel'][0]]
    return [decode(texel[c] / 255.0) for c in range(3)]


def effective_alpha_modes(image_alpha_modes, config):
    """The alpha mode each image renders with: the stored one, or the scenario's ``imageAlphaMode`` for every image."""
    forced = config.get('imageAlphaMode')
    if forced is None:
        return dict(image_alpha_modes)
    if forced not in ALPHA_MODES:
        raise ValueError('scenario imageAlphaMode %r is not one of %s' % (forced, ALPHA_MODES))
    return {index: forced for index in image_alpha_modes}


def render(package_path, spec, config, image_alpha_modes):
    """Samples for ``spec`` from the package; ``image_alpha_modes`` maps each image index to the alpha mode the importer
    stored on its Blender image (``fake_blender.fake_probe`` reads it from the fake .blend)."""
    view = PackageView(package_path)
    modes = effective_alpha_modes(image_alpha_modes, config)
    backgrounds = {int(b['id'][2:]): b['linear'] for b in spec.get('background') or []}
    samples = []
    for sample in spec.get('samples') or []:
        cell = spec['cells'][sample['id']]
        if spec['probe'] == 'lit-blend':
            rgb = lit_sample(view, cell, backgrounds.get(cell['column']), config, modes)
        elif spec['probe'] == 'blend-pairs' and cell.get('kind', 'blend') == 'opaque':
            rgb = opaque_sample(view, cell, config, modes)
        elif spec['probe'] == 'blend-pairs':
            rgb = blend_sample(view, cell, backgrounds[cell['column']], config, modes)
        elif spec['probe'] == 'color-ramp':
            rgb = ramp_sample(view, cell)
        else:
            rgb = orientation_sample(view, cell, config)
        samples.append({'id': sample['id'], 'pixel': sample['pixel'], 'linear': [float(np.float32(v)) for v in rgb] + [1.0],
                        'spread': 0.0})
    result = {'schema': 'gate1a-blender-probe/1', 'probe': spec['probe'], 'samples': samples, 'errors': [],
              'engine': (spec.get('render') or {}).get('engine'), 'exr': None, 'png': None,
              'settings': {'fake': True, 'renderModel': config.get('renderModel', 'drawn'),
                           'flipDdsOnLoad': bool(config.get('flipDdsOnLoad')),
                           'imageAlphaModes': {str(index): mode for index, mode in sorted(modes.items())}}}
    if spec.get('imagePixels'):
        images = []
        for index, entry in enumerate(view.manifest['images']):
            topdown = view.image_topdown(index, bool(config.get('flipDdsOnLoad')))
            bottom_up = topdown[::-1].astype(np.float32) / np.float32(255.0)
            images.append({'index': index, 'name': entry['name'], 'size': [int(topdown.shape[1]), int(topdown.shape[0])],
                           'channels': 4, 'colorspace': 'sRGB', 'is_float': False, 'alpha_mode': modes.get(index),
                           'file_format': 'DDS' if entry['container'] == 'dds' else 'PNG',
                           'pixels': [float(v) for v in bottom_up.reshape(-1)]})
        result['images'] = images
    return result
