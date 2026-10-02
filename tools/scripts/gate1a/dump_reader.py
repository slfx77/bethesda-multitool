# SPDX-License-Identifier: 0BSD
"""Reader for the exact model-document JSON dump (schema 1, ``shared/Multitool.Shared/docs/model-dump-json.md``).

The envelope is ``{"schemaVersion": 1, "document": {...}}``. Vectors are ``[x, y, z]`` lists, quaternions
``[x, y, z, w]``, matrices sixteen row-major numbers. Every float32 the writer held is printed as its
shortest round-trip decimal, so parsing the decimal as a double and rounding to float32 recovers the exact
stored bits (checked by ``self_check_float_roundtrip``). Attribute streams carry their raw bytes in base64
with the declared component type, component count, stride and count; image payloads carry their complete
bytes in base64 with a SHA-256 that this reader verifies.
"""

import base64

import numpy as np

from gate1a_common import OracleError, load_json, sha256_bytes

COMPONENT_DTYPES = {
    'Float32': np.dtype('<f4'), 'Float64': np.dtype('<f8'), 'Int32': np.dtype('<i4'), 'UInt32': np.dtype('<u4'),
    'Int16': np.dtype('<i2'), 'UInt16': np.dtype('<u2'), 'Int8': np.dtype('<i1'), 'UInt8': np.dtype('<u1'),
    'Int64': np.dtype('<i8'), 'UInt64': np.dtype('<u8'),
}


def _f32(values, shape):
    return np.asarray(values, dtype=np.float64).astype(np.float32).reshape(shape)


class DumpPrimitive:
    def __init__(self, raw, mesh_index, primitive_index):
        self.raw = raw
        self.mesh_index = mesh_index
        self.primitive_index = primitive_index
        self.name = raw.get('name')
        vertices = raw['vertices']
        n = len(vertices)
        self.vertex_count = n
        self.positions = _f32([v['position'] for v in vertices], (n, 3))
        self.normals = _f32([v['normal'] for v in vertices], (n, 3))
        self.colors = _f32([v['color'] for v in vertices], (n, 4))
        self.uv0 = _f32([v['texCoord'] for v in vertices], (n, 2))
        self.indices = np.asarray(raw['indices'], dtype=np.int64)
        self.material_index = raw.get('materialIndex')
        self.color_encoding = raw.get('colorEncoding')
        self.normal_mode = raw.get('normalMode')
        self.purpose = raw.get('purpose')
        tangents = raw.get('tangents')
        self.tangents = _f32(tangents['values'], (len(tangents['values']), 4)) if tangents else None
        self.additional_uvs = [_f32(s['values'], (len(s['values']), 2)) for s in raw.get('additionalTextureCoordinates') or []]
        skin = raw.get('skinInfluences')
        if skin:
            self.influences_per_vertex = skin['influencesPerVertex']
            self.joint_indices = np.asarray(skin['jointIndices'], dtype=np.int64)
            self.weights = _f32(skin['weights'], (len(skin['weights']),))
        else:
            self.influences_per_vertex = 0
            self.joint_indices = None
            self.weights = None
        self.morph_targets = []
        for target in raw.get('morphTargets') or []:
            self.morph_targets.append({
                'name': target.get('name'),
                'positionDeltas': _f32(target['positionDeltas'], (len(target['positionDeltas']), 3)),
                'absolutePositions': _f32(target['absolutePositions'], (len(target['absolutePositions']), 3)) if target.get('absolutePositions') is not None else None,
                'normalDeltas': _f32(target['normalDeltas'], (len(target['normalDeltas']), 3)) if target.get('normalDeltas') is not None else None,
                'tangentDeltas': _f32(target['tangentDeltas'], (len(target['tangentDeltas']), 3)) if target.get('tangentDeltas') is not None else None,
            })
        faces = raw.get('faces')
        self.faces = None
        if faces:
            self.faces = {'faceSizes': np.asarray(faces['faceSizes'], dtype=np.int64),
                          'cornerIndices': np.asarray(faces['cornerIndices'], dtype=np.int64),
                          'faceCount': faces.get('faceCount'), 'cornerCount': faces.get('cornerCount')}
        points = raw.get('pointIndices')
        self.point_indices = None
        if points:
            self.point_indices = {'values': np.asarray(points['values'], dtype=np.int64), 'pointCount': points.get('pointCount')}
        self.attributes = []
        for stream in raw.get('attributes') or []:
            data = base64.b64decode(stream['data']) if stream.get('data') is not None else b''
            self.attributes.append({
                'name': stream.get('name'), 'semantic': stream.get('semantic'), 'domain': stream.get('domain'),
                'componentType': stream.get('componentType'), 'components': stream.get('components'),
                'count': stream.get('count'), 'byteStride': stream.get('byteStride'), 'normalized': stream.get('normalized'),
                'colorSpace': stream.get('colorSpace'), 'colorSpaceProvenance': stream.get('colorSpaceProvenance'),
                'bytes': data,
            })
        self.primary_color_attribute_index = raw.get('primaryColorAttributeIndex')

    def attribute_tuples(self, index):
        """The tuples of attribute ``index`` as an array (count, components) in the declared component type."""
        stream = self.attributes[index]
        dtype = COMPONENT_DTYPES[stream['componentType']]
        count, components, stride = stream['count'], stream['components'], stream['byteStride']
        data = stream['bytes']
        if stride == dtype.itemsize * components:
            return np.frombuffer(data, dtype=dtype, count=count * components).reshape(count, components)
        tuples = [np.frombuffer(data[i * stride:i * stride + dtype.itemsize * components], dtype=dtype) for i in range(count)]
        return np.array(tuples, dtype=dtype).reshape(count, components)


