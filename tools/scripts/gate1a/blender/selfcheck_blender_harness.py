# SPDX-License-Identifier: 0BSD
"""Self-check of every non-Blender path of the Blender-hop harness, with a FAKE Blender (``fake_blender.py``).

Nothing here launches Blender, builds, or runs dotnet. Exit 1 when any expectation fails; every expectation prints one
line and the whole run is saved as ``<out>/selfcheck.json``.

Sections:
 1. The launcher: admission waits and admits, admission times out and aborts (never bypasses), admission waits while
    another Blender runs, weaker limits are refused, the watchdog kills on low available memory and on private bytes
    (scripted readings, and the REAL private-bytes measurement of a fake that allocates), the per-launch timeout, the
    BelowNormal priority and the job object are recorded, PYTHONHASHSEED is scrubbed from the child, launches are
    serial, and the (Global) run mutex excludes a second run (a separate process). The process tree: a timeout kill
    through the job ends a GRANDCHILD the fake started (processes are created suspended and assigned before they run),
    an exception escaping the monitoring loop kills the tree before it propagates, the taskkill fallback ends a tree
    that has no job, unreadable private bytes or priority kill fail-closed, a priority other than BelowNormal is
    killed, and the REAL Toolhelp32 process scan finds (and excludes) a running process.
 2. The driver on a small synthetic manifest: weaker limits and the wrong Blender version are refused, an admission
    timeout aborts with exit 3 before any launch, a first run launches what it should, a rerun launches nothing
    (resume), a deleted receipt relaunches one process, a changed input relaunches its two, a failed launch is reused
    until --retry-failed, --limit and --filter select, --dry-run launches nothing, and a sample whose declared package
    is missing on disk FAILS instead of vanishing from the gate.
 3. The comparators on hand vectors: hop D on a hand-typed readback (and every mutation failing at its element,
    the node hierarchy, transforms, parent inverses, copied mt_* properties, native states and stray skin data
    included), import_rules against the DESIGN's declared conversions (hand vectors typed from sections 6.2 to 6.4,
    not from the importer), import_rules against the importer's own pure functions (tables, the E/T terms of the 11
    pairs typed by hand, and every real package of the gate manifest when it is present; a disagreement is a finding
    to adjudicate against the design, never presumed an oracle bug), the display records the harness restates for the
    11 pairs (set B), an untabulated equation (the smallest candidate) and a lit-scaled one (the generic linear lit
    curve) against the numpy mirror of the C# routine, and the importer's display graph against
    import_rules.display_blend on a (Cs, As) grid and its lit factors against display_blend_math (every lit form), with
    the record-less refusal as its control, the three blend models (drawn, Gamebryo, linear) on hand values, the fake's
    texel model against the RECORDED 2026-09-25 Blender render (``fixtures/hop_f_blend_pairs_20260925.json``: STRAIGHT
    reproduces it, straight texels are the control that must miss it, and the unmeasured PREMUL texel is refused) and the
    texel-rule check on analytic renders, the B'
    launch, sample and control verdicts (the control counts only the set-order defect's signature), the D control
    verdict with element-boundary matching and masking past the detail cap, the transmission binding identity and its
    E/T swap verdict, the DDS writers (BC3 included) against both decoders and the blend probe's three texture copies at
    every cell texel (the design's worst-case sources included), the probe script's pixel helpers, the control builders
    against independently built controls (C2's planted nodes and C4's planted exponent included), hop D's display-record
    checks each failing at its element (a changed mt_blend_graph display, an unlit route on a lit surface, a package
    record drifting from set B), hop F's bound-holds clauses failing on bounds planted below what a render drawing the
    declared fits attains (blend pairs and lit route) and check 3 failing on bounds planted above what the probe
    attains, and (when restored) the Khronos validator on the synthetic GLBs.
 4. Every hop end to end through the fake: everything passes with every control detected when Blender draws the
    declared display fit and lit curve, with straight texels on every container, in BOTH engines (hop F renders Cycles
    and EEVEE on every run: a probes document per engine, the factor-swap, display-node, lit-exponent, ramp and DDS
    controls per engine); the retired linear-space graph fails the drawn reference and the receipt says so, and the
    retired lit route fails the lit check on the tabulated curves and on SRC_ALPHA/ZERO's generic curve at exponent 2.07
    while the generic linear curve at exponent 1 (the same graph) passes; hop D held the blend probe's display records
    against the constants file; an ideal Gamebryo framebuffer holds every declared bound and lit bound yet fails the drawn
    and lit checks (hop F judges the graph the package declares); an importer that leaves images at Blender's default STRAIGHT
    fails the texel rule, which names Cycles premultiplication;
    flipped DDS loading, dropped transmission,
    crossed E/T bindings, a dropped COLOR_0, corrupted shape keys and a refusing importer each fail with their control
    masked; a masked D candidate is skipped and the control still runs; a failed control launch next to a stale probe
    result from an earlier run reads NEVER EXERCISED; a B' control that never fails reads NOT DETECTED, one that fails
    without the defect's signature or in every process reads INCONCLUSIVE; a disagreeing or crashing pristine B' launch
    fails its sample.

Usage: python selfcheck_blender_harness.py [--out DIR] [--skip-end-to-end] [--real-packages N]
"""

import argparse
import collections
import datetime
import hashlib
import io
import json
import math
import os
import shutil
import subprocess
import sys
import time
import traceback
import types

HERE = os.path.dirname(os.path.abspath(__file__))
GATE1A = os.path.dirname(HERE)
sys.path.insert(0, HERE)
sys.path.insert(0, GATE1A)

import numpy as np  # noqa: E402

import blender_launcher as bl  # noqa: E402
import compare_bprime  # noqa: E402
import compare_d  # noqa: E402
import compare_f  # noqa: E402
import controls  # noqa: E402
import import_rules as rules  # noqa: E402
import probe_builders as builders  # noqa: E402
import run_blender_hops as run  # noqa: E402
from gate1a_common import HopResult  # noqa: E402

REPO = os.path.normpath(os.path.join(GATE1A, '..', '..', '..'))
FAKE = os.path.join(HERE, 'fake_blender.py')
SHARED = run.find_shared_root(None)
IMPORT_MODEL = os.path.join(SHARED, 'src', 'Slfx77.Multitool.Media.Blender', 'Python', 'import_model.py')
READBACK = os.path.join(SHARED, 'tools', 'blender', 'readback.py')
GIB = bl.GIB
SELF_MUTEX = r'Global\Multitool.Gate1a.BlenderHops.selfcheck.%d' % os.getpid()  # never the real run's mutex


class Report:
    def __init__(self):
        self.rows = []

    def expect(self, name, ok, detail=''):
        self.rows.append({'name': name, 'ok': bool(ok), 'detail': str(detail)[:1500]})
        print('%s %s%s' % ('ok  ' if ok else 'FAIL', name, (': ' + str(detail)[:300]) if detail and not ok else ''), flush=True)
        return ok

    def section(self, title):
        print('\n== %s' % title, flush=True)

    @property
    def failures(self):
        return [r for r in self.rows if not r['ok']]


class ScriptedProbe:
    """Available memory (and optionally private bytes) from scripts; the real probe for the rest."""

    def __init__(self, available, private=None, real_process=True):
        self.available = available
        self.private = private
        self.calls = 0
        self.real = bl.WindowsMemoryProbe() if real_process else None

    def available_physical_bytes(self):
        self.calls += 1
        value = self.available(self.calls) if callable(self.available) else self.available
        return int(value)

    def process_memory(self, proc):
        if self.private is not None:
            value = int(self.private)
            return {'private': value, 'peakPrivate': value}
        return self.real.process_memory(proc) if self.real else None

    def priority_class(self, proc):
        return self.real.priority_class(proc) if self.real else bl.BELOW_NORMAL_PRIORITY_CLASS


class FakeClock:
    def __init__(self):
        self.t = 0.0
        self.sleeps = []

    def clock(self):
        return self.t

    def sleep(self, seconds):
        self.sleeps.append(seconds)
        self.t += seconds


def plenty():
    return ScriptedProbe(8 * GIB)


def write_scenario(out, **scenario):
    path = os.path.join(out, 'scenario.json')
    scenario.setdefault('stateDir', os.path.join(out, 'fake-state'))
    with open(path, 'w', encoding='utf-8') as f:
        json.dump(scenario, f)
    os.environ['FAKE_BLENDER_SCENARIO'] = path
    return scenario['stateDir']


def invocations(state_dir):
    path = os.path.join(state_dir, 'counter.txt')
    if not os.path.isfile(path):
        return 0
    with open(path, 'r', encoding='utf-8') as f:
        return int(f.read().strip() or 0)


def fake_run_command(sleep_script='none.py'):
    return bl.executable_prefix(FAKE) + ['--background', '--python', sleep_script, '--']


def fresh(root, name):
    path = os.path.join(root, name)
    if os.path.isdir(path):
        shutil.rmtree(path)
    os.makedirs(path)
    return path


# --------------------------------------------------------------------------------------------------------------------
# Section 1: the launcher
# --------------------------------------------------------------------------------------------------------------------


def section_launcher(report, out):
    report.section('1. launcher: admission, watchdog, timeout, priority, environment, serial, mutex')
    clock = FakeClock()
    readings = [4 * GIB, 5 * GIB, 7 * GIB]
    probe = ScriptedProbe(lambda n: readings[min(n, len(readings)) - 1], real_process=False)
    launcher = bl.Launcher(bl.Limits(admission_timeout_seconds=100, admission_poll_seconds=5), probe=probe,
                           foreign_processes=lambda: [], clock=clock.clock, sleep=clock.sleep)
    admission = launcher.admit('test')
    report.expect('admission waits below 6 GiB and admits at 7 GiB after two polls',
                  admission['admitted'] and admission['polls'] == 3 and admission['waitedSeconds'] == 10.0
                  and admission['lowestAvailableBytesWhileWaiting'] == 4 * GIB, admission)

    clock = FakeClock()
    launcher = bl.Launcher(bl.Limits(admission_timeout_seconds=20, admission_poll_seconds=5),
                           probe=ScriptedProbe(3 * GIB, real_process=False), foreign_processes=lambda: [],
                           clock=clock.clock, sleep=clock.sleep)
    try:
        launcher.admit('test')
        report.expect('admission at 3 GiB times out and raises AdmissionTimeout', False, 'admitted')
    except bl.AdmissionTimeout as failure:
        report.expect('admission at 3 GiB times out and raises AdmissionTimeout (the run aborts, never bypasses)',
                      clock.t >= 20 and 'aborts rather than bypass' in str(failure), failure)

    clock = FakeClock()
    foreign = [[4242], [4242], []]
    launcher = bl.Launcher(bl.Limits(admission_timeout_seconds=100, admission_poll_seconds=5),
                           probe=ScriptedProbe(8 * GIB, real_process=False),
                           foreign_processes=lambda: foreign.pop(0) if foreign else [], clock=clock.clock, sleep=clock.sleep)
    admission = launcher.admit('test')
    report.expect('admission waits while another blender.exe runs (exactly one Blender at a time)',
                  admission['waitedSeconds'] == 10.0 and admission['polls'] == 3, admission)

    refused = []
    for kwargs in ({'min_free_bytes': 4 * GIB}, {'kill_free_bytes': 1 * GIB}, {'max_private_bytes': 8 * GIB}):
        try:
            bl.Limits(**kwargs)
        except bl.LimitError as failure:
            refused.append(str(failure))
    stricter = bl.Limits(min_free_bytes=8 * GIB, kill_free_bytes=3 * GIB, max_private_bytes=1 * GIB)
    report.expect('limits refuse a lower admission floor, a lower watchdog floor and a higher private ceiling; stricter ones are accepted',
                  len(refused) == 3 and stricter.max_private_bytes == GIB, refused)

    state = write_scenario(fresh(out, 'launcher-state'), sleepSeconds=30)
    readings_free = lambda n: 8 * GIB if n <= 2 else int(1.5 * GIB)  # noqa: E731
    launcher = bl.Launcher(bl.Limits(timeout_seconds=60, poll_seconds=0.2), probe=ScriptedProbe(readings_free), foreign_processes=lambda: [])
    record = launcher.run(fake_run_command(), os.path.join(out, 'w1.out'), os.path.join(out, 'w1.err'))
    report.expect('the watchdog kills when available memory drops below 2 GiB, recorded as a failure',
                  record['outcome'] == bl.OUTCOME_WATCHDOG_FREE and record['seconds'] < 15 and record['exitCode'] == bl.KILL_EXIT_CODE
                  and record['minAvailableBytes'] == int(1.5 * GIB), {k: record[k] for k in ('outcome', 'seconds', 'exitCode', 'minAvailableBytes')})

    launcher = bl.Launcher(bl.Limits(timeout_seconds=60, poll_seconds=0.2), probe=ScriptedProbe(8 * GIB, private=5 * GIB),
                           foreign_processes=lambda: [])
    record = launcher.run(fake_run_command(), os.path.join(out, 'w2.out'), os.path.join(out, 'w2.err'))
    report.expect('the watchdog kills above 4 GiB private bytes (scripted reading)',
                  record['outcome'] == bl.OUTCOME_WATCHDOG_PRIVATE and record['seconds'] < 15, record['outcome'])

    write_scenario(os.path.join(out, 'launcher-state'), sleepSeconds=30, allocateMb=400)
    cap = 150 * 1024 * 1024
    launcher = bl.Launcher(bl.Limits(timeout_seconds=60, poll_seconds=0.2, max_private_bytes=cap), probe=ScriptedProbe(8 * GIB),
                           foreign_processes=lambda: [])
    record = launcher.run(fake_run_command(), os.path.join(out, 'w3.out'), os.path.join(out, 'w3.err'))
    report.expect('the watchdog kills on the REAL private-bytes measurement (a fake allocating 400 MB against a 150 MB cap)',
                  record['outcome'] == bl.OUTCOME_WATCHDOG_PRIVATE and (record['peakPrivateBytes'] or 0) > cap and record['seconds'] < 25,
                  {k: record[k] for k in ('outcome', 'peakPrivateBytes', 'peakPrivateSources', 'seconds')})

    write_scenario(os.path.join(out, 'launcher-state'), sleepSeconds=30)
    launcher = bl.Launcher(bl.Limits(timeout_seconds=2, poll_seconds=0.2), probe=ScriptedProbe(8 * GIB), foreign_processes=lambda: [])
    record = launcher.run(fake_run_command(), os.path.join(out, 't.out'), os.path.join(out, 't.err'))
    report.expect('the per-launch timeout kills and records outcome timeout', record['outcome'] == bl.OUTCOME_TIMEOUT
                  and 2.0 <= record['seconds'] < 12, {k: record[k] for k in ('outcome', 'seconds')})

    env_file = os.path.join(out, 'child-environment.json')
    write_scenario(os.path.join(out, 'launcher-state'), sleepSeconds=1, recordEnvironment=env_file)
    store = bl.LaunchStore(fresh(out, 'launcher-store'), {'path': FAKE, 'sha256': bl.sha256_file(FAKE)})
    launcher = bl.Launcher(bl.Limits(poll_seconds=0.2), probe=ScriptedProbe(8 * GIB), foreign_processes=lambda: [])
    parent = dict(os.environ, PYTHONHASHSEED='0', PYTHONPATH='x')
    receipt = bl.launch(bl.LaunchSpec('selfcheck', 'unit', 'env', bl.executable_prefix(FAKE) + ['--version']), store, launcher, env=parent)
    with open(env_file, 'r', encoding='utf-8') as f:
        child = json.load(f)
    run_record = receipt['run']
    report.expect('launches are created suspended and run at BelowNormal priority inside a kill-on-close job object',
                  run_record['priorityClass'] == 'BelowNormal' and run_record['jobObject'] is True and run_record['createdSuspended'] is True,
                  run_record)
    report.expect('PYTHONHASHSEED and PYTHONPATH are scrubbed from the child environment and recorded',
                  'PYTHONHASHSEED' not in child and 'PYTHONPATH' not in child and {'PYTHONHASHSEED', 'PYTHONPATH'} <= set(receipt['environmentScrubbed']),
                  receipt['environmentScrubbed'])
    report.expect('the launch receipt records the command, exit code, seconds, peak private bytes and minimum available memory',
                  all(receipt.get('command')) and run_record['exitCode'] == 0 and run_record['seconds'] is not None
                  and run_record['peakPrivateBytes'] and run_record['minAvailableBytes'] == 8 * GIB and receipt['admission']['admitted'],
                  {k: run_record.get(k) for k in ('exitCode', 'seconds', 'peakPrivateBytes', 'minAvailableBytes')})

    launcher.active = True
    try:
        launcher.run(fake_run_command(), os.path.join(out, 's.out'), os.path.join(out, 's.err'))
        report.expect('a second process cannot start while one runs under the launcher', False)
    except RuntimeError:
        report.expect('a second process cannot start while one runs under the launcher', True)
    launcher.active = False

    section_process_tree(report, out)

    lock = bl.RunLock(SELF_MUTEX)
    held = lock.acquire(1)
    probe_code = 'import sys; sys.path.insert(0, %r); import blender_launcher as bl; print(bl.RunLock(%r).acquire(0.5))' % (HERE, SELF_MUTEX)
    other = subprocess.run([sys.executable, '-c', probe_code], capture_output=True, text=True, timeout=60).stdout.strip()
    lock.release()
    after = subprocess.run([sys.executable, '-c', probe_code], capture_output=True, text=True, timeout=60).stdout.strip()
    report.expect('the run mutex excludes a second harness process and is released after the run',
                  held and other == 'False' and after == 'True', (held, other, after))


