# SPDX-License-Identifier: 0BSD
"""Hop-F probe: runs INSIDE Blender 5.1. This harness never runs it itself; the driver launches Blender with it.

    blender --background --factory-startup --python-exit-code 1 --python probe_render.py -- \\
        --blend <probe.blend> --spec <spec.json> --out <result.json> --work <directory>

The .blend is the one Shared ``import_model.py`` built from a probe package. The spec (``probe_builders.spec_document``)
names an orthographic camera looking down -Z over a world rectangle, the resolution, emission background planes and the
pixels to sample; ``imagePixels`` asks for the pixel buffers of every image the importer loaded (``mt_image_index``).

What it does, in order: open the .blend; read the image buffers (``Image.pixels``, bottom row first, as Blender stores
them); set up the render (Cycles on the CPU by default, no denoising, no adaptive sampling, no clamping, film not
transparent, the Standard view transform with exposure 0 and gamma 1, dither 0, a black world of strength 0); add the
camera and the background planes (Emission shaders whose colors are the spec's scene-linear values); render; save the
Render Result as a 32-bit OpenEXR (scene-linear) and as an 8-bit PNG (display-referred, Blender's own view transform);
load the EXR back and sample the requested pixels, recording each sample's scene-linear RGBA and the spread over its
3 x 3 neighborhood (a misaligned sample shows as a nonzero spread). Every setting it applied or could not apply is
recorded in ``settings`` / ``errors``.

A spec whose render names ``lighting`` (the lit-blend probe, ``probe_builders.build_lit_blend_probe``) also gets, in
the manner of the display-blend variant beside this file (``display_blend_variant``, whose helpers it reuses): the
variant's engine settings (no indirect diffuse or glossy light in Cycles; EEVEE's screen-space effects and shadows off),
one sun (strength, angle, rotation, shadow and specular factor from the spec), and the variant's lit pass, which
measures every lit and twin cell as an inner-block mean (``cellMeans``; in Cycles from a border render at
``litSamples``, whose closure pick makes a lit blended cell noisy).

Exit codes: 0 the result was written with no error; 4 the result was written but a step failed (``errors``); 2 usage
or a Blender older than 5.0; 1 any unhandled error (``--python-exit-code 1``).
"""

import argparse
import array
import json
import os
import sys

SCHEMA = 'gate1a-blender-probe/1'
MINIMUM_BLENDER = (5, 0, 0)


def pixel_offset(width, height, x, y):
    """RGBA offset of pixel (x, y), counted from the TOP-left, in a Blender buffer whose rows run bottom to top."""
    return ((height - 1 - y) * width + x) * 4


def sample_pixel(buffer, width, height, x, y):
    """(rgba, spread): the pixel's RGBA and the largest channel difference to its 3 x 3 neighbours inside the image."""
    offset = pixel_offset(width, height, x, y)
    center = [float(buffer[offset + c]) for c in range(4)]
    spread = 0.0
    for dy in (-1, 0, 1):
        for dx in (-1, 0, 1):
            nx, ny = x + dx, y + dy
            if 0 <= nx < width and 0 <= ny < height:
                other = pixel_offset(width, height, nx, ny)
                spread = max(spread, max(abs(float(buffer[other + c]) - center[c]) for c in range(3)))
    return center, spread


def quad_vertices(rect, z):
    x0, y0, x1, y1 = rect
    return [(x0, y0, z), (x1, y0, z), (x1, y1, z), (x0, y1, z)]


def _try(errors, label, action):
    try:
        action()
        return True
    except Exception as failure:  # noqa: BLE001 - every failed setting is recorded, never hidden
        errors.append('%s: %s: %s' % (label, type(failure).__name__, failure))
        return False


def _material_tree(bpy, material):
    if material.node_tree is None or ('use_nodes' in material.bl_rna.properties and not material.use_nodes):
        material.use_nodes = True
    return material.node_tree


def _emission_material(bpy, name, linear):
    material = bpy.data.materials.new(name)
    tree = _material_tree(bpy, material)
    for node in list(tree.nodes):
        tree.nodes.remove(node)
    emission = tree.nodes.new('ShaderNodeEmission')
    emission.inputs['Color'].default_value = (float(linear[0]), float(linear[1]), float(linear[2]), 1.0)
    emission.inputs['Strength'].default_value = 1.0
    output = tree.nodes.new('ShaderNodeOutputMaterial')
    tree.links.new(emission.outputs['Emission'], output.inputs['Surface'])
    return material


def dump_images(bpy, limit, errors):
    images = []
    for image in bpy.data.images:
        index = image.get('mt_image_index')
        if index is None:
            continue
        entry = {'index': int(index), 'name': image.name, 'source': image.source, 'file_format': image.file_format,
                 'colorspace': image.colorspace_settings.name, 'alpha_mode': image.alpha_mode,
                 'is_float': bool(image.is_float), 'pixels': None}
        try:
            count = len(image.pixels)
            width, height = int(image.size[0]), int(image.size[1])
            entry.update(size=[width, height], channels=int(image.channels), valueCount=count)
            if 0 < width * height <= limit and count:
                buffer = array.array('f', [0.0]) * count
                image.pixels.foreach_get(buffer)
                entry['pixels'] = [float(v) for v in buffer]
        except Exception as failure:  # noqa: BLE001
            errors.append('image %s: %s: %s' % (image.name, type(failure).__name__, failure))
        images.append(entry)
    return images


