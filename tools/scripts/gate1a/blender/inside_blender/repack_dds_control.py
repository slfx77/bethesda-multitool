# SPDX-License-Identifier: 0BSD
"""Hop-D control: runs INSIDE Blender 5.1. This harness never runs it itself; the driver launches Blender with it.

    blender --background --factory-startup --python-exit-code 1 --python repack_dds_control.py -- \\
        --blend <pristine.blend> --image <mt_image_index> --out <control.blend> --result <result.json> --work <directory>

It makes the control design section 5.3 warns about: the packed DDS the importer stored exactly is replaced by the same
pixels re-encoded through ``save()`` (a generated image filled with the decoded pixels, saved as PNG and packed again,
the ``images.new`` plus ``save()`` route of NMT import_package.py that re-encodes), then the .blend is saved under a new
name. The replacement keeps the image's name and every ``mt_*`` property, so only the packed bytes tell it apart; hop D
must report ``images/<index>/packed_sha256``.

Exit codes: 0 written; 4 the image was not found, not packed or not decodable (result written with ``error``); 2 usage
or a Blender older than 5.0; 1 any unhandled error.
"""

import argparse
import array
import hashlib
import json
import os
import sys

SCHEMA = 'gate1a-blender-repack/1'
MINIMUM_BLENDER = (5, 0, 0)


def _write(path, document):
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(document, f, indent=1)


def main():
    import bpy
    if tuple(bpy.app.version) < MINIMUM_BLENDER:
        sys.stderr.write('repack_dds_control: Blender 5.0 or newer is required; running %s\n' % bpy.app.version_string)
        return 2
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    parser = argparse.ArgumentParser(prog='repack_dds_control.py')
    parser.add_argument('--blend', required=True)
    parser.add_argument('--image', required=True, type=int)
    parser.add_argument('--out', required=True)
    parser.add_argument('--result', required=True)
    parser.add_argument('--work', required=True)
    args = parser.parse_args(argv)
    os.makedirs(args.work, exist_ok=True)
    result = {'schema': SCHEMA, 'image': args.image, 'blender': bpy.app.version_string}
    bpy.ops.wm.open_mainfile(filepath=os.path.abspath(args.blend), load_ui=False)
    image = next((i for i in bpy.data.images if i.get('mt_image_index') == args.image), None)
    if image is None or image.packed_file is None:
        result['error'] = 'no packed image with mt_image_index %d' % args.image
        _write(args.result, result)
        return 4
    before = bytes(image.packed_file.data)
    count = len(image.pixels)
    width, height = int(image.size[0]), int(image.size[1])
    if not count or not width or not height:
        result['error'] = 'image %s did not decode (size %s)' % (image.name, [width, height])
        _write(args.result, result)
        return 4
    pixels = array.array('f', [0.0]) * count
    image.pixels.foreach_get(pixels)
    replacement = bpy.data.images.new(image.name + '.repacked', width=width, height=height, alpha=True, float_buffer=False)
    replacement.colorspace_settings.name = image.colorspace_settings.name
    replacement.pixels.foreach_set(pixels)
    path = os.path.join(os.path.abspath(args.work), 'repacked.png')
    replacement.filepath_raw = path
    replacement.file_format = 'PNG'
    try:
        replacement.save()
        method = 'Image.save() to PNG, then pack()'
    except RuntimeError as failure:
        replacement.save_render(filepath=path)
        method = 'Image.save_render() to PNG after save() failed (%s), then pack()' % failure
    replacement.pack()
    replacement.alpha_mode = image.alpha_mode
    for key in image.keys():
        replacement[key] = image[key]
    name = image.name
    image.user_remap(replacement)
    bpy.data.images.remove(image)
    replacement.name = name
    after = bytes(replacement.packed_file.data)
    bpy.ops.wm.save_as_mainfile(filepath=os.path.abspath(args.out), compress=True, check_existing=False)
    result.update(name=name, method=method, beforeSha256=hashlib.sha256(before).hexdigest(), beforeBytes=len(before),
                  afterSha256=hashlib.sha256(after).hexdigest(), afterBytes=len(after), size=[width, height])
    _write(args.result, result)
    sys.stderr.write('repack_dds_control: image %d re-encoded (%d -> %d bytes)\n' % (args.image, len(before), len(after)))
    return 0


if __name__ == '__main__':
    sys.exit(main())
