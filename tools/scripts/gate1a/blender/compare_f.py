# SPDX-License-Identifier: 0BSD
"""Hop F: render probes on Blender 5.1 and the transmission route in Blender's glTF importer.

Probes (inputs from ``probe_builders``, samples from ``inside_blender/probe_render.py``, both run by the driver):

* Blend pairs. The 11 FNV pairs over five known background colors, eleven source colors and alphas per pair (six
  historical ones and the design 6.4 worst-case sources), each through
  three copies of the source texture (a PNG, an uncompressed BGRA8 DDS and a solid-block BC3 DDS; the corpus ships DDS).
  The measured value of a cell is its rendered scene-linear color encoded with the sRGB curve (the Standard view
  transform), compared per channel with analytic results, the texel being the cell's own ``sourceBytes``. Three models
  are computed for every cell and reported (per pair, and per container in the table):
    ``drawn``:    the display-space fit the package DECLARES for the pair (``renderState.blend.display``, which the
                  importer requires and draws; the cell carries it as ``display``), composited as Blender does,
                  E + T * lin(Cd) through the Standard view (display_blend_math.drawn);
    ``gamebryo``: Gamebryo's framebuffer blend, out = sf * Cs + df * Cd on display-encoded values, clamped (the source's
                  result; its framebuffer model is inferred for the Gamebryo PC source, display-blend CHALLENGE item 3);
    ``linear``:   the retired linear graph, E + clamp(T) * Cd with E and T over linear values (a diagnostic: it names
                  a render that still composites the old way).
  The gate is fixed, no switch chooses it: every cell within 1/255 of ``drawn`` (Blender draws the declared graph) and
  within the declared bound + 1/255 of ``gamebryo`` (the reported bound holds on a real render; Exact pairs, bound 0,
  within 1/255). A probe-design check beside it (``check_blend_exercise``, design 6.4 check 3, no render): each
  non-exact pair's declared fit reaches its bound - 2/255 against Gamebryo on some cell, recomputed from the cells' own
  bytes, so the render exercises the bound it holds (the design's five worst-case sources are there for this).
  Controls: every pair's source and destination factors exchanged in a package copy (E and T swapped, each swapped
  equation with the display record the writer would give it), judged against the unswapped ``drawn`` expectation; and
  C2 (``display_node_control``), every declared two-node or anchored fit's nodes planted at (0, 1) and every one-node
  fit's node at 0, which must miss the pristine ``drawn`` expectation wherever the planted fit moves it by more than
  1/255 (a render that still matches would show the importer ignores the record's constants).
* Lit route (``check_lit_blend``, review finding 3, design 6.4 "lit twin probe"). The importer's OWN lit route, one lit
  family per lit curve it draws (over: power; SRC_ALPHA/ONE: scaled-power; ONE/ONE: constant; premultiplied over
  ONE/INV_SRC_ALPHA: the generic linear curve at exponent 1; SRC_ALPHA/ZERO: the generic linear curve at its chosen
  exponent 2.07, review fixes 2), lit blended cells beside one lit twin per texel drawn with a replace blend (the same
  forward path as the lit cells) and one opaque twin per texel (``check_lit_twins``: in Cycles the two twins agree within
  0.25/255, which proves the replace route draws L; EEVEE's deferred path quantizes a dark albedo, so there the opaque
  twin is a recorded diagnostic), under a sun. Every blended lit cell within 1/255 of m(As) L_twin + tau(As) lin(Cd),
  L_twin the forward twin's rendered color
  and (m, tau) the declared curve (the importer draws the lit route it declares), and within the declared lit bound +
  1/255 of Gamebryo blending the lit display color display(L_twin) (the lit bound holds on a real render). Lit and twin
  cells are read as inner-block means where the probe measured them (``cellMeans``). Control C4
  (``lit_exponent_control``): every power or generic linear curve's exponent planted at 1 in a package copy must miss the
  pristine drawn expectation on the over and SRC_ALPHA/ZERO cells (premultiplied over is at exponent 1 already, so the
  plant leaves it unchanged and it is not applicable).
* Both engines, every run (design 6.4): hop F renders every probe and control in Cycles AND EEVEE; no switch chooses.
* Texel rule (``check_texels``, on the blend-pair probe's OPAQUE cells). Unlit opaque cells (no blend, no alpha test, so
  the importer never links the texture's Alpha output) sample every source texel, alpha below 255 included, through each
  container. Every cell must show its STRAIGHT texel within 1/255: the importer loads every image not declared
  premultiplied as CHANNEL_PACKED (display-blend design 4.1). Two premultiplied hypotheses are reported beside it, so the
  receipt names the rule Blender applied: ``cycles-premultiplied`` (STRAIGHT in Cycles, floor(c * a / 255), measured on
  the 2026-09-25 render) and ``eevee-premultiplied`` (STRAIGHT in EEVEE, the decoded color times alpha, recalled). The
  check also fails when a partial-alpha cell cannot tell the hypotheses apart (a probe-design guard), so a passing
  render has excluded premultiplication on every container. The alpha-255 cells agree under every hypothesis.
* Asymmetric color ramp. Sixteen asymmetric texels times eight vertex colors, eight-bit and float storage, unlit: the
  expectation is the gamma-space product tex * vc (RE-11, design section 6.3). Control: the vertex colors declared Linear
  in a package copy (the gamma-space step omitted), which must miss the expectation by more than 1/255 somewhere.
* DDS orientation. One asymmetric 16 x 16 pattern as a PNG, an uncompressed DDS and a BC1 DDS, each on its own quad. The
  gate is the render: every 4 x 4 block must show the authored color at its authored place. The image buffers Blender
  loaded are also read back and each is classified ``same`` / ``flipped`` relative to the authored rows (a diagnostic:
  design section 6.4 decides from it whether ``V_FLIP_CONTAINERS`` needs ``dds``). Control: both DDS images stored
  upside down in a package copy.
* Transmission route (R22). ``readback.py --import-glb`` on the synthetic transmission GLB must pass its
  ``--expect-*`` assertions (Transmission Weight 1, IOR 1.0, roughness 0, metallic 0, base-color and emission images).
  readback.py only asks that SOME image feeds each socket, so the bindings are also judged here by identity
  (``check_bindings``): the one image upstream of Base Color must hold the bytes of the GLB's baseColorTexture (T), the
  one upstream of Emission Color the bytes of its emissiveTexture (E), compared by packed SHA-256; a vertex-color node
  must feed Base Color (T = baseColor x COLOR_0) and must NOT feed Emission Color (glTF's COLOR_0 never multiplies
  emission); and the mesh must carry the color attribute. Controls: the same GLB with KHR_materials_transmission
  stripped must FAIL the assertions and read back opaque (``--expect-opaque``, re-evaluated here with readback.py's own
  ``check_expectations``); and the same GLB with E and T exchanged (``controls.swap_transmission_terms``) must fail the
  identity check at BOTH sockets (design row F: "E and T swapped (both blend checks)").

* Display blend (``check_display_blend``, the display-blend probe run by ``run_display_blend_probe.py``). The 11 pairs
  under both constant sets of ``display_blend_constants.json`` through the PNG, BGRA8 and BC3 copies, drawn by the
  probe-only variant ``inside_blender/display_blend_variant.py``, in Cycles and EEVEE. Checks: the node trees Blender
  holds, read back (links, operations, stored socket defaults), compute every cell's declared model; the texel rule on
  the probe's opaque cells; every blend cell within 1/255 of its declared fit; every pristine cell within its reported
  bound + 1/255 of Gamebryo; every blend and lit bound realized by a worst-case cell within 2/255 (probe design,
  recomputed from the cell's bytes); every set-B pair drawing T > 1 has a cell where a clamp at 1 shows by more than
  2/255 (probe design); the two-cell T > 1 sentinels' verdict (Transparent BSDF colors above 1 composite
  unclamped, or clamped at 1), with their non-discriminating rows held to the composite model; the lit route against
  lit twins; the additive dark-background profile beside the worst case. Every check that needs T > 1 (set B) is an
  ANSWER check, reported apart from the VALIDITY checks, plus a consistency check between the two. Control
  (``display_blend_control``): set A's nodes -> (0, 1) and xa -> 0, set B's tmax -> 1, the lit curves -> today's, in a
  spec copy.

A control is detected only where the pristine probe passed (a pristine failure at the same element masks it; masking is
reported, never counted as a detection).
"""

import io
import json
import math
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from gate1a_common import HopResult, Mismatch  # noqa: E402
import display_blend_math as dbm  # noqa: E402
import import_rules as rules  # noqa: E402
import probe_builders as builders  # noqa: E402

TOLERANCE = 1.0 / 255.0
EPSILON = 1e-6
PROBE_SCHEMA = 'gate1a-blender-probe/1'


def encode(x):
    return rules.srgb_encode(min(1.0, max(0.0, float(x))))


def decode(x):
    return rules.srgb_decode(x)


FACTOR_VALUE = {  # the NIF AlphaFunction on display-encoded values (design section 6.2)
    'ONE': lambda cs, a: [1.0, 1.0, 1.0],
    'ZERO': lambda cs, a: [0.0, 0.0, 0.0],
    'SRC_COLOR': lambda cs, a: list(cs),
    'INV_SRC_COLOR': lambda cs, a: [1.0 - c for c in cs],
    'SRC_ALPHA': lambda cs, a: [a, a, a],
    'INV_SRC_ALPHA': lambda cs, a: [1.0 - a] * 3,
}


def display_model(sf, df, cs_display, a, cd_display):
    """Gamebryo's framebuffer blend on display-encoded values, clamped to [0, 1] (the ``gamebryo`` model)."""
    s, d = FACTOR_VALUE[sf](cs_display, a), FACTOR_VALUE[df](cs_display, a)
    return [min(1.0, max(0.0, cs_display[c] * s[c] + cd_display[c] * d[c])) for c in range(3)]


def linear_model(sf, df, cs_display, a, cd_display):
    """The retired linear graph E + clamp(T) * Cd in scene-linear space, displayed through the sRGB curve (the ``linear``
    diagnostic; the importer still draws it for portable alpha modes only)."""
    emission, transmission = rules.material_blend({'renderState': {'blend': builders.blend_state(sf, df, with_display=False)}})
    out = rules.unlit_blend_linear(emission, transmission, [decode(c) for c in cs_display], a, [decode(c) for c in cd_display])
    return [encode(v) for v in out]


def drawn_model(display, sf, df, cs_display, a, cd_display):
    """The display-space fit a package declares, as Blender draws it: E and T per channel from display-space k0 and k1
    (display_blend_math.fit_terms, which import_model._display_fit_channel draws node for node), then E + T * lin(Cd)
    through the Standard view."""
    return [dbm.drawn(display['fit'], sf, df, cs_display[c], a, cd_display[c]) for c in range(3)]


MODELS = ('drawn', 'gamebryo', 'linear')
CONTROL_MODELS = ('drawn', 'linear')  # the E/T control is decided by the drawn model; linear when drawn is masked


def cell_models(cell, sf, df, display):
    """{model: expected display RGB} for one blend cell under the given factors and display record."""
    cs = [b / 255.0 for b in cell['sourceBytes'][:3]]
    a = cell['sourceBytes'][3] / 255.0
    cd = [b / 255.0 for b in cell['backgroundBytes']]
    return {'drawn': drawn_model(display, sf, df, cs, a, cd), 'gamebryo': display_model(sf, df, cs, a, cd),
            'linear': linear_model(sf, df, cs, a, cd)}


def load_probe(path):
    with open(path, 'r', encoding='utf-8') as f:
        result = json.load(f)
    if result.get('schema') != PROBE_SCHEMA:
        raise ValueError('probe result schema %r' % result.get('schema'))
    return result


def measured_display(sample):
    return [encode(v) for v in sample['linear'][:3]]


def png_cross_check(result, spec):
    """Largest difference between Blender's own 8-bit display PNG and the encoded EXR samples (a diagnostic)."""
    path = result.get('png')
    if not path or not os.path.isfile(path):
        return None
    from PIL import Image
    image = np.asarray(Image.open(path).convert('RGB'), dtype=np.float64) / 255.0
    worst = 0.0
    for sample in result.get('samples') or []:
        x, y = sample['pixel']
        if 0 <= y < image.shape[0] and 0 <= x < image.shape[1]:
            worst = max(worst, max(abs(image[y, x, c] - measured_display(sample)[c]) for c in range(3)))
    return worst


