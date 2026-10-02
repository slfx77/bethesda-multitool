# SPDX-License-Identifier: 0BSD
"""Control builders for the Blender hops: each makes a copy of a pristine input with ONE declared change.

* hop D, shape key: ``move_shape_key_vertex`` moves one Blender vertex of the first morph target in a package copy
  (every primitive vertex welded to that Blender vertex moves together, so the importer still accepts the package).
* hop D, animation key: ``move_animation_key`` adds 0.5 to key 1 of component 0 of the first Translation, Scale,
  EulerRotation or MorphWeights channel with two keys or more in a package copy (the importer admits it: those
  properties have no admission rule a 0.5 offset can break; its F-curve keys and its evaluation then differ).
* hop D, DDS repack: ``repack_target_image`` picks the image; the repack itself happens inside Blender
  (``inside_blender/repack_dds_control.py``: the image re-encoded through ``save()`` and packed again).
* hop F, blend: ``swap_blend_factors`` exchanges every material's source and destination factors (E and T swapped),
  each swapped equation carrying the display record the writer would give it; ``plant_display_nodes`` (design 6.4
  control C2) moves every declared two-node or anchored fit's nodes to (0, 1) and every one-node fit's node to 0, so a
  render that still passes would prove the importer ignores the record's constants.
* hop F, lit route: ``plant_lit_exponent`` (design 6.4 control C4) replaces every declared power lit curve's exponent
  (lit over's p 1.5) and every generic linear lit curve's exponent other than 1 (SRC_ALPHA/ZERO's p 2.07) by 1, the lit
  graph before the display-blend change and before review fixes 2 respectively.
* hop F, ramp: ``declare_linear_vertex_colors`` declares every primitive's vertex colors Linear (the gamma-space
  step omitted).
* hop F, orientation: ``flip_dds_images`` stores every DDS image upside down.
* hop F, transmission: ``strip_transmission`` removes KHR_materials_transmission from a GLB copy;
  ``swap_transmission_terms`` exchanges the route's E and T (base-color and emissive textures and factors).
* hop B': the custom-attribute GLB is built from scratch (``probe_builders.build_custom_attribute_glb``).

Every function returns a dict describing the change, which the receipt records beside the mutated file's SHA-256.
"""

import hashlib
import json
import os
import sys
import zipfile

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from glb_reader import Glb  # noqa: E402
from hop_b import rebuild_glb  # noqa: E402
from package_reader import Package  # noqa: E402
import import_rules as rules  # noqa: E402
import probe_builders as builders  # noqa: E402


def _rewrite_package(source_path, target_path, manifest_edit=None, entry_edit=None, extra_entries=()):
    """Copy a package keeping entry order and storage; ``manifest_edit(manifest)`` edits the parsed manifest in place,
    ``entry_edit(name, bytes) -> bytes`` rewrites entries, ``extra_entries`` appends (name, bytes)."""
    os.makedirs(os.path.dirname(os.path.abspath(target_path)), exist_ok=True)
    with zipfile.ZipFile(source_path) as src, zipfile.ZipFile(target_path, 'w', compression=zipfile.ZIP_STORED) as dst:
        names = src.namelist()
        for info in src.infolist():
            data = src.read(info.filename)
            if info.filename == 'manifest.json' and manifest_edit is not None:
                manifest = json.loads(data.decode('utf-8'))
                manifest_edit(manifest)
                data = json.dumps(manifest, indent=1).encode('utf-8')
            elif entry_edit is not None:
                data = entry_edit(info.filename, data)
            out = zipfile.ZipInfo(info.filename, date_time=info.date_time)
            dst.writestr(out, data)
        for name, data in extra_entries:
            if name not in names:
                dst.writestr(zipfile.ZipInfo(name, date_time=(2026, 9, 25, 0, 0, 0)), data)
    return target_path


def first_morph_primitive(package_path):
    """(mesh index, primitive index, primitive manifest) of the first primitive with a morph target, or None."""
    package = Package(package_path)
    try:
        for mesh_index, primitive_index, _mesh, primitive in package.primitives():
            if primitive.get('morphs'):
                return mesh_index, primitive_index, primitive
    finally:
        package.close()
    return None


def shape_key_where(mesh_index, primitive_index, target=0):
    """The comparison element the shape-key control mutates (``compare_d`` reports shape keys under this prefix)."""
    return 'mesh %d prim %d/shape_keys/%d' % (mesh_index, primitive_index, target + 1)


