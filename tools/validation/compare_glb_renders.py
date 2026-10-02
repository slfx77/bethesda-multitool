"""Compare two GLB exports in Blender with identical evaluated-world framing.

Run with ordinary Python (Blender 5.1 required):
    python tools/validation/compare_glb_renders.py --blender <blender.exe> \
        --native <native.glb> --shared <shared.glb> --output <capture-directory>

This measures third-party export rendering, not Bethesda's native D3D renderer.
Successful execution means valid captures and measurements, not automatic parity
acceptance. Source assets are read in place; imported materials are never edited.
"""

import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import subprocess
import sys


VIEWS = ((0, 0), (35, 15), (85, 5), (35, 55))
THREADS = 4
SEED = 137


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def load_scene(path):
    import bpy

    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(
        filepath=str(path), import_shading="NORMALS", merge_vertices=False,
        bone_heuristic="BLENDER", guess_original_bind_pose=True,
        disable_bone_shape=True,
    )
    bpy.context.scene.frame_set(0)
    bpy.context.view_layer.update()


def evaluated_bounds():
    import bpy
    import numpy as np

    graph = bpy.context.evaluated_depsgraph_get()
    graph.update()
    low, high = np.full(3, np.inf), np.full(3, -np.inf)
    mesh_count = vertex_count = 0
    for obj in bpy.context.scene.objects:
        if obj.type != "MESH" or obj.hide_render:
            continue
        evaluated = obj.evaluated_get(graph)
        mesh = evaluated.to_mesh()
        try:
            if not mesh.vertices or not mesh.polygons:
                continue
            positions = np.empty(len(mesh.vertices) * 3, dtype=np.float32)
            mesh.vertices.foreach_get("co", positions)
            matrix = np.asarray(evaluated.matrix_world, dtype=np.float64)
            world = positions.reshape((-1, 3)) @ matrix[:3, :3].T + matrix[:3, 3]
            if not np.isfinite(world).all():
                raise ValueError("Non-finite evaluated world vertex")
            low, high = np.minimum(low, world.min(axis=0)), np.maximum(high, world.max(axis=0))
            mesh_count += 1
            vertex_count += len(mesh.vertices)
        finally:
            evaluated.to_mesh_clear()
    if not vertex_count or not np.isfinite(low).all() or max(high - low) <= 1e-8:
        raise ValueError("No drawable evaluated geometry; refusing an empty frame")
    return {"minimum": low.tolist(), "maximum": high.tolist(),
            "meshCount": mesh_count, "evaluatedVertexCount": vertex_count}


def setup_scene(size, samples):
    import bpy

    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.device = "CPU"
    scene.cycles.samples = samples
    scene.cycles.seed = SEED
    scene.cycles.use_animated_seed = False
    scene.cycles.use_adaptive_sampling = False
    scene.cycles.use_denoising = False
    scene.render.threads_mode = "FIXED"
    scene.render.threads = THREADS
    scene.render.resolution_x = scene.render.resolution_y = size
    scene.render.resolution_percentage = 100
    scene.render.film_transparent = True
    scene.render.image_settings.file_format = "PNG"
    scene.render.image_settings.color_mode = "RGBA"
    scene.render.image_settings.color_depth = "16"
    scene.view_settings.view_transform = "Standard"
    scene.view_settings.look = "None"
    scene.view_settings.exposure = 0
    scene.view_settings.gamma = 1
    scene.world = bpy.data.worlds.new("comparison_world")
    scene.world.use_nodes = True
    background = scene.world.node_tree.nodes.get("Background")
    background.inputs["Color"].default_value = (0.18, 0.18, 0.18, 1)
    background.inputs["Strength"].default_value = 0.5
    for name, energy, rotation in (
        ("key", 3.0, (0.9, 0.0, -0.4)),
        ("fill", 1.0, (-0.5, 0.8, 2.3)),
    ):
        data = bpy.data.lights.new(name, "SUN")
        data.energy = energy
        data.angle = 0.08
        light = bpy.data.objects.new(name, data)
        scene.collection.objects.link(light)
        light.rotation_euler = rotation
    return scene


