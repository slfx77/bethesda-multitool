# SPDX-License-Identifier: 0BSD
"""Self-check of the display-blend probe without Blender (plain Python, numpy).

    python selfcheck_display_blend.py --out <directory>

1. Graphs equal the model. Every material recipe of the probe is evaluated through the variant's own graph code
   (``display_blend_variant.material_closure``) on ``NumericGraph`` (float32 Math nodes) at every cell's texel, and the
   displayed composite is compared with ``display_blend_math`` (float64): at most 0.05/255 apart. Control: a planted
   error in each non-exact recipe (x1 + 0.05 with tmax 1, xa + 0.05, the lit exponent + 0.5) must move some cell by more
   than 1/255, so this check can fail. The same recipes are also built by ``BpyGraph`` into a mock node tree
   (``mock_node_tree.py``); the tree is evaluated both by the mock's own Evaluator and by the variant's
   ``GraphReader`` (the readback the variant runs inside Blender), and both must match the numeric backend.
2. The judge discriminates. Synthetic renders (their graph readbacks built exactly as the variant builds them: BpyGraph
   into a mock tree, read back by GraphReader) go through ``run_display_blend_probe.judge`` and must give exactly the
   stated verdicts:
   * CYCLES as ``exact`` (Blender draws the declared graph, T above 1 unclamped): the probe valid, every answer check
     passes, sentinels 'unclamped', the control detected, set B available.
   * EEVEE as ``clamped`` (the composite clamps T at 1): the probe STILL VALID (a clean answer is not a broken probe),
     sentinels 'clamped at 1', exactly the four set-B T > 1 answer checks fail, the control detected, set B
     unavailable; so the driver's exit code for {exact, clamped} is 0.
   * ``premultiplied`` (Blender's default STRAIGHT alpha mode in Cycles): the texel rule fails.
   * a control render that ignores the control spec (draws the pristine constants): the control NOT detected.
   * review 2026-09-27 controls, each of which must fail exactly where stated:
     - sentinel rows that cannot discriminate (T <= 1) drawn 3/255 off the composite model: the sentinel check fails;
     - graph readbacks built from the CONTROL spec's recipes judged against the pristine spec: the graphs check fails;
     - a Blender that stores color socket defaults clamped at 1 (mock emulation): with the variant's Value-node rule
       every graphs check still passes and the 'socket' sentinels' stored defaults are reported; with constants above
       1 folded into socket defaults (the pre-review variant), the set-B T > 1 graphs answer check fails;
     - linked sentinels unclamped while set B's T > 1 cells draw clamped: the consistency check fails (probe invalid);
     - worst cells and clamp cells moved off their loci (texel and background 0): the blend and lit worst-cell checks
       and the clamp-coverage check fail.
3. The driver's dry run builds the probe and prints its five launches.
Exit code 0 when every expectation holds, 1 otherwise. Writes ``<out>/selfcheck.json``.
"""

import argparse
import copy
import json
import os
import subprocess
import sys

sys.dont_write_bytecode = True

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, 'inside_blender'))
import run_display_blend_probe as driver  # noqa: E402  (sets up the harness path)
import display_blend_math as dbm  # noqa: E402
import display_blend_variant as variant  # noqa: E402
import mock_node_tree as mock  # noqa: E402
import probe_builders as builders  # noqa: E402

GRAPH_TOLERANCE255 = 0.05
SENTINEL_PLANT255 = 3.0