def move_shape_key_vertex(source_path, target_path, offset=1.0):
    """Move Blender vertex 0 of target 0 of the first morphed primitive by ``offset`` source units along x.

    Returns {mesh, primitive, target, blenderVertex, primitiveVertices, stream, expectedWhere} or None when the package
    has no morph target."""
    found = first_morph_primitive(source_path)
    if found is None:
        return None
    mesh_index, primitive_index, primitive = found
    package = Package(source_path)
    try:
        plan = rules.PrimitivePlan(rules.PackagePrimitiveSource(package, primitive), False, 'control')
    finally:
        package.close()
    target = primitive['morphs'][0]
    stream = target['absolutePositions'] if str(target.get('form')) == 'absolute' else target['positionDeltas']
    blender_vertex = 0
    members = np.nonzero(plan.vertex_to_blender == blender_vertex)[0].tolist()

    def edit(name, data):
        if name != stream['path']:
            return data
        values = np.frombuffer(data, dtype='<f4').reshape(-1, 3).copy()
        values[members, 0] = values[members, 0] + np.float32(offset)
        return values.tobytes()

    _rewrite_package(source_path, target_path, entry_edit=edit)
    return {'mesh': mesh_index, 'primitive': primitive_index, 'target': 0, 'targetName': target.get('name'),
            'blenderVertex': blender_vertex, 'primitiveVertices': members, 'stream': stream['path'], 'offset': offset,
            'expectedWhere': shape_key_where(mesh_index, primitive_index)}


def repack_target_image(package_path):
    """The index of the first packed DDS image that decodes as a single 2D surface (the DDS-repack control target)."""
    from dds_decode import DdsHeader, decode
    package = Package(package_path)
    try:
        for index, image in enumerate(package.manifest.get('images') or []):
            if not image.get('path') or str(image.get('container') or '').lower() != 'dds':
                continue
            data = package.image_payload(image)
            try:
                header = DdsHeader(data)
                if header.is_cube or header.is_volume:
                    continue
                decode(data)
            except Exception:  # noqa: BLE001 - an undecodable payload is simply not a candidate
                continue
            return {'image': index, 'name': image.get('name'), 'sha256': image.get('sha256'),
                    'expectedWhere': 'images/%d/packed_sha256' % index}
    finally:
        package.close()
    return None


def swap_blend_factors(source_path, target_path):
    """Exchange sourceFactor and destinationFactor in both equations of every enabled blend (E and T swapped). Each
    swapped equation takes the display record the writer would give it (``probe_builders.display_record_for_blend``),
    since the importer refuses an enabled affine blend without one; the control is judged against the UNSWAPPED
    expectation, so the record only has to be the one a writer would emit."""
    swapped = []

    def edit(manifest):
        for index, material in enumerate(manifest.get('materials') or []):
            blend = (material.get('renderState') or {}).get('blend')
            if not blend or not blend.get('enabled', True):
                continue
            for key in ('colorEquation', 'alphaEquation'):
                equation = blend.get(key)
                if equation:
                    equation['sourceFactor'], equation['destinationFactor'] = equation['destinationFactor'], equation['sourceFactor']
            blend.pop('display', None)
            display = builders.display_record_for_blend(blend)
            if display is not None:
                blend['display'] = display
            swapped.append(index)

    _rewrite_package(source_path, target_path, manifest_edit=edit)
    return {'change': 'source and destination factors exchanged', 'materials': swapped}


def planted_display_fit(fit):
    """The fit control C2 plants in place of a declared one (a new dict): a two-node or anchored fit's nodes at x1 0 and
    x2 1, a one-node fit's node at 0; an exact fit unchanged."""
    planted = dict(fit)
    if planted.get('kind') in ('two', 'anchored'):
        planted.update(x1=0.0, x2=1.0)
    elif planted.get('kind') == 'one':
        planted['xa'] = 0.0
    return planted


