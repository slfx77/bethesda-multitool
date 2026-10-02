# SPDX-License-Identifier: 0BSD
"""A recording stand-in for the part of ``bpy`` the self-check needs; FOR THE SELF-CHECK ONLY, never for a gate verdict.

With it, ``fake_blender.py`` runs the REAL Shared scripts without Blender:

* ``tools/blender/readback.py``: ``main()`` runs unchanged against this module (its whole dump, its ``--expect-*``
  checks, its exit codes), reading the data a fake import saved;
* ``import_model.py``: its pure functions and its geometry, image, shape-key, root, object-hierarchy, native-state and
  scene-metadata builders (``_make_root``, ``_load_images``, ``_make_meshes`` / ``_build_primitive_mesh``,
  ``_make_objects`` (with the billboard anchor constraint), ``_apply_billboards``, ``_apply_shape_keys``,
  ``_attach_native_states``, ``_apply_scene_settings``, ``_apply_document_metadata``, ``_summary``) run against these
  mocks, with a float32 ``mathutils`` stand-in (``Matrix``, ``Vector``, ``Quaternion``) and a recording
  ``Object.constraints``; only the material node graphs, armature bones and skin weights are emulated in
  ``fake_blender``. A constraint is recorded, never evaluated.

So the hop-D comparison is checked against the importer's own geometry code and readback's own dump layout, not
against a second copy of the harness's rules. The mocks store what the scripts write (``foreach_set``, custom
properties) and return it (``foreach_get``, iteration). Blender behaviour the scripts rely on is reproduced where it
matters to the comparison: a polygon's ``loop_total`` derives from the loop starts, new datablocks and attributes take
unique names, ``color_srgb`` on eight-bit storage stores bytes, custom normals come back normalized, a new shape key
starts from the current coordinates, and a packed image holds its file's bytes.
"""

import array
import os
import pickle
import sys
import types

import numpy as np

VERSION = (5, 1, 0)
VERSION_STRING = '5.1.0 (fake)'
FAKE_BLEND_MAGIC = b'GATE1A-FAKE-BLEND\n'


def _identity():
    return [[1.0 if r == c else 0.0 for c in range(4)] for r in range(4)]


# --------------------------------------------------------------------------------------------------------------------
# mathutils, in float32 like Blender's (enough for import_model's node code; never used for a verdict)
# --------------------------------------------------------------------------------------------------------------------


def _values(value):
    if isinstance(value, Vector):
        return value._v
    return np.asarray([float(x) for x in value], dtype=np.float32)


class Vector:
    def __init__(self, values=(0.0, 0.0, 0.0)):
        self._v = np.asarray([float(x) for x in values], dtype=np.float32)

    def __iter__(self):
        return (float(x) for x in self._v)

    def __len__(self):
        return len(self._v)

    def __getitem__(self, index):
        return float(self._v[index])

    def __neg__(self):
        return Vector(-self._v)

    def __add__(self, other):
        return Vector(self._v + _values(other))

    def __sub__(self, other):
        return Vector(self._v - _values(other))

    @property
    def length_squared(self):
        return float(np.dot(self._v, self._v))

    @property
    def length(self):
        return float(np.sqrt(np.dot(self._v, self._v)))

    def normalize(self):
        """In place, as mathutils (import_model._axis classifies typed billboard directions with it)."""
        length = self.length
        if length > 0.0:
            self._v = (self._v / np.float32(length)).astype(np.float32)

    def dot(self, other):
        return float(np.dot(self._v, _values(other)))


class Quaternion:
    def __init__(self, values=(1.0, 0.0, 0.0, 0.0)):
        self._q = np.asarray([float(x) for x in values], dtype=np.float32)  # (w, x, y, z)

    def __iter__(self):
        return (float(x) for x in self._q)

    def to_matrix(self):
        w, x, y, z = (self._q.astype(np.float64) / max(np.linalg.norm(self._q.astype(np.float64)), 1e-30)).tolist()
        return Matrix(((1 - 2 * (y * y + z * z), 2 * (x * y - w * z), 2 * (x * z + w * y)),
                       (2 * (x * y + w * z), 1 - 2 * (x * x + z * z), 2 * (y * z - w * x)),
                       (2 * (x * z - w * y), 2 * (y * z + w * x), 1 - 2 * (x * x + y * y))))


