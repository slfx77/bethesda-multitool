# SPDX-License-Identifier: 0BSD
"""Gate 1b: an independent reader and sampler of glTF 2.0 animations, including KHR_animation_pointer targets.

Written from the glTF 2.0 specification (section 3.11 "Animations" and Appendix C "Animation Sampler Interpolation
Modes") and the KHR_animation_pointer / KHR_node_visibility / KHR_texture_transform extension texts; the accessors are
read by this harness's own ``glb_reader.Glb``. No glTF library is used, and in particular not SharpGLTF, whose
CUBICSPLINE sampler omits the key-interval tangent scaling the specification requires (the writer's own tests hit that
trap; the ``unscaled`` flag below reproduces it only for the gate's control).

Sampling rules implemented here, for an input time ``t`` and keys ``t_0 < ... < t_{n-1}``:

* before ``t_0`` the first key holds and from ``t_{n-1}`` on the last key holds;
* STEP holds key ``k`` on ``[t_k, t_{k+1})``;
* LINEAR interpolates each component, except a rotation, which is spherical: ``a = acos(|d|)``, ``s = sign(d)``,
  ``v = sin(a (1-u)) / sin(a) v_k + s sin(a u) / sin(a) v_{k+1}`` (the shortest path; ``d`` the key dot product);
* CUBICSPLINE stores in-tangent ``a_k``, value ``v_k`` and out-tangent ``b_k`` per key and evaluates
  ``(2u^3 - 3u^2 + 1) v_k + td (u^3 - 2u^2 + u) b_k + (-2u^3 + 3u^2) v_{k+1} + td (u^3 - u^2) a_{k+1}`` with
  ``td = t_{k+1} - t_k``; a rotation is normalized afterwards;
* a normalized integer output is decoded by the accessor rules; a KHR_node_visibility ``visible`` output is an
  UNSIGNED_BYTE where 0 is false and any other value true, sampled with STEP.

Pointer paths resolve to the pose keys ``anim_dump`` uses: ``/nodes/{n}/extensions/KHR_node_visibility/visible``,
``/materials/{m}/pbrMetallicRoughness/baseColorFactor``, ``/materials/{m}/emissiveFactor``,
``/materials/{m}/extensions/KHR_materials_emissive_strength/emissiveStrength``,
``/materials/{m}/extensions/KHR_materials_specular/specularColorFactor`` and
``/materials/{m}/<textureInfo>/extensions/KHR_texture_transform/{offset,scale,rotation}`` (the texture-info paths the
Shared writer names in ModelGltfPropertyBinding.Texture). Any other pointer is reported as not compared.
"""

import bisect
import math
import re

import numpy as np

from gate1a_common import OracleError

F32 = np.float32

PATH_WIDTH = {'translation': 3, 'rotation': 4, 'scale': 3}
TEXTURE_INFO_ROLES = {
    '/pbrMetallicRoughness/baseColorTexture': 'BaseColor',
    '/normalTexture': 'Normal',
    '/occlusionTexture': 'Occlusion',
    '/pbrMetallicRoughness/metallicRoughnessTexture': 'MetallicRoughness',
    '/extensions/KHR_materials_specular/specularTexture': 'Specular',
    '/emissiveTexture': 'Glow',
    '/extensions/KHR_materials_specular/specularColorTexture': 'SpecularColor',
}
MATERIAL_POINTERS = {
    '/pbrMetallicRoughness/baseColorFactor': ('baseColorFactor', 4),
    '/emissiveFactor': ('emissiveFactor', 3),
    '/extensions/KHR_materials_emissive_strength/emissiveStrength': ('emissiveStrength', 1),
    '/extensions/KHR_materials_specular/specularColorFactor': ('specularColorFactor', 3),
}
POINTER_NODE_VISIBILITY = re.compile(r'^/nodes/(\d+)/extensions/KHR_node_visibility/visible$')
POINTER_MATERIAL = re.compile(r'^/materials/(\d+)(/.*)$')


