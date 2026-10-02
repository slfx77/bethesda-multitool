# SPDX-License-Identifier: 0BSD
"""A mock of the few shader-node APIs ``display_blend_variant.BpyGraph`` calls, plus an evaluator of the recorded graph,
so ``selfcheck_display_blend.py`` can prove, without Blender, that the nodes and links BpyGraph builds compute what the
numeric backend computes. Socket names and indices follow Blender 5.1's (ShaderNodeMath inputs[0..2], ShaderNodeMixShader
inputs 'Fac' / [1] / [2], ShaderNodeClamp 'Value' / 'Min' / 'Max' -> 'Result', and so on); an unknown node type, socket
name or index raises, so a wiring typo fails here instead of in the owner's run. Math is float32, as Blender's.

Sockets also expose the bpy link API (``is_linked``, ``links[0].from_socket``, ``identifier``) and nodes a unique
``name``, so ``display_blend_variant.GraphReader`` reads a mock tree exactly as it reads a Blender one. A socket default
is stored through its declared range, as Blender's RNA stores it: Combine Color inputs are clamped to [0, 1] (their
declared factor range, EMULATED here, not measured), and a Tree made with ``clamp_color_defaults=True`` also stores every
Emission / Transparent BSDF color default clamped to [0, 1] (the hypothesis the 'socket' sentinels measure), so the
self-check can show what the graph readback does when Blender clamps a default."""

import numpy as np

SOCKETS = {  # idname: (inputs as (name, default), outputs)
    'ShaderNodeMath': ([('Value', 0.5), ('Value_001', 0.5), ('Value_002', 0.5)], ['Value']),
    'ShaderNodeClamp': ([('Value', 1.0), ('Min', 0.0), ('Max', 1.0)], ['Result']),
    'ShaderNodeValue': ([], ['Value']),
    'ShaderNodeUVMap': ([], ['UV']),
    'ShaderNodeTexImage': ([('Vector', None)], ['Color', 'Alpha']),
    'ShaderNodeSeparateColor': ([('Color', (0.8, 0.8, 0.8, 1.0))], ['Red', 'Green', 'Blue']),
    'ShaderNodeCombineColor': ([('Red', 0.0), ('Green', 0.0), ('Blue', 0.0)], ['Color']),
    'ShaderNodeEmission': ([('Color', (1.0, 1.0, 1.0, 1.0)), ('Strength', 1.0), ('Weight', 0.0)], ['Emission']),
    'ShaderNodeBsdfTransparent': ([('Color', (1.0, 1.0, 1.0, 1.0)), ('Weight', 0.0)], ['BSDF']),
    'ShaderNodeBsdfPrincipled': ([('Base Color', (0.8, 0.8, 0.8, 1.0)), ('Metallic', 0.0), ('Roughness', 0.5), ('IOR', 1.5),
                                  ('Alpha', 1.0), ('Specular IOR Level', 0.5), ('Coat Weight', 0.0), ('Sheen Weight', 0.0),
                                  ('Transmission Weight', 0.0), ('Subsurface Weight', 0.0), ('Emission Strength', 0.0),
                                  ('Diffuse Roughness', 0.0)], ['BSDF']),
    'ShaderNodeAddShader': ([('Shader', None), ('Shader_001', None)], ['Shader']),
    'ShaderNodeMixShader': ([('Fac', 0.5), ('Shader', None), ('Shader_001', None)], ['Shader']),
    'ShaderNodeOutputMaterial': ([('Surface', None), ('Volume', None), ('Displacement', None)], []),
}


COLOR_DEFAULT_SOCKETS = {('ShaderNodeEmission', 'Color'), ('ShaderNodeBsdfTransparent', 'Color')}
FACTOR_SOCKETS = {('ShaderNodeCombineColor', 'Red'), ('ShaderNodeCombineColor', 'Green'), ('ShaderNodeCombineColor', 'Blue')}


class _Link:
    def __init__(self, from_socket):
        self.from_socket = from_socket


