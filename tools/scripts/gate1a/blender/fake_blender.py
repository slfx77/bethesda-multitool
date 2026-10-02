# SPDX-License-Identifier: 0BSD
"""A FAKE Blender for ``selfcheck_blender_harness.py``: a Python script that accepts Blender's command line.

It is never used for a gate verdict and never launches Blender. ``run_blender_hops.py --blender fake_blender.py``
runs it through ``sys.executable`` (``blender_launcher.is_python_fake``) with the same arguments a real Blender gets:

    fake_blender.py --version
    fake_blender.py --background --factory-startup --python-exit-code 1 [file.blend] --python <script> -- <args>

What each script does here:
* ``tools/blender/readback.py`` (the REAL script): ``main()`` runs unchanged against ``fake_bpy``; ``--blend`` opens a
  fake .blend written by the fake import, ``--import-glb`` goes through ``fake_gltf_import``.
* ``import_model.py`` (the REAL script's functions): ``fake_import`` runs its manifest parser and checks, its root,
  image loader, mesh builder, object hierarchy (the camera-facing controller, presentation child, pivot and anchor
  empties and the anchor constraint included), billboard constraints and their records (``_apply_billboards``), shape
  keys, native states and document metadata against ``fake_bpy`` (with its float32 mathutils; constraints are recorded,
  never evaluated); the material node graphs, armatures (no bones) and skin weights are emulated.
* ``inside_blender/repack_dds_control.py``: the fake re-encodes the target image as PNG and packs it again.
* ``inside_blender/probe_render.py``: the fake computes the probe samples analytically (``render_model``).

Scenario (JSON file named by the environment variable FAKE_BLENDER_SCENARIO), all optional:
  sleepSeconds, allocateMb             hold memory / time before the script runs (watchdog and timeout tests)
  exitCode                             exit with this code before doing anything
  stateDir                             where the invocation counter lives (counter.txt)
  crashInvocations [n]                 exit 0xC0000005 on these global invocation numbers
  gltfCustomAttributeFailures [n]      on these invocations a GLB with two underscore attributes of different widths
                                       fails the import with numpy's concatenation error (the hop-B' defect)
  gltfCustomAttributeFailEvery n       the same defect on every invocation number divisible by n
  volatileReadbackInvocations [n]      add a varying field to the readback on these invocations (disagreement)
  gltfCustomAttributeMessage "text"    the scripted custom-attribute failure raises this message instead (a failure
                                       that is NOT the set-order defect)
  renderModel "drawn" | "linear" | "display"
                                       blend compositing: the display fit the package declares, composited in
                                       Blender's linear space (what the importer draws, default); the retired linear
                                       graph; or an ideal Gamebryo framebuffer in display space. The lit-blend probe
                                       likewise: the importer's own lit route, the retired lit route, or Gamebryo on
                                       the lit display color (``fake_render.lit_sample``)
  flipDdsOnLoad                        DDS images load upside down relative to PNG
  imageAlphaMode "STRAIGHT" | ...      the probe renders every image with this Blender alpha mode instead of the one
                                       the importer stored (STRAIGHT is the importer before the straight-texel fix)
  dropTransmission                     the glTF importer ignores KHR_materials_transmission
  crossTransmissionBindings            the glTF importer wires baseColorTexture to Emission Color and emissiveTexture
                                       to Base Color (an importer that exchanges E and T)
  dropVertexColor                      the glTF importer ignores COLOR_0
  corruptShapeKeys                     the importer moves every shape-key vertex (masks the shape-key control)
  corruptShapeKeysMatching ["text"]    the same, only for packages whose path contains one of the strings
  importFailure "message"              the importer refuses the package (exit 1, one import_model: line)
  importFailureMatching ["text"]       the importer refuses only packages whose path contains one of the strings
  recordPid path                       write this process's pid to ``path`` (the self-check's process-tree tests)
  spawnChild path                      start a child Python that sleeps 120 s and write its pid to ``path``
  version "4.2.0"                      the version --version prints (default 5.1.0)
  recordEnvironment path               write the sorted environment variable names this process received
"""