def plant_display_nodes(source_path, target_path):
    """Control C2: every enabled blend's display record keeps its form, but a two-node or anchored fit's nodes become
    x1 0 and x2 1 and a one-node fit's node 0 (the endpoint and black-anchored graphs); exact fits are untouched. The
    importer accepts the record (x1 in [0, 1], x2 above it and at most 1), so a render that still matches the PRISTINE
    expectation would show the importer draws a hard-coded rule instead of the record's constants."""
    planted = []

    def edit(manifest):
        for index, material in enumerate(manifest.get('materials') or []):
            display = ((material.get('renderState') or {}).get('blend') or {}).get('display')
            if display is None or (display.get('fit') or {}).get('kind') not in ('two', 'anchored', 'one'):
                continue
            display['fit'] = planted_display_fit(display['fit'])
            planted.append(index)

    _rewrite_package(source_path, target_path, manifest_edit=edit)
    return {'change': 'display fit nodes planted: two-node and anchored (x1, x2) -> (0, 1), one-node xa -> 0', 'materials': planted}


def plant_lit_exponent(source_path, target_path):
    """Control C4: every enabled blend's power lit curve (lit over, p 1.5: m = As, tau = 1 - As, today's lit graph) and
    every generic linear lit curve whose exponent is not 1 (SRC_ALPHA/ZERO, p 2.07: m = As, the route before review
    fixes 2) takes p 1; nothing else changes. A linear curve already at exponent 1 is left as it is (and not listed)."""
    planted = []

    def edit(manifest):
        for index, material in enumerate(manifest.get('materials') or []):
            lit = (((material.get('renderState') or {}).get('blend') or {}).get('display') or {}).get('lit')
            if lit is not None and (lit.get('form') == 'power' or (lit.get('form') == 'linear' and float(lit.get('p', 1.0)) != 1.0)):
                lit['p'] = 1.0
                planted.append(index)

    _rewrite_package(source_path, target_path, manifest_edit=edit)
    return {'change': 'lit exponent planted: power and generic linear lit curves p -> 1', 'materials': planted}


def declare_linear_vertex_colors(source_path, target_path):
    """Declare every primitive's vertex colors Linear, so the importer multiplies in linear space."""
    changed = []

    def edit(manifest):
        for mesh_index, mesh in enumerate(manifest.get('meshes') or []):
            for primitive_index, primitive in enumerate(mesh.get('primitives') or []):
                primitive['colorSpace'] = 'Linear'
                changed.append([mesh_index, primitive_index])

    _rewrite_package(source_path, target_path, manifest_edit=edit)
    return {'change': 'colorSpace Linear', 'primitives': changed}


def flip_dds_images(source_path, target_path):
    """Replace every DDS image produced by ``probe_builders`` with the same pattern stored upside down (new SHA-256,
    new entry path), leaving PNG images untouched."""
    replaced = []
    new_entries = []
    package = Package(source_path)
    try:
        images = package.manifest.get('images') or []
        payloads = {index: package.image_payload(image) for index, image in enumerate(images) if image.get('path')}
    finally:
        package.close()
    flipped_pattern = builders.orientation_pattern(flip=True)

    def edit(manifest):
        for index, image in enumerate(manifest.get('images') or []):
            if str(image.get('container') or '').lower() != 'dds' or index not in payloads:
                continue
            original = payloads[index]
            fourcc = original[84:88]
            data = builders.dds_bc1_bytes(flipped_pattern) if fourcc == b'DXT1' else builders.dds_bgra8_bytes(flipped_pattern)
            sha = hashlib.sha256(data).hexdigest()
            path = 'images/%s.dds' % sha
            new_entries.append((path, data))
            replaced.append({'image': index, 'from': image.get('sha256'), 'to': sha})
            image.update(path=path, sha256=sha, byteLength=len(data))

    # The manifest is the first entry, so ``edit`` fills ``new_entries`` before the extra entries are appended.
    _rewrite_package(source_path, target_path, manifest_edit=edit, extra_entries=new_entries)
    return {'change': 'DDS images stored upside down', 'images': replaced}


def strip_transmission(source_path, target_path):
    """A GLB copy with KHR_materials_transmission removed from every material and from the extension lists."""
    with open(source_path, 'rb') as f:
        glb = Glb(f.read())
    document = json.loads(glb.json_bytes.decode('utf-8'))
    stripped = []
    for index, material in enumerate(document.get('materials') or []):
        extensions = material.get('extensions') or {}
        if extensions.pop('KHR_materials_transmission', None) is not None:
            stripped.append(index)
        if not extensions and 'extensions' in material:
            del material['extensions']
    for key in ('extensionsUsed', 'extensionsRequired'):
        if key in document:
            document[key] = [name for name in document[key] if name != 'KHR_materials_transmission']
            if not document[key]:
                del document[key]
    data = rebuild_glb(document, glb.bin)
    with open(target_path, 'wb') as f:
        f.write(data)
    return {'change': 'KHR_materials_transmission removed', 'materials': stripped}