class Socket:
    def __init__(self, node, name, default, is_output, clamp_color_defaults=False):
        self.node = node
        self.name = name
        self.identifier = name
        self.is_output = is_output
        self.link = None
        self._range = None
        if (node.bl_idname, name) in FACTOR_SOCKETS or (clamp_color_defaults and (node.bl_idname, name) in COLOR_DEFAULT_SOCKETS):
            self._range = (0.0, 1.0)
        self._default = default

    @property
    def default_value(self):
        return self._default

    @default_value.setter
    def default_value(self, value):
        if self._range is not None and value is not None:
            lo, hi = self._range
            if isinstance(value, (tuple, list)):
                value = tuple(min(hi, max(lo, float(v))) for v in value[:3]) + tuple(value[3:])
            else:
                value = min(hi, max(lo, float(value)))
        self._default = value

    @property
    def is_linked(self):
        return self.link is not None

    @property
    def links(self):
        return [_Link(self.link)] if self.link is not None else []


class Sockets:
    def __init__(self, sockets):
        self._list = sockets

    def __getitem__(self, key):
        if isinstance(key, int):
            return self._list[key]
        for socket in self._list:
            if socket.name == key:
                return socket
        raise KeyError('no socket %r on %s' % (key, self._list[0].node.bl_idname if self._list else '?'))

    def __contains__(self, key):
        return any(socket.name == key for socket in self._list)


class Node:
    def __init__(self, bl_idname, name='', clamp_color_defaults=False):
        if bl_idname not in SOCKETS:
            raise KeyError('the mock does not know node type %s' % bl_idname)
        self.bl_idname = bl_idname
        self.name = name or bl_idname
        inputs, outputs = SOCKETS[bl_idname]
        self.inputs = Sockets([Socket(self, name, default, False, clamp_color_defaults) for name, default in inputs])
        self.outputs = Sockets([Socket(self, name, 0.0, True) for name in outputs])
        self.operation = 'ADD'
        self.use_clamp = False
        self.clamp_type = 'MINMAX'
        self.mode = 'RGB'
        self.image = None
        self.interpolation = 'Linear'
        self.extension = 'REPEAT'
        self.uv_map = ''
        self.target = 'ALL'


class Nodes:
    def __init__(self, clamp_color_defaults=False):
        self.all = []
        self.created = 0
        self.clamp_color_defaults = clamp_color_defaults

    def new(self, bl_idname):
        self.created += 1
        node = Node(bl_idname, '%s.%04d' % (bl_idname, self.created), self.clamp_color_defaults)
        self.all.append(node)
        return node

    def remove(self, node):
        self.all.remove(node)

    def __iter__(self):
        return iter(list(self.all))


class Links:
    def new(self, from_socket, to_socket):
        if not from_socket.is_output or to_socket.is_output:
            raise ValueError('a link must run from an output to an input')
        to_socket.link = from_socket


class Tree:
    def __init__(self, clamp_color_defaults=False):
        self.nodes = Nodes(clamp_color_defaults)
        self.links = Links()