import json
import os
import sys
import time
import traceback

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
sys.path.insert(0, os.path.dirname(HERE))

CRASH_EXIT_CODE = 0xC0000005
# numpy's text for the hop-B' defect; held in a constant so a traceback's source echo of the raise line never carries
# it (the harness reads the whole stderr, and an override message must be the ONLY failure text a launch shows).
CONCATENATION_ERROR = ('all the input array dimensions except for the concatenation axis must match exactly, '
                       'but along dimension 1, the array at index 0 has size 4 and the array at index 1 has size 3')


def scenario():
    path = os.environ.get('FAKE_BLENDER_SCENARIO')
    if not path or not os.path.isfile(path):
        return {}
    with open(path, 'r', encoding='utf-8') as f:
        return json.load(f)


def next_invocation(state_dir):
    if not state_dir:
        return 0
    os.makedirs(state_dir, exist_ok=True)
    path = os.path.join(state_dir, 'counter.txt')
    count = 0
    if os.path.isfile(path):
        with open(path, 'r', encoding='utf-8') as f:
            count = int(f.read().strip() or 0)
    count += 1
    with open(path, 'w', encoding='utf-8') as f:
        f.write(str(count))
    with open(os.path.join(state_dir, 'invocations.log'), 'a', encoding='utf-8') as f:
        f.write('%d %s\n' % (count, ' '.join(sys.argv[1:])))
    return count


def parse(argv):
    """(script, script args, blend file or None) from Blender's command line."""
    script, blend = None, None
    rest = argv
    if '--' in argv:
        index = argv.index('--')
        rest, script_args = argv[:index], argv[index + 1:]
    else:
        script_args = []
    i = 0
    while i < len(rest):
        token = rest[i]
        if token == '--python':
            script = rest[i + 1]
            i += 2
            continue
        if token == '--python-exit-code':
            i += 2
            continue
        if token.lower().endswith('.blend'):
            blend = token
        i += 1
    return script, script_args, blend


# --------------------------------------------------------------------------------------------------------------------
# import_model.py
# --------------------------------------------------------------------------------------------------------------------


def fake_import(script_path, script_args, config):
    import numpy as np
    import fake_bpy
    bpy = fake_bpy.install()
    module = fake_bpy.load_module(script_path, 'import_model_under_fake_bpy')
    package_path, output_path = module._parse_arguments(['blender', '--'] + script_args)
    matching = [text for text in config.get('importFailureMatching') or [] if text in package_path.replace('\\', '/')]
    if config.get('importFailure') or matching:
        sys.stderr.write('import_model: %s\n' % (config.get('importFailure') or 'scripted refusal of %s' % matching))
        return 1
    corrupt = config.get('corruptShapeKeys') or any(text in package_path.replace('\\', '/')
                                                    for text in config.get('corruptShapeKeysMatching') or [])
    package = module.Package(package_path)
    try:
        manifest = package.read_manifest()
        module._check_manifest(manifest)
        import tempfile
        with tempfile.TemporaryDirectory(prefix='fake-import-') as directory:
            ctx = module._BuildContext(manifest, package, directory)
            module._apply_scene_settings(manifest)
            ctx.root = module._make_root(manifest)
            ctx.images = module._load_images(ctx)
            _fake_materials(bpy, module, ctx)
            module._make_meshes(ctx)
            module._make_objects(ctx)
            _fake_armatures(bpy, module, ctx)
            for node_index, mesh_index, primitive_index, obj in ctx.primitive_objects:
                geometry = ctx.geometry[(mesh_index, primitive_index)]
                module._apply_shape_keys(ctx, obj, geometry, mesh_index, primitive_index)
                if corrupt and geometry.mesh.shape_keys is not None:
                    for block in geometry.mesh.shape_keys.key_blocks[1:]:
                        block.data.fields['co'] = block.data.fields['co'] + np.float32(0.5)
            # The real importer's order: billboards after the objects, skins and shape keys, before the native states.
            module._apply_billboards(ctx)
            module._attach_native_states(ctx)
            module._apply_document_metadata(ctx)
            bpy.data.fake_package = os.path.abspath(package_path)
            module._save(output_path)
        summary = module._summary(ctx, output_path)
    except module.PackageError as error:
        sys.stderr.write('import_model: %s\n' % error)
        return 1
    finally:
        package.close()
    sys.stdout.write(module._json(summary) + '\n')
    return 0