def camera_for_view(scene, bounds, azimuth, elevation):
    import bpy
    from mathutils import Vector

    low, high = Vector(bounds["minimum"]), Vector(bounds["maximum"])
    center = (low + high) / 2
    extent = max(high - low)
    radius = extent * 2.4
    azimuth, elevation = math.radians(azimuth), math.radians(elevation)
    offset = Vector((math.sin(azimuth) * math.cos(elevation),
                     -math.cos(azimuth) * math.cos(elevation), math.sin(elevation))) * radius
    data = bpy.data.cameras.new("comparison_camera")
    data.type = "PERSP"
    data.lens = 50
    data.sensor_width = 36
    data.clip_start = max(extent * 1e-5, 1e-6)
    data.clip_end = radius * 8
    camera = bpy.data.objects.new("comparison_camera", data)
    scene.collection.objects.link(camera)
    camera.location = center + offset
    camera.rotation_euler = (center - camera.location).to_track_quat("-Z", "Y").to_euler()
    scene.camera = camera
    bpy.context.view_layer.update()
    return {"worldMatrix": [list(row) for row in camera.matrix_world],
            "lensMm": data.lens, "clipStart": data.clip_start, "clipEnd": data.clip_end}


def read_pixels(path, size):
    import bpy
    import numpy as np

    image = bpy.data.images.load(str(path), check_existing=False)
    try:
        if tuple(image.size) != (size, size):
            raise ValueError(f"Unexpected capture dimensions: {path}")
        pixels = np.empty(size * size * 4, dtype=np.float32)
        image.pixels.foreach_get(pixels)
        pixels = pixels.reshape((size, size, 4))
        if not np.isfinite(pixels).all():
            raise ValueError(f"Non-finite rendered pixels: {path}")
        mask = pixels[:, :, 3] > 0.01
        minimum = max(64, int(size * size * 0.0005))
        if int(mask.sum()) < minimum:
            raise ValueError(f"Empty/tiny capture: {path}; fewer than {minimum} visible pixels")
        return pixels.copy()
    finally:
        bpy.data.images.remove(image)


def compare_pixels(native, shared):
    import numpy as np

    masks = [pixels[:, :, 3] > 0.01 for pixels in (native, shared)]
    union = masks[0] | masks[1]
    # PNGs are decoded to scene-linear float values by Blender. Composite in
    # linear light onto a fixed gray so transparent RGB cannot dominate error.
    composites = [pixels[:, :, :3] * pixels[:, :, 3:] + 0.18 * (1 - pixels[:, :, 3:])
                  for pixels in (native, shared)]
    error = composites[0] - composites[1]
    alpha_error = native[:, :, 3] - shared[:, :, 3]
    return {
        "foregroundPixels": {"native": int(masks[0].sum()), "shared": int(masks[1].sum())},
        "foregroundMaskIoU": float((masks[0] & masks[1]).sum() / union.sum()),
        "compositedLinearRgbRmse": float(np.sqrt(np.mean(error ** 2))),
        "foregroundLinearRgbRmse": float(np.sqrt(np.mean(error[union] ** 2))),
        "compositedLinearRgbMaxError": float(np.abs(error).max()),
        "alphaRmse": float(np.sqrt(np.mean(alpha_error ** 2))),
        "pixelsOverOne255LinearRgb": int((np.abs(error).max(axis=2) > 1 / 255).sum()),
    }