def _samples(result):
    return {sample['id']: sample for sample in result.get('samples') or []}


# --------------------------------------------------------------------------------------------------------------------
# Blend pairs
# --------------------------------------------------------------------------------------------------------------------


def blend_cells(spec):
    """(id, cell) of the blend-pair cells; the probe's opaque texel-rule cells (``kind`` 'opaque') are not blend cells.
    A spec written before the texel cells existed has no ``kind``: every cell is then a blend cell."""
    return [(cell_id, cell) for cell_id, cell in spec['cells'].items() if cell.get('kind', 'blend') == 'blend']


def blend_errors(spec, result):
    """Per pair: per model, the worst per-channel error over its cells (and per texture container), the declared bound,
    plus per-cell details. The ``drawn`` expectation uses the display record the cell names (the pristine package's)."""
    samples = _samples(result)
    pairs = {}
    for cell_id, cell in blend_cells(spec):
        sample = samples.get(cell_id)
        display = cell['display']
        pair = pairs.setdefault(cell['pairIndex'], {'pair': cell['pair'], 'fnvFiles': cell['fnvFiles'], 'cells': [],
                                                    'missing': 0, 'worst': {m: 0.0 for m in MODELS},
                                                    'worstByContainer': {}, 'bound': float(display['bound']),
                                                    'family': display['family'], 'fit': display['fit']})
        if sample is None:
            pair['missing'] += 1
            continue
        measured = measured_display(sample)
        container = cell.get('container', 'png')
        entry = {'id': cell_id, 'container': container, 'measured': measured, 'spread': sample.get('spread')}
        by_container = pair['worstByContainer'].setdefault(container, {m: 0.0 for m in MODELS})
        for name, expected in cell_models(cell, cell['sourceFactor'], cell['destinationFactor'], display).items():
            error = max(abs(measured[c] - expected[c]) for c in range(3))
            entry[name] = {'expected': expected, 'error': error}
            pair['worst'][name] = max(pair['worst'][name], error)
            by_container[name] = max(by_container[name], error)
        pair['cells'].append(entry)
    return pairs


def pair_passes(pair):
    """Blender draws the declared fit (within 1/255) and the declared bound holds (within bound + 1/255 of Gamebryo)."""
    return (not pair['missing'] and pair['worst']['drawn'] <= TOLERANCE + EPSILON
            and pair['worst']['gamebryo'] <= pair['bound'] + TOLERANCE + EPSILON)


def fitting_models(worst):
    """The models a pair's render fits within 1/255."""
    return sorted(name for name, error in worst.items() if error <= TOLERANCE + EPSILON)


def check_blend(result_obj, spec, probe):
    check = result_obj.check('blend pairs: rendered cells within 1/255 of the declared display fit and within its bound + 1/255 of '
                             'Gamebryo (11 FNV pairs)')
    pairs = blend_errors(spec, probe)
    table = []
    for index in sorted(pairs):
        pair = pairs[index]
        verdict = pair_passes(pair)
        worst_cell = max(pair['cells'], key=lambda c: c['drawn']['error']) if pair['cells'] else None
        row = {'pair': pair['pair'], 'fnvFiles': pair['fnvFiles'], 'passed': verdict, 'family': pair['family'], 'fit': pair['fit'],
               'bound255': round(pair['bound'] * 255.0, 4), 'worstDrawn255': round(pair['worst']['drawn'] * 255.0, 3),
               'worstGamebryo255': round(pair['worst']['gamebryo'] * 255.0, 3), 'worstLinear255': round(pair['worst']['linear'] * 255.0, 3),
               'fitsModels': fitting_models(pair['worst']), 'missingSamples': pair['missing'],
               'worstByContainer255': {container: {name: round(error * 255.0, 3) for name, error in worst.items()}
                                       for container, worst in sorted(pair['worstByContainer'].items())}}
        if worst_cell is not None:
            row['worstCell'] = {'id': worst_cell['id'], 'measured255': [round(v * 255.0, 2) for v in worst_cell['measured']],
                                'drawn255': [round(v * 255.0, 2) for v in worst_cell['drawn']['expected']],
                                'gamebryo255': [round(v * 255.0, 2) for v in worst_cell['gamebryo']['expected']]}
        table.append(row)
        if not verdict:
            check.fail(Mismatch('pair %s' % pair['pair'], 'tolerance' if not pair['missing'] else 'presence',
                                'drawn model: worst %.2f/255 at %s (measured %s, drawn %s); Gamebryo %.2f/255 against the declared bound '
                                '%.4f/255 (+1/255); linear %.2f/255; %d sample(s) missing'
                                % (pair['worst']['drawn'] * 255.0, (worst_cell or {}).get('id'), row.get('worstCell', {}).get('measured255'),
                                   row.get('worstCell', {}).get('drawn255'), pair['worst']['gamebryo'] * 255.0, pair['bound'] * 255.0,
                                   pair['worst']['linear'] * 255.0, pair['missing'])))
    check.data['pairs'] = table
    check.data['reference'] = 'drawn (the declared display fit) and gamebryo within the declared bound'
    fits = [set(row['fitsModels']) for row in table]
    check.data['blenderBlendedIn'] = ('the declared display fit' if fits and all('drawn' in f for f in fits) else
                                      'linear space' if fits and all('linear' in f for f in fits) else 'mixed or neither')
    check.ok('%d pairs draw their declared fit within 1/255 and hold their bound' % len(table))
    return pairs


def blend_control(spec, pristine_pairs, control_probe):
    """(detected, detail, per-model verdicts). Under each model of CONTROL_MODELS a pair is applicable when its pristine
    cells passed that model and the swap changes that model's expectation by more than 1/255 (the swapped expectation
    uses the display record the writer gives the swapped equation); it is detected when the control cells miss the
    UNSWAPPED expectation by more than 1/255. The drawn model decides; when every pair the swap changes is masked under
    it (the pristine render misses it), the linear model decides and the detail says so."""
    control_pairs = blend_errors(spec, control_probe)
    swap_change = {}
    for _cell_id, cell in blend_cells(spec):
        sf, df = cell['sourceFactor'], cell['destinationFactor']
        straight = cell_models(cell, sf, df, cell['display'])
        swapped = cell_models(cell, df, sf, builders.display_record(df, sf))
        for name in CONTROL_MODELS:
            key = (cell['pairIndex'], name)
            swap_change[key] = max(swap_change.get(key, 0.0), max(abs(straight[name][c] - swapped[name][c]) for c in range(3)))
    per_model = {}
    for model in CONTROL_MODELS:
        verdicts = []
        for index in sorted(pristine_pairs):
            pair = pristine_pairs[index]
            if swap_change[(index, model)] <= TOLERANCE + EPSILON:
                verdicts.append({'pair': pair['pair'], 'verdict': 'not applicable (the swap leaves the expectation unchanged)'})
            elif pair['worst'][model] > TOLERANCE + EPSILON or pair['missing']:
                verdicts.append({'pair': pair['pair'], 'verdict': 'masked (the pristine pair misses this model)'})
            else:
                detected = control_pairs[index]['worst'][model] > TOLERANCE + EPSILON
                verdicts.append({'pair': pair['pair'], 'verdict': 'detected' if detected else 'NOT DETECTED',
                                 'controlWorst255': round(control_pairs[index]['worst'][model] * 255.0, 3)})
        applicable = [v for v in verdicts if v['verdict'] in ('detected', 'NOT DETECTED')]
        per_model[model] = {'verdicts': verdicts, 'applicable': len(applicable),
                            'masked': sum(1 for v in verdicts if v['verdict'].startswith('masked')),
                            'missed': [v['pair'] for v in applicable if v['verdict'] == 'NOT DETECTED']}

    def describe(model):
        entry = per_model[model]
        return '%s model: %d of %d applicable pair(s) detected, %d masked' % (
            model, entry['applicable'] - len(entry['missed']), entry['applicable'], entry['masked'])

    reference, other = CONTROL_MODELS
    for model in (reference, other):
        entry = per_model[model]
        if not entry['applicable']:
            continue
        note = '' if model == reference else ' (decided by the %s model: the %s reference is masked on every pair the swap changes)' % (model, reference)
        if entry['missed']:
            return False, 'E/T swap NOT detected on %s%s; %s; %s' % (', '.join(entry['missed']), note, describe(reference), describe(other)), per_model
        return True, 'E/T swap detected%s; %s; %s' % (note, describe(reference), describe(other)), per_model
    return None, 'masked under both models: no pair both passed pristine and changes under the swap; %s; %s' % (describe(reference), describe(other)), per_model


EXERCISE_SLACK255 = 2.0  # design 6.4 check 3: a non-exact pair's worst cell must reach its bound within 2/255


def check_blend_exercise(result_obj, spec):
    """Design 6.4 check 3, a PROBE-DESIGN check that needs no render: for each non-exact pair, the analytic error of its
    declared fit against Gamebryo, recomputed from every cell's own bytes, reaches the declared bound - 2/255 on some
    cell. Otherwise the render could not show an understated bound; the design's worst-case sources
    (``probe_builders.BLEND_WORST_SOURCES``) are there for this."""
    check = result_obj.check('blend pairs: the probe exercises every declared bound (a cell whose declared fit misses Gamebryo '
                             'by at least the bound - 2/255; design 6.4 check 3, recomputed from the cells\' bytes)')
    pairs = {}
    for cell_id, cell in blend_cells(spec):
        display = cell['display']
        if float(display['bound']) == 0.0:
            continue
        models = cell_models(cell, cell['sourceFactor'], cell['destinationFactor'], display)
        error = max(abs(models['drawn'][c] - models['gamebryo'][c]) for c in range(3))
        entry = pairs.setdefault(cell['pair'], {'bound255': float(display['bound']) * 255.0, 'attained255': 0.0, 'at': None})
        if error * 255.0 > entry['attained255']:
            entry.update(attained255=error * 255.0, at=cell_id)
    for pair, entry in sorted(pairs.items()):
        if entry['attained255'] < entry['bound255'] - EXERCISE_SLACK255:
            check.fail(Mismatch('pair %s' % pair, 'probe', 'the probe reaches only %.3f/255 of the declared bound %.4f/255 (at %s)'
                                % (entry['attained255'], entry['bound255'], entry['at'])))
    check.data['pairs'] = {pair: {'bound255': round(entry['bound255'], 4), 'attained255': round(entry['attained255'], 4),
                                  'at': entry['at']} for pair, entry in sorted(pairs.items())}
    check.ok('%d non-exact pairs exercised within %.0f/255 of their bound' % (len(pairs), EXERCISE_SLACK255))
    return pairs


def display_node_control(spec, pristine_pairs, control_probe):
    """(detected, detail, per-pair verdicts) of control C2, the planted display nodes (``controls.plant_display_nodes``).
    A pair is applicable when its pristine cells passed (drawn within 1/255 and the bound held) and the planted fit moves
    its drawn expectation by more than 1/255 on some cell; it is detected when the control cells miss the PRISTINE drawn
    expectation by more than 1/255. Exact pairs carry no nodes and are not applicable."""
    import controls
    control_pairs = blend_errors(spec, control_probe)
    change = {}
    for _cell_id, cell in blend_cells(spec):
        sf, df = cell['sourceFactor'], cell['destinationFactor']
        planted = dict(cell['display'], fit=controls.planted_display_fit(cell['display']['fit']))
        straight = drawn_model(cell['display'], sf, df, *_cell_values(cell))
        moved = drawn_model(planted, sf, df, *_cell_values(cell))
        change[cell['pairIndex']] = max(change.get(cell['pairIndex'], 0.0), max(abs(straight[c] - moved[c]) for c in range(3)))
    verdicts = []
    for index in sorted(pristine_pairs):
        pair = pristine_pairs[index]
        if change.get(index, 0.0) <= TOLERANCE + EPSILON:
            verdicts.append({'pair': pair['pair'], 'verdict': 'not applicable (the planted nodes leave the expectation unchanged)'})
        elif not pair_passes(pair):
            verdicts.append({'pair': pair['pair'], 'verdict': 'masked (the pristine pair fails)'})
        else:
            worst = control_pairs[index]['worst']['drawn']
            verdicts.append({'pair': pair['pair'], 'verdict': 'detected' if worst > TOLERANCE + EPSILON else 'NOT DETECTED',
                             'controlWorst255': round(worst * 255.0, 3), 'plantedChange255': round(change[index] * 255.0, 3)})
    applicable = [v for v in verdicts if v['verdict'] in ('detected', 'NOT DETECTED')]
    missed = [v['pair'] for v in applicable if v['verdict'] == 'NOT DETECTED']
    masked = sum(1 for v in verdicts if v['verdict'].startswith('masked'))
    if not applicable:
        return None, 'masked: no pair both passed pristine and moves under the planted nodes (%d masked)' % masked, verdicts
    if missed:
        return False, 'planted display nodes NOT detected on %s (%d of %d applicable, %d masked)' % (
            ', '.join(missed), len(applicable) - len(missed), len(applicable), masked), verdicts
    return True, 'planted display nodes detected on %d of %d applicable pair(s), %d masked' % (len(applicable), len(applicable), masked), verdicts