def texel_linear(cell, recipe, hypothesis):
    """The linear texel Blender hands the graph for this cell under a hypothesis (straight unless 'premultiplied',
    where an 8-bit texel whose Alpha output is unused is associated in byte space with truncation)."""
    color, alpha = [int(v) for v in cell['sourceBytes'][:3]], int(cell['sourceBytes'][3])
    uses_alpha = recipe['kind'] == 'lit' or (recipe['kind'] == 'blend' and any(
        t[2] or t[4] for t in recipe['emissionTerms'] + recipe['transmissionTerms']))
    if hypothesis == 'premultiplied' and not uses_alpha and recipe['kind'] in ('blend', 'opaque', 'twin'):
        color = [(c * alpha) // 255 for c in color]
    return [dbm.srgb_decode(c / 255.0) for c in color], alpha / 255.0


def cell_linear(cell, recipe, hypothesis):
    """The scene-linear RGB a cell renders under a hypothesis, through the variant's own graph code. 'clamped' clamps
    every T at 1; 'clamped-fits' clamps it only on blend and lit cells (the sentinels stay unclamped: a contradiction
    the consistency check must catch); 'sentinel-off' draws the non-discriminating sentinel rows 3/255 too bright."""
    if recipe['kind'] == 'sentinel':
        closure = variant.material_closure(variant.NumericGraph(), recipe)
        texel, lit = [0.0, 0.0, 0.0], [0.0, 0.0, 0.0]
    else:
        texel, alpha = texel_linear(cell, recipe, hypothesis)
        closure = variant.material_closure(variant.NumericGraph(texel, alpha), recipe)
        lit = texel  # a Lambert surface under the probe's sun of strength pi reflects its linear albedo
    cd = [dbm.srgb_decode(b / 255.0) for b in (cell.get('backgroundBytes') or [0, 0, 0])]
    clamp_t = hypothesis == 'clamped' or (hypothesis == 'clamped-fits' and recipe['kind'] in ('blend', 'lit'))
    t = [min(1.0, v) for v in closure['T']] if clamp_t else closure['T']
    linear = [closure['E'][c] + closure['lit'] * lit[c] + t[c] * cd[c] for c in range(3)]
    if hypothesis == 'sentinel-off' and recipe['kind'] == 'sentinel' and float(recipe['sentinel']['t']) <= 1.0:
        shifted = min(1.0, dbm.display(linear[0]) + SENTINEL_PLANT255 / 255.0)
        linear = [dbm.srgb_decode(shifted)] * 3
    return linear


class SocketRouteGraph(variant.BpyGraph):
    """The pre-review variant: a constant Transparent color above 1 folded into the socket default."""

    def transparent(self, color, route='value'):
        return super().transparent(color, route='socket')


def graph_records(spec, draw_spec=None, clamp_color_defaults=False, graph_class=variant.BpyGraph):
    """The graph records the variant writes: each recipe of ``draw_spec`` (default ``spec``) built by ``graph_class``
    into a mock tree (``clamp_color_defaults`` emulates a Blender storing color defaults clamped at 1), linked to a
    Material Output as build_material links it, and read back by ``variant.read_back`` at the texels of ``spec``'s cells."""
    draw = {m['index']: m for m in (draw_spec or spec)['materials']}
    records = []
    for material in spec['materials']:
        recipe = draw[material['index']]
        tree = mock.Tree(clamp_color_defaults=clamp_color_defaults)
        graph = graph_class(None, tree, {k: 'image %d' % k for k in range(len(spec['images']))})
        closure = variant.material_closure(graph, recipe)
        output = tree.nodes.new('ShaderNodeOutputMaterial')
        tree.links.new(closure, output.inputs['Surface'])
        readback, error = variant.read_back(tree, material, spec['cells'])
        records.append({'index': material['index'], 'name': material['name'], 'kind': material['kind'],
                        **{k: recipe[k] for k in ('set', 'pair', 'fit', 'lit', 'sentinel') if recipe.get(k) is not None},
                        'nodes': graph.nodes + 1, 'socketDefaults': graph.socket_defaults, 'readback': readback, 'readbackError': error})
    return records


def fake_result(spec, hypothesis, engine, draw_spec=None, graphs=None):
    """A probe result as the variant would write it, rendered from the recipes of ``draw_spec`` (default: ``spec``)."""
    draw_spec = draw_spec or spec
    recipes = {m['index']: m for m in draw_spec['materials']}
    samples = []
    for cell_id, cell in spec['cells'].items():
        linear = cell_linear(draw_spec['cells'][cell_id], recipes[cell['material']], hypothesis)
        samples.append({'id': cell_id, 'pixel': cell['pixel'], 'linear': linear + [1.0], 'spread': 0.0})
    means = {s['id']: {'linear': s['linear'][:3], 'std': [0.0, 0.0, 0.0], 'pixels': 64} for s in samples
             if spec['cells'][s['id']]['kind'] in ('lit', 'twin')}
    return {'schema': 'gate1a-blender-probe/1', 'engine': engine, 'errors': [], 'settings': {'hypothesis': hypothesis},
            'graphs': graphs if graphs is not None else graph_records(spec, draw_spec), 'samples': samples, 'cellMeans': means,
            'png': None, 'exr': None}


def graph_check(spec):
    """(worst255 over every recipe and cell, per-kind worst) of the variant's graph against display_blend_math."""
    recipes = {m['index']: m for m in spec['materials']}
    worst, per_kind = 0.0, {}
    for cell in spec['cells'].values():
        recipe = recipes[cell['material']]
        rendered = [dbm.display(v) for v in cell_linear(cell, recipe, 'exact')]
        error = max(abs(rendered[c] - driver.compare_f.db_declared_model(cell)[c]) for c in range(3)) * 255.0
        worst = max(worst, error)
        per_kind[cell['kind']] = max(per_kind.get(cell['kind'], 0.0), error)
    return worst, per_kind


def bpy_graph_check(spec):
    """(worst mock-Evaluator versus numeric, worst GraphReader versus mock Evaluator, graphs built): every recipe built
    by BpyGraph into a mock tree and evaluated at each of its cells by the mock's Evaluator and by the variant's
    GraphReader. Proves BpyGraph's wiring computes the numeric graph, and that the readback reads a tree correctly."""
    worst, worst_reader, built = 0.0, 0.0, 0
    cells_by_material = {}
    for cell in spec['cells'].values():
        cells_by_material.setdefault(cell['material'], []).append(cell)
    for recipe in spec['materials']:
        tree = mock.Tree()
        graph = variant.BpyGraph(None, tree, {k: 'image %d' % k for k in range(len(spec['images']))})
        closure = variant.material_closure(graph, recipe)
        output = tree.nodes.new('ShaderNodeOutputMaterial')
        tree.links.new(closure, output.inputs['Surface'])
        built += 1
        for cell in cells_by_material.get(recipe['index'], []):
            if recipe['kind'] == 'sentinel':
                texel, alpha = [0.0, 0.0, 0.0], 1.0
            else:
                texel, alpha = texel_linear(cell, recipe, 'exact')
            result = mock.Evaluator(texel, alpha).surface(tree)
            reader = variant.GraphReader(texel, alpha).surface(tree)
            cd = [dbm.srgb_decode(b / 255.0) for b in (cell.get('backgroundBytes') or [0, 0, 0])]
            lit = texel if recipe['kind'] != 'sentinel' else [0.0, 0.0, 0.0]
            mocked = [dbm.display(result['E'][c] + result['lit'] * lit[c] + result['T'][c] * cd[c]) for c in range(3)]
            read = [dbm.display(reader['E'][c] + reader['lit'] * lit[c] + reader['T'][c] * cd[c]) for c in range(3)]
            numeric = [dbm.display(v) for v in cell_linear(cell, recipe, 'exact')]
            worst = max(worst, max(abs(mocked[c] - numeric[c]) for c in range(3)) * 255.0)
            worst_reader = max(worst_reader, max(abs(read[c] - mocked[c]) for c in range(3)) * 255.0)
    return worst, worst_reader, built


def scene_check(work, spec):
    """Run the variant's scene assembly (build_scene: images, materials, meshes with UVs, background quads, the sun)
    against mock_node_tree.FakeBpy and return the list of problems found (empty when the assembly matches the spec)."""
    fake = mock.FakeBpy()
    errors, settings, graphs = [], {}, []
    variant.build_scene(fake, fake.scene, dict(spec, _specDirectory=work), errors, settings, graphs)
    problems = ['build_scene error: %s' % e for e in errors]
    if len(graphs) != len(spec['materials']):
        problems.append('%d graphs for %d materials' % (len(graphs), len(spec['materials'])))
    problems += ['graph %s: readback error %s' % (g['name'], g['readbackError']) for g in graphs if g.get('readbackError')]
    problems += ['graph %s: no readback' % g['name'] for g in graphs if not g.get('readback')]
    expected_objects = len(spec['materials']) + len(spec['background']) + 1
    if len(fake.linked) != expected_objects:
        problems.append('%d objects linked, expected %d' % (len(fake.linked), expected_objects))
    for image in fake.data.images.items:
        if image.alpha_mode != 'CHANNEL_PACKED' or image.colorspace_settings.name != 'sRGB' or 'mt_image_index' not in image.custom:
            problems.append('image %s: alpha %s, color space %s' % (image.name, image.alpha_mode, image.colorspace_settings.name))
    meshes = {mesh.name: mesh for mesh in fake.data.meshes.items}
    materials = {material.name: material for material in fake.data.materials.items}
    for recipe in spec['materials']:
        mesh = meshes.get(recipe['name'])
        if mesh is None or len(mesh.vertices) != 4 * len(recipe['quads']):
            problems.append('mesh %s: %s vertices for %d quads' % (recipe['name'], None if mesh is None else len(mesh.vertices), len(recipe['quads'])))
            continue
        uv = mesh.uv_layers.layers['UVMap'].data.values
        wanted = [v for quad in recipe['quads'] for v in quad['uv'] * 4]
        if uv is None or any(abs(a - b) > 1e-12 for a, b in zip(uv, wanted)) or len(uv) != len(wanted):
            problems.append('mesh %s: UVs differ from the spec' % recipe['name'])
        if materials[recipe['name']].surface_render_method != recipe['renderMethod']:
            problems.append('material %s: render method %s' % (recipe['name'], materials[recipe['name']].surface_render_method))
    return problems


def planted(spec):
    """Per non-exact recipe: the largest display change a planted constant error causes over its cells (must be
    above 1/255 for the graph check to be able to fail)."""
    out = {}
    for recipe in spec['materials']:
        if recipe['kind'] not in ('blend', 'lit'):
            continue
        broken = copy.deepcopy(recipe)
        if recipe['kind'] == 'blend':
            fit = broken['fit']
            if fit['kind'] == 'exact':
                continue
            if fit['kind'] == 'one':
                fit['xa'] += 0.05
            else:
                fit['x1'] += 0.05  # and a set-B tmax back to 1: set B's alpha-over alone moves no cell by 1/255 on tmax
                fit['tmax'] = min(float(fit.get('tmax', 1.0)), 1.0)
        else:
            lit = broken['lit']
            if lit['form'] == 'constant':
                lit['m'] += 0.5
            else:
                lit['p'] += 0.5
        change = 0.0
        for cell in spec['cells'].values():
            if cell['material'] != recipe['index']:
                continue
            a = [dbm.display(v) for v in cell_linear(cell, recipe, 'exact')]
            b = [dbm.display(v) for v in cell_linear(cell, broken, 'exact')]
            change = max(change, max(abs(a[c] - b[c]) for c in range(3)) * 255.0)
        out[recipe['name']] = round(change, 3)
    return out


def moved_worst_cells(spec):
    """A spec copy whose blend (PNG) and lit worst-case cells and clamp cells sit at texel 0 over background 0, where no
    fit errs and no clamp shows."""
    moved = copy.deepcopy(spec)
    ids = ['db_worst_%s_%s_png' % (case['set'], case['pair'].replace('/', '_')) for case in moved['worstCases']]
    ids += [case['cell'] for case in moved['litWorstCases']] + [case['cell'] for case in moved['clampCases']]
    for cell_id in ids:
        cell = moved['cells'][cell_id]
        cell['sourceBytes'] = [0, 0, 0, cell['sourceBytes'][3]]
        cell['backgroundBytes'] = [0, 0, 0]
    return moved


def check(condition, label, failures, detail=None):
    if not condition:
        failures.append({'expectation': label, 'detail': detail})
    return condition


def statuses(document, name_part, where='result'):
    return [c['status'] for c in ((document.get(where) or {}).get('checks') or []) if name_part in c['name']]


def failed(document, where='result'):
    return sorted(c['name'] for c in ((document.get(where) or {}).get('checks') or []) if c['status'] == 'fail')


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--out', required=True)
    args = parser.parse_args(argv)
    out = os.path.abspath(args.out)
    work = os.path.join(out, 'work')
    failures = []
    report = {}
    constants, spec, control_spec, _paths = driver.build(work, 16)
    report['probe'] = {'cells': len(spec['cells']), 'materials': len(spec['materials']), 'resolution': spec['render']['resolution'],
                       'sources': len(spec['sources']), 'worstCases': spec['worstCases'], 'litWorstCases': spec['litWorstCases'],
                       'clampCases': spec['clampCases']}

    # 1. graphs equal the model, and the check can fail
    worst, per_kind = graph_check(spec)
    worst_control, _ = graph_check(control_spec)
    changes = planted(spec)
    report['graphs'] = {'worst255': round(worst, 5), 'perKind255': {k: round(v, 5) for k, v in per_kind.items()},
                        'controlSpecWorst255': round(worst_control, 5), 'plantedErrorChange255': changes}
    check(worst <= GRAPH_TOLERANCE255, 'graphs: the variant graph equals the model within %.2f/255' % GRAPH_TOLERANCE255, failures, worst)
    check(worst_control <= GRAPH_TOLERANCE255, 'graphs: the control spec\'s graphs equal their model', failures, worst_control)
    bpy_worst, reader_worst, bpy_built = bpy_graph_check(spec)
    report['graphs'].update(bpyGraphVersusNumeric255=round(bpy_worst, 6), graphReaderVersusMockEvaluator255=round(reader_worst, 6),
                            bpyGraphsBuilt=bpy_built)
    check(bpy_worst <= 1e-3, 'graphs: the BpyGraph nodes and links (mock tree) compute what the numeric backend computes', failures, bpy_worst)
    check(reader_worst <= 1e-3, 'graphs: the variant\'s GraphReader reads a tree as the mock Evaluator does', failures, reader_worst)
    problems = scene_check(work, spec)
    report['graphs']['sceneAssemblyProblems'] = problems
    check(not problems, 'scene: build_scene assembles every image, material, mesh, UV, background and the sun as the spec says, '
          'and reads every graph back', failures, problems[:20])
    weak = {name: v for name, v in changes.items() if v <= 1.0}
    check(not weak, 'graphs: every planted constant error moves some cell by more than 1/255', failures, weak)
    rule = [(g['name'], d['set']) for g in graph_records(spec) for d in g['socketDefaults']
            if any(v > 1.0 for v in d['set']) and d['route'] != 'socket']
    check(not rule, 'rule: no drawn fit or lit curve folds a constant above 1 into a socket default', failures, rule)

    # 2. the judge discriminates
    results = {('CYCLES', 'pristine'): (fake_result(spec, 'exact', 'CYCLES'), None),
               ('CYCLES', 'control'): (fake_result(control_spec, 'exact', 'CYCLES'), None),
               ('EEVEE', 'pristine'): (fake_result(spec, 'clamped', 'BLENDER_EEVEE'), None),
               ('EEVEE', 'control'): (fake_result(control_spec, 'clamped', 'BLENDER_EEVEE'), None)}
    engines = driver.judge(spec, control_spec, results)
    decisions = driver.decisions(engines, constants)
    probe_valid = all(d.get('passed') and (d.get('control') or {}).get('detected') is True for d in engines.values())
    cycles, eevee = engines['CYCLES'], engines['EEVEE']
    report['exact'] = {'failedValidity': failed(cycles), 'failedAnswer': failed(cycles, 'answer'), 'control': cycles['control']['detail'],
                       'decision': decisions['CYCLES']}
    report['clamped'] = {'failedValidity': failed(eevee), 'failedAnswer': failed(eevee, 'answer'), 'control': eevee['control']['detail'],
                         'decision': decisions['EEVEE'], 'sentinels': eevee['summary']['transparentAboveOne']}
    report['driverExitCodeFor'] = {'exact CYCLES + clamped EEVEE': 0 if probe_valid else 1}
    check(cycles['passed'] is True, 'exact: the probe is valid', failures, failed(cycles))
    check(cycles['setBAnswerPassed'] is True, 'exact: every set-B answer check passes', failures, failed(cycles, 'answer'))
    check(cycles['summary']['transparentAboveOne'] == {'linked': 'unclamped', 'socket': 'unclamped'},
          'exact: sentinels unclamped', failures, cycles['summary']['transparentAboveOne'])
    check(cycles['control']['detected'] is True, 'exact: the control is detected', failures, cycles['control']['detail'])
    check(decisions['CYCLES']['setBAvailable'] is True, 'exact: set B available', failures, decisions['CYCLES'])
    check(eevee['summary']['transparentAboveOne'] == {'linked': 'clamped at 1', 'socket': 'clamped at 1'},
          'clamped: sentinels clamped at 1', failures, eevee['summary']['transparentAboveOne'])
    check(eevee['passed'] is True, 'clamped: the probe is STILL VALID (a clean answer is not a broken probe)', failures, failed(eevee))
    expected_answer = {'EEVEE drawn: set B, T > 1', 'EEVEE bound: set B, T > 1', 'EEVEE lit: set B, T > 1', 'EEVEE lit bound: set B, T > 1'}
    got = {name.split(' cells')[0].split(' within')[0].split(' blended')[0] for name in failed(eevee, 'answer')}
    check(got == expected_answer, 'clamped: exactly the four set-B T > 1 answer checks fail (blend drawn and bound, lit drawn and bound)',
          failures, sorted(got))
    check(eevee['control']['detected'] is True, 'clamped: the control is still detected on the drawable groups', failures,
          eevee['control']['detail'])
    b_groups = {k: v['verdict'] for k, v in eevee['control']['groups'].items() if ' B ' in k}
    check(bool(b_groups) and all(v == 'detected' or v.startswith('masked') or v.startswith('not applicable') for v in b_groups.values()),
          'clamped: set B control groups are detected (through E) or masked, never NOT DETECTED', failures, b_groups)
    report['clamped']['setBControlGroups'] = b_groups
    check(decisions['EEVEE']['setBAvailable'] is False, 'clamped: set B unavailable', failures, decisions['EEVEE'])
    check(probe_valid, 'the driver exits 0 on {exact CYCLES, clamped EEVEE}: validity decides the exit code', failures,
          {engine: (d['passed'], d['control']['detected']) for engine, d in engines.items()})

    premultiplied = driver.judge(spec, control_spec, {('CYCLES', 'pristine'): (fake_result(spec, 'premultiplied', 'CYCLES'), None)})['CYCLES']
    report['premultiplied'] = {'failedValidity': failed(premultiplied), 'texelRule': premultiplied['summary']['texelRule']}
    check('fail' in statuses(premultiplied, 'texel rule'), 'premultiplied: the texel rule fails', failures, failed(premultiplied))
    check(premultiplied['passed'] is False, 'premultiplied: the probe is invalid', failures, failed(premultiplied))

    ignoring = driver.judge(spec, control_spec, {('CYCLES', 'pristine'): (fake_result(spec, 'exact', 'CYCLES'), None),
                                                 ('CYCLES', 'control'): (fake_result(control_spec, 'exact', 'CYCLES', draw_spec=spec), None)})['CYCLES']
    report['controlIgnored'] = {'control': ignoring['control']['detail']}
    check(ignoring['control']['detected'] is False, 'a control render drawing the pristine constants is NOT detected', failures,
          ignoring['control']['detail'])

    # 2b. the review's controls
    sentinel_off = driver.judge(spec, control_spec, {('CYCLES', 'pristine'): (fake_result(spec, 'sentinel-off', 'CYCLES'), None)})['CYCLES']
    report['sentinelOff'] = {'failedValidity': failed(sentinel_off), 'verdicts': sentinel_off['summary']['transparentAboveOne']}
    check(set(statuses(sentinel_off, 'sentinels (linked)')) == {'fail'} and set(statuses(sentinel_off, 'sentinels (socket)')) == {'fail'},
          'sentinel control: non-discriminating rows 3/255 off the model fail the sentinel check (both modes)', failures, failed(sentinel_off))
    check(sentinel_off['summary']['transparentAboveOne'] == {'linked': 'unclamped', 'socket': 'unclamped'},
          'sentinel control: the discriminating verdict itself is unchanged (only the model rows fail)', failures,
          sentinel_off['summary']['transparentAboveOne'])

    wrong_graphs = fake_result(spec, 'exact', 'CYCLES', graphs=graph_records(spec, draw_spec=control_spec))
    readback = driver.judge(spec, control_spec, {('CYCLES', 'pristine'): (wrong_graphs, None)})['CYCLES']
    report['readbackControl'] = {'failedValidity': failed(readback), 'failedAnswer': failed(readback, 'answer')}
    check('fail' in statuses(readback, 'graphs: the node trees'), 'readback control: trees built from the control spec fail the graphs '
          'check against the pristine spec', failures, failed(readback))

    clamping = fake_result(spec, 'exact', 'CYCLES', graphs=graph_records(spec, clamp_color_defaults=True))
    clamped_defaults = driver.judge(spec, control_spec, {('CYCLES', 'pristine'): (clamping, None)})['CYCLES']
    stored = clamped_defaults['summary']['graphs']['socketSentinelDefaultsStoredOtherwise']
    report['socketClampWithRule'] = {'failedValidity': failed(clamped_defaults), 'failedAnswer': failed(clamped_defaults, 'answer'),
                                     'socketSentinelDefaultsStoredOtherwise': len(stored)}
    check(not [n for n in failed(clamped_defaults) + failed(clamped_defaults, 'answer') if 'graphs' in n],
          'socket clamp, with the Value-node rule: every graphs check passes', failures,
          failed(clamped_defaults) + failed(clamped_defaults, 'answer'))
    check(len(stored) > 0, 'socket clamp: the socket sentinels\' stored defaults are reported', failures, stored[:3])
    folding = fake_result(spec, 'exact', 'CYCLES', graphs=graph_records(spec, clamp_color_defaults=True, graph_class=SocketRouteGraph))
    folded = driver.judge(spec, control_spec, {('CYCLES', 'pristine'): (folding, None)})['CYCLES']
    report['socketClampWithoutRule'] = {'failedValidity': failed(folded), 'failedAnswer': failed(folded, 'answer'),
                                        'decision': driver.decisions({'CYCLES': folded}, constants)['CYCLES']['setBAvailable']}
    check('fail' in statuses(folded, 'graphs (set B, T > 1)', 'answer'),
          'socket clamp, constants folded into socket defaults: the set-B T > 1 graphs answer check fails', failures,
          failed(folded, 'answer'))
    check(driver.decisions({'CYCLES': folded}, constants)['CYCLES']['setBAvailable'] is False,
          'socket clamp, constants folded: set B unavailable', failures, None)

    contradiction = driver.judge(spec, control_spec, {('CYCLES', 'pristine'): (fake_result(spec, 'clamped-fits', 'CYCLES'), None)})['CYCLES']
    report['consistencyControl'] = {'failedValidity': failed(contradiction), 'verdicts': contradiction['summary']['transparentAboveOne']}
    check('fail' in statuses(contradiction, 'consistency') and contradiction['passed'] is False,
          'consistency control: unclamped sentinels with clamped set-B cells make the probe invalid', failures, failed(contradiction))

    moved = moved_worst_cells(spec)
    worst_moved = driver.judge(moved, control_spec, {('CYCLES', 'pristine'): (fake_result(moved, 'exact', 'CYCLES'), None)})['CYCLES']
    report['worstCellControl'] = {'failedValidity': failed(worst_moved)}
    check('fail' in statuses(worst_moved, 'worst cells: each non-exact'), 'worst-cell control: moved blend worst cells fail', failures,
          failed(worst_moved))
    check('fail' in statuses(worst_moved, 'lit worst cells'), 'worst-cell control: moved lit worst cells fail', failures, failed(worst_moved))
    check('fail' in statuses(worst_moved, 'clamp coverage'), 'clamp-cell control: moved clamp cells fail the coverage check', failures,
          failed(worst_moved))

    # 3. the driver's dry run
    completed = subprocess.run([sys.executable, '-B', os.path.join(HERE, 'run_display_blend_probe.py'), '--out', os.path.join(out, 'dry-run'),
                                '--dry-run'], capture_output=True, text=True, timeout=600)
    launches = [line for line in completed.stdout.splitlines() if line.startswith('[dry run]')]
    report['dryRun'] = {'exitCode': completed.returncode, 'launches': launches, 'stderrTail': completed.stderr[-2000:]}
    check(completed.returncode == 0 and len(launches) == 5, 'dry run: exit 0 and five launches', failures, report['dryRun'])

    # the receipt a clean clamped run writes (read it to see how a clamped answer reads)
    receipt = {'started': 'selfcheck', 'finished': 'selfcheck', 'blender': {'path': 'fake', 'version': 'fake'},
               'constantsSha256': spec['constantsSha256'], 'engines': engines, 'decisions': decisions, 'probeValid': probe_valid}
    os.makedirs(out, exist_ok=True)
    with open(os.path.join(out, 'fake-receipt.md'), 'w', encoding='utf-8', newline='\n') as f:
        f.write(driver.markdown(receipt))

    report['failures'] = failures
    report['passed'] = not failures
    with open(os.path.join(out, 'selfcheck.json'), 'w', encoding='utf-8') as f:
        json.dump(report, f, indent=1, default=str)
    for failure in failures:
        print('FAIL: %s: %s' % (failure['expectation'], str(failure['detail'])[:400]))
    print('self-check %s (%d expectation(s) failed); graphs worst %.5f/255; report %s'
          % ('PASSED' if not failures else 'FAILED', len(failures), worst, os.path.join(out, 'selfcheck.json')))
    return 0 if not failures else 1


if __name__ == '__main__':
    sys.exit(main())