def worker(args):
    import bpy

    sources = {"native": args.native.resolve(), "shared": args.shared.resolve()}
    hashes = {kind: digest(path) for kind, path in sources.items()}
    bounds = {}
    for kind, path in sources.items():
        load_scene(path)
        bounds[kind] = evaluated_bounds()
    union = {"minimum": [min(bounds[k]["minimum"][i] for k in sources) for i in range(3)],
             "maximum": [max(bounds[k]["maximum"][i] for k in sources) for i in range(3)]}
    captures, camera_settings = {}, {}
    args.output.mkdir(parents=True, exist_ok=True)
    for kind, path in sources.items():
        load_scene(path)
        scene = setup_scene(args.size, args.samples)
        captures[kind], camera_settings[kind] = [], []
        for index, (azimuth, elevation) in enumerate(VIEWS):
            camera_settings[kind].append(camera_for_view(scene, union, azimuth, elevation))
            capture = args.output / f"{kind}.view{index}.png"
            scene.render.filepath = str(capture.resolve())
            bpy.ops.render.render(write_still=True)
            captures[kind].append(capture)
    if camera_settings["native"] != camera_settings["shared"]:
        raise ValueError("Paired cameras differ")
    comparisons = []
    for index, angles in enumerate(VIEWS):
        pixels = {kind: read_pixels(captures[kind][index], args.size) for kind in sources}
        comparisons.append({"view": index, "azimuthElevationDegrees": angles,
                            "camera": camera_settings["native"][index],
                            "captures": {kind: str(captures[kind][index].resolve()) for kind in sources},
                            **compare_pixels(pixels["native"], pixels["shared"])})
    if hashes != {kind: digest(path) for kind, path in sources.items()}:
        raise ValueError("Input GLB changed during comparison")
    report = {"schemaVersion": 1, "scope": "Blender GLB export rendering; not native Bethesda D3D parity",
              "acceptance": "Measured only; no automatic parity threshold", "blender": bpy.app.version_string,
              "renderer": "Cycles CPU", "threads": THREADS, "samples": args.samples, "seed": SEED,
              "size": args.size, "animationFrame": 0, "materialOverrides": False,
              "scriptSha256": digest(Path(__file__)),
              "sources": {kind: {"path": str(path), "sha256": hashes[kind], "bounds": bounds[kind]}
                          for kind, path in sources.items()},
              "sharedCameraBounds": union, "views": comparisons}
    report_path = args.output / "comparison.json"
    report_path.write_text(json.dumps(report, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    print(f"COMPARISON_READY {report_path.resolve()}", flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--blender", type=Path)
    parser.add_argument("--native", type=Path, required=True)
    parser.add_argument("--shared", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--size", type=int, default=384, choices=range(128, 1025))
    parser.add_argument("--samples", type=int, default=32, choices=range(1, 257))
    parser.add_argument("--timeout-seconds", type=int, default=900)
    parser.add_argument("--worker", action="store_true", help=argparse.SUPPRESS)
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else sys.argv[1:]
    args = parser.parse_args(argv)
    if args.worker:
        worker(args)
        return
    if args.blender is None or not args.blender.is_file():
        parser.error("--blender must name the Blender executable")
    if not args.native.is_file() or not args.shared.is_file():
        parser.error("Both GLB inputs must exist")
    args.output.mkdir(parents=True, exist_ok=True)
    command = [str(args.blender.resolve()), "--background", "--factory-startup", "--threads", str(THREADS),
               "--python-exit-code", "1", "--python", str(Path(__file__).resolve()), "--", "--worker",
               "--native", str(args.native.resolve()), "--shared", str(args.shared.resolve()),
               "--output", str(args.output.resolve()), "--size", str(args.size), "--samples", str(args.samples)]
    environment = {**os.environ, "OMP_NUM_THREADS": str(THREADS), "OPENBLAS_NUM_THREADS": "1"}
    with (args.output / "blender.log").open("w", encoding="utf-8") as log:
        subprocess.run(command, stdout=log, stderr=subprocess.STDOUT, check=True,
                       timeout=args.timeout_seconds, env=environment,
                       creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    print(f"COMPARISON_READY {(args.output / 'comparison.json').resolve()}")


if __name__ == "__main__":
    main()