def _fake_armatures(bpy, module, ctx):
    """One ARMATURE object per skin under mt_root with the importer's object properties and no bones (the fake has no
    edit bones); ``ctx.armatures`` is filled so native-state rows targeting a skin resolve as they do in Blender."""
    import fake_bpy
    for skin_index, skin in enumerate(ctx.manifest.get('skins') or []):
        name = str(skin.get('name') or ('skin_%04d' % skin_index))
        data = fake_bpy.IDBlock(name=name, pose_position='POSE', bones=[])
        obj = bpy.data.objects.new(name, data)
        obj.type = 'ARMATURE'
        obj.pose = None
        obj.parent = ctx.root
        module._set_prop(obj, 'mt_name', name)
        module._set_prop(obj, 'mt_skin_index', skin_index)
        ctx.armatures[skin_index] = (obj, [])


def _fake_materials(bpy, module, ctx):
    """Material datablocks with the properties _build_material sets, decided by the importer's pure functions; the node
    graph itself is not built (fake_bpy has no node trees)."""
    ctx.material_keys = module._material_variant_keys(ctx.manifest)
    for index, entry in enumerate(ctx.manifest.get('materials') or []):
        culling = module._culling(entry)
        ctx.reverse_winding.append(culling[1])
        ctx.depth_bias.append(module._has_depth_bias(entry))
        for key in ctx.material_keys[index]:
            limitation = module._ordinary_transmission_limitation(entry)
            if limitation is not None:
                raise module.PackageError('Material %d: %s' % (index, limitation))
            name = str(entry.get('name') or ('material_%04d' % index))
            material = bpy.data.materials.new(name)
            emission, _transmission, record = module._material_blend(entry)
            if record.get('display') is not None:  # _display_blend_closure records the route its graph takes
                record['route'] = module._display_blend_route(not bool(entry.get('unlit', False)), record['display'])
            material.surface_render_method = 'BLENDED' if emission is not None else 'DITHERED'
            material.use_backface_culling = culling[0]
            module._set_prop(material, 'mt_name', name)
            module._set_prop(material, 'mt_material_index', index)
            module._set_prop(material, 'mt_vertex_color_variant', {'domain': key[0], 'storage': key[1]})
            module._set_prop(material, 'mt_blend_graph', record)
            module._set_prop(material, 'mt_culling', culling[2])
            # the remaining properties exactly as _build_material sets them (the graph itself is not built)
            import types as _types
            state = entry.get('renderState') or {}
            module._set_prop(material, 'mt_lighting_model', module._apply_lighting_model(entry.get('source'), entry, _types.SimpleNamespace()))
            module._set_prop(material, 'mt_portable', {m: v for m, v in entry.items() if m not in ('source', 'renderState', 'extras')})
            module._set_prop(material, 'mt_render_state', entry.get('renderState'))
            module._set_prop(material, 'mt_material_source', entry.get('source'))
            if module._has_depth_bias(entry):
                module._set_prop(material, 'mt_depth_bias', state.get('depth'))
            module._set_prop(material, 'mt_draw_order', state.get('drawOrder'))
            referenced = [int((layer.get('binding') or {}).get('image', -1)) for layer in module._material_layers(entry)]
            missing = sorted({i for i in referenced if 0 <= i < len(ctx.images) and ctx.images[i] is None})
            if missing:
                module._set_prop(material, 'mt_missing_images', missing)
            module._set_prop(material, 'mt_extras', entry.get('extras'))
            ctx.materials[(index, key)] = material