def parse_pointer(pointer):
    """A pointer string as ('visibility', node) / ('material-factor', material, name, width) /
    ('texture-transform', material, role, member, width), or ('unknown', pointer)."""
    match = POINTER_NODE_VISIBILITY.match(pointer)
    if match:
        return ('visibility', int(match.group(1)))
    match = POINTER_MATERIAL.match(pointer)
    if match:
        material = int(match.group(1))
        rest = match.group(2)
        if rest in MATERIAL_POINTERS:
            name, width = MATERIAL_POINTERS[rest]
            return ('material-factor', material, name, width)
        for prefix, role in TEXTURE_INFO_ROLES.items():
            for member, width in (('offset', 2), ('scale', 2), ('rotation', 1)):
                if rest == prefix + '/extensions/KHR_texture_transform/' + member:
                    return ('texture-transform', material, role, member, width)
    return ('unknown', pointer)


class GlbChannel:
    """One animation channel with its sampler's keys, read from the accessors."""

    def __init__(self, glb, animation_index, animation, channel_index, channel):
        self.animation_index = animation_index
        self.animation_name = animation.get('name')
        self.index = channel_index
        sampler_index = channel['sampler']
        sampler = animation['samplers'][sampler_index]
        self.sampler_index = sampler_index
        self.input_accessor = sampler['input']
        self.output_accessor = sampler['output']
        self.interpolation = sampler.get('interpolation', 'LINEAR')
        target = channel['target']
        path = target.get('path')
        if path == 'pointer':
            pointer = ((target.get('extensions') or {}).get('KHR_animation_pointer') or {}).get('pointer')
            if pointer is None:
                raise OracleError('channel %d of animation %d targets "pointer" without a KHR_animation_pointer pointer'
                                  % (channel_index, animation_index))
            self.pointer = pointer
            self.target = parse_pointer(pointer)
            self.node = None
            self.path = 'pointer'
        else:
            self.pointer = None
            self.node = target.get('node')
            self.path = path
            self.target = ('node', self.node, path)
        times = glb.accessor(self.input_accessor).reshape(-1)
        if times.dtype != np.float32:
            raise OracleError('animation %d sampler %d input is not FLOAT' % (animation_index, sampler_index))
        self.times = np.ascontiguousarray(times, dtype=F32)
        output_meta = glb.json['accessors'][self.output_accessor]
        self.output_component_type = output_meta['componentType']
        self.output_normalized = bool(output_meta.get('normalized'))
        raw = glb.accessor(self.output_accessor, normalize=True)
        self.raw_output = glb.accessor(self.output_accessor)
        flat = np.asarray(raw, dtype=np.float64).reshape(-1)
        keys = len(self.times)
        parts = 3 if self.interpolation == 'CUBICSPLINE' else 1
        if keys == 0 or flat.size % (keys * parts):
            raise OracleError('animation %d channel %d: %d output components do not divide into %d keys x %d parts'
                              % (animation_index, channel_index, flat.size, keys, parts))
        self.parts = parts
        self.width = flat.size // (keys * parts)
        self.values = flat.reshape(keys, parts, self.width)
        self.is_rotation = self.path == 'rotation'
        self.is_boolean = self.target[0] == 'visibility'
        self.label = 'animations/%d (%s)/channels/%d (%s)' % (animation_index, self.animation_name, channel_index,
                                                            self.describe_target())

    def describe_target(self):
        if self.pointer is not None:
            return 'pointer %s' % self.pointer
        return 'node %d %s' % (self.node, self.path)

    def value_part(self, key):
        return self.values[key, 1 if self.parts == 3 else 0]

    def segment(self, t):
        """(k, u): the key interval holding ``t`` and the fraction in it; k = -1 before the first key, n - 1 at or after
        the last."""
        n = len(self.times)
        k = bisect.bisect_right(self.times.tolist(), float(t)) - 1
        if k < 0:
            return -1, 0.0
        if k >= n - 1:
            return n - 1, 0.0
        t0 = float(self.times[k])
        t1 = float(self.times[k + 1])
        return k, (float(t) - t0) / (t1 - t0)

    def sample(self, t, unscaled=False):
        """The channel value at input time ``t`` as a float64 vector (a unit quaternion for a rotation). ``unscaled``
        evaluates CUBICSPLINE with the tangents NOT multiplied by the key interval: the SharpGLTF trap, used only to
        prove the tangent scaling matters."""
        k, u = self.segment(t)
        n = len(self.times)
        if k < 0:
            return self._finish(self.value_part(0))
        if k >= n - 1 or self.interpolation == 'STEP':
            return self._finish(self.value_part(k))
        if self.interpolation == 'LINEAR':
            v0 = self.value_part(k)
            v1 = self.value_part(k + 1)
            if self.is_rotation:
                return gltf_slerp(v0, v1, u)
            return (1 - u) * v0 + u * v1
        if self.interpolation != 'CUBICSPLINE':
            raise OracleError('%s: unknown sampler interpolation %r' % (self.label, self.interpolation))
        td = 1.0 if unscaled else float(self.times[k + 1]) - float(self.times[k])
        v0 = self.values[k, 1]
        b0 = self.values[k, 2]
        v1 = self.values[k + 1, 1]
        a1 = self.values[k + 1, 0]
        u2 = u * u
        u3 = u2 * u
        value = (2 * u3 - 3 * u2 + 1) * v0 + td * (u3 - 2 * u2 + u) * b0 + (-2 * u3 + 3 * u2) * v1 + td * (u3 - u2) * a1
        return self._finish(value)

    def _finish(self, value):
        value = np.asarray(value, dtype=np.float64)
        if self.is_rotation:
            norm = math.sqrt(float(np.dot(value, value)))
            if norm == 0 or not math.isfinite(norm):
                raise OracleError('%s: a sampled rotation has no direction' % self.label)
            return value / norm
        return value