def setup_render(bpy, scene, spec, work, errors, settings):
    render = spec['render']
    requested = str(render.get('engine') or 'CYCLES').upper()
    engines = ['CYCLES'] if requested == 'CYCLES' else ['BLENDER_EEVEE', 'BLENDER_EEVEE_NEXT']
    for engine in engines:
        if _try(errors if engine == engines[-1] else [], 'engine %s' % engine, lambda: setattr(scene.render, 'engine', engine)):
            break
    settings['engine'] = scene.render.engine
    if scene.render.engine == 'CYCLES':
        cycles = scene.cycles
        for name, value in (('device', 'CPU'), ('samples', int(render.get('samples') or 16)), ('use_denoising', False),
                            ('use_adaptive_sampling', False), ('transparent_max_bounces', 64), ('max_bounces', 12),
                            ('sample_clamp_direct', 0.0), ('sample_clamp_indirect', 0.0), ('seed', 0),
                            ('filter_width', 1.5)):
            if _try(errors, 'cycles.%s' % name, lambda n=name, v=value: setattr(cycles, n, v)):
                settings['cycles.%s' % name] = value
    width, height = render['resolution']
    for owner, name, value in ((scene.render, 'resolution_x', int(width)), (scene.render, 'resolution_y', int(height)),
                               (scene.render, 'resolution_percentage', 100), (scene.render, 'pixel_aspect_x', 1.0),
                               (scene.render, 'pixel_aspect_y', 1.0), (scene.render, 'film_transparent', False),
                               (scene.render, 'use_border', False), (scene.render, 'use_compositing', False),
                               (scene.render, 'use_sequencer', False), (scene.render, 'dither_intensity', 0.0),
                               (scene.view_settings, 'view_transform', 'Standard'), (scene.view_settings, 'look', 'None'),
                               (scene.view_settings, 'exposure', 0.0), (scene.view_settings, 'gamma', 1.0),
                               (scene.display_settings, 'display_device', 'sRGB')):
        if _try(errors, name, lambda o=owner, n=name, v=value: setattr(o, n, v)):
            settings[name] = value
    world = bpy.data.worlds.new('gate1a_probe_world')
    scene.world = world
    world_color = tuple(float(v) for v in render.get('world') or (0.0, 0.0, 0.0))
    _try(errors, 'world.color', lambda: setattr(world, 'color', world_color))
    if world.node_tree is None and 'use_nodes' in world.bl_rna.properties:
        _try(errors, 'world.use_nodes', lambda: setattr(world, 'use_nodes', True))
    if world.node_tree is not None:
        background = next((n for n in world.node_tree.nodes if n.bl_idname == 'ShaderNodeBackground'), None)
        if background is not None:
            background.inputs['Color'].default_value = world_color + (1.0,)
            background.inputs['Strength'].default_value = 0.0
            settings['world'] = 'Background node, strength 0'
    camera_data = bpy.data.cameras.new('gate1a_probe_camera')
    camera_data.type = 'ORTHO'
    camera_data.ortho_scale = float(render['camera']['orthoScale'])
    camera_data.sensor_fit = 'AUTO'
    camera_data.shift_x = 0.0
    camera_data.shift_y = 0.0
    camera_data.clip_start, camera_data.clip_end = (float(v) for v in render.get('clip') or (0.1, 100.0))
    camera = bpy.data.objects.new('gate1a_probe_camera', camera_data)
    scene.collection.objects.link(camera)
    cx, cy = render['camera']['center']
    camera.location = (float(cx), float(cy), float(render['camera']['height']))
    camera.rotation_euler = (0.0, 0.0, 0.0)
    scene.camera = camera
    for plane in spec.get('background') or []:
        mesh = bpy.data.meshes.new('gate1a_background_%s' % plane['id'])
        mesh.from_pydata(quad_vertices(plane['rect'], float(plane['z'])), [], [(0, 1, 2, 3)])
        mesh.update()
        mesh.materials.append(_emission_material(bpy, 'gate1a_background_%s' % plane['id'], plane['linear']))
        obj = bpy.data.objects.new('gate1a_background_%s' % plane['id'], mesh)
        scene.collection.objects.link(obj)
    settings['backgroundPlanes'] = len(spec.get('background') or [])


def add_sun(bpy, scene, sun, errors, settings):
    """One sun lamp as the spec's render.lighting.sun declares it (the display-blend variant's sun, for the lit probe)."""
    light = bpy.data.lights.new('gate1a_probe_sun', type='SUN')
    light.energy = float(sun['strength'])
    for name, value in (('angle', float(sun['angle'])), ('use_shadow', bool(sun['castShadow'])),
                        ('specular_factor', float(sun['specularFactor']))):
        if name in light.bl_rna.properties and _try(errors, 'sun.%s' % name, lambda n=name, v=value: setattr(light, n, v)):
            settings['sun.%s' % name] = value
    obj = bpy.data.objects.new('gate1a_probe_sun', light)
    obj.rotation_euler = tuple(float(v) for v in sun['rotationEuler'])
    scene.collection.objects.link(obj)
    settings['sun.energy'] = light.energy


