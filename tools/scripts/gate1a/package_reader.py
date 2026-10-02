# SPDX-License-Identifier: 0BSD
"""The Blender package (versions 2 to 7) reader: manifest.json, packed streams and image entries.

Layout from Media.Blender's writer documentation (BlenderPackageWriter.cs remarks, BlenderPackageStreams.cs
remarks, BlenderPackageGeometryMapper.cs remarks, BlenderPackageImageMapper.cs) and the manifest
vocabulary the Blender importer and ``tools/blender/readback.py`` use: ``manifest.json`` first, then
``streams/mesh_NNNN_prim_NNNN.<name>.bin`` (tightly packed little-endian float32 or int32 tuples), then
``images/<sha256>.<ext>`` stored uncompressed. This module only parses bytes; it never calls the writer.
"""

import base64
import json
import zipfile

import numpy as np

from gate1a_common import OracleError, json_loads, sha256_bytes

COMPONENT_DTYPES = {
    'Float32': np.dtype('<f4'), 'Float64': np.dtype('<f8'), 'Int32': np.dtype('<i4'), 'UInt32': np.dtype('<u4'),
    'Int16': np.dtype('<i2'), 'UInt16': np.dtype('<u2'), 'Int8': np.dtype('<i1'), 'UInt8': np.dtype('<u1'),
    'Int64': np.dtype('<i8'), 'UInt64': np.dtype('<u8'),
}


class Package:
    def __init__(self, path):
        self.path = path
        self.zip = zipfile.ZipFile(path)
        names = self.zip.namelist()
        if not names or names[0] != 'manifest.json':
            raise OracleError('the first package entry is %r, not manifest.json' % (names[0] if names else None))
        self.entry_names = names
        self.manifest = json_loads(self.zip.read('manifest.json').decode('utf-8'))
        version = self.manifest.get('packageVersion')
        # Version 4 (animation) keeps the static layout of 2 and 3 and adds `animations`; the static hops read the static
        # part and report the clips they do not compare (gate 1b's hops compare animation). Versions 5 (retained extended
        # facing), 6 (typed facing) and 7 (reflected faces) name their layout separately in `evaluationVersion` (2, 3 or
        # 4) and add only declarations the streams do not depend on (Shared 0152179, BlenderPackageManifest and
        # import_model._evaluation_version / _check_manifest / _reflection_rule, restated here, never imported).
        if isinstance(version, bool) or version not in (2, 3, 4, 5, 6, 7):
            raise OracleError('package version %r; the reader knows 2 to 7' % (version,))
        evaluation = self.manifest.get('evaluationVersion')
        if version in (5, 6, 7):
            if isinstance(evaluation, bool) or not isinstance(evaluation, int) or evaluation not in (2, 3, 4):
                raise OracleError('package version %d requires evaluationVersion 2, 3 or 4, not %r' % (version, evaluation))
        elif evaluation is not None:
            raise OracleError('evaluationVersion %r requires package version 5, 6 or 7 (the package is %d)'
                              % (evaluation, version))
        else:
            evaluation = version
        reflected = self.manifest.get('reflectedFaces')
        if reflected is not None and version not in (5, 6, 7):
            raise OracleError('reflectedFaces requires package version 5, 6 or 7 (the package is %d)' % version)
        if version == 7 and (not isinstance(reflected, dict) or
                             reflected.get('rule') not in ('AuthoredFront', 'DrawnWinding')):
            raise OracleError('package version 7 requires a reflectedFaces declaration with rule AuthoredFront or '
                              'DrawnWinding, not %r' % (reflected,))
        self.version = version
        # The static (2), projected-bind (3) or numeric-animation (4) layout the streams follow.
        self.evaluation_version = evaluation
        # The document's reflected-face declaration ({rule, provenance, evidence}), or None; a declaration, never a
        # stream the static hops compare.
        self.reflected_faces = reflected
        self.animation_count = len(self.manifest.get('animations') or [])
        for info in self.zip.infolist():
            if info.compress_type != zipfile.ZIP_STORED:
                raise OracleError('entry %s is compressed (%d); the writer stores every entry' % (info.filename, info.compress_type))

    def entry(self, name):
        try:
            return self.zip.read(name)
        except KeyError:
            raise OracleError('package entry %s is absent' % name) from None

    def stream(self, manifest):
        """A stream manifest row -> array of shape (count, components) in its declared component type."""
        data = self.entry(manifest['path'])
        dtype = COMPONENT_DTYPES.get(manifest['componentType'])
        if dtype is None:
            raise OracleError('stream %s has component type %s' % (manifest['path'], manifest['componentType']))
        components = manifest['components']
        count = manifest['count']
        stride = manifest['byteStride']
        if manifest.get('byteLength') is not None and manifest['byteLength'] != len(data):
            raise OracleError('stream %s declares %d bytes, entry holds %d' % (manifest['path'], manifest['byteLength'], len(data)))
        if stride != dtype.itemsize * components:
            # A source attribute stream keeps its declared stride; slice each tuple.
            rows = np.frombuffer(data, dtype=np.uint8)
            tuples = []
            for i in range(count):
                tuples.append(np.frombuffer(rows[i * stride:i * stride + dtype.itemsize * components].tobytes(), dtype=dtype))
            return np.array(tuples, dtype=dtype).reshape(count, components)
        if len(data) < count * stride:
            raise OracleError('stream %s holds %d bytes for %d tuples of %d' % (manifest['path'], len(data), count, stride))
        return np.frombuffer(data, dtype=dtype, count=count * components).reshape(count, components).copy()

    def primitives(self):
        """(mesh index, primitive index, mesh manifest, primitive manifest) in document order."""
        for mesh_index, mesh in enumerate(self.manifest['meshes']):
            for primitive_index, primitive in enumerate(mesh['primitives']):
                yield mesh_index, primitive_index, mesh, primitive

    def image_payload(self, image):
        """The stored bytes of an image row, or None for a missing packing; verifies the SHA-256 the row states."""
        path = image.get('path')
        if path is None:
            return None
        data = self.entry(path)
        stated = image.get('sha256')
        actual = sha256_bytes(data)
        if stated and stated != actual:
            raise OracleError('image %s states SHA-256 %s but its entry hashes to %s' % (path, stated, actual))
        if image.get('byteLength') is not None and image['byteLength'] != len(data):
            raise OracleError('image %s states %d bytes, entry holds %d' % (path, image['byteLength'], len(data)))
        return data

    def close(self):
        self.zip.close()


def mutate_package(source_path, target_path, mutate):
    """Copy a package, applying ``mutate(name, bytes) -> bytes`` to every entry, keeping order and storage."""
    with zipfile.ZipFile(source_path) as src, zipfile.ZipFile(target_path, 'w', compression=zipfile.ZIP_STORED) as dst:
        for info in src.infolist():
            data = mutate(info.filename, src.read(info.filename))
            out = zipfile.ZipInfo(info.filename, date_time=info.date_time)
            out.compress_type = zipfile.ZIP_STORED
            dst.writestr(out, data)