class DumpImage:
    def __init__(self, raw, index):
        self.raw = raw
        self.index = index
        self.name = raw.get('name')
        source = raw.get('source')
        self.source = source
        self.legacy_content = base64.b64decode(raw['legacyContent']) if raw.get('legacyContent') else b''
        self.descriptor = source.get('descriptor') if source else None
        self.original = self._payload(source.get('original')) if source else None
        self.standard = None
        self.standard_descriptor = None
        if source and source.get('standardPayload'):
            self.standard = self._payload(source['standardPayload']['payload'])
            self.standard_descriptor = source['standardPayload'].get('descriptor')
        self.derivation = source.get('derivation') if source else None
        self.derivation_payload = self._payload(self.derivation['payload']) if self.derivation and self.derivation.get('payload') else None
        self.display_transforms = (source.get('displayTransforms') or []) if source else []
        self.origin = source.get('origin') if source else None
        self.missing_reason = source.get('missingReason') if source else None

    @staticmethod
    def _payload(raw):
        if raw is None:
            return None
        content = base64.b64decode(raw['content']) if raw.get('content') is not None else b''
        if raw.get('byteLength') is not None and raw['byteLength'] != len(content):
            raise OracleError('image payload declares %d bytes, base64 holds %d' % (raw['byteLength'], len(content)))
        digest = sha256_bytes(content)
        if raw.get('sha256') and raw['sha256'] != digest:
            raise OracleError('image payload states SHA-256 %s but hashes to %s' % (raw['sha256'], digest))
        return {'container': raw.get('container'), 'bytes': content, 'sha256': digest}

    def writer_selected_payload(self):
        """The payload ModelGltfImages.PlanCore selects, and its selection label.

        Reproduced from the writer's declared rule: a Derived image uses its derivation payload; otherwise the
        original, replaced by a PNG or DDS StandardPayload when the original is not PNG, replaced by the
        recovery (derivation) payload when neither original nor standard is PNG/DDS/TGA; a legacy image uses
        its legacy PNG bytes.
        """
        if self.source is None:
            return {'container': 'PNG', 'bytes': self.legacy_content, 'sha256': sha256_bytes(self.legacy_content)}, 'legacy'
        if self.origin == 'Derived':
            return self.derivation_payload, 'derived'
        payload, selection = self.original, 'original'
        if payload is None:
            return None, 'missing'
        if not _is(payload['container'], 'png') and self.standard is not None and \
                (_is(self.standard['container'], 'png') or _is(self.standard['container'], 'dds')):
            payload, selection = self.standard, 'standard'
        if not _is(payload['container'], 'png') and not _is(payload['container'], 'dds') and not _is(payload['container'], 'tga') \
                and self.derivation_payload is not None:
            payload, selection = self.derivation_payload, 'recovery'
        return payload, selection


def _is(container, fmt):
    c = (container or '').lower()
    return c == fmt or c == 'image/' + fmt or (fmt == 'dds' and c == 'image/vnd-ms.dds') or (fmt == 'tga' and c == 'image/x-tga')


class Dump:
    def __init__(self, path):
        envelope = load_json(path)
        if envelope.get('schemaVersion') != 1:
            raise OracleError('dump schemaVersion %r, expected 1' % envelope.get('schemaVersion'))
        self.path = path
        self.document = envelope['document']
        d = self.document
        self.name = d.get('name')
        self.source_format = d.get('sourceFormat')
        self.source_provenance = d.get('sourceProvenance')
        self.units = d.get('units')
        self.basis = d.get('sourceBasis')
        self.nodes = d.get('nodes') or []
        self.scenes = d.get('scenes') or []
        self.default_scene_index = d.get('defaultSceneIndex', 0)
        self.materials = d.get('materials') or []
        self.samplers = d.get('samplers') or []
        self.skins = d.get('skins') or []
        self.animations = d.get('animations') or []
        self.meshes = []
        for mesh_index, mesh in enumerate(d.get('meshes') or []):
            primitives = [DumpPrimitive(p, mesh_index, i) for i, p in enumerate(mesh.get('primitives') or [])]
            self.meshes.append({'name': mesh.get('name'), 'primitives': primitives, 'morphWeights': mesh.get('morphWeights'), 'raw': mesh})
        self.images = [DumpImage(image, i) for i, image in enumerate(d.get('images') or [])]

    def primitive_count(self):
        return sum(len(m['primitives']) for m in self.meshes)


def self_check_float_roundtrip(dump):
    """Every vertex component of the dump re-serializes to the same float32 through double: a reader sanity check."""
    for mesh in dump.meshes:
        for primitive in mesh['primitives']:
            for vertex in primitive.raw['vertices'][:64]:
                for component in vertex['position'] + vertex['normal'] + vertex['color'] + vertex['texCoord']:
                    value = np.float32(component)
                    if float(value) != float(np.float32(repr(float(value)))):
                        return False
    return True