# --------------------------------------------------------------------------------------------------------------------
# readback.py and the glTF importer
# --------------------------------------------------------------------------------------------------------------------


def fake_readback(script_path, script_args, config, invocation):
    import fake_bpy

    def importer(bpy, filepath):
        return fake_gltf_import(bpy, filepath, config, invocation)

    fake_bpy.install(importer)
    module = fake_bpy.load_module(script_path, 'readback_under_fake_bpy')
    sys.argv = ['blender', '--'] + list(script_args)
    code = module.main()
    if invocation in (config.get('volatileReadbackInvocations') or []):
        out = script_args[script_args.index('--out') + 1]
        with open(out, 'r', encoding='utf-8') as f:
            dump = json.load(f)
        dump['volatile'] = {'invocation': invocation, 'pid': os.getpid()}
        with open(out, 'w', encoding='utf-8') as f:
            json.dump(dump, f, indent=1, sort_keys=True)
    return code


class _Socket:
    def __init__(self, node, identifier, name, kind, default):
        self.node = node
        self.identifier = identifier
        self.name = name
        self.type = kind
        self.default_value = default
        self.is_linked = False
        self.enabled = True


class _Node:
    def __init__(self, name, bl_idname, inputs=(), outputs=()):
        self.name = name
        self.label = ''
        self.bl_idname = bl_idname
        self.mute = False
        self.inputs = [_Socket(self, identifier, identifier, kind, default) for identifier, kind, default in inputs]
        self.outputs = [_Socket(self, identifier, identifier, kind, None) for identifier, kind in outputs]
        self.image = None
        self.interpolation = 'Linear'
        self.extension = 'REPEAT'
        self.projection = 'FLAT'

    def input(self, name):
        return next(s for s in self.inputs if s.name == name)

    def output(self, name):
        return next(s for s in self.outputs if s.name == name)


class _Link:
    def __init__(self, source, target):
        self.from_node, self.from_socket, self.to_node, self.to_socket = source.node, source, target.node, target
        self.is_valid = True
        target.is_linked = True


class _Tree:
    def __init__(self, name):
        self.name = name
        self.animation_data = None  # the fake import animates no material
        self.nodes = []
        self.links = []
        self.output = None

    def get_output_node(self, target):
        return self.output