def _quaternion_from_matrix(m):
    m = np.asarray(m, dtype=np.float64)
    trace = m[0, 0] + m[1, 1] + m[2, 2]
    if trace > 0:
        s = 0.5 / np.sqrt(trace + 1.0)
        q = (0.25 / s, (m[2, 1] - m[1, 2]) * s, (m[0, 2] - m[2, 0]) * s, (m[1, 0] - m[0, 1]) * s)
    elif m[0, 0] > m[1, 1] and m[0, 0] > m[2, 2]:
        s = 2.0 * np.sqrt(1.0 + m[0, 0] - m[1, 1] - m[2, 2])
        q = ((m[2, 1] - m[1, 2]) / s, 0.25 * s, (m[0, 1] + m[1, 0]) / s, (m[0, 2] + m[2, 0]) / s)
    elif m[1, 1] > m[2, 2]:
        s = 2.0 * np.sqrt(1.0 + m[1, 1] - m[0, 0] - m[2, 2])
        q = ((m[0, 2] - m[2, 0]) / s, (m[0, 1] + m[1, 0]) / s, 0.25 * s, (m[1, 2] + m[2, 1]) / s)
    else:
        s = 2.0 * np.sqrt(1.0 + m[2, 2] - m[0, 0] - m[1, 1])
        q = ((m[1, 0] - m[0, 1]) / s, (m[0, 2] + m[2, 0]) / s, (m[1, 2] + m[2, 1]) / s, 0.25 * s)
    q = np.asarray(q)
    return Quaternion(q / max(np.linalg.norm(q), 1e-30))


class Matrix:
    def __init__(self, rows=None):
        rows = np.eye(4) if rows is None else rows
        self._m = np.asarray([[float(x) for x in row] for row in rows], dtype=np.float32)

    @classmethod
    def Identity(cls, size):
        return cls(np.eye(size))

    @classmethod
    def Translation(cls, vector):
        m = np.eye(4)
        m[:3, 3] = list(vector)
        return cls(m)

    @classmethod
    def LocRotScale(cls, translation, rotation, scale):
        m = np.eye(4)
        m[:3, :3] = rotation.to_matrix()._m.astype(np.float64) @ np.diag(list(scale))
        m[:3, 3] = list(translation)
        return cls(m)

    def __getitem__(self, row):
        return self._m[row]

    def __len__(self):
        return len(self._m)

    def __iter__(self):
        return iter(self._m)

    def __matmul__(self, other):
        if isinstance(other, Matrix):
            return Matrix((self._m.astype(np.float64) @ other._m.astype(np.float64)))
        v = _values(other).astype(np.float64)
        if self._m.shape == (4, 4) and len(v) == 3:
            return Vector((self._m.astype(np.float64) @ np.append(v, 1.0))[:3])
        return Vector(self._m.astype(np.float64) @ v)

    def inverted(self):
        m = self._m.astype(np.float64)
        if abs(np.linalg.det(m)) < 1e-30:
            raise ValueError('matrix does not have an inverse')
        return Matrix(np.linalg.inv(m))

    def inverted_safe(self):
        try:
            return self.inverted()
        except ValueError:
            return Matrix.Identity(len(self._m))

    def to_3x3(self):
        return Matrix(self._m[:3, :3])

    @property
    def translation(self):
        return Vector(self._m[:3, 3])

    def decompose(self):
        linear = self._m[:3, :3].astype(np.float64)
        size = np.linalg.norm(linear, axis=0)
        if np.linalg.det(linear) < 0:
            size = -size
        rotation = linear / np.where(size == 0, 1.0, size)
        return Vector(self._m[:3, 3]), _quaternion_from_matrix(rotation), Vector(size)