def process_alive(pid):
    """Whether ``pid`` is a running process (OpenProcess + a zero-timeout wait)."""
    import ctypes
    from ctypes import wintypes
    kernel32 = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel32.OpenProcess.restype = wintypes.HANDLE
    kernel32.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
    kernel32.WaitForSingleObject.restype = wintypes.DWORD
    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
    handle = kernel32.OpenProcess(0x00100000 | 0x1000, False, int(pid))  # SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION
    if not handle:
        return False
    try:
        return kernel32.WaitForSingleObject(handle, 0) == 0x102  # WAIT_TIMEOUT: still running
    finally:
        kernel32.CloseHandle(handle)


def read_pid(path, timeout=30.0):
    deadline = time.time() + timeout
    while time.time() < deadline:
        if os.path.isfile(path):
            text = open(path, encoding='utf-8').read().strip()
            if text:
                return int(text)
        time.sleep(0.1)
    return None


def all_gone(pids, timeout=15.0):
    deadline = time.time() + timeout
    while time.time() < deadline:
        if not any(pid is not None and process_alive(pid) for pid in pids):
            return True
        time.sleep(0.2)
    return False


class NoMemoryProbe(ScriptedProbe):
    def process_memory(self, proc):
        return None


class NormalPriorityProbe(ScriptedProbe):
    def priority_class(self, proc):
        return 0x20


class WhenReadyProbe(ScriptedProbe):
    """8 GiB until every file in ``ready`` exists (the fake and its child are running, however loaded the host is),
    then either 1 GiB (the watchdog fires) or, with ``explode``, an exception out of the monitoring loop."""

    def __init__(self, ready, explode=False):
        super().__init__(8 * GIB)
        self.ready = ready
        self.explode = explode

    def available_physical_bytes(self):
        super().available_physical_bytes()
        if all(os.path.isfile(path) and open(path, encoding='utf-8').read().strip() for path in self.ready):
            if self.explode:
                raise RuntimeError('scripted probe failure')
            return 1 * GIB
        return 8 * GIB