def fake_gltf_import(bpy, filepath, config, invocation):
    """Build meshes, images and Principled materials from a GLB the way io_scene_gltf2 lays them out, closely enough
    for readback.py's dump and --expect-* checks. The custom-attribute defect is reproduced on scripted invocations."""
    import fake_bpy
    import numpy as np
    from glb_reader import Glb
    if len(os.path.abspath(filepath)) > 259:
        # Blender 5.1 is not long-path aware: io_scene_gltf2's isfile() is False for a longer path that exists.
        raise RuntimeError('Error: Please select a file')
    with open(filepath, 'rb') as f:
        glb = Glb(f.read())
    document = glb.json
    for mesh in document.get('meshes') or []:
        for primitive in mesh.get('primitives') or []:
            custom = [name for name in primitive['attributes'] if name.startswith('_')]
            widths = {document['accessors'][primitive['attributes'][name]]['type'] for name in custom}
            every = config.get('gltfCustomAttributeFailEvery')
            scripted = invocation in (config.get('gltfCustomAttributeFailures') or []) or bool(every and invocation % int(every) == 0)
            if len(custom) >= 2 and len(widths) >= 2 and scripted:
                message = config.get('gltfCustomAttributeMessage') or CONCATENATION_ERROR
                raise ValueError(message)
    images = []
    for index, image in enumerate(document.get('images') or []):
        data, _mime = glb.image_bytes(index)
        blender = bpy.data.images.link(fake_bpy.Image(image.get('name') or ('Image_%d' % index), '', data))
        blender.pack()
        images.append(blender)
    materials = []
    for index, material in enumerate(document.get('materials') or []):
        blender = bpy.data.materials.new(material.get('name') or ('Material_%d' % index))
        blender.use_nodes = True
        tree = _Tree('Shader Nodetree')
        pbr = material.get('pbrMetallicRoughness') or {}
        extensions = material.get('extensions') or {}
        transmission = (extensions.get('KHR_materials_transmission') or {}).get('transmissionFactor', 0.0)
        if config.get('dropTransmission'):
            transmission = 0.0
        ior = (extensions.get('KHR_materials_ior') or {}).get('ior', 1.5)
        strength = (extensions.get('KHR_materials_emissive_strength') or {}).get('emissiveStrength', 1.0)
        principled = _Node('Principled BSDF', 'ShaderNodeBsdfPrincipled', inputs=[
            ('Base Color', 'RGBA', list(pbr.get('baseColorFactor', [1, 1, 1, 1]))),
            ('Metallic', 'VALUE', float(pbr.get('metallicFactor', 1.0))),
            ('Roughness', 'VALUE', float(pbr.get('roughnessFactor', 1.0))), ('IOR', 'VALUE', float(ior)),
            ('Alpha', 'VALUE', 1.0), ('Transmission Weight', 'VALUE', float(transmission)),
            ('Emission Color', 'RGBA', list(material.get('emissiveFactor', [0, 0, 0])) + [1.0]),
            ('Emission Strength', 'VALUE', float(strength))], outputs=[('BSDF', 'SHADER')])
        output = _Node('Material Output', 'ShaderNodeOutputMaterial', inputs=[('Surface', 'SHADER', None)])
        tree.nodes += [principled, output]
        tree.links.append(_Link(principled.output('BSDF'), output.input('Surface')))
        tree.output = output
        vertex_color = not config.get('dropVertexColor') and any(
            primitive.get('material') == index and 'COLOR_0' in primitive['attributes']
            for mesh in document.get('meshes') or [] for primitive in mesh.get('primitives') or [])
        base_socket = principled.input('Base Color')
        if vertex_color:  # io_scene_gltf2 pbrMetallicRoughness.base_color: texture x vertex color through a MULTIPLY mix
            mix = _Node('Mix Vertex Color', 'ShaderNodeMix', inputs=[('A', 'RGBA', None), ('B', 'RGBA', None)], outputs=[('Result', 'RGBA')])
            mix.data_type, mix.blend_type, mix.clamp_factor, mix.clamp_result = 'RGBA', 'MULTIPLY', True, False
            vcol = _Node('Color Attribute', 'ShaderNodeVertexColor', outputs=[('Color', 'RGBA'), ('Alpha', 'VALUE')])
            tree.nodes += [mix, vcol]
            tree.links.append(_Link(mix.output('Result'), base_socket))
            tree.links.append(_Link(vcol.output('Color'), mix.input('B')))
            base_socket = mix.input('A')
        wiring = ((pbr.get('baseColorTexture'), base_socket), (material.get('emissiveTexture'), principled.input('Emission Color')))
        if config.get('crossTransmissionBindings'):
            wiring = ((wiring[0][0], wiring[1][1]), (wiring[1][0], wiring[0][1]))
        for key, socket in wiring:
            if key is None:
                continue
            texture = document['textures'][key['index']]
            node = _Node('Image Texture %s' % socket.identifier, 'ShaderNodeTexImage', inputs=[('Vector', 'VECTOR', None)],
                         outputs=[('Color', 'RGBA'), ('Alpha', 'VALUE')])
            node.image = images[texture['source']]
            tree.nodes.append(node)
            tree.links.append(_Link(node.output('Color'), socket))
        blender.node_tree = tree
        materials.append(blender)
    for node_index, node in enumerate(document.get('nodes') or []):
        mesh_data = None
        if node.get('mesh') is not None:
            mesh = document['meshes'][node['mesh']]
            mesh_data = bpy.data.meshes.new(mesh.get('name') or 'Mesh')
            positions, loops = [], []
            for primitive in mesh['primitives']:
                base = len(positions)
                positions += glb.accessor(primitive['attributes']['POSITION']).tolist()
                indices = glb.accessor(primitive['indices']).reshape(-1).tolist() if 'indices' in primitive else list(range(len(positions) - base))
                loops += [base + i for i in indices]
                if primitive.get('material') is not None and materials[primitive['material']] not in mesh_data.materials:
                    mesh_data.materials.append(materials[primitive['material']])
            mesh_data.vertices.add(len(positions))
            mesh_data.vertices.foreach_set('co', np.asarray(positions, dtype=np.float32).ravel())
            mesh_data.loops.add(len(loops))
            mesh_data.loops.foreach_set('vertex_index', np.asarray(loops, dtype=np.int32))
            mesh_data.polygons.add(len(loops) // 3)
            mesh_data.polygons.foreach_set('loop_start', np.arange(0, len(loops), 3, dtype=np.int32))
            colors = []
            for primitive in mesh['primitives']:
                if 'COLOR_0' in primitive['attributes'] and not config.get('dropVertexColor'):
                    values = glb.accessor(primitive['attributes']['COLOR_0']).reshape(-1, 4)
                    indices = glb.accessor(primitive['indices']).reshape(-1) if 'indices' in primitive else np.arange(len(values))
                    colors.append(values[indices])
            if colors:
                attribute = mesh_data.color_attributes.new('Color', 'FLOAT_COLOR', 'CORNER')
                attribute.data.foreach_set('color', np.concatenate(colors).astype(np.float32).ravel())
        bpy.data.objects.new(node.get('name') or ('Node_%d' % node_index), mesh_data)
    return {'FINISHED'}


# --------------------------------------------------------------------------------------------------------------------
# inside_blender scripts, emulated
# --------------------------------------------------------------------------------------------------------------------


def _arg(args, name, default=None):
    return args[args.index(name) + 1] if name in args else default


def fake_repack(script_args, config):
    """Emulates inside_blender/repack_dds_control.py: the target image re-encoded (PNG) and packed again."""
    import io
    import fake_bpy
    from dds_decode import decode
    from PIL import Image as PilImage
    blend, out, target = _arg(script_args, '--blend'), _arg(script_args, '--out'), int(_arg(script_args, '--image'))
    result_path = _arg(script_args, '--result')
    data = fake_bpy.load_fake_blend(blend)
    image = next((i for i in data.images if i.get('mt_image_index') == target), None)
    result = {'schema': 'gate1a-blender-repack/1', 'image': target, 'blender': fake_bpy.VERSION_STRING}
    if image is None or image.packed_file is None:
        result['error'] = 'image %d not found or not packed' % target
        _write(result_path, result)
        return 4
    before = image.packed_file.data
    rgba, _info = decode(before) if before[:4] == b'DDS ' else (None, None)
    buffer = io.BytesIO()
    PilImage.fromarray(rgba, 'RGBA').save(buffer, format='PNG')
    image._data = buffer.getvalue()
    image.file_format = 'PNG'
    image.pack()
    fake_bpy.save_fake_blend(data, out)
    import hashlib
    result.update(beforeSha256=hashlib.sha256(before).hexdigest(), afterSha256=hashlib.sha256(image._data).hexdigest(),
                  afterBytes=len(image._data), method='fake: decoded and re-encoded as PNG')
    _write(result_path, result)
    return 0


def _write(path, document):
    if path:
        with open(path, 'w', encoding='utf-8') as f:
            json.dump(document, f, indent=1)


def stored_alpha_modes(data):
    """{document image index: alpha_mode} of each image's CANONICAL datablock (always CHANNEL_PACKED since the per-use
    PREMUL rule, Shared 314f125). A " [PREMUL]" variant datablock shares the index; where only a variant exists its mode
    is kept, so ``fake_render.delivered_texel`` refuses it rather than backing a verdict on an unmeasured texel."""
    result = {}
    for image in data.images:
        index = image.get('mt_image_index')
        if index is None:
            continue
        index = int(index)
        if index not in result or image.alpha_mode == 'CHANNEL_PACKED':
            result[index] = image.alpha_mode
    return result


def fake_probe(script_args, config):
    """Emulates inside_blender/probe_render.py: samples and image pixels computed from the package the fake .blend
    came from, under the scenario's render model (see compare_f for the two models), with each image's texel delivered
    as Cycles delivers it for the alpha mode the importer stored (``fake_render.delivered_texel``)."""
    import fake_bpy
    import fake_render
    blend, spec_path, out = _arg(script_args, '--blend'), _arg(script_args, '--spec'), _arg(script_args, '--out')
    data = fake_bpy.load_fake_blend(blend)
    with open(spec_path, 'r', encoding='utf-8') as f:
        spec = json.load(f)
    result = fake_render.render(data.fake_package, spec, config, stored_alpha_modes(data))
    result['blenderVersion'] = fake_bpy.VERSION_STRING
    _write(out, result)
    return 0


# --------------------------------------------------------------------------------------------------------------------


def main(argv):
    config = scenario()
    invocation = next_invocation(config.get('stateDir'))
    if config.get('recordEnvironment'):
        with open(config['recordEnvironment'], 'w', encoding='utf-8') as f:
            json.dump(sorted(os.environ), f)
    if config.get('recordPid'):
        with open(config['recordPid'], 'w', encoding='utf-8') as f:
            f.write(str(os.getpid()))
    if config.get('spawnChild'):
        import subprocess
        child = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(120)'])
        with open(config['spawnChild'], 'w', encoding='utf-8') as f:
            f.write(str(child.pid))
    if config.get('allocateMb'):
        ballast = bytearray(int(config['allocateMb']) * 1024 * 1024)
        for i in range(0, len(ballast), 4096):
            ballast[i] = 1
    if config.get('sleepSeconds'):
        time.sleep(float(config['sleepSeconds']))
    if '--version' in argv:
        sys.stdout.write('Blender %s (fake)\n\tbuild date: fake\n' % config.get('version', '5.1.0'))
        # Real Blender 5.1 follows the version line with several kilobytes of build details and compiler flags,
        # so a check that reads only the tail of the output misses the version line (found on the first real run).
        sys.stdout.write('\tbuild c flags: ' + ' '.join('-Wno-fake-warning-%d' % index for index in range(400)) + '\n')
        return 0
    if config.get('exitCode') is not None:
        return int(config['exitCode'])
    if invocation in (config.get('crashInvocations') or []):
        return CRASH_EXIT_CODE
    script, script_args, _blend = parse(argv)
    if script is None:
        sys.stderr.write('fake_blender: no --python script\n')
        return 2
    name = os.path.basename(script).lower()
    try:
        if name == 'readback.py':
            return fake_readback(script, script_args, config, invocation)
        if name == 'import_model.py':
            return fake_import(script, script_args, config)
        if name == 'repack_dds_control.py':
            return fake_repack(script_args, config)
        if name == 'probe_render.py':
            return fake_probe(script_args, config)
        sys.stderr.write('fake_blender: unknown script %s\n' % script)
        return 2
    except SystemExit as stop:
        return int(stop.code or 0)
    except Exception:  # noqa: BLE001 - Blender's --python-exit-code 1 on an unhandled error
        traceback.print_exc()
        return 1


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