class CollectionObjects:
    """``scene.collection.objects``: ``link`` records the names (objects live in bpy.data.objects)."""

    def __init__(self):
        self.names = []

    def link(self, obj):
        self.names.append(obj.name)


class IDBlock:
    """Attributes plus ID properties (``block[name]``), as a Blender ID."""

    def __init__(self, **attributes):
        self._props = {}
        self.animation_data = None  # the fake import creates no actions, drivers or NLA tracks
        self.__dict__.update(attributes)

    def as_pointer(self):
        return id(self)

    def __getitem__(self, key):
        return self._props[key]

    def __setitem__(self, key, value):
        # Blender stores an ID property's value, not the Python object: a str subclass (import_model's RawJson)
        # becomes a plain string, a tuple a list.
        if isinstance(value, str):
            value = str(value)
        self._props[key] = list(value) if isinstance(value, tuple) else value

    def __contains__(self, key):
        return key in self._props

    def keys(self):
        return list(self._props.keys())

    def get(self, key, default=None):
        return self._props.get(key, default)


def _fill(buffer, values):
    values = np.asarray(values).reshape(-1)
    if isinstance(buffer, array.array):
        if len(buffer) != len(values):
            raise RuntimeError('foreach_get buffer holds %d values, %d are needed' % (len(buffer), len(values)))
        cast = float if buffer.typecode in 'fd' else int
        buffer[:] = array.array(buffer.typecode, (cast(v) for v in values.tolist()))
    else:
        buffer[...] = values.reshape(buffer.shape)


class Element:
    def __init__(self, owner, index):
        object.__setattr__(self, '_owner', owner)
        object.__setattr__(self, '_index', index)

    def __getattr__(self, name):
        values = self._owner.field(name)
        width = self._owner.width(name)
        if width == 1:
            value = values[self._index]
            return bool(value) if values.dtype == bool else value.item()
        return tuple(values[self._index * width:(self._index + 1) * width].tolist())


