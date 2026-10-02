# SPDX-License-Identifier: 0BSD
"""An independent GLB 2.0 reader: header, JSON and BIN chunks, accessors, images, extension record.

Written from the glTF 2.0 specification (section 5, "Properties Reference", and the GLB container
specification): a 12-byte header (magic ``glTF``, version 2, total length), then chunks of
``uint32 length, uint32 type, payload``, the first ``JSON`` and the optional second ``BIN``.
Accessor element types, component types and normalization follow the specification's tables. No
glTF library is used; SharpGLTF (the writer's encoder) is never imported.
"""

import json
import struct

import numpy as np

from gate1a_common import OracleError, json_loads

COMPONENT_DTYPES = {
    5120: np.dtype('<i1'), 5121: np.dtype('<u1'), 5122: np.dtype('<i2'),
    5123: np.dtype('<u2'), 5125: np.dtype('<u4'), 5126: np.dtype('<f4'),
}
TYPE_COMPONENTS = {'SCALAR': 1, 'VEC2': 2, 'VEC3': 3, 'VEC4': 4, 'MAT2': 4, 'MAT3': 9, 'MAT4': 16}


class Glb:
    """One parsed GLB. ``json`` is the JSON chunk as parsed; ``bin`` the BIN chunk bytes (or b'')."""

    def __init__(self, data):
        if len(data) < 12:
            raise OracleError('GLB shorter than its 12-byte header')
        magic, version, length = struct.unpack_from('<4sII', data, 0)
        if magic != b'glTF':
            raise OracleError('GLB magic is %r, not glTF' % magic)
        if version != 2:
            raise OracleError('GLB container version %d, not 2' % version)
        if length != len(data):
            raise OracleError('GLB header length %d differs from the file length %d' % (length, len(data)))
        self.length = length
        self.chunks = []
        offset = 12
        while offset < length:
            if offset + 8 > length:
                raise OracleError('GLB chunk header at %d runs past the file' % offset)
            chunk_length, chunk_type = struct.unpack_from('<I4s', data, offset)
            payload = data[offset + 8:offset + 8 + chunk_length]
            if len(payload) != chunk_length:
                raise OracleError('GLB chunk %r at %d is truncated' % (chunk_type, offset))
            if chunk_length % 4 != 0:
                raise OracleError('GLB chunk %r length %d is not 4-byte aligned' % (chunk_type, chunk_length))
            self.chunks.append((chunk_type, offset + 8, chunk_length))
            offset += 8 + chunk_length
        if not self.chunks or self.chunks[0][0] != b'JSON':
            raise OracleError('the first GLB chunk is not JSON')
        json_type, json_offset, json_length = self.chunks[0]
        self.json_bytes = data[json_offset:json_offset + json_length]
        self.json = json_loads(self.json_bytes.decode('utf-8'))
        self.bin = b''
        if len(self.chunks) > 1:
            bin_type, bin_offset, bin_length = self.chunks[1]
            if bin_type != b'BIN\x00':
                raise OracleError('the second GLB chunk is %r, not BIN' % bin_type)
            self.bin = data[bin_offset:bin_offset + bin_length]
        buffers = self.json.get('buffers', [])
        if len(buffers) > 1 or (buffers and 'uri' in buffers[0]):
            raise OracleError('the GLB references external buffers; only the embedded BIN chunk is supported')
        if buffers and buffers[0].get('byteLength', 0) > len(self.bin):
            raise OracleError('buffer 0 declares %d bytes but the BIN chunk holds %d' % (buffers[0]['byteLength'], len(self.bin)))
        self.extensions_used = list(self.json.get('extensionsUsed', []))
        self.extensions_required = list(self.json.get('extensionsRequired', []))
        self.extension_sites = self._collect_extensions()

    @classmethod
    def from_path(cls, path):
        with open(path, 'rb') as f:
            return cls(f.read())

    def _collect_extensions(self):
        """Every ``extensions`` object in the document, as (json path, extension name) pairs."""
        sites = []

        def walk(node, path):
            if isinstance(node, dict):
                for key, value in node.items():
                    if key == 'extensions' and isinstance(value, dict):
                        for name in value:
                            sites.append((path, name))
                    else:
                        walk(value, path + '/' + str(key))
            elif isinstance(node, list):
                for index, value in enumerate(node):
                    walk(value, path + '/' + str(index))

        walk(self.json, '')
        return sites

    def buffer_view_bytes(self, index):
        view = self.json['bufferViews'][index]
        if view.get('buffer', 0) != 0:
            raise OracleError('bufferView %d references buffer %d' % (index, view['buffer']))
        start = view.get('byteOffset', 0)
        end = start + view['byteLength']
        if end > len(self.bin):
            raise OracleError('bufferView %d [%d, %d) runs past the BIN chunk (%d bytes)' % (index, start, end, len(self.bin)))
        return self.bin[start:end], view.get('byteStride')

    def accessor(self, index, normalize=False):
        """The accessor's values as an array of shape (count, components) in its stored dtype.

        Interleaved views are honored through ``byteStride``; sparse accessors and accessors without a
        bufferView (all zeros by the specification) fail loudly, because the writer never emits them
        and silently synthesizing zeros could hide a missing stream. With ``normalize`` a normalized
        integer accessor is converted to float32 by the specification's rule.
        """
        accessor = self.json['accessors'][index]
        if 'sparse' in accessor:
            raise OracleError('accessor %d is sparse; the oracle does not resolve sparse storage' % index)
        if 'bufferView' not in accessor:
            raise OracleError('accessor %d has no bufferView (implicit zeros); the oracle refuses to synthesize it' % index)
        dtype = COMPONENT_DTYPES[accessor['componentType']]
        components = TYPE_COMPONENTS[accessor['type']]
        count = accessor['count']
        element_bytes = dtype.itemsize * components
        data, stride = self.buffer_view_bytes(accessor['bufferView'])
        offset = accessor.get('byteOffset', 0)
        if stride is None or stride == element_bytes:
            needed = offset + count * element_bytes
            if needed > len(data):
                raise OracleError('accessor %d needs %d bytes of its bufferView, which has %d' % (index, needed, len(data)))
            values = np.frombuffer(data, dtype=dtype, count=count * components, offset=offset).reshape(count, components)
        else:
            if stride < element_bytes:
                raise OracleError('accessor %d stride %d is smaller than its element (%d bytes)' % (index, stride, element_bytes))
            needed = offset + (count - 1) * stride + element_bytes if count else 0
            if needed > len(data):
                raise OracleError('accessor %d strided extent %d exceeds its bufferView (%d bytes)' % (index, needed, len(data)))
            rows = np.frombuffer(data, dtype=np.uint8, count=(count - 1) * stride + element_bytes if count else 0,
                                 offset=offset)
            strided = np.lib.stride_tricks.as_strided(rows, shape=(count, element_bytes), strides=(stride, 1)) if count else rows.reshape(0, element_bytes)
            values = np.ascontiguousarray(strided).view(dtype).reshape(count, components)
        values = np.array(values)  # own the memory
        if normalize and accessor.get('normalized') and dtype.kind in 'iu':
            info = np.iinfo(dtype)
            if dtype.kind == 'u':
                return (values.astype(np.float64) / info.max).astype(np.float32)
            return np.maximum(values.astype(np.float64) / info.max, -1.0).astype(np.float32)
        return values

    def image_bytes(self, index):
        image = self.json['images'][index]
        if 'uri' in image:
            raise OracleError('image %d uses a uri; only bufferView images are expected in a GLB' % index)
        data, _ = self.buffer_view_bytes(image['bufferView'])
        return bytes(data), image.get('mimeType')

    def node_matrix(self, index):
        """The node's local transform as 16 floats in glTF column-major order (matrix or composed TRS)."""
        node = self.json['nodes'][index]
        if 'matrix' in node:
            return [float(v) for v in node['matrix']]
        t = node.get('translation', [0, 0, 0])
        r = node.get('rotation', [0, 0, 0, 1])
        s = node.get('scale', [1, 1, 1])
        x, y, z, w = r
        rot = np.array([
            [1 - 2 * (y * y + z * z), 2 * (x * y + z * w), 2 * (x * z - y * w)],
            [2 * (x * y - z * w), 1 - 2 * (x * x + z * z), 2 * (y * z + x * w)],
            [2 * (x * z + y * w), 2 * (y * z - x * w), 1 - 2 * (x * x + y * y)],
        ], dtype=np.float64)
        columns = []
        for column in range(3):
            columns.extend([rot[row][column] * s[column] for row in range(3)] + [0.0])
        columns.extend([t[0], t[1], t[2], 1.0])
        return columns

    def scene_roots(self):
        scene = self.json.get('scene', 0)
        return list(self.json['scenes'][scene].get('nodes', []))

    def primitive_extra(self, primitive, key):
        return (primitive.get('extras') or {}).get(key)

    def texture_image(self, texture_index):
        texture = self.json['textures'][texture_index]
        return texture['source'], texture.get('sampler')