def _cell_values(cell):
    """(Cs, As, Cd) of a cell in display units, as ``drawn_model`` takes them."""
    return ([b / 255.0 for b in cell['sourceBytes'][:3]], cell['sourceBytes'][3] / 255.0, [b / 255.0 for b in cell['backgroundBytes']])


# --------------------------------------------------------------------------------------------------------------------
# Lit route (the lit-blend probe: the importer's own lit route against lit twins)
# --------------------------------------------------------------------------------------------------------------------


def _lit_sample(probe):
    """cell id -> the sample a lit or twin cell is judged on: the inner-block mean where the probe measured one
    (``cellMeans``, the display-blend variant's lit pass), else the center sample (the fake)."""
    means = probe.get('cellMeans') or {}
    samples = _samples(probe)
    return lambda cell_id: means.get(cell_id) or samples.get(cell_id)


def lit_expectation(cell, twin_linear, display=None):
    """(drawn, gamebryo) per channel of a lit blended cell from its twin's rendered lit color L (scene-linear): drawn
    m(As) L + tau(As) lin(Cd) with (m, tau) the declared lit curve (``display``, default the cell's); Gamebryo blends the
    lit display color display(L)."""
    display = display or cell['display']
    a = cell['sourceBytes'][3] / 255.0
    m, tau = dbm.lit_terms(display['lit'], a)
    cd = [b / 255.0 for b in cell['backgroundBytes']]
    drawn = [dbm.display(m * twin_linear[c] + tau * dbm.srgb_decode(cd[c])) for c in range(3)]
    ld = [dbm.display(twin_linear[c]) for c in range(3)]
    return drawn, [dbm.gamebryo(cell['sourceFactor'], cell['destinationFactor'], ld[c], a, cd[c]) for c in range(3)]


def lit_errors(spec, probe):
    """Per lit pair: the declared lit curve and bound, the worst drawn and Gamebryo errors over its cells, the cells."""
    sample_of = _lit_sample(probe)
    pairs = {}
    for cell_id, cell in spec['cells'].items():
        if cell.get('kind') != 'lit':
            continue
        lit = cell['display']['lit']
        pair = pairs.setdefault(cell['pairIndex'], {'pair': cell['pair'], 'form': lit['form'], 'litBound': float(lit['bound']),
                                                    'cells': [], 'missing': 0, 'worst': {'drawn': 0.0, 'gamebryo': 0.0}})
        sample, twin = sample_of(cell_id), sample_of(cell['twin'])
        if sample is None or twin is None:
            pair['missing'] += 1
            continue
        twin_linear = [float(v) for v in twin['linear'][:3]]
        drawn, target = lit_expectation(cell, twin_linear)
        measured = measured_display(sample)
        entry = {'id': cell_id, 'measured': measured, 'twinLinear': twin_linear,
                 'drawn': {'expected': drawn, 'error': max(abs(measured[c] - drawn[c]) for c in range(3))},
                 'gamebryo': {'expected': target, 'error': max(abs(measured[c] - target[c]) for c in range(3))}}
        for model in ('drawn', 'gamebryo'):
            pair['worst'][model] = max(pair['worst'][model], entry[model]['error'])
        pair['cells'].append(entry)
    return pairs


def lit_pair_passes(pair):
    """Blender draws the declared lit curve (within 1/255) and the lit bound holds (within bound + 1/255 of Gamebryo)."""
    return (not pair['missing'] and pair['worst']['drawn'] <= TOLERANCE + EPSILON
            and pair['worst']['gamebryo'] <= pair['litBound'] + TOLERANCE + EPSILON)


TWIN_AGREEMENT = 0.25 / 255.0  # Cycles: the forward (replace-blend) twin against the opaque twin, display units


def check_lit_twins(result_obj, spec, probe):
    """The lit probe's two twins per texel: the forward twin (``path`` forward, a replace blend, the lit cells'
    reference) and the opaque twin (``path`` deferred). In Cycles they must agree within ``TWIN_AGREEMENT`` in display
    units, which proves the replace route draws the lit color L unscaled (a lit-route bug that scaled every lit closure
    would otherwise cancel between the lit cells and a forward twin). In EEVEE the deferred path quantizes a dark albedo
    (measured 2026-09-27), so the difference is recorded, not judged. Returns the worst difference in display units."""
    engine = str((probe.get('settings') or {}).get('engine') or probe.get('engine') or '')
    check = result_obj.check('lit twins: the forward (replace-blend) twin equals the opaque twin in Cycles within %.2f/255'
                             % (TWIN_AGREEMENT * 255.0))
    sample_of = _lit_sample(probe)
    worst, where, missing, rows = 0.0, None, 0, []
    for cell_id, cell in sorted(spec['cells'].items()):
        if cell.get('kind') != 'twin' or cell.get('path') != 'forward':
            continue
        opaque_id = cell_id.replace('twin_src', 'twinopaque_src', 1)
        forward, opaque = sample_of(cell_id), sample_of(opaque_id)
        if forward is None or opaque is None or opaque_id not in spec['cells']:
            missing += 1
            continue
        difference = max(abs(dbm.display(float(forward['linear'][c])) - dbm.display(float(opaque['linear'][c]))) for c in range(3))
        rows.append({'texel': cell_id, 'difference255': round(difference * 255.0, 3),
                     'forwardLinear': [round(float(v), 5) for v in forward['linear'][:3]],
                     'opaqueLinear': [round(float(v), 5) for v in opaque['linear'][:3]]})
        if difference > worst:
            worst, where = difference, cell_id
    check.data.update({'engine': engine, 'twins': rows, 'worst255': round(worst * 255.0, 3), 'worstTexel': where})
    if missing or not rows:
        check.fail(Mismatch('lit twins', 'presence', '%d twin pair(s) missing of %d' % (missing, missing + len(rows))))
    elif 'EEVEE' in engine.upper():
        check.ok('EEVEE: recorded only (the deferred path quantizes a dark albedo); worst %.2f/255 at %s' % (worst * 255.0, where))
        return worst
    elif worst > TWIN_AGREEMENT + EPSILON:
        check.fail(Mismatch(where, 'tolerance', 'the forward twin differs from the opaque twin by %.3f/255 (at most %.2f/255)'
                            % (worst * 255.0, TWIN_AGREEMENT * 255.0)))
    check.ok('%d twin pair(s) agree within %.2f/255 (worst %.3f/255)' % (len(rows), TWIN_AGREEMENT * 255.0, worst * 255.0))
    return worst


def check_lit_blend(result_obj, spec, probe):
    """The lit route through the real importer (see the module docstring). Returns the per-pair errors."""
    families = len({cell['pairIndex'] for cell in spec['cells'].values() if cell.get('kind') == 'lit'})
    check = result_obj.check('lit route: every blended lit cell within 1/255 of m(As) L_twin + tau(As) Cd (the importer\'s declared '
                             'lit curve) and within its lit bound + 1/255 of Gamebryo on the lit display color (%d lit families)' % families)
    pairs = lit_errors(spec, probe)
    table = []
    for index in sorted(pairs):
        pair = pairs[index]
        verdict = lit_pair_passes(pair)
        worst = max(pair['cells'], key=lambda c: c['drawn']['error']) if pair['cells'] else None
        row = {'pair': pair['pair'], 'form': pair['form'], 'passed': verdict, 'litBound255': round(pair['litBound'] * 255.0, 4),
               'worstDrawn255': round(pair['worst']['drawn'] * 255.0, 3), 'worstGamebryo255': round(pair['worst']['gamebryo'] * 255.0, 3),
               'missingSamples': pair['missing']}
        if worst is not None:
            row['worstCell'] = {'id': worst['id'], 'measured255': [round(v * 255.0, 2) for v in worst['measured']],
                                'drawn255': [round(v * 255.0, 2) for v in worst['drawn']['expected']],
                                'twinLinear': [round(v, 5) for v in worst['twinLinear']]}
        table.append(row)
        if not verdict:
            check.fail(Mismatch('lit pair %s' % pair['pair'], 'tolerance' if not pair['missing'] else 'presence',
                                '%s curve: drawn worst %.2f/255 at %s; Gamebryo %.2f/255 against the lit bound %.4f/255 (+1/255); '
                                '%d sample(s) or twins missing' % (pair['form'], pair['worst']['drawn'] * 255.0, (worst or {}).get('id'),
                                                                   pair['worst']['gamebryo'] * 255.0, pair['litBound'] * 255.0, pair['missing'])))
    check.data['pairs'] = table
    check.data['engine'] = (probe.get('settings') or {}).get('engine') or probe.get('engine')
    check.ok('%d lit families draw their declared lit curve within 1/255 and hold their lit bound' % len(table))
    return pairs


PLANTED_EXPONENT_FORMS = ('power', 'linear')  # the lit forms whose exponent control C4 plants at 1


def lit_exponent_control(spec, pristine_pairs, control_probe):
    """(detected, detail, per-pair verdicts) of control C4 (``controls.plant_lit_exponent``: every power or generic
    linear lit curve takes p 1). A pair is applicable when its declared curve is a power or linear curve, its pristine
    cells passed, and p 1 moves its drawn expectation by more than 1/255 on some cell (with the control's own twins); it is
    detected when the control cells miss the PRISTINE drawn expectation by more than 1/255. A linear curve already at
    exponent 1 (premultiplied over) is not applicable: the plant leaves it unchanged."""
    control_pairs = lit_errors(spec, control_probe)
    sample_of = _lit_sample(control_probe)
    verdicts = []
    for index in sorted(pristine_pairs):
        pair = pristine_pairs[index]
        if pair['form'] not in PLANTED_EXPONENT_FORMS:
            verdicts.append({'pair': pair['pair'], 'verdict': 'not applicable (not a power or linear curve)'})
            continue
        change = 0.0
        for cell_id, cell in spec['cells'].items():
            twin = sample_of(cell.get('twin')) if cell.get('kind') == 'lit' and cell['pairIndex'] == index else None
            if twin is None:
                continue
            planted = dict(cell['display'], lit=dict(cell['display']['lit'], p=1.0))
            twin_linear = [float(v) for v in twin['linear'][:3]]
            straight, _ = lit_expectation(cell, twin_linear)
            moved, _ = lit_expectation(cell, twin_linear, planted)
            change = max(change, max(abs(straight[c] - moved[c]) for c in range(3)))
        if change <= TOLERANCE + EPSILON:
            verdicts.append({'pair': pair['pair'], 'verdict': 'not applicable (p 1 leaves the expectation unchanged)'})
        elif not lit_pair_passes(pair):
            verdicts.append({'pair': pair['pair'], 'verdict': 'masked (the pristine pair fails)'})
        else:
            worst = control_pairs[index]['worst']['drawn']
            verdicts.append({'pair': pair['pair'], 'verdict': 'detected' if worst > TOLERANCE + EPSILON else 'NOT DETECTED',
                             'controlWorst255': round(worst * 255.0, 3), 'plantedChange255': round(change * 255.0, 3)})
    applicable = [v for v in verdicts if v['verdict'] in ('detected', 'NOT DETECTED')]
    missed = [v['pair'] for v in applicable if v['verdict'] == 'NOT DETECTED']
    if not applicable:
        return None, 'masked or not applicable: no power or linear curve pair both passed pristine and moves under p 1', verdicts
    if missed:
        return False, 'planted lit exponent NOT detected on %s' % ', '.join(missed), verdicts
    return True, 'planted lit exponent detected on %s' % ', '.join(v['pair'] for v in applicable), verdicts