class Evaluator:
    """Evaluates the recorded graph for one texel: closures as {'E': rgb, 'T': rgb, 'lit': weight}."""

    def __init__(self, texel_linear, alpha):
        self.texel = [np.float32(v) for v in texel_linear]
        self.alpha = np.float32(alpha)

    def input(self, node, key):
        socket = node.inputs[key]
        if socket.link is not None:
            return self.output(socket.link)
        return socket.default_value

    def scalar(self, value):
        if isinstance(value, (tuple, list)):
            raise TypeError('a color reached a scalar input')
        return np.float32(value)

    def color(self, value):
        if isinstance(value, (tuple, list)):
            return [np.float32(v) for v in value[:3]]
        return [np.float32(value)] * 3

    def output(self, socket):
        node, name = socket.node, socket.name
        kind = node.bl_idname
        if kind == 'ShaderNodeMath':
            a, b = self.scalar(self.input(node, 0)), self.scalar(self.input(node, 1))
            op = node.operation
            with np.errstate(all='ignore'):
                r = {'ADD': lambda: a + b, 'SUBTRACT': lambda: a - b, 'MULTIPLY': lambda: a * b,
                     'DIVIDE': lambda: a / b if b != 0 else np.float32(0.0),
                     'POWER': lambda: np.power(a, b) if (a >= 0 or float(b).is_integer()) else np.float32(0.0),
                     'MINIMUM': lambda: min(a, b), 'MAXIMUM': lambda: max(a, b),
                     'LESS_THAN': lambda: np.float32(1.0 if a < b else 0.0)}[op]()
            return min(np.float32(1.0), max(np.float32(0.0), r)) if node.use_clamp else np.float32(r)
        if kind == 'ShaderNodeClamp':
            v, lo, hi = (self.scalar(self.input(node, k)) for k in ('Value', 'Min', 'Max'))
            return min(hi, max(lo, v))
        if kind == 'ShaderNodeValue':
            return np.float32(node.outputs[0].default_value)
        if kind == 'ShaderNodeTexImage':
            if node.inputs['Vector'].link is None or node.inputs['Vector'].link.node.bl_idname != 'ShaderNodeUVMap':
                raise ValueError('the image texture is not fed by the UV map')
            return list(self.texel) if name == 'Color' else self.alpha
        if kind == 'ShaderNodeSeparateColor':
            rgb = self.color(self.input(node, 'Color'))
            return rgb[['Red', 'Green', 'Blue'].index(name)]
        if kind == 'ShaderNodeCombineColor':
            return [self.scalar(self.input(node, k)) for k in ('Red', 'Green', 'Blue')]
        if kind == 'ShaderNodeEmission':
            strength = self.scalar(self.input(node, 'Strength'))
            return {'E': [float(c * strength) for c in self.color(self.input(node, 'Color'))], 'T': [0.0] * 3, 'lit': 0.0}
        if kind == 'ShaderNodeBsdfTransparent':
            return {'E': [0.0] * 3, 'T': [float(c) for c in self.color(self.input(node, 'Color'))], 'lit': 0.0}
        if kind == 'ShaderNodeBsdfPrincipled':
            return {'E': [0.0] * 3, 'T': [0.0] * 3, 'lit': 1.0}
        if kind == 'ShaderNodeAddShader':
            first, second = (node.inputs[k].link for k in (0, 1))
            if first is None or second is None:
                raise ValueError('an Add Shader with an empty input')
            a, b = self.output(first), self.output(second)
            return {'E': [x + y for x, y in zip(a['E'], b['E'])], 'T': [x + y for x, y in zip(a['T'], b['T'])], 'lit': a['lit'] + b['lit']}
        if kind == 'ShaderNodeMixShader':
            f = float(min(1.0, max(0.0, self.scalar(self.input(node, 'Fac')))))
            out = {'E': [0.0] * 3, 'T': [0.0] * 3, 'lit': 0.0}
            for weight, key in ((1.0 - f, 1), (f, 2)):
                link = node.inputs[key].link
                if link is not None:
                    c = self.output(link)
                    out = {'E': [x + weight * y for x, y in zip(out['E'], c['E'])], 'T': [x + weight * y for x, y in zip(out['T'], c['T'])],
                           'lit': out['lit'] + weight * c['lit']}
            return out
        raise KeyError('cannot evaluate %s' % kind)

    def surface(self, tree):
        outputs = [n for n in tree.nodes.all if n.bl_idname == 'ShaderNodeOutputMaterial']
        if len(outputs) != 1 or outputs[0].inputs['Surface'].link is None:
            raise ValueError('expected one Material Output with a Surface link')
        return self.output(outputs[0].inputs['Surface'].link)


# --------------------------------------------------------------------------------------------------------------------
# A fake bpy for display_blend_variant.build_scene: images, materials with node trees, meshes with UVs, objects, lights
# --------------------------------------------------------------------------------------------------------------------