def gltf_slerp(a, b, u):
    """The specification's spherical interpolation of two unit quaternions (shortest path, Appendix C)."""
    a = np.asarray(a, dtype=np.float64)
    b = np.asarray(b, dtype=np.float64)
    d = float(np.dot(a, b))
    s = -1.0 if d < 0 else 1.0
    angle = math.acos(min(1.0, abs(d)))
    sine = math.sin(angle)
    if sine < 1e-12:
        value = (1 - u) * a + s * u * b
    else:
        value = math.sin(angle * (1 - u)) / sine * a + s * math.sin(angle * u) / sine * b
    norm = math.sqrt(float(np.dot(value, value)))
    return value / norm


def plain_nlerp(a, b, u):
    """Component-linear interpolation, then normalization, with the signs as stored: what a component-wise player does."""
    value = (1 - u) * np.asarray(a, dtype=np.float64) + u * np.asarray(b, dtype=np.float64)
    norm = math.sqrt(float(np.dot(value, value)))
    return value / norm if norm else value


class GlbAnimations:
    """Every animation of a GLB, its channels and the writer's clip metadata (``multitoolAnimationMetadata``)."""

    def __init__(self, glb):
        self.glb = glb
        self.animations = []
        for ai, animation in enumerate(glb.json.get('animations') or []):
            channels = [GlbChannel(glb, ai, animation, ci, channel) for ci, channel in enumerate(animation.get('channels') or [])]
            self.animations.append({'index': ai, 'name': animation.get('name'), 'channels': channels})
        self.metadata = ((glb.json.get('extras') or {}).get('multitoolAnimationMetadata') or {})
        self.metadata_clips = list(self.metadata.get('clips') or [])

    def clip_metadata(self, output_index):
        for clip in self.metadata_clips:
            if clip.get('outputAnimation') == output_index:
                return clip
        return None

    def node_rest(self, node_index, path):
        """The GLB node's rest value for a TRS path or its morph weights (node weights, else the mesh's, else zeros)."""
        node = self.glb.json['nodes'][node_index]
        if path == 'translation':
            return np.asarray(node.get('translation', [0, 0, 0]), dtype=np.float64)
        if path == 'rotation':
            return np.asarray(node.get('rotation', [0, 0, 0, 1]), dtype=np.float64)
        if path == 'scale':
            return np.asarray(node.get('scale', [1, 1, 1]), dtype=np.float64)
        if path == 'weights':
            if node.get('weights') is not None:
                return np.asarray(node['weights'], dtype=np.float64)
            mesh = self.glb.json['meshes'][node['mesh']] if node.get('mesh') is not None else None
            if mesh is None:
                return np.zeros(0)
            if mesh.get('weights') is not None:
                return np.asarray(mesh['weights'], dtype=np.float64)
            return np.zeros(len((mesh['primitives'][0].get('targets') or [])))
        raise OracleError('unknown node path %r' % path)