def swap_transmission_terms(source_path, target_path, material_name=None):
    """A GLB copy with E and T exchanged on the transmission route: baseColorTexture <-> emissiveTexture and the base
    color factor's RGB <-> emissiveFactor (alpha, transmission, IOR and every other member unchanged)."""
    with open(source_path, 'rb') as f:
        glb = Glb(f.read())
    document = json.loads(glb.json_bytes.decode('utf-8'))
    swapped = []
    for index, material in enumerate(document.get('materials') or []):
        if material_name is not None and material.get('name') != material_name:
            continue
        pbr = material.setdefault('pbrMetallicRoughness', {})
        base_texture, emissive_texture = pbr.pop('baseColorTexture', None), material.pop('emissiveTexture', None)
        if emissive_texture is not None:
            pbr['baseColorTexture'] = emissive_texture
        if base_texture is not None:
            material['emissiveTexture'] = base_texture
        base = list(pbr.get('baseColorFactor', [1.0, 1.0, 1.0, 1.0]))
        emissive = list(material.get('emissiveFactor', [0.0, 0.0, 0.0]))
        pbr['baseColorFactor'] = emissive[:3] + base[3:4]
        material['emissiveFactor'] = base[:3]
        swapped.append(index)
    data = rebuild_glb(document, glb.bin)
    with open(target_path, 'wb') as f:
        f.write(data)
    return {'change': 'E and T exchanged (baseColorTexture <-> emissiveTexture, base color RGB <-> emissive factor)',
            'materials': swapped}


ANIMATION_CONTROL_PROPERTIES = ('Translation', 'Scale', 'EulerRotation', 'MorphWeights')


def package_has_animations(package_path):
    """Whether the package declares at least one animation channel."""
    package = Package(package_path)
    try:
        return any(clip.get('channels') for clip in package.manifest.get('animations') or [])
    finally:
        package.close()


def first_animation_key(package_path):
    """(clip index, channel index, stream path, values array, parts, width) of the first channel of an
    ANIMATION_CONTROL_PROPERTIES kind with at least two keys, or None. Rotation (amplitude and hemisphere admission),
    visibility (binary) and material properties (matched by content, so a moved key would read as a missing curve, not
    as a value the evaluation check reports) are never mutated."""
    import anim_package
    package = Package(package_path)
    try:
        animations = anim_package.PackageAnimations(package)
        for clip in animations.clips:
            for channel in clip['channels']:
                if channel.property in ANIMATION_CONTROL_PROPERTIES and len(channel.times) >= 2:
                    return clip['index'], channel.index, channel.raw['values']['path'], channel.values.copy(), channel.parts, channel.width
    finally:
        package.close()
    return None


def animation_key_where(clip_index, channel_index, component=0):
    """The comparison element the animation-key control mutates (``compare_d_anim.channel_where``)."""
    return 'animations/%d/channels/%d/%d' % (clip_index, channel_index, component)


def move_animation_key(source_path, target_path, offset=0.5):
    """Add ``offset`` to key 1 (its value part) of component 0 of the channel ``first_animation_key`` picks.

    Returns {clip, channel, key, stream, before, after, expectedWhere} or None when no channel qualifies."""
    found = first_animation_key(source_path)
    if found is None:
        return None
    clip_index, channel_index, stream, values, parts, width = found
    part = 1 if parts == 3 else 0
    before = float(values[1, part, 0])
    values[1, part, 0] = np.float32(before + offset)
    data = np.asarray(values, dtype='<f4').reshape(-1).tobytes()
    os.makedirs(os.path.dirname(os.path.abspath(target_path)), exist_ok=True)
    _rewrite_package(source_path, target_path, entry_edit=lambda name, raw: data if name == stream else raw)
    return {'clip': clip_index, 'channel': channel_index, 'key': 1, 'stream': stream, 'before': before,
            'after': float(values[1, part, 0]), 'expectedWhere': animation_key_where(clip_index, channel_index)}