class _Properties(dict):
    """``bl_rna.properties``: membership by property name."""


class _Rna:
    def __init__(self, names):
        self.properties = _Properties({name: True for name in names})


class _ID:
    def __init__(self, name):
        self.name = name
        self.custom = {}

    def __setitem__(self, key, value):
        self.custom[key] = value

    def __getitem__(self, key):
        return self.custom[key]


class _Image(_ID):
    def __init__(self, path):
        super().__init__(path)
        from PIL import Image as PilImage
        if path.lower().endswith('.dds'):
            import struct
            with open(path, 'rb') as f:
                header = f.read(20)
            height, width = struct.unpack('<2I', header[12:20])
        else:
            with PilImage.open(path) as image:
                width, height = image.size
        self.size = [width, height]
        self.colorspace_settings = type('ColorSpace', (), {'name': 'sRGB'})()
        self.alpha_mode = 'STRAIGHT'
        self.filepath = path


class _Material(_ID):
    def __init__(self, name):
        super().__init__(name)
        self.node_tree = Tree()
        self.bl_rna = _Rna(['use_transparent_shadow'])
        self.surface_render_method = 'DITHERED'
        self.use_backface_culling = False


class _Polygon:
    def __init__(self, index, loop_total):
        self.index = index
        self.loop_total = loop_total


class _UVData:
    def __init__(self, loops):
        self.loops = loops
        self.values = None

    def foreach_set(self, name, values):
        if name != 'uv' or len(values) != 2 * self.loops:
            raise ValueError('uv foreach_set with %d values for %d loops' % (len(values), self.loops))
        self.values = list(values)


class _UVLayers:
    def __init__(self, mesh):
        self.mesh = mesh
        self.layers = {}

    def new(self, name='UVMap'):
        layer = type('UVLayer', (), {})()
        layer.name = name
        layer.data = _UVData(sum(p.loop_total for p in self.mesh.polygons))
        self.layers[name] = layer
        return layer


class _Mesh(_ID):
    def __init__(self, name):
        super().__init__(name)
        self.vertices, self.polygons, self.materials = [], [], []
        self.uv_layers = _UVLayers(self)

    def from_pydata(self, vertices, edges, faces):
        if edges:
            raise ValueError('the probe never passes edges')
        self.vertices = [tuple(v) for v in vertices]
        self.polygons = [_Polygon(i, len(face)) for i, face in enumerate(faces)]
        for face in faces:
            if any(not 0 <= v < len(self.vertices) for v in face):
                raise ValueError('a face indexes a missing vertex')

    def update(self):
        pass


class _Object(_ID):
    def __init__(self, name, data):
        super().__init__(name)
        self.data = data
        self.rotation_euler = (0.0, 0.0, 0.0)


class _Light(_ID):
    def __init__(self, name, light_type):
        super().__init__(name)
        self.type = light_type
        self.energy = 10.0
        self.angle = 0.00918
        self.use_shadow = True
        self.specular_factor = 1.0
        self.bl_rna = _Rna(['angle', 'use_shadow', 'specular_factor', 'energy'])


class _Collection:
    def __init__(self, factory):
        self.items = []
        self.factory = factory

    def new(self, name, *args, **kwargs):
        item = self.factory(name, *args, **kwargs)
        self.items.append(item)
        return item


class FakeBpy:
    """``bpy.data`` for build_scene, and a scene whose collection records linked objects."""

    def __init__(self):
        images = _Collection(_Image)
        images.load = lambda path, check_existing=False: images.new(path)
        self.data = type('Data', (), {})()
        self.data.images = images
        self.data.materials = _Collection(_Material)
        self.data.meshes = _Collection(_Mesh)
        self.data.objects = _Collection(_Object)
        self.data.lights = _Collection(lambda name, type='POINT': _Light(name, type))
        self.scene = type('Scene', (), {})()
        linked = []
        self.scene.collection = type('Collection', (), {'objects': type('Objects', (), {'link': staticmethod(linked.append)})()})()
        self.linked = linked