def render_and_sample(bpy, scene, spec, work, errors, result):
    bpy.ops.render.render(write_still=False)
    rendered = bpy.data.images.get('Render Result')
    exr = os.path.join(work, 'render.exr')
    png = os.path.join(work, 'render.png')
    image_settings = scene.render.image_settings
    if 'media_type' in image_settings.bl_rna.properties:
        _try(errors, 'image_settings.media_type', lambda: setattr(image_settings, 'media_type', 'IMAGE'))
    for name, value in (('file_format', 'OPEN_EXR'), ('color_depth', '32'), ('color_mode', 'RGBA'), ('exr_codec', 'ZIP')):
        _try(errors, 'exr %s' % name, lambda n=name, v=value: setattr(image_settings, n, v))
    rendered.save_render(filepath=exr, scene=scene)
    result['exr'] = exr
    for name, value in (('file_format', 'PNG'), ('color_depth', '8'), ('color_mode', 'RGBA'), ('compression', 15)):
        _try(errors, 'png %s' % name, lambda n=name, v=value: setattr(image_settings, n, v))
    if _try(errors, 'png save_render', lambda: rendered.save_render(filepath=png, scene=scene)):
        result['png'] = png
    loaded = bpy.data.images.load(exr, check_existing=False)
    count = len(loaded.pixels)
    width, height = int(loaded.size[0]), int(loaded.size[1])
    result['exrSize'] = [width, height]
    result['exrColorspace'] = loaded.colorspace_settings.name
    expected = list(spec['render']['resolution'])
    if [width, height] != expected:
        errors.append('the EXR is %s, the spec asked for %s' % ([width, height], expected))
    buffer = array.array('f', [0.0]) * count
    loaded.pixels.foreach_get(buffer)
    samples = []
    for sample in spec.get('samples') or []:
        x, y = sample['pixel']
        if not (0 <= x < width and 0 <= y < height):
            errors.append('sample %s at %s is outside the render' % (sample['id'], sample['pixel']))
            continue
        rgba, spread = sample_pixel(buffer, width, height, int(x), int(y))
        samples.append({'id': sample['id'], 'pixel': [int(x), int(y)], 'linear': rgba, 'spread': spread})
    result['samples'] = samples


def main():
    import bpy
    if tuple(bpy.app.version) < MINIMUM_BLENDER:
        sys.stderr.write('probe_render: Blender 5.0 or newer is required; running %s\n' % bpy.app.version_string)
        return 2
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    parser = argparse.ArgumentParser(prog='probe_render.py')
    parser.add_argument('--blend', required=True)
    parser.add_argument('--spec', required=True)
    parser.add_argument('--out', required=True)
    parser.add_argument('--work', required=True)
    args = parser.parse_args(argv)
    with open(args.spec, 'r', encoding='utf-8') as f:
        spec = json.load(f)
    os.makedirs(args.work, exist_ok=True)
    errors = []
    settings = {}
    result = {'schema': SCHEMA, 'probe': spec.get('probe'), 'blenderVersion': bpy.app.version_string,
              'blend': os.path.abspath(args.blend), 'errors': errors, 'settings': settings, 'samples': [],
              'exr': None, 'png': None}
    bpy.ops.wm.open_mainfile(filepath=os.path.abspath(args.blend), load_ui=False)
    scene = bpy.context.scene
    if spec.get('imagePixels'):
        result['images'] = dump_images(bpy, int(spec['imagePixels'].get('maxTexels') or 4096), errors)
    if spec.get('render'):
        try:
            setup_render(bpy, scene, spec, args.work, errors, settings)
            lighting = spec['render'].get('lighting')
            if lighting:
                here = os.path.dirname(os.path.abspath(__file__))
                if here not in sys.path:
                    sys.path.insert(0, here)
                import display_blend_variant as variant
                variant.engine_settings(scene, 'CYCLES' if scene.render.engine == 'CYCLES' else 'EEVEE',
                                        spec['render'].get('samples') or 16, settings, errors)
                if lighting.get('sun'):
                    add_sun(bpy, scene, lighting['sun'], errors, settings)
            render_and_sample(bpy, scene, spec, args.work, errors, result)
            if lighting:
                variant.lit_pass(bpy, scene, spec, args.work, errors, settings, result)
        except Exception as failure:  # noqa: BLE001 - the result records the failure; the exit code says so
            errors.append('render: %s: %s' % (type(failure).__name__, failure))
    with open(args.out, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(result, f, indent=1)
    sys.stderr.write('probe_render: wrote %s (%d samples, %d errors)\n' % (args.out, len(result['samples']), len(errors)))
    return 4 if errors else 0


if __name__ == '__main__':
    sys.exit(main())