class Elements:
    """A bpy_prop_collection of elements with typed fields (``foreach_set`` / ``foreach_get`` / iteration)."""

    WIDTHS = {'co': 3, 'vector': None, 'color': 4, 'color_srgb': 4, 'uv': 2}

    def __init__(self, count=0, defaults=None, widths=None):
        self.count = count
        self.fields = {}
        self.defaults = defaults or {}
        self.widths = widths or {}

    def add(self, count):
        self.count += count

    def __len__(self):
        return self.count

    def __iter__(self):
        return (Element(self, i) for i in range(self.count))

    def width(self, name):
        if name in self.widths:
            return self.widths[name]
        if name in self.fields and self.count:
            return max(1, len(self.fields[name]) // self.count)
        return 1

    def foreach_set(self, name, values):
        data = np.array(values).reshape(-1).copy()
        self.fields[name] = data
        if self.count and name not in self.widths:
            self.widths[name] = max(1, len(data) // self.count)

    def field(self, name):
        if name in self.fields:
            return self.fields[name]
        if name in self.defaults:
            return self.defaults[name](self)
        raise KeyError('field %s was never written' % name)

    def foreach_get(self, name, buffer):
        _fill(buffer, self.field(name))


class Attribute:
    def __init__(self, name, data_type, domain, count, internal=False):
        self.name = name
        self.data_type = data_type
        self.domain = domain
        self.is_internal = internal
        self.is_required = False
        width = {'FLOAT_VECTOR': 3, 'FLOAT_COLOR': 4, 'BYTE_COLOR': 4, 'FLOAT2': 2, 'INT32_2D': 2}.get(data_type, 1)
        self.data = Elements(count, widths={'vector': width, 'color': width, 'color_srgb': width, 'value': width})
        self._bytes = None

    def store_color_srgb(self):
        """Eight-bit storage: the written floats become bytes (round half up, clamped) and read back as byte / 255."""
        if self.data_type == 'BYTE_COLOR' and 'color_srgb' in self.data.fields:
            values = self.data.fields['color_srgb'].astype(np.float64)
            stored = np.clip(np.floor(values * 255.0 + 0.5), 0, 255)
            self.data.fields['color_srgb'] = (stored.astype(np.float32) / np.float32(255.0)).astype(np.float32)


class AttributeGroup:
    def __init__(self, mesh):
        self.mesh = mesh
        self.items = []

    def domain_size(self, domain):
        return {'POINT': len(self.mesh.vertices), 'CORNER': len(self.mesh.loops), 'FACE': len(self.mesh.polygons),
                'EDGE': len(self.mesh.edges)}[domain]

    def new(self, name, data_type, domain):
        existing = {a.name for a in self.items}
        unique = name
        counter = 1
        while unique in existing:
            unique = '%s.%03d' % (name, counter)
            counter += 1
        attribute = Attribute(unique, data_type, domain, self.domain_size(domain))
        self.items.append(attribute)
        if unique == 'custom_normal' and data_type == 'FLOAT_VECTOR' and domain == 'CORNER':
            # Blender 5's own custom-normal storage (import_model.py writes it since Shared da616d4).
            self.mesh.has_custom_normals = True
        return attribute

    def remove(self, attribute):
        self.items.remove(attribute)

    def get(self, name):
        return next((a for a in self.items if a.name == name), None)

    def __iter__(self):
        return iter(list(self.items))

    def __len__(self):
        return len(self.items)


class ColorAttributes:
    def __init__(self, mesh):
        self.mesh = mesh
        self.active_color = None
        self.render_color_index = -1
        self.bl_rna = types.SimpleNamespace(properties={'active_color_index': None, 'render_color_index': None})

    def _colors(self):
        return [a for a in self.mesh.attributes.items if a.data_type in ('BYTE_COLOR', 'FLOAT_COLOR') and a.domain in ('POINT', 'CORNER')]

    def new(self, name, data_type, domain):
        attribute = self.mesh.attributes.new(name, data_type, domain)
        if self.active_color is None:
            self.active_color = attribute
            self.render_color_index = len(self._colors()) - 1
        return attribute

    def __iter__(self):
        return iter(self._colors())

    def __len__(self):
        return len(self._colors())

    @property
    def active_color_index(self):
        colors = self._colors()
        return colors.index(self.active_color) if self.active_color in colors else -1

    @active_color_index.setter
    def active_color_index(self, index):
        self.active_color = self._colors()[index]


class UVLayer:
    def __init__(self, name, attribute, active):
        self.name = name
        self.uv = attribute.data
        self.active = active
        self.active_render = active


class UVLayers:
    def __init__(self, mesh):
        self.mesh = mesh
        self.items = []

    def new(self, name='UVMap', do_init=True):
        attribute = self.mesh.attributes.new(name, 'FLOAT2', 'CORNER')
        layer = UVLayer(attribute.name, attribute, not self.items)
        self.items.append(layer)
        return layer

    def __iter__(self):
        return iter(list(self.items))

    def __len__(self):
        return len(self.items)


def _loop_totals(polygons):
    mesh = polygons.mesh
    starts = polygons.fields.get('loop_start')
    if starts is None:
        return np.zeros(len(polygons), dtype=np.int64)
    ends = np.append(starts[1:], len(mesh.loops))
    return (ends - starts).astype(np.int64)


def _all_false(elements):
    return np.zeros(len(elements), dtype=bool)


def _all_zero(elements):
    return np.zeros(len(elements), dtype=np.int64)


class Polygons(Elements):
    def __init__(self, mesh):
        super().__init__(0, defaults={'loop_total': _loop_totals, 'use_smooth': _all_false, 'material_index': _all_zero})
        self.mesh = mesh


class ShapeKeyBlock:
    def __init__(self, name, data, relative):
        self.name = name
        self.data = data
        self.relative_key = relative or self
        self.value = 0.0
        self.mute = False
        self.slider_min = 0.0
        self.slider_max = 1.0
        self.vertex_group = ''
        self.interpolation = 'KEY_LINEAR'

    def path_from_id(self, prop):
        """As Blender: the block name quoted with BLI_str_escape (backslash and double quote escaped)."""
        escaped = self.name.replace(chr(92), chr(92) * 2).replace('"', chr(92) + '"')
        return 'key_blocks["%s"].%s' % (escaped, prop)


class Key(IDBlock):
    def __init__(self):
        super().__init__(name='Key', key_blocks=[], use_relative=True, reference_key=None, eval_time=0.0)


class Mesh(IDBlock):
    def __init__(self, name):
        super().__init__(name=name, users=1, vertices=Elements(widths={'co': 3}), loops=Elements(),
                         edges=Elements(), materials=[], shape_keys=None, has_custom_normals=False)
        self.polygons = Polygons(self)
        self.attributes = AttributeGroup(self)
        self.color_attributes = ColorAttributes(self)
        self.uv_layers = UVLayers(self)
        self._custom = None

    def update(self, calc_edges=False):
        for attribute in self.attributes.items:
            attribute.store_color_srgb()

    def normals_split_custom_set(self, corner):
        corner = np.asarray(corner, dtype=np.float64).reshape(-1, 3)
        length = np.linalg.norm(corner, axis=1, keepdims=True)
        normalized = np.where(length > 0, corner / np.where(length > 0, length, 1.0), np.array([[0.0, 0.0, 1.0]]))
        self._custom = normalized.astype(np.float32)
        self.has_custom_normals = True

    @property
    def corner_normals(self):
        values = self._custom
        attribute = self.attributes.get('custom_normal')
        if values is None and attribute is not None and attribute.domain == 'CORNER':
            corner = attribute.data.fields['vector'].astype(np.float64).reshape(-1, 3)
            length = np.linalg.norm(corner, axis=1, keepdims=True)
            values = np.where(length > 0, corner / np.where(length > 0, length, 1.0), np.array([[0.0, 0.0, 1.0]])).astype(np.float32)
        if values is None:
            values = np.tile(np.float32([0, 0, 1]), (len(self.loops), 1))
        collection = Elements(len(self.loops), widths={'vector': 3})
        collection.fields['vector'] = np.asarray(values, dtype=np.float32).reshape(-1)
        return collection

    def shape_key_add(self, name='Key', from_mix=True):
        if self.shape_keys is None:
            self.shape_keys = Key()
        key = self.shape_keys
        existing = {block.name for block in key.key_blocks}
        unique, counter = name, 1
        while unique in existing:
            unique = '%s.%03d' % (name, counter)
            counter += 1
        data = Elements(len(self.vertices), widths={'co': 3})
        data.fields['co'] = np.array(self.vertices.field('co'), dtype=np.float32)
        block = ShapeKeyBlock(unique, data, key.reference_key)
        key.key_blocks.append(block)
        if key.reference_key is None:
            key.reference_key = block
            block.relative_key = block
        return block


class Material(IDBlock):
    def __init__(self, name):
        super().__init__(name=name, use_nodes=False, surface_render_method='DITHERED', use_backface_culling=False,
                         use_backface_culling_shadow=False, use_transparent_shadow=False, pass_index=0,
                         diffuse_color=[0.8, 0.8, 0.8, 1.0], node_tree=None)


class Image(IDBlock):
    def __init__(self, name, path=None, data=None):
        super().__init__(name=name, source='FILE', file_format='PNG', filepath_raw=path or '', packed_file=None,
                         colorspace_settings=types.SimpleNamespace(name='sRGB'), alpha_mode='STRAIGHT', size=[0, 0],
                         channels=4, depth=32, is_float=False)
        self._data = data
        if data is not None:
            self.file_format, self.size = _image_format(data)

    def pack(self):
        if self._data is None:
            raise RuntimeError('image %s has no file data to pack' % self.name)
        self.packed_file = types.SimpleNamespace(size=len(self._data), data=bytes(self._data))


def _image_format(data):
    """(file_format, [width, height]) as Blender would report it; DDX and unknown data load with no size."""
    if data[:8] == b'\x89PNG\r\n\x1a\n':
        import struct
        width, height = struct.unpack('>II', data[16:24])
        return 'PNG', [width, height]
    if data[:4] == b'DDS ':
        import struct
        height, width = struct.unpack('<II', data[12:20])
        return 'DDS', [width, height]
    return 'PNG', [0, 0]


class Constraint:
    """One object constraint: name, type, mute, influence and target, plus whatever settings the importer assigns
    (readback.py dumps the settings it lists when present)."""

    def __init__(self, kind):
        self.type, self.name, self.mute, self.influence, self.target, self.space_object = kind, kind.title(), False, 1.0, None, None


class Constraints(list):
    """Object.constraints: ``new(type)`` appends a constraint, as import_model's billboard and anchor code call it."""

    def new(self, kind):
        constraint = Constraint(kind)
        self.append(constraint)
        return constraint


class Object(IDBlock):
    def __init__(self, name, data=None, kind=None):
        super().__init__(name=name, data=data, type=kind or ('MESH' if isinstance(data, Mesh) else 'EMPTY'), parent=None,
                         parent_type='OBJECT', parent_bone='', matrix_local=_identity(), matrix_parent_inverse=_identity(),
                         matrix_world=_identity(), location=[0.0, 0.0, 0.0], rotation_mode='QUATERNION',
                         scale=[1.0, 1.0, 1.0], hide_viewport=False, hide_render=False, hide_select=False,
                         empty_display_type='PLAIN_AXES', users_collection=[], modifiers=[], constraints=Constraints(),
                         vertex_groups=[])

    @property
    def material_slots(self):
        materials = self.data.materials if isinstance(self.data, Mesh) else []
        return [types.SimpleNamespace(material=m) for m in materials]

    def shape_key_add(self, name='Key', from_mix=True):
        return self.data.shape_key_add(name=name, from_mix=from_mix)


class NamedCollection:
    """bpy.data.<kind>: iteration, ``new`` with unique names, ``get``, ``remove``."""

    def __init__(self, factory=None):
        self.items = []
        self.factory = factory

    def unique(self, name):
        existing = {item.name for item in self.items}
        unique, counter = name, 1
        while unique in existing:
            unique = '%s.%03d' % (name, counter)
            counter += 1
        return unique

    def new(self, name, *args, **kwargs):
        item = self.factory(self.unique(name), *args, **kwargs)
        self.items.append(item)
        return item

    def link(self, item):
        item.name = self.unique(item.name)
        self.items.append(item)
        return item

    def get(self, name):
        return next((item for item in self.items if item.name == name), None)

    def remove(self, item):
        self.items.remove(item)

    def __iter__(self):
        return iter(list(self.items))

    def __len__(self):
        return len(self.items)


class Images(NamedCollection):
    def load(self, filepath, check_existing=False):
        with open(filepath, 'rb') as f:
            data = f.read()
        return self.link(Image(os.path.basename(filepath), filepath, data))


class LayerCollection:
    def __init__(self, name):
        self.name = name
        self.exclude = False
        self.hide_viewport = False
        self.holdout = False
        self.indirect_only = False
        self.is_visible = True
        self.children = []


class Scene(IDBlock):
    def __init__(self, name='Scene'):
        super().__init__(name=name,
                         unit_settings=types.SimpleNamespace(system='METRIC', scale_length=1.0, length_unit='METERS',
                                                             system_rotation='DEGREES', mass_unit='KILOGRAMS',
                                                             time_unit='SECONDS', temperature_unit='KELVIN'),
                         view_settings=types.SimpleNamespace(view_transform='AgX', look='None', exposure=0.0, gamma=1.0),
                         display_settings=types.SimpleNamespace(display_device='sRGB'), frame_start=1, frame_end=250,
                         frame_current=1, render=types.SimpleNamespace(fps=24, fps_base=1.0), timeline_markers=[],
                         collection=types.SimpleNamespace(name='Scene Collection', objects=CollectionObjects(), children=[]),
                         view_layers=[types.SimpleNamespace(layer_collection=LayerCollection('Scene Collection'))])
        self._data = None

    @property
    def objects(self):
        return list(self._data.objects) if self._data is not None else []


def _new_object(name, data=None, kind=None):
    return Object(name, data, kind)


class Data:
    """bpy.data."""

    def __init__(self):
        self.objects = NamedCollection(_new_object)
        self.meshes = NamedCollection(Mesh)
        self.materials = NamedCollection(Material)
        self.images = Images(Image)
        self.node_groups = NamedCollection()
        self.collections = NamedCollection()
        self.actions = NamedCollection()
        self.scenes = NamedCollection(Scene)
        scene = self.scenes.new('Scene')
        scene._data = self
        self.filepath = ''

    @property
    def scene(self):
        return self.scenes.items[0]

    @property
    def shape_keys(self):
        """bpy.data.shape_keys: every mesh's Key block, its ``user`` the mesh."""
        keys = []
        for mesh in self.meshes:
            if mesh.shape_keys is not None:
                mesh.shape_keys.user = mesh
                keys.append(mesh.shape_keys)
        return keys


class Ops:
    """bpy.ops with the calls the scripts make: open/save a fake .blend, factory settings, and the glTF importer."""

    def __init__(self, module, gltf_importer=None):
        self._module = module
        self.wm = types.SimpleNamespace(open_mainfile=self._open, read_factory_settings=self._factory,
                                        save_as_mainfile=self._save)
        self.import_scene = types.SimpleNamespace(gltf=lambda filepath, **kwargs: gltf_importer(self._module, filepath))
        self.object = types.SimpleNamespace(select_all=lambda **kwargs: {'FINISHED'}, delete=lambda **kwargs: {'FINISHED'})

    def _open(self, filepath, load_ui=False):
        self._module.data = load_fake_blend(filepath)
        self._module.data.filepath = filepath
        self._module.context.scene = self._module.data.scene
        return {'FINISHED'}

    def _factory(self, use_empty=True):
        self._module.data = Data()
        self._module.context.scene = self._module.data.scene
        return {'FINISHED'}

    def _save(self, filepath, compress=True, check_existing=False, copy=False):
        save_fake_blend(self._module.data, filepath)
        return {'FINISHED'}


def save_fake_blend(data, path):
    with open(path, 'wb') as f:
        f.write(FAKE_BLEND_MAGIC)
        pickle.dump(data, f, protocol=pickle.HIGHEST_PROTOCOL)


def load_fake_blend(path):
    with open(path, 'rb') as f:
        if f.read(len(FAKE_BLEND_MAGIC)) != FAKE_BLEND_MAGIC:
            raise RuntimeError('%s is not a fake .blend written by fake_blender.py' % path)
        data = pickle.load(f)
    data.scene._data = data
    return data


class NodeSocket:
    """bpy.types.NodeSocket, for import_model._is_socket."""


def install(gltf_importer=None):
    """Create the fake ``bpy``, ``mathutils`` and ``addon_utils`` modules in sys.modules and return ``bpy``."""
    bpy = types.ModuleType('bpy')
    bpy.app = types.SimpleNamespace(version=VERSION, version_string=VERSION_STRING)
    bpy.types = types.SimpleNamespace(NodeSocket=NodeSocket, Bone=None)
    bpy.data = Data()
    bpy.context = types.SimpleNamespace(scene=bpy.data.scene)
    bpy.ops = Ops(bpy, gltf_importer)
    mathutils = types.ModuleType('mathutils')
    mathutils.Matrix, mathutils.Quaternion, mathutils.Vector = Matrix, Quaternion, Vector
    addon_utils = types.ModuleType('addon_utils')
    addon_utils.enable = lambda name, default_set=False: types.SimpleNamespace(__name__=name)
    sys.modules['bpy'] = bpy
    sys.modules['mathutils'] = mathutils
    sys.modules['addon_utils'] = addon_utils
    return bpy


def load_module(path, name):
    """Execute a Shared script as a module under the installed fake modules (its top level runs; main does not)."""
    import importlib.util
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module