# --------------------------------------------------------------------------------------------------------------------
# Texel rule (the blend-pair probe's opaque cells)
# --------------------------------------------------------------------------------------------------------------------

TEXEL_MODELS = ('straight', 'cycles-premultiplied', 'eevee-premultiplied')
# A partial-alpha cell discriminates only when every wrong hypothesis sits this far from the straight texel.
TEXEL_SEPARATION = 2.0 / 255.0


def texel_model(name, source_bytes):
    """The display-referred RGB an unlit opaque cell shows for its 8-bit texel under one hypothesis:
    ``straight``: the authored byte (CHANNEL_PACKED in either engine; also what the source renderer samples);
    ``cycles-premultiplied``: Blender's default STRAIGHT in Cycles with the Alpha output unlinked, floor(c * a / 255) in
    byte space (measured on the 2026-09-25 render, display-blend design section 1);
    ``eevee-premultiplied``: STRAIGHT in EEVEE with the Alpha output unlinked, the decoded linear color times alpha
    (``color_alpha_premultiply``; recalled, not yet rendered)."""
    color, alpha = [int(v) for v in source_bytes[:3]], int(source_bytes[3])
    if name == 'straight':
        return [v / 255.0 for v in color]
    if name == 'cycles-premultiplied':
        return [((v * alpha) // 255) / 255.0 for v in color]
    if name == 'eevee-premultiplied':
        return [encode(decode(v / 255.0) * (alpha / 255.0)) for v in color]
    raise ValueError('unknown texel model %r' % (name,))


def texel_separation(source_bytes):
    """The smallest distance (per-channel maximum, display units) between the straight texel and each premultiplied
    hypothesis; zero for an opaque or black texel, where every hypothesis agrees."""
    straight = texel_model('straight', source_bytes)
    return min(max(abs(straight[c] - texel_model(name, source_bytes)[c]) for c in range(3)) for name in TEXEL_MODELS[1:])


def texel_errors(spec, probe):
    """Per container: per hypothesis, the worst per-channel error over the opaque cells, plus per-cell details."""
    samples = _samples(probe)
    containers = {}
    for cell_id, cell in spec['cells'].items():
        if cell.get('kind') != 'opaque':
            continue
        entry = containers.setdefault(cell['container'], {'cells': [], 'missing': 0, 'worst': {m: 0.0 for m in TEXEL_MODELS},
                                                          'discriminating': 0})
        sample = samples.get(cell_id)
        if sample is None:
            entry['missing'] += 1
            continue
        measured = measured_display(sample)
        row = {'id': cell_id, 'sourceBytes': cell['sourceBytes'], 'measured': measured,
               'separation': texel_separation(cell['sourceBytes'])}
        for name in TEXEL_MODELS:
            expected = texel_model(name, cell['sourceBytes'])
            error = max(abs(measured[c] - expected[c]) for c in range(3))
            row[name] = {'expected': expected, 'error': error}
            entry['worst'][name] = max(entry['worst'][name], error)
        if cell['sourceBytes'][3] < 255 and max(cell['sourceBytes'][:3]) > 0:
            entry['discriminating'] += 1
        entry['cells'].append(row)
    return containers


def check_texels(result_obj, spec, probe):
    """The texel rule on the blend-pair probe's opaque cells (see the module docstring). Returns the per-container
    table, or None for a spec without opaque cells (a probe built before they existed)."""
    if not any(cell.get('kind') == 'opaque' for cell in spec['cells'].values()):
        return None
    check = result_obj.check('texel rule: every opaque cell shows its straight texel within 1/255 (PNG, DDS BGRA8, DDS BC3)')
    containers = texel_errors(spec, probe)
    table = {}
    for container, entry in sorted(containers.items()):
        if entry['missing']:
            check.fail(Mismatch('texels %s' % container, 'presence', '%d opaque sample(s) missing' % entry['missing']))
        if not entry['discriminating']:
            check.fail(Mismatch('texels %s' % container, 'probe', 'no opaque cell has a partial alpha and a nonzero color, '
                                'so no premultiplication could show'))
        for row in entry['cells']:
            partial = row['sourceBytes'][3] < 255 and max(row['sourceBytes'][:3]) > 0
            if partial and row['separation'] <= TEXEL_SEPARATION:
                check.fail(Mismatch(row['id'], 'probe', 'the premultiplied hypotheses sit within %.2f/255 of the straight '
                                    'texel %s, so this cell cannot discriminate' % (row['separation'] * 255.0, row['sourceBytes'])))
            if row['straight']['error'] > TOLERANCE + EPSILON:
                check.fail(Mismatch(row['id'], 'tolerance', 'measured %s, straight texel %s (%.2f/255); cycles-premultiplied '
                                    '%.2f/255, eevee-premultiplied %.2f/255' % (
                                        [round(v * 255.0, 2) for v in row['measured']], row['sourceBytes'][:3],
                                        row['straight']['error'] * 255.0, row['cycles-premultiplied']['error'] * 255.0,
                                        row['eevee-premultiplied']['error'] * 255.0)))
        fits = [name for name in TEXEL_MODELS if entry['worst'][name] <= TOLERANCE + EPSILON]
        table[container] = {'worst255': {name: round(error * 255.0, 3) for name, error in entry['worst'].items()},
                            'texelRule': ' and '.join(fits) if fits else 'neither', 'cells': len(entry['cells']),
                            'discriminatingCells': entry['discriminating'], 'missingSamples': entry['missing']}
    check.data['containers'] = table
    check.data['engine'] = (probe.get('settings') or {}).get('engine') or probe.get('engine')
    check.ok('%d containers show straight texels on every opaque cell' % len(table))
    return table


# --------------------------------------------------------------------------------------------------------------------
# Color ramp
# --------------------------------------------------------------------------------------------------------------------


def ramp_errors(spec, probe):
    samples = _samples(probe)
    cells = {}
    for cell_id, cell in spec['cells'].items():
        sample = samples.get(cell_id)
        tex = [b / 255.0 for b in cell['texelBytes'][:3]]
        vc = [b / 255.0 for b in cell['vertexColorBytes']]
        gamma = [tex[c] * vc[c] for c in range(3)]
        linear = [encode(decode(tex[c]) * vc[c]) for c in range(3)]
        if sample is None:
            cells[cell_id] = {'missing': True}
            continue
        measured = measured_display(sample)
        cells[cell_id] = {'storage': cell['storage'], 'measured': measured, 'gamma': gamma, 'linear': linear,
                          'gammaError': max(abs(measured[c] - gamma[c]) for c in range(3)),
                          'linearError': max(abs(measured[c] - linear[c]) for c in range(3))}
    return cells


def check_ramp(result_obj, spec, probe):
    check = result_obj.check('asymmetric color ramp: tex * vc in gamma space within 1/255 (eight-bit and float storage)')
    cells = ramp_errors(spec, probe)
    worst = {}
    for cell_id, cell in cells.items():
        if cell.get('missing'):
            check.fail(Mismatch(cell_id, 'presence', 'no sample'))
            continue
        storage = cell['storage']
        if cell['gammaError'] > worst.get(storage, (-1.0, None))[0]:
            worst[storage] = (cell['gammaError'], cell_id)
        if cell['gammaError'] > TOLERANCE + EPSILON:
            check.fail(Mismatch(cell_id, 'tolerance', 'measured %s, expected %s (%.2f/255); the linear-space product is %s (%.2f/255)'
                                % ([round(v * 255, 2) for v in cell['measured']], [round(v * 255, 2) for v in cell['gamma']],
                                   cell['gammaError'] * 255, [round(v * 255, 2) for v in cell['linear']], cell['linearError'] * 255)))
    check.data['worst255'] = {storage: round(error * 255.0, 3) for storage, (error, _) in worst.items()}
    check.data['cells'] = len(cells)
    check.ok('%d cells within 1/255 of tex * vc' % len(cells))
    return cells


def ramp_control(pristine_cells, control_probe, spec):
    control = ramp_errors(spec, control_probe)
    applicable = [cid for cid, c in pristine_cells.items() if not c.get('missing') and c['gammaError'] <= TOLERANCE + EPSILON]
    if not applicable:
        return None, 'masked: no pristine ramp cell is within 1/255'
    hits = [cid for cid in applicable if not control[cid].get('missing') and control[cid]['gammaError'] > TOLERANCE + EPSILON]
    if hits:
        worst = max(hits, key=lambda cid: control[cid]['gammaError'])
        return True, 'the Linear declaration misses tex * vc on %d of %d cells (worst %.2f/255 at %s; it matches the linear-space product within %.2f/255)' % (
            len(hits), len(applicable), control[worst]['gammaError'] * 255, worst, control[worst]['linearError'] * 255)
    return False, 'NOT detected: every control cell stays within 1/255 of the gamma-space product'


# --------------------------------------------------------------------------------------------------------------------
# DDS orientation
# --------------------------------------------------------------------------------------------------------------------


def orientation_render(spec, probe):
    samples = _samples(probe)
    by_kind = {}
    for cell_id, cell in spec['cells'].items():
        entry = by_kind.setdefault(cell['kind'], {'worst': 0.0, 'failures': [], 'missing': 0})
        sample = samples.get(cell_id)
        if sample is None:
            entry['missing'] += 1
            continue
        measured = measured_display(sample)
        expected = [b / 255.0 for b in cell['expectedBytes']]
        error = max(abs(measured[c] - expected[c]) for c in range(3))
        entry['worst'] = max(entry['worst'], error)
        if error > TOLERANCE + EPSILON:
            entry['failures'].append({'id': cell_id, 'measured255': [round(v * 255, 1) for v in measured], 'expected255': cell['expectedBytes']})
    return by_kind


def orientation_pixels(spec, probe):
    """Per image kind: 'same' / 'flipped' / 'neither' relative to the authored rows, from the buffers Blender loaded."""
    pattern = np.asarray(builders.orientation_pattern(), dtype=np.int64)
    kinds = {image['index']: image['kind'] for image in spec.get('images') or []}
    out = {}
    for image in probe.get('images') or []:
        kind = kinds.get(image.get('index'))
        if kind is None:
            continue
        pixels = image.get('pixels')
        width, height = image.get('size') or [0, 0]
        if pixels is None or width * height == 0:
            out[kind] = {'orientation': 'unreadable', 'size': [width, height]}
            continue
        channels = len(pixels) // (width * height)
        buffer = np.rint(np.asarray(pixels, dtype=np.float64).reshape(height, width, channels) * 255.0).astype(np.int64)
        topdown = buffer[::-1, :, :3]  # Blender's pixel rows run bottom to top
        if topdown.shape != pattern[:, :, :3].shape:
            out[kind] = {'orientation': 'neither', 'size': [width, height], 'detail': 'size differs from the 16 x 16 pattern'}
        elif np.array_equal(topdown, pattern[:, :, :3]):
            out[kind] = {'orientation': 'same'}
        elif np.array_equal(topdown, pattern[::-1, :, :3]):
            out[kind] = {'orientation': 'flipped'}
        else:
            out[kind] = {'orientation': 'neither', 'maxDifference': int(np.abs(topdown - pattern[:, :, :3]).max())}
    return out


def check_orientation(result_obj, spec, probe):
    check = result_obj.check('DDS orientation: every block renders at its authored place (PNG, DDS BGRA8, DDS BC1)')
    by_kind = orientation_render(spec, probe)
    for kind, entry in sorted(by_kind.items()):
        if entry['missing']:
            check.fail(Mismatch(kind, 'presence', '%d block sample(s) missing' % entry['missing']))
        if entry['failures']:
            first = entry['failures'][0]
            check.fail(Mismatch(kind, 'orientation', '%d of 16 blocks differ; first %s measured %s expected %s'
                                % (len(entry['failures']), first['id'], first['measured255'], first['expected255'])))
    check.data['renderWorst255'] = {kind: round(entry['worst'] * 255.0, 3) for kind, entry in by_kind.items()}
    pixels = orientation_pixels(spec, probe)
    check.data['loadedBufferOrientation'] = pixels
    dds_flipped = [kind for kind, value in pixels.items() if kind != 'png' and value.get('orientation') == 'flipped']
    check.data['designQuestion'] = ('DDS buffers load flipped relative to PNG (%s): V_FLIP_CONTAINERS would need dds' % ', '.join(dds_flipped)
                                    if dds_flipped else 'DDS buffers load with the PNG orientation: V_FLIP_CONTAINERS stays empty'
                                    if pixels and all(v.get('orientation') == 'same' for v in pixels.values()) else
                                    'buffer orientation not established (%s)' % pixels)
    check.ok('all 48 blocks at their authored place')
    return by_kind


def orientation_control(pristine_by_kind, control_probe, spec):
    control = orientation_render(spec, control_probe)
    verdicts = {}
    for kind in ('dds-bgra8', 'dds-bc1'):
        pristine = pristine_by_kind.get(kind) or {}
        if pristine.get('failures') or pristine.get('missing') or not pristine:
            verdicts[kind] = 'masked (the pristine %s quad already fails)' % kind
        else:
            verdicts[kind] = 'detected' if control.get(kind, {}).get('failures') else 'NOT DETECTED'
    applicable = [k for k, v in verdicts.items() if v in ('detected', 'NOT DETECTED')]
    if not applicable:
        return None, 'masked: %s' % verdicts, verdicts
    missed = [k for k in applicable if verdicts[k] == 'NOT DETECTED']
    if missed:
        return False, 'flipped image NOT detected on %s' % ', '.join(missed), verdicts
    if (control.get('png') or {}).get('failures'):
        return False, 'the control changed the PNG quad as well, which it must not', verdicts
    return True, 'flipped DDS detected on %s' % ', '.join(applicable), verdicts


# --------------------------------------------------------------------------------------------------------------------
# Transmission route
# --------------------------------------------------------------------------------------------------------------------


def readback_module(shared_root):
    """Shared tools/blender/readback.py as a module (its top level imports the standard library only)."""
    import importlib.util
    path = os.path.join(shared_root, 'tools', 'blender', 'readback.py')
    spec = importlib.util.spec_from_file_location('shared_readback_offline', path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def evaluate_expectations(readback, dump, expectation_args):
    args = readback.parse_arguments(list(expectation_args) + ['--out', 'unused.json'])
    return readback.check_expectations(dump, args)


def principled_values(dump, material_name):
    for material in dump.get('materials') or []:
        if material.get('name') != material_name:
            continue
        for node in (material.get('node_tree') or {}).get('nodes') or []:
            if node.get('type') == 'ShaderNodeBsdfPrincipled':
                principled = node.get('principled') or {}
                return {key: principled.get(key) for key in ('Transmission Weight', 'IOR', 'Roughness', 'Metallic', 'Emission Strength')}
    return None


def _material_entry(dump, name):
    return next((m for m in dump.get('materials') or [] if m.get('name') == name), None)


def upstream_nodes(material_entry, node_name, socket_identifier, depth=8):
    """Every node feeding ``socket_identifier`` of ``node_name`` through the material's links, at most ``depth`` links
    back (the same search bound as readback.py's ``_upstream_image``)."""
    tree = (material_entry or {}).get('node_tree') or {}
    nodes = {node['name']: node for node in tree.get('nodes') or []}
    incoming = {}
    for link in tree.get('links') or []:
        incoming.setdefault(link['to_node'], []).append(link)
    frontier = [(link['from_node'], 1) for link in incoming.get(node_name, []) if link['to_socket'] == socket_identifier]
    seen, found = set(), []
    while frontier:
        name, level = frontier.pop(0)
        if name in seen or level > depth:
            continue
        seen.add(name)
        if name in nodes:
            found.append(nodes[name])
        frontier.extend((link['from_node'], level + 1) for link in incoming.get(name, []))
    return found


VERTEX_COLOR_NODES = ('ShaderNodeVertexColor', 'ShaderNodeAttribute')


def binding_mismatches(dump, bindings):
    """[(where, detail)] where the route's material binds other images than the GLB declares, or routes the vertex
    color elsewhere than into Base Color. ``bindings`` comes from ``probe_builders.transmission_bindings``."""
    out = []
    material = _material_entry(dump, bindings['material'])
    if material is None:
        return [('bindings/material', 'material %r is absent' % bindings['material'])]
    principled = next((n for n in ((material.get('node_tree') or {}).get('nodes') or []) if n.get('type') == 'ShaderNodeBsdfPrincipled'), None)
    if principled is None:
        return [('bindings/material', 'material %r has no Principled BSDF' % bindings['material'])]
    images = {image['name']: image for image in dump.get('images') or []}
    for socket, sha_key, name_key in (('Base Color', 'baseColorImageSha256', 'baseColorImage'),
                                      ('Emission Color', 'emissionImageSha256', 'emissionImage')):
        upstream = upstream_nodes(material, principled['name'], socket)
        bound = [node.get('image') for node in upstream if node.get('type') == 'ShaderNodeTexImage' and node.get('image')]
        shas = [(images.get(name) or {}).get('packed_sha256') for name in bound]
        expected = bindings.get(sha_key)
        if expected is not None and shas != [expected]:
            out.append(('bindings/%s/image' % socket, 'fed by %s (packed SHA-256 %s), expected exactly the image with SHA-256 %s (%s)'
                        % (bound, [s[:12] if s else s for s in shas], expected[:12], bindings.get(name_key))))
        vertex_color = any(node.get('type') in VERTEX_COLOR_NODES for node in upstream)
        wanted = bool(bindings.get('baseColorVertexColor')) if socket == 'Base Color' else False
        if vertex_color != wanted:
            out.append(('bindings/%s/vertex color' % socket, 'a vertex-color node %s upstream, expected %s'
                        % ('is' if vertex_color else 'is not', 'one' if wanted else 'none')))
    if bindings.get('baseColorVertexColor'):
        meshes = [mesh for mesh in dump.get('meshes') or [] if bindings['material'] in (mesh.get('materials') or [])]
        if not meshes or any(not mesh.get('color_attributes') for mesh in meshes):
            out.append(('bindings/mesh color attribute', 'the mesh drawing %r carries no color attribute' % bindings['material']))
    return out


def check_transmission(result_obj, receipt, dump_path, readback, expectation_args, bindings=None):
    check = result_obj.check('transmission route: readback.py --import-glb asserts weight 1, IOR 1.0, roughness 0, metallic 0 and both bindings; '
                             'the bound images are T and E by SHA-256 and COLOR_0 feeds Base Color only')
    run = (receipt or {}).get('run') or {}
    dump = None
    if dump_path and os.path.isfile(dump_path):
        with open(dump_path, 'r', encoding='utf-8') as f:
            dump = json.load(f)
    if run.get('outcome') != 'ok':
        check.fail(Mismatch('readback', 'launch', '%s (exit %s); readback exits 3 when an --expect-* assertion fails: %s'
                            % (run.get('outcome'), run.get('exitCode'), ((dump or {}).get('expectations') or {}).get('failures'))))
    if dump is None:
        check.fail(Mismatch('readback', 'presence', 'no dump'))
        return None
    failures = evaluate_expectations(readback, dump, expectation_args)
    for failure in failures:
        check.fail(Mismatch('expectation', 'value', failure))
    stated = dump.get('expectations') or {}
    if not stated.get('checked'):
        check.fail(Mismatch('readback/expectations', 'value', 'readback did not check the expectations'))
    check.data['principled'] = principled_values(dump, builders.TRANSMISSION_MATERIAL)
    check.data['readbackFailures'] = stated.get('failures')
    if bindings is not None:
        mismatches = binding_mismatches(dump, bindings)
        for where, detail in mismatches:
            check.fail(Mismatch(where, 'binding', detail))
        check.data['bindings'] = {'expected': bindings, 'mismatches': [{'where': w, 'detail': d} for w, d in mismatches]}
    check.ok('all expectations held; T, E and COLOR_0 bound where the GLB declares them')
    return dump


def transmission_control(pristine_passed, receipt, dump_path, readback, expectation_args):
    if not pristine_passed:
        return None, 'masked: the pristine transmission GLB already fails its expectations'
    run = (receipt or {}).get('run') or {}
    if not dump_path or not os.path.isfile(dump_path):
        return False, 'NOT detected: the stripped copy produced no dump (%s, exit %s)' % (run.get('outcome'), run.get('exitCode'))
    with open(dump_path, 'r', encoding='utf-8') as f:
        dump = json.load(f)
    failures = evaluate_expectations(readback, dump, expectation_args)
    opaque = evaluate_expectations(readback, dump, ['--expect-material', builders.TRANSMISSION_MATERIAL, '--expect-opaque'])
    values = principled_values(dump, builders.TRANSMISSION_MATERIAL)
    if not failures:
        return False, 'NOT detected: the stripped copy still passes the transmission expectations (%s)' % values
    if run.get('exitCode') != 3:
        return False, 'the expectations fail offline but readback exited %s, not 3' % run.get('exitCode')
    if opaque:
        return False, 'detected, but the stripped copy does not read back opaque: %s' % opaque
    return True, 'stripped copy fails the transmission expectations (%s) and reads back opaque (weight %r)' % (
        failures[0], (values or {}).get('Transmission Weight'))


def transmission_swap_control(pristine_passed, receipt, dump_path, bindings):
    """E and T exchanged in a GLB copy: detected when the identity check fails at BOTH sockets against the pristine
    bindings. Masked (None) when the pristine route already failed; None (never exercised) when the copy produced no
    usable dump or readback failed its value expectations (the exchange changes no value it asserts)."""
    if not pristine_passed:
        return None, 'masked: the pristine transmission GLB already fails the route'
    run = (receipt or {}).get('run') or {}
    if run.get('outcome') != 'ok' or not dump_path or not os.path.isfile(dump_path):
        return None, 'NEVER EXERCISED: the E/T-swapped copy did not read back cleanly (%s, exit %s)' % (run.get('outcome'), run.get('exitCode'))
    with open(dump_path, 'r', encoding='utf-8') as f:
        dump = json.load(f)
    wheres = {where for where, _ in binding_mismatches(dump, bindings)}
    hit = {'bindings/Base Color/image', 'bindings/Emission Color/image'}
    if hit <= wheres:
        return True, 'E/T swap detected: Base Color and Emission Color each bind the other image'
    return False, 'NOT detected: the E/T-swapped copy binds %s' % (sorted(wheres & hit) or 'the pristine images')


# --------------------------------------------------------------------------------------------------------------------
# The display-blend probe (run_display_blend_probe.py): both constant sets, both engines, drawn by the probe-only
# importer variant. Expectations come from display_blend_math, never from the render.
#
# Two kinds of check (review 2026-09-27 item 10). VALIDITY checks say whether the probe works: the graphs Blender holds
# compute the declared models, the texel rule, set A / set K / every T <= 1 cell drawn as declared and within its bound,
# the worst cells exercise their bounds, the sentinels give a clean verdict and their T <= 1 rows fit the composite
# model, the lit route on T <= 1, and the consistency of the set-B answer with the sentinel verdict. ANSWER checks are
# the set-B cells and materials that need T > 1: whether they pass IS the answer (set B available or not), so a clean
# "clamped at 1" run fails them and still leaves the probe valid.
# --------------------------------------------------------------------------------------------------------------------

DB_SLACK = TOLERANCE + EPSILON
DB_ATTAINED_SLACK255 = 2.0  # a worst-case cell must realize its bound within 2/255, or the probe cannot exercise it
DB_SENTINEL_SEPARATION = 2.0 / 255.0  # a sentinel cell discriminates when the two compositing models differ by this
DB_READBACK_SLACK = 0.05 / 255.0  # the graph read back from Blender against the declared model (float32 storage)


def _channels(bytes_list):
    return [b / 255.0 for b in bytes_list[:3]]


def db_texel_key(cell):
    """The key of a cell's graph readback (display_blend_variant.texel_key)."""
    if cell.get('kind') == 'sentinel' or cell.get('sourceBytes') is None:
        return 'constant'
    return ','.join(str(int(v)) for v in cell['sourceBytes'])


def db_texel_linear(cell):
    """The cell's straight texel, scene-linear (also the lit color L of a lit cell under the probe's sun of strength pi,
    where a Lambert surface reflects its linear albedo)."""
    return [dbm.srgb_decode(b / 255.0) for b in cell['sourceBytes'][:3]]


def db_blend_expectation(cell, fit_key='fit'):
    """(drawn, gamebryo) per channel of a blend cell: the fit ``cell[fit_key]`` and the source's display blend."""
    sf, df = cell['sourceFactor'], cell['destinationFactor']
    cs, cd = _channels(cell['sourceBytes']), _channels(cell['backgroundBytes'])
    a = cell['sourceBytes'][3] / 255.0
    fit = cell[fit_key]
    return ([dbm.drawn(fit, sf, df, cs[c], a, cd[c]) for c in range(3)],
            [dbm.gamebryo(sf, df, cs[c], a, cd[c]) for c in range(3)])


def db_transparency(cell, fit_key='fit'):
    """The largest T any channel of the cell's fit draws (above 1 only under set B)."""
    sf, df = cell['sourceFactor'], cell['destinationFactor']
    cs = _channels(cell['sourceBytes'])
    a = cell['sourceBytes'][3] / 255.0
    return max(dbm.fit_terms(cell[fit_key], *dbm.k_terms(sf, df, cs[c], a))[1] for c in range(3))


def db_lit_transparency(cell, lit_key='lit'):
    """tau(As) of a lit cell's curve (above 1 only under set B)."""
    return dbm.lit_terms(cell[lit_key], cell['sourceBytes'][3] / 255.0)[1]


def db_material_needs_t_above_one(material):
    """Whether a material's recipe can draw T above 1 (set B's two-node or anchored fits with tmax > 1, and lit curves
    whose tau exceeds 1): its graph and cells are ANSWER checks, never validity checks."""
    if material.get('kind') == 'blend':
        fit = material['fit']
        return fit['kind'] in ('two', 'anchored') and float(fit.get('tmax', 1.0)) > 1.0
    if material.get('kind') == 'lit':
        lit = material['lit']
        if lit['form'] == 'constant':
            return float(lit['tau']) > 1.0
        if lit['form'] == 'scaled-power':
            return float(lit['tmax']) > 1.0 and float(lit['q']) > 0.0
    return False


def db_sentinel_models(cell):
    """(unclamped, clamped at 1): E + T lin(Cd) against E + min(T, 1) lin(Cd), displayed."""
    cd = dbm.srgb_decode(cell['backgroundBytes'][0] / 255.0)
    return (dbm.display(cell['e'] + cell['t'] * cd), dbm.display(cell['e'] + min(cell['t'], 1.0) * cd))


def db_lit_expectation(cell, twin_linear, lit_key='lit'):
    """(drawn, gamebryo) per channel of a lit cell from its twin's rendered lit color L (scene-linear): drawn
    m(As) L + tau(As) lin(Cd); Gamebryo blends the lit display color Ld = display(L)."""
    a = cell['sourceBytes'][3] / 255.0
    m, tau = dbm.lit_terms(cell[lit_key], a)
    cd = _channels(cell['backgroundBytes'])
    drawn = [dbm.display(m * twin_linear[c] + tau * dbm.srgb_decode(cd[c])) for c in range(3)]
    ld = [dbm.display(twin_linear[c]) for c in range(3)]
    return drawn, [dbm.gamebryo(cell['sourceFactor'], cell['destinationFactor'], ld[c], a, cd[c]) for c in range(3)]


def db_declared_model(cell):
    """The displayed RGB the declared recipe draws for a cell, analytically (the graph-readback reference): the fit
    for a blend cell, m L + tau Cd for a lit cell (L the straight texel), the texel for a twin or opaque cell, the
    unclamped E + T Cd for a sentinel."""
    kind = cell['kind']
    if kind == 'blend':
        return db_blend_expectation(cell)[0]
    if kind == 'lit':
        return db_lit_expectation(cell, db_texel_linear(cell))[0]
    if kind == 'sentinel':
        return [db_sentinel_models(cell)[0]] * 3
    return [dbm.display(v) for v in db_texel_linear(cell)]


def db_readback_display(cell, closure):
    """The displayed RGB of a cell from its material's read-back closure: E + lit * L + T lin(Cd)."""
    lit = db_texel_linear(cell) if cell['kind'] in ('lit', 'twin') else [0.0, 0.0, 0.0]
    cd = [dbm.srgb_decode(v) for v in _channels(cell.get('backgroundBytes') or [0, 0, 0])]
    return [dbm.display(closure['E'][c] + closure['lit'] * lit[c] + closure['T'][c] * cd[c]) for c in range(3)]


def _max_error(measured, expected):
    return max(abs(measured[c] - expected[c]) for c in range(3))


def db_evaluate(spec, probe):
    """Per cell: the measured display RGB and its errors against every model (the input of every check)."""
    samples = _samples(probe)
    means = probe.get('cellMeans') or {}
    out = {}
    for cell_id, cell in spec['cells'].items():
        sample = samples.get(cell_id)
        if cell['kind'] in ('lit', 'twin'):
            # measured as inner-block means (display_blend_variant.lit_pass), never the single center pixel, which
            # carries Cycles' closure-pick noise on a lit blended cell
            sample = means.get(cell_id)
        entry = {'kind': cell['kind'], 'missing': sample is None}
        if sample is not None:
            entry['measured'] = measured_display(sample)
            entry['linear'] = list(sample['linear'][:3])
            entry['spread'] = sample.get('spread', max(sample.get('std') or [0.0]))
        if sample is not None and cell['kind'] == 'blend':
            drawn, target = db_blend_expectation(cell)
            entry.update(drawn=drawn, gamebryo=target, drawnError=_max_error(entry['measured'], drawn),
                         gamebryoError=_max_error(entry['measured'], target), modelError=_max_error(drawn, target),
                         transparency=db_transparency(cell))
        elif sample is not None and cell['kind'] == 'sentinel':
            unclamped, clamped = db_sentinel_models(cell)
            entry.update(unclamped=unclamped, clamped=clamped, unclampedError=abs(entry['measured'][0] - unclamped),
                         clampedError=abs(entry['measured'][0] - clamped))
        out[cell_id] = entry
    for cell_id, cell in spec['cells'].items():
        if cell['kind'] != 'lit' or out[cell_id]['missing']:
            continue
        twin = out.get(cell['twin'])
        if twin is None or twin['missing']:
            out[cell_id]['twinMissing'] = True
            continue
        drawn, target = db_lit_expectation(cell, twin['linear'])
        out[cell_id].update(drawn=drawn, gamebryo=target, drawnError=_max_error(out[cell_id]['measured'], drawn),
                            gamebryoError=_max_error(out[cell_id]['measured'], target), twinLinear=twin['linear'],
                            transparency=db_lit_transparency(cell))
    return out


def _group_worst(rows, key):
    worst = {}
    for row in rows:
        k = key(row)
        if k not in worst or row['error'] > worst[k]['error']:
            worst[k] = row
    return worst


def _sink(result_obj, answer_obj, is_answer):
    return answer_obj if (is_answer and answer_obj is not None) else result_obj


def db_check_graphs(result_obj, spec, probe, answer_obj=None):
    """The graph Blender HOLDS computes each cell's declared model (review 2026-09-27 item 8). The variant reads every
    material's node tree back (display_blend_variant.GraphReader: links, operations, clamp flags and the socket defaults
    Blender stored) at each texel of its cells; here that read-back closure, composited over the cell's background,
    must equal the analytic model of the recipe the SPEC declares (not the one the variant echoes) within 0.05/255. So a
    socket default stored as something else (a color above 1 clamped by its RNA range, say), a missing link or a
    control spec's constants reaching a pristine judge all fail. A Transparent BSDF color default the variant set that
    Blender stored otherwise fails too, except on the 'socket' sentinels, whose stored default is what they measure (it
    is reported). Materials that need T above 1 are judged on ``answer_obj``."""
    graphs = {g.get('index'): g for g in probe.get('graphs') or []}
    materials = {m['index']: m for m in spec['materials']}
    checks = {False: result_obj.check('graphs: the node trees Blender holds (read back) compute every cell\'s declared model '
                                      'within 0.05/255 (materials drawable with T <= 1)')}
    if answer_obj is not None:
        checks[True] = answer_obj.check('graphs (set B, T > 1): the node trees Blender holds (read back) compute every cell\'s '
                                        'declared model within 0.05/255')
    else:
        checks[True] = checks[False]
    worst = {False: 0.0, True: 0.0}
    socket_sentinels, stored_otherwise, echo = [], [], []
    for index, material in materials.items():
        needs = db_material_needs_t_above_one(material)
        check = checks[needs]
        drawn = graphs.get(index)
        if drawn is None:
            check.fail(Mismatch('material %s' % material['name'], 'presence', 'no graph record'))
            continue
        for key in ('fit', 'lit', 'sentinel'):
            if material.get(key) is not None and drawn.get(key) != material[key]:
                echo.append(material['name'])
                check.fail(Mismatch('material %s/%s' % (material['name'], key), 'value',
                                    'the variant echoes %s, the spec says %s' % (drawn.get(key), material[key])))
        if drawn.get('readbackError'):
            check.fail(Mismatch('material %s' % material['name'], 'readback', drawn['readbackError']))
        is_socket_sentinel = material.get('kind') == 'sentinel' and material['sentinel']['mode'] == 'socket'
        for entry in drawn.get('socketDefaults') or []:
            if any(abs(a - b) > 1e-6 for a, b in zip(entry['set'], entry['stored'])):
                row = {'material': material['name'], 'set': entry['set'], 'stored': entry['stored']}
                if is_socket_sentinel:
                    socket_sentinels.append(row)
                else:
                    stored_otherwise.append(row)
                    check.fail(Mismatch('material %s' % material['name'], 'socket default',
                                        'set %s, Blender stored %s' % (entry['set'], entry['stored'])))
        readback = drawn.get('readback') or {}
        for quad in material['quads']:
            cell = spec['cells'][quad['cell']]
            closure = readback.get(db_texel_key(cell))
            if closure is None:
                check.fail(Mismatch(cell['id'], 'presence', 'no graph readback for texel %s' % db_texel_key(cell)))
                continue
            error = _max_error(db_readback_display(cell, closure), db_declared_model(cell))
            if is_socket_sentinel:
                continue  # its model assumes the default was stored as set; what was stored is reported above
            worst[needs] = max(worst[needs], error)
            if error > DB_READBACK_SLACK:
                check.fail(Mismatch(cell['id'], 'readback', 'the read-back graph draws %.3f/255 from the declared model'
                                    % (error * 255.0)))
    for needs, check in checks.items():
        check.data.update(materials=sum(1 for m in materials.values() if db_material_needs_t_above_one(m) == needs),
                          worstReadback255=round(worst[needs] * 255.0, 5), nodes=sum(int(g.get('nodes') or 0) for g in graphs.values()),
                          socketDefaultsStoredOtherwise=stored_otherwise, recipeEchoMismatches=echo,
                          socketSentinelDefaultsStoredOtherwise=socket_sentinels)
        check.ok('%d materials: the read-back graphs compute their declared models' % check.data['materials'])
    return {'worstReadback255': {'T <= 1': round(worst[False] * 255.0, 5), 'T > 1': round(worst[True] * 255.0, 5)},
            'socketDefaultsStoredOtherwise': stored_otherwise, 'socketSentinelDefaultsStoredOtherwise': socket_sentinels}


def db_check_drawn(result_obj, spec, evaluation, label, answer_obj=None):
    """Blender draws the declared fit: every blend cell within 1/255 of its fit's drawn value. Set B is split by
    whether the cell's fit draws T above 1 (only the sentinels' unclamped verdict makes those drawable); that group is
    an answer check."""
    rows = []
    for cell_id, cell in spec['cells'].items():
        if cell['kind'] != 'blend':
            continue
        entry = evaluation[cell_id]
        rows.append({'id': cell_id, 'set': cell['set'], 'pair': cell['pair'], 'container': cell['container'],
                     'section': cell['section'], 'missing': entry['missing'], 'error': entry.get('drawnError', 0.0),
                     'tAboveOne': db_transparency(cell) > 1.0 + 1e-9})
    groups = (('A', lambda r: r['set'] == 'A', False), ('K', lambda r: r['set'] == 'K', False),
              ('B, T <= 1', lambda r: r['set'] == 'B' and not r['tAboveOne'], False),
              ('B, T > 1', lambda r: r['set'] == 'B' and r['tAboveOne'], True))
    table = {}
    for name, keep, is_answer in groups:
        selected = [r for r in rows if keep(r)]
        check = _sink(result_obj, answer_obj, is_answer).check('%s drawn: set %s cells within 1/255 of the declared display-space fit '
                                                               '(%d cells)' % (label, name, len(selected)))
        worst = _group_worst(selected, lambda r: (r['pair'], r['container']))
        failures = [r for r in selected if r['missing'] or r['error'] > DB_SLACK]
        for r in failures[:200]:
            check.fail(Mismatch(r['id'], 'presence' if r['missing'] else 'tolerance',
                                'missing' if r['missing'] else '%.2f/255 from the drawn fit' % (r['error'] * 255.0)))
        if len(failures) > 200:
            check.data['failuresNotListed'] = len(failures) - 200
        check.data['failures'] = len(failures)
        check.data['worst255'] = {'%s %s' % k: round(v['error'] * 255.0, 3) for k, v in sorted(worst.items())}
        table[name] = {'cells': len(selected), 'failures': len(failures),
                       'worst255': round(max([r['error'] for r in selected] or [0.0]) * 255.0, 3), 'answer': is_answer}
        check.ok('%d cells within 1/255' % len(selected)) if selected else check.skip('no cells')
    return table


def db_check_gamebryo(result_obj, spec, evaluation, label, answer_obj=None):
    """The reported bound holds on the render: every pristine blend cell within bound + 1/255 of Gamebryo (Exact pairs
    within 1/255); per group and pair, the measured worst beside the bound. Set B's cells that need T > 1 are an answer
    check, like their drawn check."""
    table = {}
    groups = (('A', lambda c: c['set'] == 'A', False), ('K', lambda c: c['set'] == 'K', False),
              ('B, T <= 1', lambda c: c['set'] == 'B' and db_transparency(c) <= 1.0 + 1e-9, False),
              ('B, T > 1', lambda c: c['set'] == 'B' and db_transparency(c) > 1.0 + 1e-9, True))
    for name, keep, is_answer in groups:
        check = _sink(result_obj, answer_obj, is_answer).check('%s bound: set %s cells within their reported bound + 1/255 of '
                                                               'Gamebryo' % (label, name))
        per_pair = {}
        for cell_id, cell in spec['cells'].items():
            if cell['kind'] != 'blend' or not keep(cell) or evaluation[cell_id]['missing']:
                continue
            error = evaluation[cell_id]['gamebryoError']
            limit = cell['bound255'] / 255.0 + DB_SLACK
            pair = per_pair.setdefault(cell['pair'], {'measuredWorst255': 0.0, 'bound255': cell['bound255'], 'cells': 0, 'at': None})
            pair['cells'] += 1
            if error * 255.0 > pair['measuredWorst255']:
                pair['measuredWorst255'], pair['at'] = round(error * 255.0, 3), cell_id
            if error > limit:
                check.fail(Mismatch(cell_id, 'bound', '%.2f/255 from Gamebryo, bound %.2f/255' % (error * 255.0, cell['bound255'])))
        check.data['pairs'] = per_pair
        table[name] = per_pair
        check.ok('%d pairs within their bounds' % len(per_pair)) if per_pair else check.skip('no cells')
    return table


def db_check_worst(result_obj, spec, evaluation, label):
    """The probe exercises every blend bound: each (set, pair)'s byte-realized worst cell (PNG) reaches bound - 2/255,
    RECOMPUTED here from the cell's own bytes (a probe-design check); the measured error there is reported beside it."""
    check = result_obj.check('%s worst cells: each non-exact (set, pair) realizes its bound within 2/255' % label)
    rows = []
    for case in spec.get('worstCases') or []:
        cell_id = 'db_worst_%s_%s_png' % (case['set'], case['pair'].replace('/', '_'))
        cell = spec['cells'].get(cell_id)
        if cell is None:
            check.fail(Mismatch(cell_id, 'presence', 'no worst-case cell in the spec'))
            continue
        drawn, target = db_blend_expectation(cell)
        attained = _max_error(drawn, target) * 255.0
        entry = evaluation.get(cell_id) or {}
        rows.append(dict(case, attainedFromCell255=round(attained, 4),
                         measured255=None if entry.get('missing', True) else round(entry['gamebryoError'] * 255.0, 3)))
        if attained < case['bound255'] - DB_ATTAINED_SLACK255:
            check.fail(Mismatch(cell_id, 'probe', 'the cell reaches %.2f/255 of a %.2f/255 bound' % (attained, case['bound255'])))
    check.data['cases'] = rows
    check.ok('%d worst cells' % len(rows))
    return rows


def db_check_lit_worst(result_obj, spec, evaluation, label):
    """The probe exercises every lit bound (review 2026-09-27 item 4): each lit (set, pair)'s worst cell, byte-realized
    at the SEEDED arg-max, reaches its bound - 2/255; a second cell at the closed-form pin's locus (where it differs
    from the arg-max) reaches the pin - 2/255. Recomputed from each cell's bytes with the lit color L the straight texel
    (the probe's sun makes the twin reflect exactly that); the render's error at the cell is reported beside it."""
    check = result_obj.check('%s lit worst cells: each lit (set, pair) realizes its bound (and its closed-form pin) within 2/255' % label)
    rows = []
    for case in spec.get('litWorstCases') or []:
        cell = spec['cells'].get(case['cell'])
        if cell is None:
            check.fail(Mismatch(case['cell'], 'presence', 'no lit worst-case cell in the spec'))
            continue
        drawn, target = db_lit_expectation(cell, db_texel_linear(cell))
        attained = _max_error(drawn, target) * 255.0
        entry = evaluation.get(case['cell']) or {}
        rows.append(dict(case, attainedFromCell255=round(attained, 4),
                         measured255=None if entry.get('missing', True) or entry.get('twinMissing') else round(entry['gamebryoError'] * 255.0, 3)))
        if attained < case['target255'] - DB_ATTAINED_SLACK255:
            check.fail(Mismatch(case['cell'], 'probe', 'the lit cell reaches %.2f/255 of its %s %.2f/255'
                                % (attained, case['locus'], case['target255'])))
    check.data['cases'] = rows
    check.ok('%d lit worst cells' % len(rows)) if rows else check.skip('no lit worst cells')
    return rows


def db_check_clamp_coverage(result_obj, spec, label):
    """The probe can see a clamp on every set-B pair that draws T above 1 (a probe-design check, analytic): each such
    pair, blend and lit, has a clamp-sensitive cell (spec['clampCases']) whose drawn value moves by more than 2/255 when
    T is clamped at 1, recomputed from the cell's bytes. Without it a clamping engine could pass that pair's answer
    checks: set B's anchored alpha-over peaks at T 1.07, and no pair-block cell sits where that shows."""
    check = result_obj.check('%s clamp coverage: every set-B pair drawing T > 1 has a cell where a clamp at 1 shows by '
                             'more than 2/255' % label)
    needed = {(m['kind'], m['pair']) for m in spec['materials'] if m.get('set') == 'B' and db_material_needs_t_above_one(m)}
    covered, rows = set(), []
    for case in spec.get('clampCases') or []:
        cell = spec['cells'].get(case['cell'])
        if cell is None:
            check.fail(Mismatch(case['cell'], 'presence', 'no clamp cell in the spec'))
            continue
        if cell['kind'] == 'blend':
            sf, df = cell['sourceFactor'], cell['destinationFactor']
            cs, cd = _channels(cell['sourceBytes']), _channels(cell['backgroundBytes'])
            a = cell['sourceBytes'][3] / 255.0
            moved = 0.0
            for c in range(3):
                e, t = dbm.fit_terms(cell['fit'], *dbm.k_terms(sf, df, cs[c], a))
                moved = max(moved, abs(dbm.display(dbm.drawn_linear(e, t, cd[c])) - dbm.display(dbm.drawn_linear(e, min(t, 1.0), cd[c]))))
        else:
            m, tau = dbm.lit_terms(cell['lit'], cell['sourceBytes'][3] / 255.0)
            lit = db_texel_linear(cell)
            cd = [dbm.srgb_decode(v) for v in _channels(cell['backgroundBytes'])]
            moved = max(abs(dbm.display(m * lit[c] + tau * cd[c]) - dbm.display(m * lit[c] + min(tau, 1.0) * cd[c])) for c in range(3))
        rows.append(dict(case, movedFromCell255=round(moved * 255.0, 4)))
        if moved * 255.0 <= DB_ATTAINED_SLACK255:
            check.fail(Mismatch(case['cell'], 'probe', 'a clamp of T at 1 moves this cell by only %.2f/255' % (moved * 255.0)))
        else:
            covered.add((cell['kind'], cell['pair']))
    for kind, pair in sorted(needed - covered):
        check.fail(Mismatch('%s %s' % (kind, pair), 'probe', 'set B draws T > 1 on this pair but no cell can show a clamp'))
    check.data.update(cases=rows, pairsNeedingCoverage=sorted('%s %s' % k for k in needed))
    check.ok('%d set-B pairs covered' % len(covered)) if needed else check.skip('no set-B pair draws T > 1')
    return rows


def db_check_sentinels(result_obj, spec, evaluation, label):
    """The two-cell T > 1 probe: per mode, does Blender composite a Transparent BSDF color above 1 unclamped
    (E + T lin(Cd)) or clamped at 1? The check passes on either clean verdict; the verdict is the answer. A cell where
    the two models agree within 2/255 (T <= 1, or both clipped at 1) cannot discriminate, but it must still fit the
    shared composite model within 1/255 (review 2026-09-27 item 7): those rows are the probe's own check that
    E + T lin(Cd) holds at all."""
    verdicts = {}
    for mode in ('linked', 'socket'):
        check = result_obj.check('%s sentinels (%s): a clean verdict on Transparent BSDF colors above 1, and every '
                                 'non-discriminating cell on the composite model' % (label, mode))
        rows = [(cell_id, cell) for cell_id, cell in spec['cells'].items() if cell['kind'] == 'sentinel' and cell['mode'] == mode]
        fits = {'unclamped': 0, 'clamped at 1': 0, 'neither': 0}
        discriminating, shared, detail = 0, 0, []
        for cell_id, cell in rows:
            entry = evaluation[cell_id]
            if entry['missing']:
                check.fail(Mismatch(cell_id, 'presence', 'no sample'))
                continue
            if abs(entry['unclamped'] - entry['clamped']) <= DB_SENTINEL_SEPARATION:
                shared += 1
                miss = min(entry['unclampedError'], entry['clampedError'])
                if miss > DB_SLACK:
                    detail.append({'id': cell_id, 't': cell['t'], 'e': cell['e'], 'measured255': round(entry['measured'][0] * 255.0, 2),
                                   'model255': round(entry['unclamped'] * 255.0, 2), 'note': 'non-discriminating cell misses the model'})
                    check.fail(Mismatch(cell_id, 'model', 'a non-discriminating sentinel is %.2f/255 from E + T lin(Cd)' % (miss * 255.0)))
                continue
            discriminating += 1
            if entry['unclampedError'] <= DB_SLACK:
                fits['unclamped'] += 1
            elif entry['clampedError'] <= DB_SLACK:
                fits['clamped at 1'] += 1
            else:
                fits['neither'] += 1
                detail.append({'id': cell_id, 't': cell['t'], 'e': cell['e'], 'measured255': round(entry['measured'][0] * 255.0, 2),
                               'unclamped255': round(entry['unclamped'] * 255.0, 2), 'clamped255': round(entry['clamped'] * 255.0, 2)})
        if not discriminating:
            verdict = 'indeterminate'
            check.fail(Mismatch('sentinels %s' % mode, 'probe', 'no discriminating sentinel cell'))
        elif fits['unclamped'] == discriminating:
            verdict = 'unclamped'
        elif fits['clamped at 1'] == discriminating:
            verdict = 'clamped at 1'
        else:
            verdict = 'neither'
            check.fail(Mismatch('sentinels %s' % mode, 'verdict', 'the discriminating cells split: %s' % fits))
        check.data.update(verdict=verdict, discriminatingCells=discriminating, sharedModelCells=shared, fits=fits, detail=detail[:40])
        verdicts[mode] = verdict
        check.ok('Transparent BSDF colors above 1 composite %s (%d discriminating cells, %d on the shared model)'
                 % (verdict, discriminating, shared))
    return verdicts


def db_check_lit(result_obj, spec, evaluation, label, pristine=True, answer_obj=None):
    """The lit route against its twins, per set: drawn within 1/255 of m(As) L + tau(As) Cd with L the twin's render;
    and, on the pristine spec, within the lit bound + 1/255 of Gamebryo on the lit display color. Set B's cells whose
    tau exceeds 1 depend on the sentinels' verdict like set B's blend cells: an answer check."""
    table, twins = {}, []
    checks = {}
    present = {(cell['set'], db_lit_transparency(cell) > 1.0 + 1e-9) for cell in spec['cells'].values() if cell['kind'] == 'lit'}
    for set_name in ('A', 'B'):
        for is_answer in (False, True):
            if is_answer and (set_name, True) not in present:
                continue  # no cell of this set draws tau above 1: no answer check to report
            scope = 'T > 1' if is_answer else 'T <= 1'
            sink = _sink(result_obj, answer_obj, is_answer)
            checks[(set_name, is_answer)] = (
                sink.check('%s lit: set %s, %s blended lit cells within 1/255 of m(As) L_twin + tau(As) Cd' % (label, set_name, scope)),
                sink.check('%s lit bound: set %s, %s within the lit bound + 1/255 of Gamebryo on the lit color of the twin'
                           % (label, set_name, scope)) if pristine else None)
    counts = {key: 0 for key in checks}
    for cell_id, cell in spec['cells'].items():
        if cell['kind'] == 'twin':
            entry = evaluation[cell_id]
            if not entry['missing']:
                expected = db_texel_linear(cell)
                twins.append(max(abs(entry['linear'][c] - expected[c]) for c in range(3)))
            continue
        if cell['kind'] != 'lit':
            continue
        is_answer = db_lit_transparency(cell) > 1.0 + 1e-9
        drawn_check, bound_check = checks[(cell['set'], is_answer)]
        counts[(cell['set'], is_answer)] += 1
        entry = evaluation[cell_id]
        key = '%s %s %s' % (cell['set'], cell['pair'], 'T > 1' if is_answer else 'T <= 1')
        row = table.setdefault(key, {'drawnWorst255': 0.0, 'gamebryoWorst255': 0.0, 'bound255': cell['bound255'], 'cells': 0})
        if entry['missing'] or entry.get('twinMissing'):
            drawn_check.fail(Mismatch(cell_id, 'presence', 'no sample (or no twin sample)'))
            continue
        row['cells'] += 1
        row['drawnWorst255'] = max(row['drawnWorst255'], round(entry['drawnError'] * 255.0, 3))
        row['gamebryoWorst255'] = max(row['gamebryoWorst255'], round(entry['gamebryoError'] * 255.0, 3))
        if entry['drawnError'] > DB_SLACK:
            drawn_check.fail(Mismatch(cell_id, 'tolerance', '%.2f/255 from m L_twin + tau Cd' % (entry['drawnError'] * 255.0)))
        if bound_check is not None and entry['gamebryoError'] > cell['bound255'] / 255.0 + DB_SLACK:
            bound_check.fail(Mismatch(cell_id, 'bound', '%.2f/255 from Gamebryo, lit bound %.2f/255'
                                      % (entry['gamebryoError'] * 255.0, cell['bound255'])))
    for (set_name, is_answer), (drawn_check, bound_check) in checks.items():
        scope = 'T > 1' if is_answer else 'T <= 1'
        groups = {k: v for k, v in table.items() if k.startswith(set_name + ' ') and k.endswith(scope)}
        drawn_check.data['groups'] = groups
        # a diagnostic, not a gate: the sun of strength pi makes a Lambert twin reflect its own linear albedo
        drawn_check.data['twinVersusLinearAlbedo255'] = round(max(twins or [0.0]) * 255.0, 3)
        if counts[(set_name, is_answer)]:
            drawn_check.ok('%d lit groups' % len(groups))
        else:
            drawn_check.skip('no cells')
        if bound_check is not None:
            bound_check.data['groups'] = groups
            if counts[(set_name, is_answer)]:
                bound_check.ok('%d lit groups within their bounds' % len(groups))
            else:
                bound_check.skip('no cells')
    return table


def db_check_consistency(result_obj, answer_obj, verdicts):
    """The set-B answer agrees with the 'linked' sentinel verdict (the path every drawn T > 1 takes): unclamped means
    every answer check passes; clamped at 1 means the answer fails, since set B's T > 1 cells then see the clamp. Any
    other combination means the probe contradicts itself, which is a validity failure, never an answer."""
    check = result_obj.check('consistency: the set-B answer agrees with the linked sentinels\' verdict')
    verdict = verdicts.get('linked')
    answer_failed = any(c.passed is False for c in answer_obj.checks)
    answer_ran = any(c.passed is not None for c in answer_obj.checks)
    check.data.update(linkedVerdict=verdict, answerFailed=answer_failed, answerRan=answer_ran)
    if verdict == 'unclamped' and answer_failed:
        check.fail(Mismatch('set B', 'consistency', 'the sentinels composite T > 1 unclamped, yet set B\'s T > 1 checks fail'))
    elif verdict == 'clamped at 1' and answer_ran and not answer_failed:
        check.fail(Mismatch('set B', 'consistency', 'the sentinels clamp T at 1, yet every set-B T > 1 check passes: the probe '
                            'cannot see the clamp on set B'))
    elif verdict in ('clamped at 1', 'unclamped') and not answer_ran:
        check.fail(Mismatch('set B', 'probe', 'no set-B cell needs T > 1, so the probe cannot answer'))
    check.ok('set B %s, linked sentinels %s' % ('fails' if answer_failed else 'passes', verdict))


def db_profile(spec, evaluation):
    """The additive dark-background profile beside the worst case: per (pair, set, k0 level) the measured and the
    analytic error at each grey; plus the analytic per-grey worst over every k0 (constants['darkProfile'])."""
    rows = {}
    for cell_id, cell in spec['cells'].items():
        if cell['kind'] != 'blend' or cell['section'] != 'profile':
            continue
        entry = evaluation[cell_id]
        key = '%s %s Cs %s' % (cell['pair'], cell['set'], cell['sourceBytes'])
        grey = cell['backgroundBytes'][0]
        rows.setdefault(key, {})[grey] = {'model255': round(abs(dbm.drawn(cell['fit'], cell['sourceFactor'], cell['destinationFactor'],
                                                                           cell['sourceBytes'][0] / 255.0, cell['sourceBytes'][3] / 255.0,
                                                                           grey / 255.0)
                                                                 - dbm.gamebryo(cell['sourceFactor'], cell['destinationFactor'],
                                                                                cell['sourceBytes'][0] / 255.0, cell['sourceBytes'][3] / 255.0,
                                                                                grey / 255.0)) * 255.0, 3),
                                          'measured255': None if entry['missing'] else round(entry['gamebryoError'] * 255.0, 3)}
    worst = {name: {str(r['Cd255']): r['worst255'] for r in profile_rows}
             for name, profile_rows in (spec['constants'].get('darkProfile') or {}).items()}
    bounds = {name: spec['constants']['sets'][name]['pairs']['ONE/ONE']['bound255'] for name in ('K', 'A', 'B')}
    return {'cells': rows, 'analyticWorstOverK0': worst, 'worstCaseBound255': bounds}


def check_display_blend(result_obj, spec, probe, label, pristine=True, answer_obj=None):
    """Every display-blend check on one engine's render; returns (evaluation, summary). With ``answer_obj`` the checks
    that need T > 1 go there (the set-B answer) and the consistency check joins ``result_obj`` (validity); without it
    (a control spec) everything lands on ``result_obj``."""
    evaluation = db_evaluate(spec, probe)
    graphs = db_check_graphs(result_obj, spec, probe, answer_obj)
    texels = check_texels(result_obj, spec, probe)
    drawn = db_check_drawn(result_obj, spec, evaluation, label, answer_obj)
    bounds = db_check_gamebryo(result_obj, spec, evaluation, label, answer_obj) if pristine else None
    worst = db_check_worst(result_obj, spec, evaluation, label) if pristine else None
    lit_worst = db_check_lit_worst(result_obj, spec, evaluation, label) if pristine else None
    clamp = db_check_clamp_coverage(result_obj, spec, label) if pristine else None
    sentinels = db_check_sentinels(result_obj, spec, evaluation, label)
    lit = db_check_lit(result_obj, spec, evaluation, label, pristine, answer_obj)
    if answer_obj is not None:
        db_check_consistency(result_obj, answer_obj, sentinels)
    summary = {'engine': probe.get('engine'), 'graphs': graphs, 'texelRule': texels, 'drawn': drawn, 'bounds': bounds,
               'worstCells': worst, 'litWorstCells': lit_worst, 'clampCoverage': clamp, 'transparentAboveOne': sentinels, 'lit': lit,
               'profile': db_profile(spec, evaluation) if pristine else None}
    return evaluation, summary


def display_blend_control(spec, pristine_evaluation, control_spec, control_probe):
    """(detected, detail, per-group verdicts) of the control spec on one engine. A (set, pair) group is applicable on
    its cells whose pristine render passed the drawn check and whose expectation the control changes by more than
    1/255; it is detected when the control render misses the PRISTINE expectation there by more than 1/255. Each
    group also reports whether the control render matches the control's own constants (the variant draws what the
    spec says). Blend groups of sets A and B, and the lit groups."""
    control_evaluation = db_evaluate(control_spec, control_probe)
    groups = {}
    for cell_id, cell in spec['cells'].items():
        if cell['kind'] not in ('blend', 'lit') or cell.get('set') not in ('A', 'B'):
            continue
        control_cell = control_spec['cells'][cell_id]
        pristine_entry, control_entry = pristine_evaluation[cell_id], control_evaluation[cell_id]
        key = '%s %s %s' % (cell['kind'], cell['set'], cell['pair'])
        group = groups.setdefault(key, {'changed': 0, 'applicable': 0, 'worstVersusPristine255': 0.0, 'worstVersusOwn255': 0.0})
        if pristine_entry['missing'] or control_entry['missing'] or pristine_entry.get('twinMissing') or control_entry.get('twinMissing'):
            continue
        if cell['kind'] == 'blend':
            pristine_model = db_blend_expectation(cell)[0]
            control_model = db_blend_expectation(control_cell)[0]
        else:
            pristine_model = db_lit_expectation(cell, pristine_entry['twinLinear'])[0]
            control_model = db_lit_expectation(control_cell, control_entry['twinLinear'])[0]
        group['worstVersusOwn255'] = max(group['worstVersusOwn255'], round(_max_error(control_entry['measured'], control_model) * 255.0, 3))
        if _max_error(pristine_model, control_model) <= DB_SLACK:
            continue
        group['changed'] += 1
        if pristine_entry['drawnError'] > DB_SLACK:
            continue
        group['applicable'] += 1
        group['worstVersusPristine255'] = max(group['worstVersusPristine255'],
                                              round(_max_error(control_entry['measured'], pristine_model) * 255.0, 3))
    verdicts = {}
    for key, group in sorted(groups.items()):
        if not group['changed']:
            verdicts[key] = dict(group, verdict='not applicable (the control leaves the expectation unchanged)')
        elif not group['applicable']:
            verdicts[key] = dict(group, verdict='masked (the pristine render misses every changed cell)')
        else:
            detected = group['worstVersusPristine255'] > TOLERANCE * 255.0 + EPSILON * 255.0
            verdicts[key] = dict(group, verdict='detected' if detected else 'NOT DETECTED',
                                 drawsOwnConstants=group['worstVersusOwn255'] <= TOLERANCE * 255.0 + EPSILON * 255.0)
    applicable = [k for k, v in verdicts.items() if v['verdict'] in ('detected', 'NOT DETECTED')]
    missed = [k for k in applicable if verdicts[k]['verdict'] == 'NOT DETECTED']
    masked = [k for k, v in verdicts.items() if v['verdict'].startswith('masked')]
    if missed:
        return False, 'control NOT detected on %s' % ', '.join(missed), verdicts
    if not applicable:
        return None, 'masked: no group both passed pristine and changes under the control', verdicts
    return True, 'control detected on %d of %d applicable group(s); %d masked' % (len(applicable), len(applicable), len(masked)), verdicts