def section_process_tree(report, out):
    base = fresh(out, 'process-tree')
    child_file, self_file = os.path.join(base, 'child.pid'), os.path.join(base, 'self.pid')
    write_scenario(base, sleepSeconds=90, spawnChild=child_file, recordPid=self_file)
    launcher = bl.Launcher(bl.Limits(timeout_seconds=120, poll_seconds=0.2), probe=WhenReadyProbe((child_file, self_file)),
                           foreign_processes=lambda: [])
    record = launcher.run(fake_run_command(), os.path.join(base, 'tree.out'), os.path.join(base, 'tree.err'))
    pids = (read_pid(self_file, 5), read_pid(child_file, 5))
    report.expect('a watchdog kill through the job object also ends the GRANDCHILD the fake started (created suspended, the child joined the job before its first instruction)',
                  record['outcome'] == bl.OUTCOME_WATCHDOG_FREE and record.get('killMethod') == 'job' and record['createdSuspended']
                  and None not in pids and all_gone(pids), (record['outcome'], record.get('killMethod'), pids))

    for name in ('child.pid', 'self.pid'):
        if os.path.isfile(os.path.join(base, name)):
            os.remove(os.path.join(base, name))
    launcher = bl.Launcher(bl.Limits(timeout_seconds=120, poll_seconds=0.2), probe=WhenReadyProbe((child_file, self_file), explode=True),
                           foreign_processes=lambda: [])
    raised = None
    try:
        launcher.run(fake_run_command(), os.path.join(base, 'boom.out'), os.path.join(base, 'boom.err'))
    except RuntimeError as failure:
        raised = str(failure)
    pids = (read_pid(self_file, 5), read_pid(child_file, 5))
    report.expect('an exception escaping the monitoring loop kills the process tree (fake and grandchild) before it propagates',
                  raised == 'scripted probe failure' and not launcher.active and None not in pids and all_gone(pids), (raised, pids))

    write_scenario(base, sleepSeconds=30)
    launcher = bl.Launcher(bl.Limits(timeout_seconds=60, poll_seconds=0.2), probe=NoMemoryProbe(8 * GIB), foreign_processes=lambda: [])
    record = launcher.run(fake_run_command(), os.path.join(base, 'unmeasured.out'), os.path.join(base, 'unmeasured.err'))
    report.expect('private bytes that cannot be read kill the launch fail-closed (watchdog-unmeasured), a failure',
                  record['outcome'] == bl.OUTCOME_WATCHDOG_UNMEASURED and record['seconds'] < 15 and bl.launch_failed_by_harness({'run': record}),
                  {k: record.get(k) for k in ('outcome', 'seconds', 'unmeasuredMemorySamples')})

    launcher = bl.Launcher(bl.Limits(timeout_seconds=60, poll_seconds=0.2), probe=NormalPriorityProbe(8 * GIB), foreign_processes=lambda: [])
    record = launcher.run(fake_run_command(), os.path.join(base, 'priority.out'), os.path.join(base, 'priority.err'))
    report.expect('a process that reads back at any priority but BelowNormal is killed (priority-not-below-normal), a failure',
                  record['outcome'] == bl.OUTCOME_PRIORITY and record['priorityClass'] == 'Normal' and record['seconds'] < 15,
                  {k: record.get(k) for k in ('outcome', 'priorityClass', 'seconds')})

    for name in ('child.pid', 'self.pid'):
        if os.path.isfile(os.path.join(base, name)):
            os.remove(os.path.join(base, name))
    write_scenario(base, sleepSeconds=60, spawnChild=child_file, recordPid=self_file)
    proc = subprocess.Popen(fake_run_command(), stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    grandchild = read_pid(child_file, 20)
    method = bl.kill_process_tree(proc, None)
    proc.wait(timeout=60)
    report.expect('without a job object the kill falls back to taskkill /T and ends the whole tree',
                  method == 'taskkill-tree' and grandchild is not None and all_gone((proc.pid, grandchild)), (method, grandchild))

    image = os.path.basename(sys.executable)
    sleeper = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(60)'])
    try:
        found = bl.find_processes(image)
        excluded = bl.find_processes(image, {sleeper.pid})
        launcher = bl.Launcher(bl.Limits(), probe=ScriptedProbe(8 * GIB), blender_image_name=image)
        launcher._own_pids.add(sleeper.pid)
        own_excluded = sleeper.pid not in launcher.foreign_processes()
        launcher._own_pids.discard(sleeper.pid)
        foreign_seen = sleeper.pid in launcher.foreign_processes()
    finally:
        sleeper.kill()
        sleeper.wait()
    report.expect('the REAL Toolhelp32 scan finds a running process by image name, honours exclusions, and the launcher skips its own pids',
                  sleeper.pid in found and sleeper.pid not in excluded and own_excluded and foreign_seen,
                  (sleeper.pid in found, sleeper.pid in excluded, own_excluded, foreign_seen))
    os.environ.pop('FAKE_BLENDER_SCENARIO', None)


# --------------------------------------------------------------------------------------------------------------------
# Section 2: the driver on a small synthetic manifest
# --------------------------------------------------------------------------------------------------------------------


def quad_glb(path):
    builder = builders.GlbBuilder()
    attributes, indices = builders._quad_mesh_accessors(builder)
    data = builder.glb(meshes=[{'name': 'quad', 'primitives': [{'attributes': attributes, 'indices': indices}]}],
                       nodes=[{'name': 'quad', 'mesh': 0}], scenes=[{'nodes': [0]}], scene=0)
    with open(path, 'wb') as f:
        f.write(data)
    return path


def small_manifest(out):
    inputs = fresh(out, 'inputs')
    d_package = builders.build_d_synthetic(os.path.join(inputs, 'd.zip'))
    d_dump = builders.write_synthetic_dump(d_package, os.path.join(inputs, 'd.dump.json'))
    blend_package = os.path.join(inputs, 'blend.zip')
    builders.build_blend_probe(blend_package, os.path.join(inputs, 'blend.spec.json'))
    blend_dump = builders.write_synthetic_dump(blend_package, os.path.join(inputs, 'blend.dump.json'))
    glb_a = builders.build_transmission_glb(os.path.join(inputs, 'a.glb'))
    glb_b = quad_glb(os.path.join(inputs, 'b.glb'))
    manifest = {'schema': 'gate1a-artifacts/1', 'samples': [
        {'id': 'selfcheck-a :: synthetic d', 'key': 'selfcheck', 'glb': glb_a, 'package': d_package, 'dump': d_dump},
        {'id': 'selfcheck-b :: blend probe', 'key': 'selfcheck', 'glb': glb_b, 'package': blend_package, 'dump': blend_dump}]}
    path = os.path.join(inputs, 'artifacts.json')
    with open(path, 'w', encoding='utf-8') as f:
        json.dump(manifest, f, indent=1)
    return path


def drive(out, manifest, *extra, probe=None, clock=None, command='d'):
    argv = [command, '--out', out, '--blender', FAKE, '--artifacts', manifest, '--poll-seconds', '0.1'] + list(extra)
    kwargs = {'probe': probe or plenty(), 'foreign_processes': lambda: []}
    if clock is not None:
        kwargs.update(sleep=clock.sleep, clock=clock.clock)
    code = run.main(argv, run_mutex=SELF_MUTEX, **kwargs)
    receipt_path = os.path.join(out, 'receipt.json')
    receipt = json.load(open(receipt_path, encoding='utf-8')) if os.path.isfile(receipt_path) else None
    return code, receipt


def section_driver(report, out, manifest):
    report.section('2. driver: refusals, abort, resume, retry, limit, filter, dry run')
    root = fresh(out, 'driver')
    state = write_scenario(root)
    code, _ = drive(os.path.join(root, 'refuse-floor'), manifest, '--min-free-gib', '4')
    report.expect('the CLI refuses --min-free-gib 4 (exit 2) and launches nothing', code == 2 and invocations(state) == 0, code)
    code, _ = drive(os.path.join(root, 'refuse-private'), manifest, '--max-private-gib', '6')
    report.expect('the CLI refuses --max-private-gib 6 (exit 2)', code == 2, code)

    state = write_scenario(fresh(root, 'wrong-version'), version='4.2.0')
    code, _ = drive(os.path.join(root, 'wrong-version', 'run'), manifest)
    report.expect('a Blender that is not 5.1.x is refused (exit 2) after only the version launch', code == 2 and invocations(state) == 1,
                  (code, invocations(state)))

    state = write_scenario(fresh(root, 'abort'))
    code, receipt = drive(os.path.join(root, 'abort', 'run'), manifest, '--admission-timeout-seconds', '30',
                          probe=ScriptedProbe(3 * GIB), clock=FakeClock())
    report.expect('an admission timeout aborts the run with exit 3 before any launch and the receipt says why',
                  code == 3 and invocations(state) == 0 and receipt and 'aborts rather than bypass' in (receipt.get('aborted') or ''),
                  (code, invocations(state), (receipt or {}).get('aborted')))

    holder_code = ('import sys, time; sys.path.insert(0, %r); import blender_launcher as bl; lock = bl.RunLock(%r); '
                   'print(lock.acquire(5), flush=True); time.sleep(60)') % (HERE, SELF_MUTEX)
    holder = subprocess.Popen([sys.executable, '-c', holder_code], stdout=subprocess.PIPE, text=True)
    try:
        holding = holder.stdout.readline().strip()
        state = write_scenario(fresh(root, 'mutex'))
        code, receipt = drive(os.path.join(root, 'mutex', 'run'), manifest, '--admission-timeout-seconds', '1')
    finally:
        holder.kill()
        holder.wait()
    report.expect('a second harness run aborts with exit 3 while another holds the run mutex, launching nothing',
                  holding == 'True' and code == 3 and invocations(state) == 0 and 'holds' in ((receipt or {}).get('aborted') or ''),
                  (holding, code, invocations(state), (receipt or {}).get('aborted')))

    base = fresh(root, 'resume')
    state = write_scenario(base)
    out_dir = os.path.join(base, 'run')
    code, receipt = drive(out_dir, manifest)
    first = invocations(state)
    summary = receipt['summary']['hops']['d']
    report.expect('hop D first run: 2 samples + the synthetic set pass, both controls detected, 11 launches (version, 3 x 2, controls 2 x 2)',
                  code == 0 and summary['samplesPassed'] == 3 and summary['controlsDetected'] == 2 and first == 11,
                  (code, summary, first))
    code, receipt = drive(out_dir, manifest)
    report.expect('a rerun resumes: no new launch, every launch reported resumed, same verdict',
                  code == 0 and invocations(state) == first and receipt['summary']['hops']['d']['launchesResumed'] == receipt['summary']['hops']['d']['launches'],
                  (code, invocations(state) - first))
    victim = os.path.join(out_dir, 'launches', 'd', 'm0-0001_selfcheck-b____blend_probe', 'readback.json')
    os.remove(victim)
    code, receipt = drive(out_dir, manifest)
    report.expect('a deleted launch receipt relaunches exactly that launch', code == 0 and invocations(state) == first + 1,
                  invocations(state) - first)
    package_b = json.load(open(manifest, encoding='utf-8'))['samples'][1]['package']
    changed = package_b + '.tmp'
    controls._rewrite_package(package_b, changed, manifest_edit=lambda manifest: manifest.update(name='gate1a_f_blend_pairs_changed'))
    os.replace(changed, package_b)
    before = invocations(state)
    code, receipt = drive(out_dir, manifest)
    report.expect('a changed input relaunches its import and its readback (2 launches), and the rest resumes',
                  code == 0 and invocations(state) == before + 2, invocations(state) - before)

    base = fresh(root, 'retry')
    state = write_scenario(base, crashInvocations=[3])
    out_dir = os.path.join(base, 'run')
    code, receipt = drive(out_dir, manifest)
    first = invocations(state)
    report.expect('a crashing launch (exit 0xC0000005) fails its sample and the run (exit 1)',
                  code == 1 and receipt['summary']['hops']['d']['samplesFailed'] >= 1, (code, receipt['summary']['hops']['d']))
    write_scenario(base)
    code, receipt = drive(out_dir, manifest)
    report.expect('without --retry-failed the failed launch is resumed, not relaunched', code == 1 and invocations(state) == first,
                  invocations(state) - first)
    code, receipt = drive(out_dir, manifest, '--retry-failed')
    report.expect('--retry-failed relaunches only the failed launch and the run passes', code == 0 and invocations(state) == first + 1,
                  (code, invocations(state) - first))

    base = fresh(root, 'select')
    state = write_scenario(base)
    code, receipt = drive(os.path.join(base, 'limit'), manifest, '--limit', '1')
    units = [s['unit'] for s in receipt['hops']['d']['samples']]
    report.expect('--limit 1 keeps the first corpus sample (plus the synthetic set) and the receipt says SUBSET',
                  units == ['m0-0000_selfcheck-a____synthetic_d', 'synthetic-d-set'] and 'limit 1' in receipt['summary']['subset'], units)
    code, receipt = drive(os.path.join(base, 'filter'), manifest, '--filter', 'selfcheck-b')
    units = [s['unit'] for s in receipt['hops']['d']['samples']]
    report.expect('--filter keeps the matching sample only', units == ['m0-0001_selfcheck-b____blend_probe', 'synthetic-d-set'], units)
    missing_manifest = os.path.join(base, 'artifacts-missing.json')
    document = json.load(open(manifest, encoding='utf-8'))
    document['samples'].append({'id': 'selfcheck-c :: missing package', 'key': 'selfcheck',
                                'package': os.path.join(base, 'absent', 'package.zip'), 'dump': os.path.join(base, 'absent', 'dump.json')})
    with open(missing_manifest, 'w', encoding='utf-8') as f:
        json.dump(document, f, indent=1)
    code, receipt = drive(os.path.join(base, 'missing'), missing_manifest, '--filter', 'selfcheck-c')
    failed = [s for s in receipt['hops']['d']['samples'] if not s.get('passed')]
    report.expect('a sample whose declared package is missing on disk is a FAILED sample (exit 1), never silently dropped',
                  code == 1 and len(failed) == 1 and 'missing on disk' in failed[0]['detail']
                  and receipt['summary']['hops']['d']['samplesFailed'] == 1, (code, [s.get('detail') for s in failed]))
    before = invocations(state)
    code, receipt = drive(os.path.join(base, 'dry'), manifest, '--dry-run', command='all')
    report.expect('--dry-run plans every launch and launches none', code == 0 and invocations(state) == before
                  and len(receipt['launches']) > 20 and all(r['outcome'] == 'dry-run' for r in receipt['launches']),
                  (code, invocations(state) - before, len(receipt['launches'])))


# --------------------------------------------------------------------------------------------------------------------
# Section 3: comparators on hand vectors
# --------------------------------------------------------------------------------------------------------------------


def load_importer():
    import fake_bpy
    fake_bpy.install()
    return fake_bpy.load_module(IMPORT_MODEL, 'import_model_selfcheck')


def load_readback():
    return compare_f.readback_module(SHARED)


def bulk(readback, values, typecode, components):
    flat = [float(v) for v in values] if typecode == 'f' else [int(v) for v in values]
    return {'count': len(flat) // components, 'components': components,
            'sha256': readback._sha256(readback._packed_bytes(typecode, flat)), 'values': flat}


def tiny_package(path):
    builder = builders.PackageBuilder('tiny')
    image = np.zeros((2, 2, 4), dtype=np.uint8)
    image[:, :, 1] = 255
    image[:, :, 3] = 255
    builder.image('textures\\tiny.png', builders.png_bytes(image), 'png', 'sRGB', 2, 2)
    material = builder.material(builders.plain_material('flat'))
    builder.mesh_name(0, 'tiny')
    builder.primitive(0, 0, 'tri', [[0, 0, 0], [1, 0, 0], [0, 1, 0]], [[0, 0, 1]] * 3,
                      [[1, 0, 0, 1], [0, 128 / 255.0, 0, 200 / 255.0], [1 / 255.0, 2 / 255.0, 3 / 255.0, 4 / 255.0]],
                      [[[0, 0], [1, 0], [0.25, 0.75]]], indices=[0, 1, 2], material=material,
                      color_encoding='UnsignedByteNormalized')
    builder.node('tiny', mesh=0)
    return builder.write(path), builder.manifest()


IDENTITY4 = [[1.0 if r == c else 0.0 for c in range(4)] for r in range(4)]


def tiny_readback(readback, manifest, png_sha, png_length):
    """The readback Blender would write for the tiny package, TYPED BY HAND from the importer's declared rules."""
    f32 = lambda v: float(np.float32(v))  # noqa: E731
    colors = [1, 0, 0, 1, 0, f32(128 / 255.0), 0, f32(200 / 255.0), f32(1 / 255.0), f32(2 / 255.0), f32(3 / 255.0), f32(4 / 255.0)]
    image = manifest['images'][0]
    root_props = {'mt_name': 'mt_root', 'mt_root_matrix': builders.IDENTITY16, 'mt_root_scale': 1.0,
                  'mt_units': json.dumps(manifest['units']), 'mt_basis': json.dumps(manifest['basis']),
                  'mt_source_format': 'gate1a.synthetic', 'mt_source_identity': 'gate1a synthetic tiny',
                  'mt_layer_selection': '{"mode":"Default","ids":[]}', 'mt_animation_count': 0, 'mt_fidelity': '[]'}
    return {
        'schema': 'multitool.blender-readback/1', 'mode': 'blend', 'include_values': True, 'blender_version_string': 'hand',
        'meshes': [{
            'name': 'tiny', 'vertex_count': 3, 'loop_count': 3, 'polygon_count': 1, 'polygon_size_histogram': [[3, 1]],
            'positions': bulk(readback, [0, 0, 0, 1, 0, 0, 0, 1, 0], 'f', 3),
            'loop_vertex_indices': bulk(readback, [0, 1, 2], 'i', 1),
            'polygon_loop_starts': bulk(readback, [0], 'i', 1), 'polygon_loop_totals': bulk(readback, [3], 'i', 1),
            'polygon_material_indices': bulk(readback, [0], 'i', 1),
            'polygon_use_smooth': bulk(readback, [1], 'b', 1),
            'uv_maps': [{'name': 'uv0', 'active': True, 'active_render': True, 'values': bulk(readback, [0, 1, 1, 1, 0.25, 0.25], 'f', 2)}],
            'color_attributes': [{'name': 'Color', 'domain': 'POINT', 'data_type': 'BYTE_COLOR', 'source_property': 'color_srgb',
                                  'values': bulk(readback, colors, 'f', 4)}],
            'active_color_attribute': 'Color',
            'custom_normals': {'has_custom_normals': True, 'corner_normals': bulk(readback, [0, 0, 1] * 3, 'f', 3)},
            'attributes': [{'name': 'mt_source_normal', 'domain': 'CORNER', 'data_type': 'FLOAT_VECTOR',
                            'values': bulk(readback, [0, 0, 1] * 3, 'f', 3)}],
            'shape_keys': None, 'materials': ['flat'],
            'properties': {'mt_mesh_index': 0, 'mt_primitive_index': 0, 'mt_name': 'tiny', 'mt_primitive_name': 'tri',
                           'mt_normal_mode': 'Vertex', 'mt_color_encoding': 'UnsignedByteNormalized',
                           'mt_winding_reversed': False, 'mt_topology': 'triangles', 'mt_purpose': 'Render',
                           'mt_normal_provenance': '{"kind":"Authored"}',
                           'mt_color_space': '{"colorSpace":"Srgb","provenance":"Assumed","attribute":null}'}}],
        'materials': [{'name': 'flat', 'use_backface_culling': False, 'surface_render_method': 'DITHERED',
                       'properties': {'mt_material_index': 0, 'mt_vertex_color_variant': '{"domain":"gamma","storage":"byte"}',
                                      'mt_culling': 'doubleSided', 'mt_name': 'flat', 'mt_lighting_model': 'MetallicRoughness',
                                      'mt_blend_graph': '{"alphaMode":"Opaque","source":"alphaMode"}',
                                      'mt_portable': json.dumps(manifest['materials'][0])}}],
        'images': [{'name': image['name'], 'is_packed': True, 'packed_sha256': png_sha, 'packed_length': png_length,
                    'colorspace': 'sRGB', 'alpha_mode': 'CHANNEL_PACKED',  # display-blend design 4.1: never STRAIGHT
                    'properties': {'mt_image_index': 0, 'mt_sha256': png_sha, 'mt_name': image['name'], 'mt_source_container': 'png',
                                   'mt_color_space': 'sRGB', 'mt_role_color_space': 'sRGB', 'mt_packing': 'Original',
                                   'mt_original_container': 'png', 'mt_descriptor': json.dumps(image['descriptor']),
                                   'mt_origin': 'SourceReference', 'mt_legacy_mip_level_count': 0}}],
        'objects': [{'name': 'tiny', 'type': 'MESH', 'data': 'tiny', 'parent': 'mt_root', 'rotation_mode': 'QUATERNION',
                     'location': [0.0, 0.0, 0.0], 'scale': [1.0, 1.0, 1.0], 'matrix_parent_inverse': IDENTITY4,
                     'modifiers': [], 'constraints': [], 'vertex_groups': [],
                     'properties': {'mt_name': 'tiny', 'mt_node_index': 0, 'mt_local_matrix': builders.IDENTITY16, 'mt_role': 'Transform',
                                    'mt_mesh_index': 0, 'mt_primitive_index': 0}},
                    {'name': 'mt_root', 'type': 'EMPTY', 'data': None, 'parent': None, 'rotation_mode': 'QUATERNION',
                     'modifiers': [], 'constraints': [], 'vertex_groups': [], 'properties': dict(root_props)}],
        'roots': [{'name': 'mt_root', 'properties': dict(root_props)}],
        'scenes': [{'name': 'Scene', 'view_settings': {'view_transform': 'Standard'},
                    'properties': {'mt_package_version': 2, 'mt_animation_count': 0, 'mt_name': 'tiny',
                                   'mt_source_format': 'gate1a.synthetic', 'mt_source_identity': 'gate1a synthetic tiny',
                                   'mt_default_scene': 0, 'mt_scenes': json.dumps(manifest['scenes']),
                                   'mt_samplers': json.dumps(manifest['samplers'])}}],
        'actions': []}


DISPLAY_MATERIAL = 'blended'  # the hand-typed blended material of ``display_record_checks``


def display_record_readback(entry, record_edit=None):
    """The readback a Blender import of one blended material would write, TYPED BY HAND from the importer's declared
    rules: the copied mt_* properties and ``mt_blend_graph`` with the package's display record verbatim and the route the
    lit surface draws (``display-lit``: the record carries a lit curve). ``record_edit`` changes ``mt_blend_graph``."""
    emission, transmission = rules.material_blend(entry)
    record = {'alphaMode': entry['alphaMode'], 'source': 'renderState', 'class': 'Affine', 'emission': emission,
              'transmission': transmission, 'display': json.loads(json.dumps(entry['renderState']['blend']['display'])),
              'route': 'display-lit', 'transmissionReadsSourceColor': False}
    if record_edit is not None:
        record_edit(record)
    portable = {member: value for member, value in entry.items() if member not in ('source', 'renderState', 'extras')}
    props = {'mt_material_index': 0, 'mt_name': entry['name'], 'mt_lighting_model': entry['lightingModel'],
             'mt_portable': json.dumps(portable), 'mt_render_state': json.dumps(entry['renderState']),
             'mt_blend_graph': json.dumps(record)}
    return {'materials': [{'name': entry['name'], 'surface_render_method': 'BLENDED', 'properties': props}]}


def display_record_checks(report):
    """Review fixes 2, finding 5: hop D's display-record checks (``compare_d.compare_display_record``, reached through
    ``compare_properties`` as a real hop D reaches it) each fail at their element on a hand-typed lit
    SRC_ALPHA/INV_SRC_ALPHA material: the readback's ``mt_blend_graph.display`` differing from the package record, the
    route ``display`` recorded on a lit surface, and a package record whose x1 drifts 0.001 from set B (the drift check,
    the only guard between the Shared BlendDisplayConstants and the committed constants file). The pristine readback
    passes and counts one record against the constants, so a regression that skips the drift branch (``nif_pair``
    returning None, a kinds lookup falling through) fails the last expectation."""
    def run_properties(entry, readback):
        check = HopResult('D', 'display-record').check('display records')
        rec = compare_d.Recorder(check)
        stats = collections.Counter()
        compare_d.compare_properties(rec, types.SimpleNamespace(manifest={'materials': [entry], 'images': []}), readback, stats)
        rec.finish()
        return [where for where in rec.wheres if where.startswith('materials/')], stats

    entry = {'name': DISPLAY_MATERIAL, 'alphaMode': 'Blend', 'unlit': False, 'lightingModel': 'MetallicRoughness',
             'renderState': {'blend': builders.blend_state('SRC_ALPHA', 'INV_SRC_ALPHA')}}
    where = 'materials/%s' % DISPLAY_MATERIAL
    pristine, stats = run_properties(entry, display_record_readback(entry))
    report.expect('hop D accepts the hand-typed readback of a lit blended material and holds its display record against set B '
                  '(one record against the constants)',
                  not pristine and stats['displayRecordsCompared'] == 1 and stats['displayRecordsAgainstConstants'] == 1,
                  (pristine, dict(stats)))

    def changed_display(record):
        record['display']['fit']['x1'] += 0.001

    def unlit_route(record):
        record['route'] = 'display'

    drifted = json.loads(json.dumps(entry))
    drifted['renderState']['blend']['display']['fit']['x1'] += 0.001
    cases = (('a display member changed in mt_blend_graph', entry, display_record_readback(entry, changed_display),
              where + '/properties/mt_blend_graph/display'),
             ("the route 'display' recorded on a lit surface", entry, display_record_readback(entry, unlit_route),
              where + '/properties/mt_blend_graph/route'),
             ('a package record whose x1 drifts 0.001 from set B (the readback copying it)', drifted,
              display_record_readback(drifted), where + '/renderState/blend/display'))
    for label, package_entry, readback, element in cases:
        wheres, _stats = run_properties(package_entry, readback)
        report.expect('hop D fails %s at %s, and nowhere else on the material' % (label, element),
                      wheres == [element], wheres)


def lit_twin_checks(report):
    """Hop F's lit-twin check (``compare_f.check_lit_twins``) on synthetic probes: equal twins pass in Cycles; a forward
    twin 1/255 off the opaque twin (display units) fails in Cycles at that texel; the same offset in EEVEE passes and is
    recorded (the deferred path quantizes a dark albedo); a missing opaque twin fails."""
    import display_blend_math as dbm
    spec = {'cells': {'twin_src0': {'kind': 'twin', 'path': 'forward'}, 'twinopaque_src0': {'kind': 'twin', 'path': 'deferred'},
                      'twin_src1': {'kind': 'twin', 'path': 'forward'}, 'twinopaque_src1': {'kind': 'twin', 'path': 'deferred'}}}
    base = {'twin_src0': [0.6, 0.074, 0.0103], 'twin_src1': [0.21, 0.21, 0.21]}

    def probe(engine, offset255=0.0, drop=None):
        means = {}
        for cell_id, linear in base.items():
            shifted = [dbm.srgb_decode(dbm.display(v) + offset255 / 255.0) for v in linear] if cell_id == 'twin_src1' else linear
            means[cell_id] = {'linear': shifted}
            means[cell_id.replace('twin_src', 'twinopaque_src')] = {'linear': list(linear)}
        if drop:
            means.pop(drop)
        return {'settings': {'engine': engine}, 'cellMeans': means}

    verdicts = {}
    for label, engine, offset, drop in (('equal', 'CYCLES', 0.0, None), ('offset', 'CYCLES', 1.0, None),
                                        ('eevee-offset', 'BLENDER_EEVEE', 1.0, None), ('missing', 'CYCLES', 0.0, 'twinopaque_src0')):
        result = HopResult('F', 'lit-twins-%s' % label)
        compare_f.check_lit_twins(result, spec, probe(engine, offset, drop))
        check = result.checks[0]
        verdicts[label] = (check.passed, [m.where for m in check.mismatches], check.data.get('worst255'))
    report.expect('the hop F lit-twin check passes equal twins in Cycles, fails a forward twin 1/255 off the opaque twin in '
                  'Cycles at that texel, records (passes) the same offset in EEVEE, and fails a missing opaque twin',
                  verdicts['equal'][0] is True and verdicts['offset'][0] is False and verdicts['offset'][1] == ['twin_src1']
                  and verdicts['eevee-offset'][0] is True and abs(verdicts['eevee-offset'][2] - 1.0) < 0.01
                  and verdicts['missing'][0] is False,
                  verdicts)


def bound_holds_checks(report, out, blend_package, blend_spec):
    """Review fixes 2, finding 6: hop F's bound-holds criterion and check 3 each shown to fail on their own. The fake
    renders the blend probe and the lit probe drawing exactly the declared fits and curves (``renderModel`` drawn,
    straight texels), so the drawn half of every check passes; then the declared bounds are planted 2/255 below what the
    render attains against Gamebryo (``pair_passes`` / ``lit_pair_passes`` allow 1/255), which must fail every planted
    pair on the Gamebryo criterion alone; and check 3 (``check_blend_exercise``) must fail a bound planted 3/255 above
    the error the probe attains and pass one planted 1/255 above (its slack is 2/255)."""
    import fake_render
    from package_reader import Package
    work = fresh(out, 'bound-holds')

    def render(package_path, spec):
        package = Package(package_path)
        try:
            modes = {index: 'CHANNEL_PACKED' for index in range(len(package.manifest['images']))}
        finally:
            package.close()
        return fake_render.render(package_path, spec, {'renderModel': 'drawn'}, modes)

    probe = render(blend_package, blend_spec)
    pristine = HopResult('F', 'bound-holds')
    pairs = compare_f.check_blend(pristine, blend_spec, probe)
    understated = json.loads(json.dumps(blend_spec))
    planted = {}
    for cell in understated['cells'].values():
        if cell.get('kind', 'blend') == 'blend' and float(cell['display']['bound']) != 0.0:
            bound = max(pairs[cell['pairIndex']]['worst']['gamebryo'] - 2.0 / 255.0, 0.0)
            cell['display'] = dict(cell['display'], bound=bound)
            planted[cell['pair']] = round(bound * 255.0, 3)
    result = HopResult('F', 'bound-understated')
    compare_f.check_blend(result, understated, probe)
    check = result.checks[0]
    failed = sorted(m.where for m in check.mismatches)
    drawn_ok = all(row['worstDrawn255'] <= 1.0 for row in check.data['pairs'])
    report.expect('hop F\'s blend check fails every non-exact pair whose bound is planted 2/255 below what the fake render '
                  'attains against Gamebryo, while that render draws every declared fit within 1/255 (the bound-holds clause '
                  'alone fails them; the pristine bounds pass)',
                  pristine.checks[0].passed is True and drawn_ok and len(planted) == 8
                  and failed == sorted('pair %s' % pair for pair in planted),
                  (pristine.checks[0].passed, drawn_ok, planted, failed))

    exercise = HopResult('F', 'exercise')
    attained = compare_f.check_blend_exercise(exercise, blend_spec)
    verdicts = {}
    for above in (3.0, 1.0):
        overstated = json.loads(json.dumps(blend_spec))
        for cell in overstated['cells'].values():
            if cell.get('kind', 'blend') == 'blend' and cell['pair'] in attained:
                cell['display'] = dict(cell['display'], bound=(attained[cell['pair']]['attained255'] + above) / 255.0)
        result = HopResult('F', 'exercise-%g' % above)
        compare_f.check_blend_exercise(result, overstated)
        verdicts[above] = (result.checks[0].passed, sorted(m.where for m in result.checks[0].mismatches))
    report.expect('check 3 fails every non-exact pair whose bound is planted 3/255 above the error the probe attains, and '
                  'passes bounds planted 1/255 above (within its 2/255 slack)',
                  exercise.checks[0].passed is True and verdicts[3.0][0] is False
                  and verdicts[3.0][1] == sorted('pair %s' % pair for pair in attained) and verdicts[1.0] == (True, []),
                  verdicts)

    lit_package = os.path.join(work, 'lit.zip')
    lit_spec = builders.build_lit_blend_probe(lit_package, os.path.join(work, 'lit.json'))
    lit_probe = render(lit_package, lit_spec)
    pristine = HopResult('F', 'lit-bound-holds')
    lit_pairs = compare_f.check_lit_blend(pristine, lit_spec, lit_probe)
    understated = json.loads(json.dumps(lit_spec))
    planted = {}
    for cell in understated['cells'].values():
        if cell.get('kind') == 'lit':
            bound = max(lit_pairs[cell['pairIndex']]['worst']['gamebryo'] - 2.0 / 255.0, 0.0)
            cell['display'] = dict(cell['display'], lit=dict(cell['display']['lit'], bound=bound))
            planted[cell['pair']] = round(bound * 255.0, 3)
    result = HopResult('F', 'lit-bound-understated')
    compare_f.check_lit_blend(result, understated, lit_probe)
    check = result.checks[0]
    failed = sorted(m.where for m in check.mismatches)
    drawn_ok = all(row['worstDrawn255'] <= 1.0 for row in check.data['pairs'])
    report.expect('hop F\'s lit check fails every lit family whose lit bound is planted 2/255 below what the fake render '
                  'attains against Gamebryo, while the render draws every declared lit curve within 1/255 (the lit '
                  'bound-holds clause alone fails them; the pristine lit bounds pass)',
                  pristine.checks[0].passed is True and drawn_ok and len(planted) == len(builders.LIT_BLEND_PAIRS)
                  and failed == sorted('lit pair %s' % pair for pair in planted),
                  (pristine.checks[0].passed, drawn_ok, planted, failed))


def section_comparators(report, out, real_packages):
    report.section('3. comparators on hand vectors')
    readback = load_readback()
    work = fresh(out, 'hand')
    package, manifest = tiny_package(os.path.join(work, 'tiny.zip'))
    dump = builders.write_synthetic_dump(package, os.path.join(work, 'tiny.dump.json'))
    from package_reader import Package
    png = Package(package).image_payload(manifest['images'][0])
    png_sha = hashlib.sha256(png).hexdigest()
    # presentationObjects / pivotObjects / anchorObjects: the importer's camera-facing counts (Shared billboards gap); the
    # tiny package has none.
    summary = json.dumps({'schema': 'multitool.blender-import/1', 'primitiveMeshes': 1, 'nodes': 1, 'images': 1, 'missingImages': 0,
                          'materials': 1, 'animationCount': 0, 'packageVersion': 2, 'presentationObjects': 0, 'pivotObjects': 0,
                          'anchorObjects': 0})
    receipts = ({'run': {'outcome': 'ok'}, 'stdoutTail': summary}, {'run': {'outcome': 'ok'}})

    def compare(document):
        path = os.path.join(work, 'readback.json')
        with open(path, 'w', encoding='utf-8') as f:
            json.dump(document, f)
        return compare_d.compare('tiny', package, dump, path, *receipts)

    pristine = compare(tiny_readback(readback, manifest, png_sha, len(png)))
    failed = [(c['name'], c['firstMismatch']) for c in pristine['checks'] if c['status'] == 'fail']
    report.expect('hop D accepts the hand-typed readback of the tiny package (every check passes)', pristine['passed'], failed)

    def mutate(label, edit, where):
        document = tiny_readback(readback, manifest, png_sha, len(png))
        edit(document)
        result = compare(document)
        hits = [m for m in compare_d.all_mismatches(result) if m['where'].startswith(where)]
        report.expect('hop D fails %s at %s' % (label, where), not result['passed'] and hits,
                      [m['where'] for m in compare_d.all_mismatches(result)][:4])

    mesh = lambda d: d['meshes'][0]  # noqa: E731
    one_ulp = float(np.nextafter(np.float32(0.25), np.float32(1)))
    mutate('a UV off by one ulp', lambda d: mesh(d)['uv_maps'][0].update(values=bulk(readback, [0, 1, 1, 1, 0.25, one_ulp], 'f', 2)),
           'mesh 0 prim 0/uv map uv0')
    mutate('two loops exchanged', lambda d: mesh(d).update(loop_vertex_indices=bulk(readback, [0, 2, 1], 'i', 1)), 'mesh 0 prim 0/loops')
    colors = [1, 0, 0, 1, 0, float(np.float32(129 / 255.0)), 0, float(np.float32(200 / 255.0))] + \
        [float(np.float32(k / 255.0)) for k in (1, 2, 3, 4)]
    mutate('an eight-bit color one step off', lambda d: mesh(d)['color_attributes'][0].update(values=bulk(readback, colors, 'f', 4)),
           'mesh 0 prim 0/Color (stored bytes)')
    mutate('a repacked image', lambda d: d['images'][0].update(packed_sha256='0' * 64), 'images/0/packed_sha256')
    mutate("an image left at Blender's default STRAIGHT alpha mode", lambda d: d['images'][0].update(alpha_mode='STRAIGHT'),
           'images/0/alpha_mode')
    mutate('an action', lambda d: d['actions'].append({'name': 'Action'}), 'actions')
    mutate('a reversed winding claim', lambda d: mesh(d)['properties'].update(mt_winding_reversed=True),
           'mesh 0 prim 0/properties/mt_winding_reversed')
    mutate('a vertex count', lambda d: mesh(d).update(vertex_count=4), 'mesh 0 prim 0/vertex_count')
    mutate('a missing mt_source_normal', lambda d: mesh(d).update(attributes=[]), 'mesh 0 prim 0/mt_source_normal')
    mutate('a stale positions hash with equal values', lambda d: mesh(d)['positions'].update(sha256='f' * 64), 'mesh 0 prim 0/positions')
    angle = math.radians(0.2)
    tilted = [0.0, math.sin(angle), math.cos(angle)]
    mutate('a custom normal 0.2 degrees off', lambda d: mesh(d)['custom_normals'].update(
        corner_normals=bulk(readback, tilted + [0, 0, 1] * 2, 'f', 3)), 'mesh 0 prim 0/custom normals')
    node = lambda d: d['objects'][0]  # noqa: E731
    shifted = list(builders.IDENTITY16)
    shifted[12] = 1.0
    mutate('a wrong parent', lambda d: node(d).update(parent='elsewhere'), 'nodes/0/parent')
    mutate('a moved location', lambda d: node(d).update(location=[0.5, 0.0, 0.0]), 'nodes/0/location')
    mutate('a changed mt_local_matrix', lambda d: node(d)['properties'].update(mt_local_matrix=shifted), 'nodes/0/properties/mt_local_matrix')
    mutate('a non-identity parent inverse', lambda d: node(d).update(matrix_parent_inverse=[[2.0, 0, 0, 0], [0, 1, 0, 0], [0, 0, 1, 0], [0, 0, 0, 1]]),
           'nodes/0/matrix_parent_inverse')
    mutate('a dropped node mt_name', lambda d: node(d)['properties'].pop('mt_name'), 'nodes/0/properties/mt_name')
    mutate('a node drawn as an empty', lambda d: node(d).update(type='EMPTY'), 'nodes/0/type')
    mutate('a changed portable member', lambda d: d['materials'][0]['properties'].update(mt_portable='{"name":"flat"}'),
           'materials/flat/properties/mt_portable')
    mutate('a blended render method on an opaque material', lambda d: d['materials'][0].update(surface_render_method='BLENDED'),
           'materials/flat/surface_render_method')
    mutate('a wrong lighting model', lambda d: d['materials'][0]['properties'].update(mt_lighting_model='Lambert'),
           'materials/flat/properties/mt_lighting_model')
    mutate('a changed image packing', lambda d: d['images'][0]['properties'].update(mt_packing='StandardPayload'),
           'images/0/properties/mt_packing')
    mutate('dropped scene samplers', lambda d: d['scenes'][0]['properties'].pop('mt_samplers'), 'scene/properties/mt_samplers')
    mutate('a changed root fidelity', lambda d: d['objects'][1]['properties'].update(mt_fidelity='[{"row":1}]'), 'mt_root/properties/mt_fidelity')
    mutate('a stray mt_native', lambda d: node(d)['properties'].update(mt_native='[]'), 'mt_native/object/tiny')
    mutate('a stray armature modifier', lambda d: node(d).update(modifiers=[{'name': 'Armature', 'type': 'ARMATURE'}]),
           'objects/tiny/modifiers')
    mutate('a stray constraint', lambda d: node(d).update(constraints=[{'name': 'mt_billboard', 'type': 'DAMPED_TRACK'}]),
           'objects/tiny/constraints')
    mutate('an armature for no skin', lambda d: d['objects'].append({'name': 'Armature', 'type': 'ARMATURE', 'properties': {'mt_skin_index': 0}}),
           'armatures')
    mutate('a changed mesh color-space record', lambda d: mesh(d)['properties'].update(mt_color_space='{"colorSpace":"Linear","provenance":"Assumed","attribute":null}'),
           'mesh 0 prim 0/properties/mt_color_space')
    mutate('a polygon on material slot 1', lambda d: mesh(d).update(polygon_material_indices=bulk(readback, [1], 'i', 1)),
           'mesh 0 prim 0/polygon material indices')

    document = tiny_readback(readback, manifest, png_sha, len(png))
    small = math.radians(0.05)
    mesh(document)['custom_normals']['corner_normals'] = bulk(readback, [0.0, math.sin(small), math.cos(small)] + [0, 0, 1] * 2, 'f', 3)
    result = compare(document)
    report.expect('hop D accepts a custom normal 0.05 degrees off (within the declared 0.1 degree tolerance)', result['passed'],
                  [m['where'] for m in compare_d.all_mismatches(result)][:3])

    # the D control verdict and its masking rule
    control = {'checks': [{'data': {'mismatches': [{'where': 'mesh 0 prim 0/shape_keys/1 (bend)/coordinates', 'kind': 'value', 'detail': 'x'}]}}]}
    elsewhere = {'checks': [{'data': {'mismatches': [{'where': 'mesh 0 prim 0/uv map uv0', 'kind': 'value', 'detail': 'y'}]}}]}
    clean = {'checks': [{'data': {'mismatches': []}}]}
    verdicts = (compare_d.control_verdict(clean, control, 'mesh 0 prim 0/shape_keys/1')[0],
                compare_d.control_verdict(control, control, 'mesh 0 prim 0/shape_keys/1')[0],
                compare_d.control_verdict(clean, elsewhere, 'mesh 0 prim 0/shape_keys/1')[0])
    report.expect('D control verdict: detected at the mutated element; masked by a pristine failure there; NOT detected when it fails elsewhere',
                  verdicts == (True, None, False), verdicts)
    ten = {'checks': [{'data': {'mismatches': [{'where': 'mesh 0 prim 0/shape_keys/10 (x)/coordinates', 'kind': 'value', 'detail': 'z'}]}}]}
    capped = {'checks': [{'data': {'mismatches': [{'where': 'mesh 0 prim 0/uv map uv0', 'kind': 'value', 'detail': 'y'}],
                                   'mismatchWheres': ['mesh 0 prim 0/uv map uv0', 'mesh 0 prim 0/shape_keys/1 (bend)/coordinates']}}]}
    boundary = (compare_d.control_verdict(clean, ten, 'mesh 0 prim 0/shape_keys/1')[0],
                compare_d.control_verdict(capped, control, 'mesh 0 prim 0/shape_keys/1')[0],
                compare_d.at_element('images/1/packed_sha256', 'images/1'), compare_d.at_element('images/10/packed_sha256', 'images/1'))
    report.expect('D control verdict: shape_keys/10 does not count for shape_keys/1; a masking mismatch past the detail cap still masks',
                  boundary == (False, None, True, False), boundary)

    # import_rules against the DESIGN (hand vectors typed from sections 6.2 to 6.4, not from the importer)
    design = {
        '6.2 stencil Both: backface culling off, winding kept': rules.culling({'renderState': {'stencil': {'drawMode': 'Both'}}})[:2] == (False, False),
        '6.2 stencil Clockwise: culling on, winding reversed': rules.culling({'renderState': {'stencil': {'drawMode': 'Clockwise'}}})[:2] == (True, True),
        '6.2 stencil CounterClockwise: culling on, winding kept': rules.culling({'renderState': {'stencil': {'drawMode': 'CounterClockwise'}}})[:2] == (True, False),
        '6.2 UV sets: Blender UV maps exact up to 8': rules.UV_LAYER_LIMIT == 8,
        '6.4 custom normals: an exact FLOAT_VECTOR corner attribute mt_source_normal': rules.SOURCE_NORMAL_ATTRIBUTE == 'mt_source_normal',
        'per-use PREMUL (Shared 314f125): canonical datablocks CHANNEL_PACKED whatever the descriptor; PREMUL only for a declared premultiplied color base sample on portable Blend/Additive': (
            all(rules.image_alpha_mode({'colorSpace': space, 'descriptor': {'alphaMeaning': meaning}}) == 'CHANNEL_PACKED'
                for space in ('sRGB', 'Mixed', 'Non-Color')
                for meaning in ('Unknown', 'Opaque', 'Straight', 'Premultiplied', 'Coverage', 'Auxiliary'))
            and rules.image_alpha_mode({'colorSpace': 'sRGB', 'descriptor': {'alphaMeaning': 'Premultiplied'}},
                                       {'alphaMode': 'Blend'}, True) == 'PREMUL'
            and rules.image_alpha_mode({'colorSpace': 'Non-Color', 'descriptor': {'alphaMeaning': 'Premultiplied'}},
                                       {'alphaMode': 'Blend'}, True) == 'CHANNEL_PACKED'
            and rules.image_alpha_mode({'colorSpace': 'sRGB', 'descriptor': {'alphaMeaning': 'Premultiplied'}},
                                       {'alphaMode': 'Blend', 'source': {}}, True) == 'CHANNEL_PACKED'
            and rules.image_alpha_mode({'colorSpace': 'sRGB', 'descriptor': {'alphaMeaning': 'Premultiplied'}},
                                       {'alphaMode': 'Blend'}, False) == 'CHANNEL_PACKED'),
    }
    tiny_plan_package = Package(package)
    tiny_primitive = tiny_plan_package.manifest['meshes'][0]['primitives'][0]
    tiny_plan = rules.PrimitivePlan(rules.PackagePrimitiveSource(tiny_plan_package, tiny_primitive), False, 'tiny')
    data_type, _domain, stored = tiny_plan.color('UnsignedByteNormalized')
    source_bytes = [[255, 0, 0, 255], [0, 128, 0, 200], [1, 2, 3, 4]]
    design['6.3 storage: BYTE_COLOR keeps the eight-bit source bytes'] = (
        data_type == 'BYTE_COLOR' and np.rint(np.asarray(stored, dtype=np.float64) * 255.0).astype(int).tolist() == source_bytes)
    design['6.2 NIF triangles exact: an unreversed triangle keeps its corners'] = tiny_plan.loop_vertex_indices().tolist() == [0, 1, 2]
    design['6.4 mt_source_normal holds the source normal per corner'] = np.asarray(tiny_plan.source_normals()).tolist() == [[0.0, 0.0, 1.0]] * 3
    tiny_plan_package.close()
    float_base = fresh(out, 'design-vectors')
    float_builder = builders.PackageBuilder('float')
    float_colors = [[0.1, 0.2, 0.3, 0.4], [1.5, -0.25, 0.0, 1.0], [0.5, 0.5, 0.5, 0.5]]
    float_builder.primitive(0, 0, 'tri', [[0, 0, 0], [1, 0, 0], [0, 1, 0]], [[0, 0, 1]] * 3, float_colors, [[[0, 0], [1, 0], [0, 1]]],
                            indices=[0, 1, 2], color_encoding='FloatingPoint',
                            morphs=[('lift', 'relative', [[0, 0, 0.1], [0, 0, 0.2], [0, 0, 0.3]])])
    float_builder.mesh_name(0, 'float')
    float_builder.node('float', mesh=0)
    float_package = Package(float_builder.write(os.path.join(float_base, 'float.zip')))
    float_plan = rules.PrimitivePlan(rules.PackagePrimitiveSource(float_package, float_package.manifest['meshes'][0]['primitives'][0]), False, 'float')
    data_type, _domain, stored = float_plan.color('FloatingPoint')
    design['6.3 storage: FLOAT_COLOR holds the raw float values'] = (
        data_type == 'FLOAT_COLOR' and np.array_equal(np.asarray(stored, dtype=np.float32), np.asarray(float_colors, dtype=np.float32)))
    keys = float_plan.shape_keys()
    design['6.2 morphs: absolute shape keys, a relative target added to the base in float32'] = (
        len(keys) == 1 and np.array_equal(keys[0][2], np.add(np.float32([[0, 0, 0], [1, 0, 0], [0, 1, 0]]),
                                                             np.float32([[0, 0, 0.1], [0, 0, 0.2], [0, 0, 0.3]]), dtype=np.float32)))
    float_package.close()
    failed_design = [name for name, ok in design.items() if not ok]
    report.expect("import_rules follows the DESIGN's declared conversions (%d hand vectors from sections 6.2 to 6.4, independent of import_model.py)" % len(design),
                  not failed_design, failed_design)

    # import_rules against the importer's own pure functions
    importer = load_importer()
    culling_cases = []
    for cull in (None, 'None', 'Front', 'Back', 'FrontAndBack'):
        for front in ('CounterClockwise', 'Clockwise'):
            for stencil in (None, 'CounterClockwise', 'Clockwise', 'Both'):
                for double in (False, True):
                    state = {'frontFace': front}
                    if cull:
                        state['cull'] = cull
                    if stencil:
                        state['stencil'] = {'drawMode': stencil}
                    entry = {'renderState': state, 'doubleSided': double}
                    culling_cases.append(importer._culling(entry) == rules.culling(entry))
    report.expect('import_rules.culling equals the importer on all %d cull x front x stencil x doubleSided cases' % len(culling_cases), all(culling_cases))
    sizes = np.array([3, 4, 5, 3])
    report.expect('import_rules.reversed_corner_order equals the importer and keeps each first corner',
                  np.array_equal(importer._reversed_corner_order(sizes), rules.reversed_corner_order(sizes))
                  and rules.reversed_corner_order(np.array([4])).tolist() == [0, 3, 2, 1])
    corners = np.array([0, 1, 1, 2, 3, 4, 5, 6, 7, 7, 8, 9])
    report.expect('import_rules.faces_without_repeated_vertices equals the importer on mixed sizes',
                  np.array_equal(importer._faces_without_repeated_vertices(np.array([3, 4, 5]), corners),
                                 rules.faces_without_repeated_vertices(np.array([3, 4, 5]), corners)))
    layouts = [(t, c, n) for t in ('Float32', 'Int32', 'Int8', 'UInt8', 'Int16', 'UInt16', 'UInt32', 'Float64') for c in (1, 2, 3, 4) for n in (False, True)]
    report.expect('import_rules.attribute_layout equals the importer on all %d type x components x normalized cases' % len(layouts),
                  all(importer._attribute_layout(*k) == rules.attribute_layout(*k) for k in layouts))
    spaces = [{'colorSpace': v} for v in ('sRGB', 'srgb', 'Mixed', 'Non-Color', 'non_color')] + \
        [{'descriptor': {'colorSpace': 'Linear'}}, {'descriptor': {'colorSpace': 'Srgb'}}, {}]
    report.expect('import_rules.image_color_space equals the importer', all(importer._image_color_space(e, 0) == rules.image_color_space(e) for e in spaces))
    _affine = builders.blend_state('SRC_ALPHA', 'INV_SRC_ALPHA', with_display=False)
    _disabled = builders.blend_state('SRC_ALPHA', 'INV_SRC_ALPHA', enabled=False, with_display=False)
    _materials = [None, {'alphaMode': 'Blend'}, {'alphaMode': 'Additive'}, {'alphaMode': 'Opaque'}, {'alphaMode': 'Mask'},
                  {'alphaMode': 'Blend', 'source': {}},
                  {'alphaMode': 'Blend', 'renderState': {'alphaTest': {'enabled': True}}},
                  {'alphaMode': 'Blend', 'renderState': {'alphaTest': {'enabled': False}}},
                  {'alphaMode': 'Blend', 'renderState': {'blend': _affine}},
                  {'alphaMode': 'Blend', 'renderState': {'blend': _disabled}}]
    alphas = [({'colorSpace': space, 'descriptor': {'alphaMeaning': meaning}}, material, portable)
              for space in ('sRGB', 'Mixed', 'Non-Color')
              for meaning in ('Unknown', 'Premultiplied', 'premultiplied')
              for material in _materials for portable in (False, True)] + \
        [({'descriptor': {'alphaMeaning': 'Premultiplied'}, 'payloadDescriptor': {'alphaMeaning': 'Straight'}}, {'alphaMode': 'Blend'}, True),
         ({'descriptor': {'alphaMeaning': 'Straight'}, 'payloadDescriptor': {'alphaMeaning': 'Premultiplied'}}, {'alphaMode': 'Blend'}, True),
         ({}, None, False)]
    # Resolved once and guarded: a Shared pin whose _image_alpha_mode predates the per-use PREMUL rule (or is absent)
    # must read as one named failed expectation, not a TypeError that aborts the rest of this section.
    alpha_rule = getattr(importer, '_image_alpha_mode', None)
    try:
        alpha_mismatches = (None if alpha_rule is None else
                            [(e, m, pb, alpha_rule(e, m, pb), rules.image_alpha_mode(e, m, pb))
                             for e, m, pb in alphas if alpha_rule(e, m, pb) != rules.image_alpha_mode(e, m, pb)])
    except TypeError as failure:
        alpha_rule, alpha_mismatches = None, None
        _alpha_note = 'the pinned Shared importer takes no per-use arguments (%s): bump the Shared pin together with this harness' % failure
    else:
        _alpha_note = 'the pinned Shared importer %s has no _image_alpha_mode: bump the Shared pin together with this harness' % IMPORT_MODEL
    report.expect('import_rules.image_alpha_mode equals the importer on all %d per-use cases (meaning x space x material x portable-base; payload descriptor first)' % len(alphas),
                  alpha_rule is not None and not alpha_mismatches,
                  _alpha_note if alpha_rule is None else alpha_mismatches)
    keys = [{'colorEncoding': e, 'colorSpace': s} for e in ('UnsignedByteNormalized', 'FloatingPoint', 'UnsignedShortNormalized')
            for s in (None, 'Srgb', 'Linear', 'Unknown')]
    report.expect('import_rules.vertex_color_key equals the importer', all(importer._vertex_color_key(k) == rules.vertex_color_key(k) for k in keys))
    hand = {  # E and T terms [c, S, Sa, S*S, S*Sa] for R, G and B, worked out by hand from out = sf * S + df * Cd
        ('SRC_ALPHA', 'INV_SRC_ALPHA'): ([0, 0, 0, 0, 1], [1, 0, -1, 0, 0]), ('SRC_ALPHA', 'ONE'): ([0, 0, 0, 0, 1], [1, 0, 0, 0, 0]),
        ('ZERO', 'SRC_COLOR'): ([0, 0, 0, 0, 0], [0, 1, 0, 0, 0]), ('ONE', 'ONE'): ([0, 1, 0, 0, 0], [1, 0, 0, 0, 0]),
        ('ONE', 'ZERO'): ([0, 1, 0, 0, 0], [0, 0, 0, 0, 0]), ('SRC_COLOR', 'ONE'): ([0, 0, 0, 1, 0], [1, 0, 0, 0, 0]),
        ('ZERO', 'INV_SRC_ALPHA'): ([0, 0, 0, 0, 0], [1, 0, -1, 0, 0]), ('SRC_COLOR', 'SRC_COLOR'): ([0, 0, 0, 1, 0], [0, 1, 0, 0, 0]),
        ('SRC_ALPHA', 'SRC_COLOR'): ([0, 0, 0, 0, 1], [0, 1, 0, 0, 0]), ('SRC_COLOR', 'ZERO'): ([0, 0, 0, 1, 0], [0, 0, 0, 0, 0]),
        ('ZERO', 'ZERO'): ([0, 0, 0, 0, 0], [0, 0, 0, 0, 0])}
    agree = []
    for (sf, df, _files) in builders.BLEND_PAIRS:
        blend = builders.blend_state(sf, df)
        ours = rules.compile_blend_state(blend)
        theirs = importer._compile_blend_state(blend)
        expected_e, expected_t = hand[(sf, df)]
        agree.append(ours == theirs and ours[0] == 'Affine' and all(ours[1][c] == expected_e and ours[2][c] == expected_t for c in range(3)))
    report.expect('the E/T terms of all 11 FNV pairs equal the hand table, in import_rules and in the importer', all(agree), agree)

    # display-blend: the records the harness restates (set B of display_blend_constants.json, the C# BlendDisplayConstants)
    # and the importer's display graph against import_rules.display_blend (design 6.2), with the refusal as the control
    families = {('SRC_ALPHA', 'INV_SRC_ALPHA'): 'over', ('SRC_ALPHA', 'ONE'): 'additive', ('ZERO', 'SRC_COLOR'): 'multiplicative',
                ('ONE', 'ONE'): 'additive', ('ONE', 'ZERO'): 'exact', ('SRC_COLOR', 'ONE'): 'additive',
                ('ZERO', 'INV_SRC_ALPHA'): 'multiplicative', ('SRC_COLOR', 'SRC_COLOR'): 'other', ('SRC_ALPHA', 'SRC_COLOR'): 'other',
                ('SRC_COLOR', 'ZERO'): 'exact', ('ZERO', 'ZERO'): 'exact'}
    display_rule = getattr(importer, '_display_fit_channel', None)
    record_faults, graph_worst = [], 0.0
    for (sf, df, _files) in builders.BLEND_PAIRS:
        blend = builders.blend_state(sf, df)
        display = blend.get('display')
        if display is None or display.get('family') != families[(sf, df)]:
            record_faults.append('%s/%s: %s' % (sf, df, display))
            continue
        if display_rule is None:
            continue
        fit = importer._display_record(display, '%s/%s' % (sf, df))['fit']
        _klass, emission, transmission = rules.compile_blend_state(blend)
        for i in range(33):
            for j in range(17):
                cs, alpha = i / 32.0, j / 16.0
                ours = rules.display_blend(display, emission, transmission, [cs] * 3, alpha)
                for channel in range(3):
                    theirs = display_rule(None, fit, emission[channel], transmission[channel], rules.srgb_decode(cs), alpha)
                    graph_worst = max(graph_worst, abs(float(theirs[0]) - ours[channel][0]), abs(float(theirs[1]) - ours[channel][1]))
    report.expect('the display records of all 11 pairs take their set-B family, and the importer\'s display graph (constants folded) '
                  'equals import_rules.display_blend on a 33 x 17 (Cs, As) grid to 1e-9',
                  display_rule is not None and not record_faults and graph_worst <= 1e-9,
                  'the pinned Shared importer %s has no _display_fit_channel: bump the Shared pin together with this harness' % IMPORT_MODEL
                  if display_rule is None else (record_faults, graph_worst))
    refusal = None
    if display_rule is not None:
        try:
            importer._material_blend({'name': 'selfcheck', 'renderState': {'blend': builders.blend_state('SRC_ALPHA', 'INV_SRC_ALPHA',
                                                                                                            with_display=False)}})
        except importer.PackageError as error:
            refusal = str(error)
    report.expect('control: the importer refuses an enabled affine render-state blend that carries no display record',
                  refusal is not None, refusal)
    # review fixes: an untabulated equation draws the smallest candidate, a lit-scaled one takes the generic linear lit
    # curve; both bounds grid-measured, against the numpy mirror of the C# routine (TestOutput/gap-impl-20260927/
    # display-blend/derive/review_fixes_mirror.json; the Shared C# tests pin the same numbers)
    premultiplied, kinds = builders.display_record_and_kinds(builders.blend_state('ONE', 'INV_SRC_ALPHA', with_display=False))
    report.expect('premultiplied over (untabulated) draws the SRC_ALPHA/SRC_COLOR candidate at 16.1935/255, not set A\'s two-node '
                  'fit at 64.5654, and takes the generic linear lit curve (m 1, tau 1 - As, exponent 1: ONE/ONE at As 0 makes '
                  'every exponent tie there) at 80.1618/255, both grid-measured',
                  premultiplied['fit'] == builders.display_record('SRC_ALPHA', 'SRC_COLOR')['fit']
                  and abs(premultiplied['bound'] * 255.0 - 16.1935) < 1e-3 and kinds == {'bound': 'grid', 'lit': 'grid'}
                  and {k: v for k, v in premultiplied['lit'].items() if k != 'bound'} == {'form': 'linear', 'p': 1.0, 'm': 1.0,
                                                                                         'tau': 1.0, 'mAlpha': 0.0, 'tauAlpha': -1.0}
                  and abs(premultiplied['lit']['bound'] * 255.0 - 80.1618) < 1e-3, (premultiplied, kinds))
    # review fixes 2, finding 4: the exponent search restated (derive/review_fixes2_mirror.json): SRC_ALPHA/ZERO takes 2.07
    # at 6.9032/255 where exponent 1 measures 73.2697
    over_nothing, over_nothing_kinds = builders.display_record_and_kinds(builders.blend_state('SRC_ALPHA', 'ZERO', with_display=False))
    exponent_one = builders.display_lit_grid_bound(dict(over_nothing['lit'], p=1.0), [0, 0, 0, 0, 1], [0, 0, 0, 0, 0])[0]
    report.expect('lit SRC_ALPHA/ZERO takes the generic linear lit curve at the searched exponent 2.07 (6.9032/255, grid-measured); '
                  'control: exponent 1 measures 73.2697/255',
                  over_nothing['lit']['p'] == 2.07 and abs(over_nothing['lit']['bound'] * 255.0 - 6.9032) < 1e-3
                  and over_nothing_kinds['lit'] == 'grid' and abs(exponent_one * 255.0 - 73.2697) < 1e-3,
                  (over_nothing['lit'], exponent_one * 255.0))
    nudged = json.loads(json.dumps(premultiplied))
    nudged['bound'] += 0.5e-6 / 255.0
    nudged['lit']['bound'] += 0.5e-6 / 255.0
    far = json.loads(json.dumps(premultiplied))
    far['bound'] += 2e-6 / 255.0
    pinned = builders.display_record('SRC_ALPHA', 'INV_SRC_ALPHA')
    pinned_nudged = dict(pinned, bound=pinned['bound'] + 0.5e-6 / 255.0)
    tolerance_verdicts = (compare_d._display_close(nudged, premultiplied, kinds), compare_d._display_close(far, premultiplied, kinds),
                          compare_d._display_close(pinned_nudged, pinned, {'bound': 'pinned', 'lit': 'pinned'}))
    report.expect('hop D holds a writer-measured bound within 1e-6/255 (0.5e-6/255 apart, a libm difference, passes; 2e-6/255 '
                  'fails) and a pinned bound exactly (0.5e-6/255 apart fails; review finding 6)',
                  tolerance_verdicts == (True, False, False), tolerance_verdicts)
    display_record_checks(report)
    lit_rule = getattr(importer, '_display_lit_factors', None)
    lit_worst, lit_forms = 0.0, set()
    import display_blend_math as dbm
    for sf, df in (('SRC_ALPHA', 'INV_SRC_ALPHA'), ('SRC_ALPHA', 'ONE'), ('ONE', 'ONE'), ('ONE', 'ZERO'), ('ONE', 'INV_SRC_ALPHA'),
                   ('SRC_ALPHA', 'ZERO')):
        lit = builders.display_record(sf, df)['lit']
        lit_forms.add(lit['form'])
        normalized = importer._display_record(builders.display_record(sf, df), '%s/%s' % (sf, df))['lit'] if lit_rule else None
        for j in range(65):
            if lit_rule is None:
                break
            ours = dbm.lit_terms(lit, j / 64.0)
            theirs = lit_rule(None, normalized, j / 64.0)
            lit_worst = max(lit_worst, abs(float(theirs[0]) - ours[0]), abs(float(theirs[1]) - ours[1]))
    report.expect('the importer\'s lit factors (constants folded) equal display_blend_math.lit_terms for every lit form it draws '
                  '(power, scaled-power, constant, linear) on 65 alphas to 1e-12',
                  lit_rule is not None and lit_forms == {'power', 'scaled-power', 'constant', 'linear'} and lit_worst <= 1e-12,
                  (sorted(lit_forms), lit_worst))

    # every real package: the importer's topology, welding, degenerate faces and attribute elements equal import_rules
    checked, failures = 0, []
    manifest_path = run.DEFAULT_ARTIFACTS[0]
    if os.path.isfile(manifest_path) and real_packages != 0:
        import types as _types
        _manifests, samples = run.load_samples(run.DEFAULT_ARTIFACTS)
        for sample in samples:
            if not sample.get('package') or not os.path.isfile(sample['package']):
                continue
            if real_packages and checked >= real_packages:
                break
            checked += 1
            try:
                pkg = importer.Package(sample['package'])
                man = pkg.read_manifest()
                ctx = _types.SimpleNamespace(package=pkg, manifest=man)
                ours = Package(sample['package'])
                for m, mesh_entry in enumerate(man.get('meshes') or []):
                    for p, primitive in enumerate(mesh_entry.get('primitives') or []):
                        n = int(primitive['vertexCount'])
                        positions = pkg.read_stream(primitive['position'], 'Float32', 3, n, 'x')
                        sizes, source, _ = importer._read_topology(ctx, primitive, n, 'x')
                        material = primitive.get('material')
                        reverse = material is not None and importer._culling(man['materials'][int(material)])[1]
                        order = importer._reversed_corner_order(sizes) if reverse else np.arange(len(source), dtype=np.int64)
                        v2b, reps, _points, welded = importer._weld(ctx, primitive, positions, n, 'x')
                        keep = importer._faces_without_repeated_vertices(sizes, v2b[source[order]])
                        plan = rules.PrimitivePlan(rules.PackagePrimitiveSource(ours, ours.manifest['meshes'][m]['primitives'][p]), reverse, 'x')
                        corner_keep = np.repeat(keep, sizes)
                        same = (np.array_equal(plan.vertex_to_blender, v2b) and np.array_equal(plan.representatives, reps)
                                and np.array_equal(plan.corners, source[order][corner_keep]) and np.array_equal(plan.sizes, sizes[keep]))
                        if not same:
                            failures.append('%s mesh %d prim %d topology' % (sample['id'], m, p))
                        context = {'label': 'x', 'vertex_count': n, 'corners': source[order][corner_keep], 'welded': welded,
                                   'points': plan.used_points, 'point_count': plan.point_count,
                                   'corner_order': order[corner_keep], 'source_face_count': len(sizes),
                                   'kept_faces': plan.kept_faces, 'source_corner_count': len(source)}
                        for row, declared in zip(plan.attributes(), primitive.get('attributes') or []):
                            if row['elements'] is None:
                                continue
                            values = pkg.read_tuples(declared['stream'], 'x')
                            domain, elements = importer._attribute_elements(row['domain'], values, context)
                            if domain != row['blenderDomain'] or not np.array_equal(np.asarray(elements).astype(row['elements'].dtype), row['elements']):
                                failures.append('%s mesh %d prim %d attribute %s' % (sample['id'], m, p, row['name']))
                pkg.close()
                ours.close()
            except Exception as failure:  # noqa: BLE001
                failures.append('%s: %s: %s' % (sample['id'], type(failure).__name__, failure))
        report.expect('import_rules and the importer agree (topology, welding, degenerate faces, attribute elements) on %d real packages; '
                      'a disagreement is a finding to adjudicate against the design, not presumed an oracle bug' % checked,
                      checked > 0 and not failures, failures[:5])
    else:
        report.expect('real packages: the gate manifest is absent here, so this cross-check was skipped (recorded, not a failure)', True)

    # the two blend models on hand values (computed independently of import_rules)
    display = compare_f.display_model('SRC_ALPHA', 'INV_SRC_ALPHA', [1, 1, 1], 0.5, [0, 0, 0])
    linear = compare_f.linear_model('SRC_ALPHA', 'INV_SRC_ALPHA', [1, 1, 1], 0.5, [0, 0, 0])
    one_one = (compare_f.display_model('ONE', 'ONE', [0.5] * 3, 1.0, [0.5] * 3), compare_f.linear_model('ONE', 'ONE', [0.5] * 3, 1.0, [0.5] * 3))
    multiply = (compare_f.display_model('ZERO', 'SRC_COLOR', [0.5] * 3, 1.0, [0.5] * 3), compare_f.linear_model('ZERO', 'SRC_COLOR', [0.5] * 3, 1.0, [0.5] * 3))
    report.expect('blend models on hand values: alpha blend 0.5 vs 0.7353569830524495, additive 1.0 vs 0.6858361190643691, multiply 0.25 vs 0.23696682464454982',
                  display == [0.5] * 3 and all(abs(v - 0.7353569830524495) < 1e-12 for v in linear)
                  and one_one[0] == [1.0] * 3 and all(abs(v - 0.6858361190643691) < 1e-12 for v in one_one[1])
                  and multiply[0] == [0.25] * 3 and all(abs(v - 0.23696682464454982) < 1e-12 for v in multiply[1]),
                  (display, linear, one_one, multiply))
    # the drawn model on set-B hand vectors computed with display_blend_math (display-blend implementation 2026-09-27,
    # derive/csharp_mirror.json handVectorsB; the Shared C# BlendDisplayApproximationTests pin the same numbers)
    drawn_over = compare_f.drawn_model(builders.display_record('SRC_ALPHA', 'INV_SRC_ALPHA'), 'SRC_ALPHA', 'INV_SRC_ALPHA',
                                       [204 / 255.0] * 3, 64 / 255.0, [0.0] * 3)
    drawn_additive = compare_f.drawn_model(builders.display_record('ONE', 'ONE'), 'ONE', 'ONE', [201 / 255.0] * 3, 1.0, [0.0] * 3)
    report.expect('the drawn model on set-B hand vectors: over 204/64 over black 64.802867/255, ONE/ONE 201 over black 209.419865/255',
                  all(abs(v * 255.0 - 64.802867) < 1e-4 for v in drawn_over) and all(abs(v * 255.0 - 209.419865) < 1e-4 for v in drawn_additive),
                  (drawn_over, drawn_additive))

    # the fake's texel model against the RECORDED 2026-09-25 Blender render (Blender 5.1.1, Cycles; the importer then left
    # the blend-source PNG at STRAIGHT). The graph is import_rules' restatement of the importer's linear E + T * Cd, the
    # graph that render drew, so a later importer graph cannot move this comparison.
    import fake_render
    from gate1a_common import HopResult
    with open(os.path.join(HERE, 'fixtures', 'hop_f_blend_pairs_20260925.json'), 'r', encoding='utf-8') as f:
        recorded = json.load(f)
    pair_factors = {'%s/%s' % (sf, df): (sf, df) for sf, df, _files in builders.BLEND_PAIRS}

    def recorded_worst(alpha_mode):
        worst = {}
        for cell in recorded['cells'].values():
            sf, df = pair_factors[cell['pair']]
            emission, transmission = rules.material_blend({'renderState': {'blend': builders.blend_state(sf, df)}})
            linked = any(float(terms[c][k]) != 0.0 for terms in (emission, transmission) for c in range(3) for k in (2, 4))
            texel = fake_render.delivered_texel(cell['sourceBytes'], alpha_mode, linked)
            drawn = rules.unlit_blend_linear(emission, transmission, [rules.srgb_decode(v) for v in texel], cell['sourceBytes'][3] / 255.0,
                                             [rules.srgb_decode(b / 255.0) for b in cell['backgroundBytes']])
            error = max(abs(compare_f.encode(drawn[c]) - compare_f.encode(cell['linear'][c])) for c in range(3)) * 255.0
            worst[cell['pair']] = max(worst.get(cell['pair'], 0.0), error)
        return worst

    as_recorded = recorded_worst('STRAIGHT')
    straight_texels = recorded_worst('CHANNEL_PACKED')
    report.expect('the fake texel model reproduces the recorded Blender render under STRAIGHT (the importer it was made with) within '
                  '0.01/255 on all 330 cells of the 11 pairs', len(recorded['cells']) == 330 and len(as_recorded) == 11
                  and max(as_recorded.values()) <= 0.01, as_recorded)
    report.expect('control: straight texels miss that render, by more than 150/255 on ONE/ZERO (alpha unlinked, so Cycles premultiplied) '
                  'and by more than 1/255 on SRC_ALPHA/INV_SRC_ALPHA (the byte truncation survives the unassociate)',
                  straight_texels.get('ONE/ZERO', 0.0) > 150.0 and straight_texels.get('SRC_ALPHA/INV_SRC_ALPHA', 0.0) > 1.0, straight_texels)
    report.expect('CHANNEL_PACKED delivers the authored straight texel whether or not the Alpha output is linked',
                  all(fake_render.delivered_texel(c['sourceBytes'], 'CHANNEL_PACKED', linked) == [v / 255.0 for v in c['sourceBytes'][:3]]
                      for c in recorded['cells'].values() for linked in (False, True)))
    premul_refusals = []
    for linked in (False, True):
        try:
            premul_refusals.append(('delivered', fake_render.delivered_texel([204, 77, 26, 64], 'PREMUL', linked)))
        except ValueError as error:
            premul_refusals.append(('refused', str(error)))
    report.expect('the fake refuses to model PREMUL, linked or not (its texel is recalled on both sides and must back no verdict)',
                  [kind for kind, _detail in premul_refusals] == ['refused', 'refused'], premul_refusals)

    texel_spec_dir = fresh(out, 'texel-rule')
    texel_spec = builders.build_blend_probe(os.path.join(texel_spec_dir, 'blend.zip'), os.path.join(texel_spec_dir, 'blend.spec.json'))

    def analytic_texels(model):
        samples = [{'id': cell_id, 'linear': [compare_f.decode(v) for v in compare_f.texel_model(model, cell['sourceBytes'])] + [1.0]}
                   for cell_id, cell in texel_spec['cells'].items() if cell.get('kind') == 'opaque']
        hop_result = HopResult('F', 'texels')
        table = compare_f.check_texels(hop_result, texel_spec, {'samples': samples, 'settings': {'engine': 'CYCLES'}})
        return hop_result.checks[0].passed, {kind: row['texelRule'] for kind, row in table.items()}

    verdicts = {model: analytic_texels(model) for model in compare_f.TEXEL_MODELS}
    report.expect('the texel rule passes a straight render and fails both premultiplied renders on every container, naming the rule '
                  '(the probe discriminates: every partial-alpha opaque cell separates the hypotheses by more than 2/255)',
                  verdicts['straight'] == (True, {'png': 'straight', 'dds-bgra8': 'straight', 'dds-bc3': 'straight'})
                  and all(verdicts[m][0] is False and set(verdicts[m][1].values()) == {m} for m in compare_f.TEXEL_MODELS[1:]),
                  verdicts)

    # B' verdicts on hand-built receipts
    ok = {'name': 'import-01', 'run': {'outcome': 'ok', 'exitCode': 0}}
    crash = {'name': 'import-02', 'run': {'outcome': 'nonzero-exit', 'exitCode': 1}, 'stderrTail': 'ValueError: concatenation'}
    killed = {'name': 'import-03', 'run': {'outcome': 'watchdog-private-bytes', 'exitCode': bl.KILL_EXIT_CODE}}
    dump_a = os.path.join(work, 'bp-a.json')
    dump_b = os.path.join(work, 'bp-b.json')
    empty = os.path.join(work, 'bp-empty.json')
    for path, document in ((dump_a, {'schema': 'multitool.blender-readback/1', 'mode': 'glb', 'objects': [{'type': 'MESH'}], 'x': 1}),
                           (dump_b, {'schema': 'multitool.blender-readback/1', 'mode': 'glb', 'objects': [{'type': 'MESH'}], 'x': 2}),
                           (empty, {'schema': 'multitool.blender-readback/1', 'mode': 'glb', 'objects': []})):
        with open(path, 'w', encoding='utf-8') as f:
            json.dump(document, f)
    j = compare_bprime.judge_launch
    passed = compare_bprime.judge_sample([j(ok, dump_a, 1)] * 3, 3)[0]
    disagree = compare_bprime.judge_sample([j(ok, dump_a, 1), j(ok, dump_a, 1), j(ok, dump_b, 1)], 3)[0]
    killed_sample = compare_bprime.judge_sample([j(ok, dump_a, 1), j(ok, dump_a, 1), j(killed, None, 1)], 3)[0]
    empty_launch = j(ok, empty, 1)[0]
    report.expect("B' sample: 3/3 identical passes; different dumps fail; a watchdog kill fails; an empty import is an importer failure",
                  passed and not disagree and not killed_sample and empty_launch['importerFailed'], (passed, disagree, killed_sample, empty_launch['reason']))
    signature = ('ValueError: all the input array dimensions except for the concatenation axis must match exactly, but along '
                 'dimension 1, the array at index 0 has size 4 and the array at index 1 has size 3')
    defect = {'name': 'import-04', 'run': {'outcome': 'nonzero-exit', 'exitCode': 1}, 'stderrTail': 'Traceback ...\n' + signature}
    other = {'name': 'import-05', 'run': {'outcome': 'nonzero-exit', 'exitCode': 1}, 'stderrTail': 'RuntimeError: Error: add-on refused _PSX_FLAGS_0'}
    control_hit = compare_bprime.judge_control([j(ok, dump_a, 1), j(defect, None, 1), j(ok, dump_a, 1)], 3)[0]
    control_miss = compare_bprime.judge_control([j(ok, dump_a, 1)] * 3, 3)[0]
    control_short = compare_bprime.judge_control([j(ok, dump_a, 1), j(killed, None, 1), j(ok, dump_a, 1)], 3)[0]
    control_split = compare_bprime.judge_control([j(ok, dump_a, 1), j(ok, dump_b, 1), j(defect, None, 1)], 3)[0]
    control_unrelated = compare_bprime.judge_control([j(ok, dump_a, 1), j(other, None, 1), j(defect, None, 1)], 3)[0]
    control_always = compare_bprime.judge_control([j(defect, None, 1)] * 3, 3)[0]
    control_bare = compare_bprime.judge_control([j(ok, dump_a, 1), j(crash, None, 1), j(ok, dump_a, 1)], 3)[0]
    report.expect("B' control: detected only by the set-order signature in some but not all processes; identical imports are NOT detected; "
                  "a harness kill is underpowered; disagreeing dumps, a failure without the signature, or the defect in every process are INCONCLUSIVE",
                  (control_hit, control_miss, control_short, control_split, control_unrelated, control_always, control_bare)
                  == (True, False, None, None, None, None, None),
                  (control_hit, control_miss, control_short, control_split, control_unrelated, control_always, control_bare))

    # DDS writers against both decoders; BC1 refuses a color RGB565 cannot hold
    from dds_decode import decode_numpy, decode_pillow
    pattern = builders.orientation_pattern()
    results = []
    for data in (builders.dds_bgra8_bytes(pattern), builders.dds_bc1_bytes(pattern)):
        a, _ = decode_numpy(data)
        b, _ = decode_pillow(data)
        results.append(np.array_equal(a, pattern) and np.array_equal(b, pattern))
    try:
        bad = pattern.copy()
        bad[0:4, 0:4, :3] = (10, 10, 10)
        builders.dds_bc1_bytes(bad)
        refused = False
    except ValueError:
        refused = True
    report.expect('the DDS writers decode to the authored pattern, rows top to bottom, in both decoders; BC1 refuses a non-565 color',
                  all(results) and refused, results)
    translucent = pattern.copy()
    for by in range(4):
        for bx in range(4):
            translucent[by * 4:by * 4 + 4, bx * 4:bx * 4 + 4, 3] = (0, 1, 64, 128, 191, 254, 255, 17, 200, 3, 99, 250, 7, 160, 90, 33)[by * 4 + bx]
    bc3 = builders.dds_bc3_bytes(translucent)
    bc3_exact = np.array_equal(decode_numpy(bc3)[0], translucent) and np.array_equal(decode_pillow(bc3)[0], translucent)
    try:
        bad = translucent.copy()
        bad[0:4, 0:4, :3] = (10, 10, 10)
        builders.dds_bc3_bytes(bad)
        bc3_refused = False
    except ValueError:
        bc3_refused = True
    exact = [builders.rgb565_exact(s[:3]) for s in builders.BLEND_SOURCES]
    idempotent = all(builders.rgb565_exact(c) == c for c in exact) and builders.rgb565_exact((128, 128, 128)) == (132, 130, 132)
    report.expect('the BC3 writer returns every color and 8-bit alpha exactly in both decoders (16 alphas, 0 and 255 included) and refuses a '
                  'non-565 color; rgb565_exact is idempotent (128 -> 132/130/132)', bc3_exact and bc3_refused and idempotent,
                  (bc3_exact, bc3_refused, exact))
    from PIL import Image as PilImage
    probe_spec = texel_spec  # the blend probe built for the texel-rule check above
    probe_package = Package(os.path.join(texel_spec_dir, 'blend.zip'))
    try:
        decoded = {}
        for index, entry in enumerate(probe_package.manifest['images']):
            data = probe_package.image_payload(entry)
            decoded[index] = (decode_numpy(data)[0] if data[:4] == b'DDS '
                              else np.asarray(PilImage.open(io.BytesIO(data)).convert('RGBA'), dtype=np.uint8))
        materials = probe_package.manifest['materials']
        meshes = probe_package.manifest['meshes']
        wrong = [cell_id for cell_id, cell in probe_spec['cells'].items()
                 if decoded[materials[meshes[cell['mesh']]['primitives'][0]['material']]['source']['layers'][0]['binding']['image']]
                 [cell['texel'][1], cell['texel'][0]].tolist() != cell['sourceBytes']]
    finally:
        probe_package.close()
    kinds = sorted({(cell.get('kind'), cell.get('container')) for cell in probe_spec['cells'].values()})
    sources = len(builders.BLEND_SOURCES)
    report.expect('the blend probe draws every pair through a PNG, a BGRA8 DDS and a BC3 DDS copy plus opaque texel cells (11 sources: '
                  'the six historical ones and the design\'s five worst-case ones), and every one of its cells samples a texel that '
                  'decodes to the cell\'s declared sourceBytes',
                  not wrong and sources == 11 and len(probe_spec['cells']) == 3 * 11 * sources * 5 + 3 * sources and len(kinds) == 6,
                  (wrong[:5], kinds, len(probe_spec['cells'])))
    exercise = HopResult('F', 'exercise')
    exercised = compare_f.check_blend_exercise(exercise, probe_spec)
    report.expect('design 6.4 check 3: every non-exact pair\'s declared fit reaches its bound - 2/255 on some probe cell (8 pairs)',
                  exercise.checks[0].passed is not False and len(exercised) == 8,
                  {pair: (round(e['bound255'], 3), round(e['attained255'], 3)) for pair, e in exercised.items()})
    bound_holds_checks(report, out, os.path.join(texel_spec_dir, 'blend.zip'), probe_spec)
    lit_twin_checks(report)

    # the probe script's pixel helpers (bottom-up buffers)
    import importlib.util
    spec = importlib.util.spec_from_file_location('probe_render_helpers', os.path.join(HERE, 'inside_blender', 'probe_render.py'))
    probe = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(probe)
    width, height = 3, 2
    buffer = [0.0] * (width * height * 4)
    top_left = probe.pixel_offset(width, height, 0, 0)
    buffer[top_left:top_left + 4] = [0.5, 0.25, 0.125, 1.0]
    rgba, spread = probe.sample_pixel(buffer, width, height, 0, 0)
    report.expect('probe_render reads pixel (0, 0) from the TOP-left of a bottom-up buffer and reports its 3 x 3 spread',
                  top_left == 12 and rgba == [0.5, 0.25, 0.125, 1.0] and spread == 0.5, (top_left, rgba, spread))

    # control builders against independently built controls
    base = fresh(out, 'controls')
    spec_path = os.path.join(base, 'spec.json')
    pristine_blend = os.path.join(base, 'blend.zip')
    builders.build_blend_probe(pristine_blend, spec_path)
    swapped_direct = os.path.join(base, 'blend-swapped-direct.zip')
    builders.build_blend_probe(swapped_direct, os.path.join(base, 'spec2.json'), swap_factors=True)
    controls.swap_blend_factors(pristine_blend, os.path.join(base, 'blend-swapped.zip'))
    same_swap = [m.get('renderState') for m in Package(os.path.join(base, 'blend-swapped.zip')).manifest['materials']] == \
        [m.get('renderState') for m in Package(swapped_direct).manifest['materials']]
    ramp = os.path.join(base, 'ramp.zip')
    builders.build_ramp_probe(ramp, os.path.join(base, 'ramp.json'))
    ramp_direct = os.path.join(base, 'ramp-linear-direct.zip')
    builders.build_ramp_probe(ramp_direct, os.path.join(base, 'ramp2.json'), color_space='Linear')
    controls.declare_linear_vertex_colors(ramp, os.path.join(base, 'ramp-linear.zip'))
    same_ramp = [p.get('colorSpace') for _, _, _, p in Package(os.path.join(base, 'ramp-linear.zip')).primitives()] == \
        [p.get('colorSpace') for _, _, _, p in Package(ramp_direct).primitives()] == ['Linear', 'Linear']
    orient = os.path.join(base, 'orient.zip')
    builders.build_orientation_probe(orient, os.path.join(base, 'orient.json'))
    orient_direct = os.path.join(base, 'orient-flip-direct.zip')
    builders.build_orientation_probe(orient_direct, os.path.join(base, 'orient2.json'), flip_dds=True)
    controls.flip_dds_images(orient, os.path.join(base, 'orient-flip.zip'))
    same_flip = [i['sha256'] for i in Package(os.path.join(base, 'orient-flip.zip')).manifest['images']] == \
        [i['sha256'] for i in Package(orient_direct).manifest['images']]
    shape = controls.move_shape_key_vertex(builders.build_d_synthetic(os.path.join(base, 'd.zip')), os.path.join(base, 'd-shape.zip'))
    moved = np.frombuffer(zip_read(os.path.join(base, 'd-shape.zip'), shape['stream']), dtype='<f4').reshape(-1, 3)
    original = np.frombuffer(zip_read(os.path.join(base, 'd.zip'), shape['stream']), dtype='<f4').reshape(-1, 3)
    same_shape = (moved - original).tolist() == [[1.0, 0.0, 0.0]] + [[0.0, 0.0, 0.0]] * (len(moved) - 1)
    glb = builders.build_transmission_glb(os.path.join(base, 't.glb'))
    controls.strip_transmission(glb, os.path.join(base, 't-stripped.glb'))
    from glb_reader import Glb
    stripped = Glb.from_path(os.path.join(base, 't-stripped.glb')).json
    same_strip = 'KHR_materials_transmission' not in json.dumps(stripped) and 'KHR_materials_ior' in stripped['extensionsUsed']
    controls.plant_display_nodes(pristine_blend, os.path.join(base, 'blend-nodes.zip'))
    pristine_materials = Package(pristine_blend).manifest['materials']
    planted_materials = Package(os.path.join(base, 'blend-nodes.zip')).manifest['materials']

    def without_fit(material):
        clone = json.loads(json.dumps(material))
        display = ((clone.get('renderState') or {}).get('blend') or {}).get('display')
        if display is not None:
            display.pop('fit')
        return clone

    same_nodes = (all(without_fit(a) == without_fit(b) for a, b in zip(pristine_materials, planted_materials))
                  and all(((b.get('renderState') or {}).get('blend') or {}).get('display', {}).get('fit') ==
                          controls.planted_display_fit((((a.get('renderState') or {}).get('blend') or {}).get('display') or {}).get('fit') or {})
                          for a, b in zip(pristine_materials, planted_materials)
                          if ((a.get('renderState') or {}).get('blend') or {}).get('display') is not None)
                  and any(((b.get('renderState') or {}).get('blend') or {}).get('display', {}).get('fit', {}).get('x1') == 0.0
                          for b in planted_materials))
    lit_pristine = os.path.join(base, 'lit.zip')
    builders.build_lit_blend_probe(lit_pristine, os.path.join(base, 'lit.json'))
    planted_lit = controls.plant_lit_exponent(lit_pristine, os.path.join(base, 'lit-exponent.zip'))
    lit_before = Package(lit_pristine).manifest['materials']
    lit_after = Package(os.path.join(base, 'lit-exponent.zip')).manifest['materials']
    changed_lit = [i for i, (a, b) in enumerate(zip(lit_before, lit_after)) if a != b]
    over_lit = ((lit_after[0].get('renderState') or {}).get('blend') or {}).get('display', {}).get('lit') or {}
    zero_lit = ((lit_after[4].get('renderState') or {}).get('blend') or {}).get('display', {}).get('lit') or {}
    same_lit = (changed_lit == planted_lit['materials'] == [0, 4] and over_lit.get('p') == 1.0 and over_lit.get('form') == 'power'
                and zero_lit.get('p') == 1.0 and zero_lit.get('form') == 'linear')
    report.expect('each control builder changes exactly its element (swap = direct swap, Linear = direct Linear, flip = direct flip, '
                  'one shape-key vertex +1, transmission stripped, C2 = every declared fit\'s nodes planted and nothing else, '
                  'C4 = the exponent 1 on lit over and on SRC_ALPHA/ZERO\'s generic curve and nothing else)',
                  same_swap and same_ramp and same_flip and same_shape and same_strip and same_nodes and same_lit,
                  (same_swap, same_ramp, same_flip, same_shape, same_strip, same_nodes, same_lit, changed_lit))

    # the transmission route's bindings, judged by identity, and the E/T swap control
    swapped_glb = os.path.join(base, 't-swapped.glb')
    swap = controls.swap_transmission_terms(glb, swapped_glb, builders.TRANSMISSION_MATERIAL)
    original = Glb.from_path(glb)
    exchanged = Glb.from_path(swapped_glb).json
    o_material, s_material = original.json['materials'][0], exchanged['materials'][0]
    restored = json.loads(json.dumps(s_material))
    restored['pbrMetallicRoughness']['baseColorTexture'], restored['emissiveTexture'] = (restored.pop('emissiveTexture'),
                                                                                         restored['pbrMetallicRoughness']['baseColorTexture'])
    restored['pbrMetallicRoughness']['baseColorFactor'], restored['emissiveFactor'] = (
        list(s_material['emissiveFactor']) + s_material['pbrMetallicRoughness']['baseColorFactor'][3:], s_material['pbrMetallicRoughness']['baseColorFactor'][:3])
    same_elsewhere = {k: v for k, v in exchanged.items() if k != 'materials'} == {k: v for k, v in original.json.items() if k != 'materials'}
    report.expect('swap_transmission_terms exchanges exactly the base-color and emissive textures and factors of the route (nothing else)',
                  swap['materials'] == [0] and restored == o_material and s_material != o_material and same_elsewhere, (swap, same_elsewhere))
    bindings = builders.transmission_bindings(glb)
    t_sha = hashlib.sha256(original.image_bytes(0)[0]).hexdigest()
    e_sha = hashlib.sha256(original.image_bytes(1)[0]).hexdigest()
    report.expect('transmission_bindings reads T, E and COLOR_0 from the GLB (T image 0, E image 1, distinct, COLOR_0 on the route primitive)',
                  bindings['baseColorImageSha256'] == t_sha and bindings['emissionImageSha256'] == e_sha and t_sha != e_sha
                  and bindings['baseColorVertexColor'] is True, bindings)

    def route_dump(base_image, emission_image, vertex_color='base', color_attribute=True):
        nodes = [{'name': 'P', 'type': 'ShaderNodeBsdfPrincipled', 'principled': {}},
                 {'name': 'T', 'type': 'ShaderNodeTexImage', 'image': base_image},
                 {'name': 'E', 'type': 'ShaderNodeTexImage', 'image': emission_image},
                 {'name': 'M', 'type': 'ShaderNodeMix'}, {'name': 'V', 'type': 'ShaderNodeVertexColor'}]
        links = [{'from_node': 'E', 'from_socket': 'Color', 'to_node': 'P', 'to_socket': 'Emission Color'}]
        if vertex_color == 'base':
            links += [{'from_node': 'T', 'from_socket': 'Color', 'to_node': 'M', 'to_socket': 'A'},
                      {'from_node': 'V', 'from_socket': 'Color', 'to_node': 'M', 'to_socket': 'B'},
                      {'from_node': 'M', 'from_socket': 'Result', 'to_node': 'P', 'to_socket': 'Base Color'}]
        else:
            links.append({'from_node': 'T', 'from_socket': 'Color', 'to_node': 'P', 'to_socket': 'Base Color'})
            if vertex_color == 'emission':
                links.append({'from_node': 'V', 'from_socket': 'Color', 'to_node': 'E', 'to_socket': 'Vector'})
        return {'materials': [{'name': builders.TRANSMISSION_MATERIAL, 'node_tree': {'nodes': nodes, 'links': links}}],
                'images': [{'name': 'transmission_T', 'packed_sha256': t_sha}, {'name': 'emission_E', 'packed_sha256': e_sha}],
                'meshes': [{'materials': [builders.TRANSMISSION_MATERIAL], 'color_attributes': [{'name': 'Color'}] if color_attribute else []}]}

    wheres = lambda dump: sorted(w for w, _ in compare_f.binding_mismatches(dump, bindings))  # noqa: E731
    cases = {'pristine': wheres(route_dump('transmission_T', 'emission_E')),
             'swapped': wheres(route_dump('emission_E', 'transmission_T')),
             'vertex color on emission': wheres(route_dump('transmission_T', 'emission_E', vertex_color='emission')),
             'no vertex color': wheres(route_dump('transmission_T', 'emission_E', vertex_color='none')),
             'no color attribute': wheres(route_dump('transmission_T', 'emission_E', color_attribute=False))}
    report.expect('the binding identity: the pristine route binds T and E; a swap fails at both sockets; COLOR_0 on emission, no COLOR_0 and no color attribute each fail',
                  cases == {'pristine': [], 'swapped': ['bindings/Base Color/image', 'bindings/Emission Color/image'],
                            'vertex color on emission': ['bindings/Base Color/vertex color', 'bindings/Emission Color/vertex color'],
                            'no vertex color': ['bindings/Base Color/vertex color'], 'no color attribute': ['bindings/mesh color attribute']},
                  cases)
    verdict_dir = fresh(out, 'swap-verdicts')
    verdicts = []
    for label, dump, pristine_ok in (('swapped', route_dump('emission_E', 'transmission_T'), True),
                                     ('unchanged', route_dump('transmission_T', 'emission_E'), True),
                                     ('masked', route_dump('emission_E', 'transmission_T'), False)):
        path = os.path.join(verdict_dir, label + '.json')
        with open(path, 'w', encoding='utf-8') as f:
            json.dump(dump, f)
        verdicts.append(compare_f.transmission_swap_control(pristine_ok, {'run': {'outcome': 'ok', 'exitCode': 0}}, path, bindings)[0])
    verdicts.append(compare_f.transmission_swap_control(True, {'run': {'outcome': 'nonzero-exit', 'exitCode': 3}}, path, bindings)[0])
    report.expect('the E/T swap verdict: detected when both sockets bind the other image; NOT detected when unchanged; masked by a failing pristine; never exercised on a failed readback',
                  verdicts == [True, False, None, None], verdicts)

    # the Khronos validator on the synthetic GLBs, when restored
    locate = os.path.join(SHARED, 'tools', 'vendor', 'gltf-validator', 'gltf_validator_tool.py')
    located = subprocess.run([sys.executable, locate, 'locate', '--json'], capture_output=True, text=True, timeout=120)
    try:
        location = json.loads(located.stdout)
    except ValueError:
        location = {}
    if location.get('status') == 'Found':
        from hop_b import run_validator
        exe = location['location']['executablePath']
        errors = {}
        control_glb = builders.build_custom_attribute_glb(os.path.join(base, 'custom.glb'))
        for path in (glb, os.path.join(base, 't-stripped.glb'), swapped_glb, control_glb):
            try:
                errors[os.path.basename(path)] = run_validator(exe, path)['issues']['numErrors']
            except Exception as failure:  # noqa: BLE001
                errors[os.path.basename(path)] = 'validator failed: %s' % failure
        report.expect('the Khronos validator reports 0 errors on the transmission GLB (with COLOR_0), its stripped and E/T-swapped copies and the VEC4 + VEC3 control GLB',
                      all(v == 0 for v in errors.values()), errors)
    else:
        report.expect('the Khronos validator is not restored here, so the synthetic GLBs were not validated (recorded, not a failure)', True)


def zip_read(path, name):
    import zipfile
    with zipfile.ZipFile(path) as z:
        return z.read(name)


# --------------------------------------------------------------------------------------------------------------------
# Section 4: every hop end to end through the fake
# --------------------------------------------------------------------------------------------------------------------


def section_long_paths(report, out):
    report.section("2b. MAX_PATH: an overlong GLB is staged short, and an overlong argument never launches")
    root = fresh(out, 'long-paths')
    state = write_scenario(root, gltfCustomAttributeFailEvery=2)
    nested = os.path.join(root, 'n' * 200)
    os.makedirs(nested)
    long_glb = quad_glb(os.path.join(nested, 'long.glb'))
    length = len(os.path.abspath(long_glb))
    report.expect('the synthetic GLB exists at a path longer than MAX_PATH (%d characters)' % bl.MAX_PATH_ARGUMENT,
                  length > bl.MAX_PATH_ARGUMENT and os.path.isfile(long_glb), length)

    # Control: handed that path directly, the fake Blender fails the way Blender 5.1 does.
    dump = os.path.join(root, 'direct.json')
    command = bl.blender_script_command(FAKE, READBACK, ['--import-glb', os.path.abspath(long_glb), '--out', dump])
    launcher = bl.Launcher(bl.Limits(), probe=plenty(), foreign_processes=lambda: [])
    record = launcher.run(command, os.path.join(root, 'direct.out'), os.path.join(root, 'direct.err'),
                          env=bl.child_environment()[0])
    stderr = bl.tail_text(os.path.join(root, 'direct.err')) or ''
    report.expect('control: given the long path directly, the fake import fails with "Please select a file" and writes no dump',
                  record['outcome'] == bl.OUTCOME_NONZERO and 'Please select a file' in stderr and not os.path.isfile(dump),
                  (record['outcome'], stderr[-300:]))

    # The guard: the same command through launch() is refused before admission and no process starts.
    before = invocations(state)
    store = bl.LaunchStore(root, {'sha256': 'selfcheck'})
    spec = bl.LaunchSpec('bprime', 'long', 'import-01', command, inputs={'glb': long_glb}, outputs={'dump': dump})
    receipt = bl.launch(spec, store, launcher)
    run_record = receipt['run']
    report.expect('launch() refuses an overlong path argument: launch-error naming MAX_PATH, no admission, no process',
                  run_record['outcome'] == bl.OUTCOME_LAUNCH_ERROR and 'MAX_PATH' in run_record.get('error', '')
                  and receipt['admission'] is None and invocations(state) == before, run_record)

    # The fix: hop B' imports a SHA-256-verified short copy and passes; a short path is never staged.
    manifest = os.path.join(root, 'artifacts.json')
    with open(manifest, 'w', encoding='utf-8') as f:
        json.dump({'schema': 'gate1a-artifacts/1', 'samples': [
            {'id': 'selfcheck-long :: overlong path', 'key': 'selfcheck', 'glb': long_glb}]}, f)
    code, receipt = drive(os.path.join(root, 'run'), manifest, '--repeats', '2', command='bprime')
    rows = [r for r in (receipt or {}).get('launches', []) if r['hop'] == 'bprime']
    commands = {r['unit']: json.load(open(r['receipt'], encoding='utf-8'))['command'] for r in rows}
    targets = {unit: c[c.index('--import-glb') + 1] for unit, c in commands.items()}
    sample = [t for unit, t in targets.items() if unit != 'control-vec4-vec3']
    control = targets.get('control-vec4-vec3')
    report.expect("hop B' passes over the overlong GLB by importing a short copy with the same SHA-256",
                  code == 0 and hop(receipt, 'bprime')['passed'] and len(sample) == 1 and len(sample[0]) <= bl.MAX_PATH_ARGUMENT
                  and os.path.dirname(sample[0]) == os.path.join(os.path.abspath(os.path.join(root, 'run')), 'in')
                  and bl.sha256_file(sample[0]) == bl.sha256_file(long_glb), (code, targets))
    report.expect("the short-path control GLB is imported from where it was built (never staged)",
                  control is not None and os.path.basename(control) == 'custom_attributes.glb'
                  and os.sep + 'in' + os.sep not in control, control)


def hop(receipt, name):
    return receipt['summary']['hops'][name]


def control_named(receipt, hop_name, fragment):
    return next((c for c in hop(receipt, hop_name)['controls'] if fragment in c['name']), None)


def hop_controls(receipt, hop_name):
    """The full control entries of a hop (the summary keeps name, verdict and detail only)."""
    return receipt['hops'][hop_name].get('controls') or []


def f_check(receipt, fragment, unit=None):
    """The first hop-F check whose name contains ``fragment``, over the documents in order (probes-cycles, probes-eevee,
    transmission), or only in the document ``unit``."""
    for sample in receipt['hops']['f']['samples']:
        if unit is not None and sample.get('unit') != unit:
            continue
        found = next((c for c in sample['result']['checks'] if fragment in c['name']), None)
        if found is not None:
            return found
    return None


def section_end_to_end(report, out, manifest):
    report.section('4. every hop end to end through the fake Blender')
    root = fresh(out, 'end-to-end')

    base = fresh(root, 'all-drawn')
    write_scenario(base, renderModel='drawn', gltfCustomAttributeFailEvery=3)
    code, receipt = drive(os.path.join(base, 'run'), manifest, command='all')
    hops = receipt['summary']['hops']
    report.expect("all hops pass with every control detected (B' 12 fresh processes, D, F in both engines with Blender drawing the "
                  "declared display fit and lit curve; 15 controls: B' 1, D 2, F 12)",
                  code == 0 and all(h['passed'] for h in hops.values()) and sum(h['controlsDetected'] for h in hops.values()) == 15,
                  {k: (v['passed'], v['controlsDetected'], len(v['controls'])) for k, v in hops.items()})
    against_constants = sum(((sample.get('result') or {}).get('stats') or {}).get('displayRecordsAgainstConstants', 0)
                            for sample in receipt['hops']['d']['samples'])
    report.expect('hop D held the blend probe\'s display records against set B of the constants file (review fixes 2, finding 5: '
                  'the drift check ran, it was not skipped)', against_constants > 0, against_constants)
    report.expect('the receipt records the Blender path, version, SHA-256, limits and each Shared script SHA-256',
                  receipt['blender']['version'] == 'Blender 5.1.0' and receipt['blender']['fake'] and receipt['limits']['minFreeGiB'] == 6.0
                  and set(receipt['scripts']) == {'readback', 'importModel', 'probeRender', 'repack'}, receipt['blender'])
    launches = receipt['launches']
    report.expect('every launch row carries outcome, exit code, seconds, peak private bytes and minimum available memory',
                  launches and all(r['outcome'] and r['seconds'] is not None and r['peakPrivateBytes'] and r['minAvailableBytes'] for r in launches),
                  launches[:1])

    base = fresh(root, 'linear-vs-drawn')
    write_scenario(base, renderModel='linear')
    code, receipt = drive(os.path.join(base, 'run'), manifest, command='f')
    blend = f_check(receipt, 'blend pairs')
    et = control_named(receipt, 'f', 'blend-pairs')
    report.expect('the retired linear-space graph fails the drawn reference, the receipt names linear space, and the E/T control is still detected',
                  code == 1 and blend['status'] == 'fail' and blend['data']['blenderBlendedIn'] == 'linear space' and et['detected'] is True,
                  (code, blend['status'], blend['data'].get('blenderBlendedIn'), et['detail']))
    lit = f_check(receipt, 'lit route')
    lit_rows = {row['pair']: row['passed'] for row in ((lit or {}).get('data') or {}).get('pairs') or []}
    report.expect('the retired lit route fails the lit check on the tabulated curves (over p 1.5, SRC_ALPHA/ONE, ONE/ONE) and on '
                  'SRC_ALPHA/ZERO\'s generic curve at exponent 2.07, while the generic linear curve at exponent 1, the same graph, '
                  'passes on premultiplied over (the lit check discriminates)',
                  lit is not None and lit['status'] == 'fail' and lit_rows == {'SRC_ALPHA/INV_SRC_ALPHA': False, 'SRC_ALPHA/ONE': False,
                                                                                'ONE/ONE': False, 'ONE/INV_SRC_ALPHA': True,
                                                                                'SRC_ALPHA/ZERO': False}, lit_rows)

    base = fresh(root, 'drawn-reference')
    write_scenario(base, renderModel='drawn')
    code, receipt = drive(os.path.join(base, 'run'), manifest, command='f')
    report.expect('Blender drawing the declared display fit and lit curve passes hop F in both engines, every F control detected '
                  '(12: factor swap, display nodes, lit exponent, ramp and DDS per engine, and the two transmission controls)',
                  code == 0 and hop(receipt, 'f')['controlsDetected'] == 12
                  and [s['unit'] for s in receipt['hops']['f']['samples']] == ['probes-cycles', 'probes-eevee', 'transmission']
                  and sorted({c['engine'] for c in hop_controls(receipt, 'f') if c.get('engine')}) == ['CYCLES', 'EEVEE'],
                  (code, hop(receipt, 'f')))
    lit = f_check(receipt, 'lit route', 'probes-eevee')
    report.expect('the lit route passes in EEVEE too, with every lit form the importer draws (power, scaled-power, constant, and '
                  'linear at exponents 1 and 2.07) within 1/255 of m L_twin + tau Cd and within its lit bound of Gamebryo',
                  lit is not None and lit['status'] != 'fail'
                  and sorted(row['form'] for row in lit['data']['pairs']) == ['constant', 'linear', 'linear', 'power', 'scaled-power']
                  and all(row['passed'] and row['worstGamebryo255'] <= row['litBound255'] + 1.0 for row in lit['data']['pairs']),
                  (lit or {}).get('data'))
    exercise = f_check(receipt, 'exercises every declared bound')
    nodes = control_named(receipt, 'f', 'display-nodes')
    exponent = control_named(receipt, 'f', 'lit-exponent')
    report.expect('design 6.4 check 3 passes on the render\'s probe, and controls C2 (planted display nodes) and C4 (planted lit '
                  'exponent) are detected',
                  exercise is not None and exercise['status'] != 'fail' and nodes['detected'] is True and exponent['detected'] is True,
                  ((exercise or {}).get('status'), nodes['detail'], exponent['detail']))
    blend = f_check(receipt, 'blend pairs')
    report.expect('the blend-pairs receipt names the declared display fit and every pair holds its bound',
                  blend is not None and blend['data']['blenderBlendedIn'] == 'the declared display fit'
                  and all(row['passed'] and row['worstGamebryo255'] <= row['bound255'] + 1.0 for row in blend['data']['pairs']),
                  (blend or {}).get('data', {}).get('pairs'))
    texels = f_check(receipt, 'texel rule')
    report.expect('the texel rule passes with straight texels on every container (the importer stores CHANNEL_PACKED)',
                  texels is not None and texels['status'] != 'fail'
                  and {k: v['texelRule'] for k, v in ((texels.get('data') or {}).get('containers') or {}).items()} == {'png': 'straight', 'dds-bgra8': 'straight', 'dds-bc3': 'straight'},
                  (texels or {}).get('data'))

    base = fresh(root, 'ideal-gamebryo')
    write_scenario(base, renderModel='display')
    code, receipt = drive(os.path.join(base, 'run'), manifest, command='f')
    blend = f_check(receipt, 'blend pairs')
    over = next((row for row in ((blend or {}).get('data') or {}).get('pairs') or [] if row['pair'] == 'SRC_ALPHA/INV_SRC_ALPHA'), None)
    report.expect('an ideal Gamebryo framebuffer holds every declared bound but fails the drawn check (hop F judges the fit the '
                  'package declares, not the source)',
                  code == 1 and blend is not None and blend['status'] == 'fail' and over is not None and over['worstDrawn255'] > 1.0
                  and all(row['worstGamebryo255'] <= row['bound255'] + 1.0 for row in blend['data']['pairs']),
                  (code, over))
    lit = f_check(receipt, 'lit route')
    report.expect('an ideal Gamebryo framebuffer blending the lit display color holds every declared lit bound but fails the lit '
                  'check (hop F judges the lit curve the package declares)',
                  lit is not None and lit['status'] == 'fail'
                  and all(row['worstGamebryo255'] <= row['litBound255'] + 1.0 for row in lit['data']['pairs'])
                  and any(row['worstDrawn255'] > 1.0 for row in lit['data']['pairs']), (lit or {}).get('data'))

    base = fresh(root, 'texels-premultiplied')
    write_scenario(base, renderModel='drawn', imageAlphaMode='STRAIGHT')
    code, receipt = drive(os.path.join(base, 'run'), manifest, command='f')
    texels = f_check(receipt, 'texel rule')
    blend = f_check(receipt, 'blend pairs')
    report.expect("an importer that leaves images at Blender's default STRAIGHT fails the texel rule on every container (named "
                  "cycles-premultiplied) and fails the blend pairs under the drawn reference",
                  code == 1 and texels is not None and blend is not None and texels['status'] == 'fail' and blend['status'] == 'fail'
                  and {v['texelRule'] for v in ((texels.get('data') or {}).get('containers') or {}).values()} == {'cycles-premultiplied'},
                  (code, ((texels or {}).get('data') or {}).get('containers'), (blend or {}).get('status')))

    base = fresh(root, 'stale-control')
    write_scenario(base, renderModel='drawn')
    out_dir = os.path.join(base, 'run')
    code_first, _ = drive(out_dir, manifest, command='f')
    stale = os.path.join(out_dir, 'work', 'f', 'blend-pairs-cycles-factor-swap-control', 'probe-result.json')
    had_result = os.path.isfile(stale)
    os.remove(os.path.join(out_dir, 'launches', 'f', 'blend-pairs-cycles-factor-swap-control', 'import.json'))
    write_scenario(base, renderModel='drawn', importFailureMatching=['blend-pairs-cycles-factor-swap-control'])
    code, receipt = drive(out_dir, manifest, command='f')
    et = control_named(receipt, 'f', 'blend-pairs')
    report.expect('a control whose import now fails is NEVER EXERCISED even though an earlier run left its probe-result.json (never judged stale)',
                  code_first == 0 and had_result and os.path.isfile(stale) and code == 1 and et['detected'] is None
                  and 'control launches failed' in et['detail'], (code_first, had_result, code, et['detail']))

    base = fresh(root, 'dds-flipped')
    write_scenario(base, renderModel='drawn', flipDdsOnLoad=True)
    code, receipt = drive(os.path.join(base, 'run'), manifest, command='f')
    check = f_check(receipt, 'DDS orientation')
    orientation = check['data']['loadedBufferOrientation']
    flip = control_named(receipt, 'f', 'dds-orientation')
    report.expect('DDS loading upside down fails the orientation render, the buffers read flipped, and its control reads masked',
                  code == 1 and check['status'] == 'fail' and orientation.get('dds-bc1', {}).get('orientation') == 'flipped'
                  and orientation.get('png', {}).get('orientation') == 'same' and flip['detected'] is None,
                  (check['status'], orientation, flip['detail']))

    base = fresh(root, 'transmission-dropped')
    write_scenario(base, renderModel='drawn', dropTransmission=True)
    code, receipt = drive(os.path.join(base, 'run'), manifest, command='f')
    check = f_check(receipt, 'transmission route')
    strip = control_named(receipt, 'f', 'transmission')
    report.expect('an importer that drops transmission fails the route (readback exits 3) and masks its control',
                  code == 1 and check['status'] == 'fail' and strip['detected'] is None, (check['firstMismatch'], strip['detail']))

    base = fresh(root, 'transmission-crossed')
    write_scenario(base, renderModel='drawn', crossTransmissionBindings=True)
    code, receipt = drive(os.path.join(base, 'run'), manifest, command='f')
    check = f_check(receipt, 'transmission route')
    swap = control_named(receipt, 'f', 'E/T swap')
    report.expect('an importer that exchanges E and T passes readback.py (some image feeds each socket) but fails the binding identity, masking the swap control',
                  code == 1 and check['status'] == 'fail' and check['firstMismatch']['where'].startswith('bindings/')
                  and not (check['data'].get('readbackFailures') or []) and swap['detected'] is None, (check['firstMismatch'], swap['detail']))

    base = fresh(root, 'transmission-no-vertex-color')
    write_scenario(base, renderModel='drawn', dropVertexColor=True)
    code, receipt = drive(os.path.join(base, 'run'), manifest, command='f')
    check = f_check(receipt, 'transmission route')
    report.expect('an importer that drops COLOR_0 fails T = baseColor x COLOR_0 (no vertex color into Base Color, no color attribute)',
                  code == 1 and check['status'] == 'fail' and any(m['where'] == 'bindings/Base Color/vertex color' for m in check['data']['bindings']['mismatches']),
                  check['data'].get('bindings'))

    base = fresh(root, 'shape-key-candidate-masked')
    write_scenario(base, corruptShapeKeysMatching=['inputs/d.zip'])
    code, receipt = drive(os.path.join(base, 'run'), manifest, command='d')
    shape = next(c for c in receipt['hops']['d']['controls'] if 'shape-key' in c['name'])
    report.expect('a D control candidate masked at its element is skipped before any launch and the control runs on the next candidate',
                  code == 1 and shape['detected'] is True and shape.get('onUnit') == 'synthetic-d-set'
                  and len(shape.get('maskedCandidatesSkipped') or []) == 1, (shape.get('onUnit'), shape['detail']))

    base = fresh(root, 'shape-keys-corrupt')
    write_scenario(base, corruptShapeKeys=True)
    code, receipt = drive(os.path.join(base, 'run'), manifest, command='d')
    shape = control_named(receipt, 'd', 'shape-key')
    report.expect('corrupted shape keys fail hop D at shape_keys and mask the shape-key control',
                  code == 1 and hop(receipt, 'd')['samplesFailed'] >= 1 and shape['detected'] is None, (hop(receipt, 'd'), shape['detail']))

    base = fresh(root, 'import-refused')
    write_scenario(base, importFailure='Material 0: synthetic refusal')
    code, receipt = drive(os.path.join(base, 'run'), manifest, command='d')
    d = hop(receipt, 'd')
    report.expect('a refusing importer fails every D sample and leaves both controls NEVER EXERCISED',
                  code == 1 and d['samplesPassed'] == 0 and d['controlsNeverExercised'] == 2, d)

    base = fresh(root, 'bprime-control-silent')
    write_scenario(base)
    code, receipt = drive(os.path.join(base, 'run'), manifest, command='bprime')
    control = control_named(receipt, 'bprime', 'VEC4')
    report.expect("a B' control that imports cleanly in all 12 fresh processes reads NOT DETECTED and fails the hop",
                  code == 1 and control['detected'] is False and not hop(receipt, 'bprime')['passed'], control['detail'])

    base = fresh(root, 'bprime-control-unrelated')
    write_scenario(base, gltfCustomAttributeFailEvery=3, gltfCustomAttributeMessage='the add-on refused an underscore attribute')
    code, receipt = drive(os.path.join(base, 'run'), manifest, command='bprime')
    control = control_named(receipt, 'bprime', 'VEC4')
    report.expect("a B' control that fails WITHOUT the set-order signature reads INCONCLUSIVE and fails the hop",
                  code == 1 and control['detected'] is None and 'WITHOUT the set-order signature' in control['detail'], control['detail'])

    base = fresh(root, 'bprime-control-always')
    write_scenario(base, gltfCustomAttributeFailEvery=1)
    code, receipt = drive(os.path.join(base, 'run'), manifest, command='bprime')
    control = control_named(receipt, 'bprime', 'VEC4')
    report.expect("a B' control that fails in EVERY fresh process reads INCONCLUSIVE (the defect is intermittent by construction)",
                  code == 1 and control['detected'] is None and 'every one of the 12' in control['detail'], control['detail'])

    base = fresh(root, 'bprime-disagree')
    write_scenario(base, volatileReadbackInvocations=[3], gltfCustomAttributeFailEvery=2)
    code, receipt = drive(os.path.join(base, 'run'), manifest, '--repeats', '3', command='bprime')
    first = receipt['hops']['bprime']['samples'][0]
    report.expect("a pristine GLB whose fresh processes disagree fails its sample (--repeats 3 marks the receipt SUBSET)",
                  not first['passed'] and first['data']['distinctDumps'] == 2 and 'repeats 3' in receipt['summary']['subset'], first['detail'])

    base = fresh(root, 'bprime-crash')
    write_scenario(base, crashInvocations=[2], gltfCustomAttributeFailEvery=2)
    code, receipt = drive(os.path.join(base, 'run'), manifest, '--repeats', '3', command='bprime')
    first = receipt['hops']['bprime']['samples'][0]
    report.expect("a pristine B' launch that crashes (0xC0000005) fails its sample as an importer failure",
                  not first['passed'] and first['data']['importerFailures'] == 1, first['detail'])


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--out', default=os.path.join(REPO, 'TestOutput', 'gate1a-blender-dev',
                                                      'selfcheck-%s' % datetime.datetime.now().strftime('%Y%m%d-%H%M%S')))
    parser.add_argument('--skip-end-to-end', action='store_true')
    parser.add_argument('--real-packages', type=int, default=None,
                        help='how many real gate packages the import_rules cross-check reads (default all; 0 skips it)')
    args = parser.parse_args(argv)
    os.makedirs(args.out, exist_ok=True)
    # The fake is a Python process; one BLAS thread keeps its commit near 50 MB instead of about 650 MB.
    os.environ['OPENBLAS_NUM_THREADS'] = '1'
    os.environ['OMP_NUM_THREADS'] = '1'
    report = Report()
    started = time.time()
    try:
        section_launcher(report, args.out)
        manifest = small_manifest(args.out)
        section_driver(report, args.out, manifest)
        section_long_paths(report, args.out)
        section_comparators(report, args.out, args.real_packages)
        if not args.skip_end_to_end:
            section_end_to_end(report, args.out, manifest)
    except Exception as failure:  # noqa: BLE001 - a self-check defect is a failure, recorded with its traceback
        report.expect('the self-check ran to completion', False, traceback.format_exc())
    document = {'schema': 'gate1a-blender-selfcheck/1', 'finished': datetime.datetime.now(datetime.timezone.utc).isoformat(),
                'seconds': round(time.time() - started, 1), 'expectations': len(report.rows), 'failures': len(report.failures),
                'rows': report.rows, 'harness': run.harness_identity(),
                'shared': {'readback': bl.sha256_file(READBACK), 'importModel': bl.sha256_file(IMPORT_MODEL), 'gitHead': run.git_head(SHARED)}}
    with open(os.path.join(args.out, 'selfcheck.json'), 'w', encoding='utf-8') as f:
        json.dump(document, f, indent=1)
    print('\n%d expectations, %d failed, %.0f s; %s' % (len(report.rows), len(report.failures), time.time() - started,
                                                          os.path.join(args.out, 'selfcheck.json')))
    return 1 if report.failures else 0


if __name__ == '__main__':
    sys.exit(main())
