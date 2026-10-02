# SPDX-License-Identifier: 0BSD
"""The serial, memory-gated launcher every Blender process of hops B', D and F goes through.

The owner's conditions (2026-09-25), each enforced here rather than left to the caller:

* Exactly one Blender process at a time. Launches are strictly serial (``Launcher.launch`` waits for its process to
  exit before returning), the whole run holds the named mutex ``RUN_MUTEX`` so two harness runs cannot overlap, and
  admission also waits while any other ``blender.exe`` is running on the machine (``find_processes``).
* Before each launch, wait until available physical memory is at least 6 GiB (``OWNER_MIN_FREE_BYTES``), polling;
  after ``admission_timeout`` seconds the run ABORTS (``AdmissionTimeout``). There is no bypass: the floor cannot be
  lowered (``Limits`` refuses a smaller value; a larger one is allowed).
* Launch at BelowNormal priority (``BELOW_NORMAL_PRIORITY_CLASS``), verified with ``GetPriorityClass`` and recorded;
  a process that reads back at any other priority is killed (outcome ``priority-not-below-normal``).
* A watchdog kills the process when available memory drops below 2 GiB (``OWNER_KILL_FREE_BYTES``) or its private
  bytes exceed 4 GiB (``OWNER_MAX_PRIVATE_BYTES``); either is recorded as a failure (outcome ``watchdog-free-memory``
  or ``watchdog-private-bytes``). The floor cannot be lowered and the ceiling cannot be raised. A process whose
  private bytes or priority cannot be read for ``UNMEASURED_SAMPLES`` consecutive samples is killed too (outcome
  ``watchdog-unmeasured``): a limit that cannot be measured is not enforced, so the launch fails closed.
* A per-launch timeout (outcome ``timeout``).
* Per launch the receipt records the command, exit code, seconds, peak private bytes (the largest sampled
  ``PrivateUsage``, the OS-tracked ``PeakPagefileUsage`` of the process and, when the job object could be assigned,
  the job's ``PeakProcessMemoryUsed``; the maximum is reported) and the minimum available memory seen while it ran.
* Resumable: a launch whose receipt exists, is complete, has the same fingerprint (command, input SHA-256s, script
  SHA-256s, Blender SHA-256) and whose recorded outputs are unchanged is skipped (``LaunchStore.reusable``).

Memory is read through ctypes (``GlobalMemoryStatusEx``: ``ullAvailPhys``, available physical memory; and
``K32GetProcessMemoryInfo``: ``PrivateUsage`` and ``PeakPagefileUsage``); psutil is not required. Each process is
created SUSPENDED, put in a job object with ``JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`` and only then resumed
(``NtResumeProcess``), so any child it starts is in the job from its first instruction and a kill takes the whole
tree. When the job cannot be assigned (a nested-job restriction) the receipt says so and a kill falls back to
``taskkill /T /F`` (the process tree by parent id) and then to killing the process alone. If anything escapes the
monitoring loop (an exception, Ctrl+C), the process tree is killed before the exception propagates, so the harness
never leaves a Blender running behind it.

Every probe, clock and sleep is injectable so ``selfcheck_blender_harness.py`` proves admission, the watchdog, the
timeout and the resume rule with a fake Blender (a Python script) and scripted memory readings. Nothing here imports
bpy or launches anything but the command it is given.
"""

import ctypes
import datetime
import hashlib
import json
import os
import subprocess
import sys
import time

GIB = 1 << 30
OWNER_MIN_FREE_BYTES = 6 * GIB
OWNER_KILL_FREE_BYTES = 2 * GIB
OWNER_MAX_PRIVATE_BYTES = 4 * GIB
DEFAULT_BLENDER = r'C:/Program Files/Blender Foundation/Blender 5.1/blender.exe'
RUN_MUTEX = 'Global\\Multitool.Gate1a.BlenderHops.v1'  # Global: also excludes a run in another logon session
BLENDER_IMAGE_NAME = 'blender.exe'

BELOW_NORMAL_PRIORITY_CLASS = 0x00004000
CREATE_NO_WINDOW = 0x08000000
CREATE_SUSPENDED = 0x00000004
PRIORITY_NAMES = {0x20: 'Normal', 0x40: 'Idle', 0x80: 'High', 0x100: 'Realtime', 0x4000: 'BelowNormal', 0x8000: 'AboveNormal'}

# Removed from the child environment and recorded: PYTHONHASHSEED would make every "fresh" process iterate sets in the
# same order, which defeats hop B' (the importer defect it guards against depends on hash-randomized set order); the
# others could change which Python or which add-ons Blender loads.
SCRUBBED_ENVIRONMENT = ('PYTHONHASHSEED', 'PYTHONPATH', 'PYTHONHOME', 'PYTHONSTARTUP', 'PYTHONUSERBASE',
                        'PYTHONNOUSERSITE', 'PYTHONSAFEPATH', 'BLENDER_USER_SCRIPTS', 'BLENDER_SYSTEM_SCRIPTS',
                        'BLENDER_USER_CONFIG', 'BLENDER_USER_DATAFILES', 'BLENDER_SYSTEM_PYTHON',
                        'BLENDER_SYSTEM_DATAFILES', 'BLENDER_USER_EXTENSIONS', 'BLENDER_SYSTEM_EXTENSIONS')

OUTCOME_OK = 'ok'
OUTCOME_NONZERO = 'nonzero-exit'
OUTCOME_TIMEOUT = 'timeout'
OUTCOME_WATCHDOG_FREE = 'watchdog-free-memory'
OUTCOME_WATCHDOG_PRIVATE = 'watchdog-private-bytes'
OUTCOME_LAUNCH_ERROR = 'launch-error'
OUTCOME_WATCHDOG_UNMEASURED = 'watchdog-unmeasured'
OUTCOME_PRIORITY = 'priority-not-below-normal'
HARNESS_OUTCOMES = (OUTCOME_TIMEOUT, OUTCOME_WATCHDOG_FREE, OUTCOME_WATCHDOG_PRIVATE, OUTCOME_LAUNCH_ERROR,
                    OUTCOME_WATCHDOG_UNMEASURED, OUTCOME_PRIORITY)
# Windows MAX_PATH is 260 characters including the terminating NUL. Blender 5.1 is not long-path aware: its glTF
# importer's isfile() is False for a longer path that exists and it reports "Please select a file" (measured
# 2026-09-25 on 22 of the 57 dacc231 sample GLBs, 264 to 297 characters), and its Python cannot write a longer dump
# path either. A launch whose command carries a longer absolute path is refused before admission, never run.
MAX_PATH_ARGUMENT = 259
UNMEASURED_SAMPLES = 8  # consecutive unreadable samples (2 s at the default 0.25 s poll) before a fail-closed kill
KILL_EXIT_CODE = 0x4B494C4C  # 'KILL', recorded as the exit code of a process the harness terminated


class AdmissionTimeout(Exception):
    """Admission could not be granted within the admission timeout; the run must abort, never bypass."""


class LimitError(ValueError):
    """A requested limit would weaken one of the owner's conditions."""


def utc_now():
    return datetime.datetime.now(datetime.timezone.utc).isoformat()


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for chunk in iter(lambda: f.read(1 << 20), b''):
            h.update(chunk)
    return h.hexdigest()


def gib(value):
    return None if value is None else round(value / GIB, 3)


class Limits:
    """The owner's memory and time limits, refusing any value that weakens them."""

    def __init__(self, min_free_bytes=OWNER_MIN_FREE_BYTES, kill_free_bytes=OWNER_KILL_FREE_BYTES,
                 max_private_bytes=OWNER_MAX_PRIVATE_BYTES, timeout_seconds=900.0, admission_timeout_seconds=3600.0,
                 poll_seconds=0.25, admission_poll_seconds=5.0, allow_test_private_cap=False):
        if min_free_bytes < OWNER_MIN_FREE_BYTES:
            raise LimitError('the admission floor is 6 GiB and cannot be lowered (asked %.3f GiB)' % (min_free_bytes / GIB))
        if kill_free_bytes < OWNER_KILL_FREE_BYTES:
            raise LimitError('the watchdog free-memory floor is 2 GiB and cannot be lowered (asked %.3f GiB)' % (kill_free_bytes / GIB))
        if max_private_bytes > OWNER_MAX_PRIVATE_BYTES:
            raise LimitError('the watchdog private-bytes ceiling is 4 GiB and cannot be raised (asked %.3f GiB)' % (max_private_bytes / GIB))
        if max_private_bytes <= 0 and not allow_test_private_cap:
            raise LimitError('the private-bytes ceiling must be positive')
        if kill_free_bytes >= min_free_bytes:
            raise LimitError('the watchdog floor must be below the admission floor')
        if timeout_seconds <= 0 or admission_timeout_seconds <= 0 or poll_seconds <= 0 or admission_poll_seconds <= 0:
            raise LimitError('timeouts and poll intervals must be positive')
        self.min_free_bytes = int(min_free_bytes)
        self.kill_free_bytes = int(kill_free_bytes)
        self.max_private_bytes = int(max_private_bytes)
        self.timeout_seconds = float(timeout_seconds)
        self.admission_timeout_seconds = float(admission_timeout_seconds)
        self.poll_seconds = float(poll_seconds)
        self.admission_poll_seconds = float(admission_poll_seconds)

    def to_dict(self):
        return {'minFreeBytes': self.min_free_bytes, 'minFreeGiB': gib(self.min_free_bytes),
                'killFreeBytes': self.kill_free_bytes, 'killFreeGiB': gib(self.kill_free_bytes),
                'maxPrivateBytes': self.max_private_bytes, 'maxPrivateGiB': gib(self.max_private_bytes),
                'timeoutSeconds': self.timeout_seconds, 'admissionTimeoutSeconds': self.admission_timeout_seconds,
                'pollSeconds': self.poll_seconds, 'admissionPollSeconds': self.admission_poll_seconds}


# --------------------------------------------------------------------------------------------------------------------
# Windows memory, priority, job objects, process enumeration and the run mutex (ctypes)
# --------------------------------------------------------------------------------------------------------------------

_IS_WINDOWS = os.name == 'nt'
if _IS_WINDOWS:
    from ctypes import wintypes

    class _MEMORYSTATUSEX(ctypes.Structure):
        _fields_ = [('dwLength', wintypes.DWORD), ('dwMemoryLoad', wintypes.DWORD),
                    ('ullTotalPhys', ctypes.c_ulonglong), ('ullAvailPhys', ctypes.c_ulonglong),
                    ('ullTotalPageFile', ctypes.c_ulonglong), ('ullAvailPageFile', ctypes.c_ulonglong),
                    ('ullTotalVirtual', ctypes.c_ulonglong), ('ullAvailVirtual', ctypes.c_ulonglong),
                    ('ullAvailExtendedVirtual', ctypes.c_ulonglong)]

    class _PROCESS_MEMORY_COUNTERS_EX(ctypes.Structure):
        _fields_ = [('cb', wintypes.DWORD), ('PageFaultCount', wintypes.DWORD),
                    ('PeakWorkingSetSize', ctypes.c_size_t), ('WorkingSetSize', ctypes.c_size_t),
                    ('QuotaPeakPagedPoolUsage', ctypes.c_size_t), ('QuotaPagedPoolUsage', ctypes.c_size_t),
                    ('QuotaPeakNonPagedPoolUsage', ctypes.c_size_t), ('QuotaNonPagedPoolUsage', ctypes.c_size_t),
                    ('PagefileUsage', ctypes.c_size_t), ('PeakPagefileUsage', ctypes.c_size_t),
                    ('PrivateUsage', ctypes.c_size_t)]

    class _IO_COUNTERS(ctypes.Structure):
        _fields_ = [(name, ctypes.c_ulonglong) for name in ('ReadOperationCount', 'WriteOperationCount',
                                                              'OtherOperationCount', 'ReadTransferCount',
                                                              'WriteTransferCount', 'OtherTransferCount')]

    class _JOBOBJECT_BASIC_LIMIT_INFORMATION(ctypes.Structure):
        _fields_ = [('PerProcessUserTimeLimit', ctypes.c_longlong), ('PerJobUserTimeLimit', ctypes.c_longlong),
                    ('LimitFlags', wintypes.DWORD), ('MinimumWorkingSetSize', ctypes.c_size_t),
                    ('MaximumWorkingSetSize', ctypes.c_size_t), ('ActiveProcessLimit', wintypes.DWORD),
                    ('Affinity', ctypes.c_size_t), ('PriorityClass', wintypes.DWORD), ('SchedulingClass', wintypes.DWORD)]

    class _JOBOBJECT_EXTENDED_LIMIT_INFORMATION(ctypes.Structure):
        _fields_ = [('BasicLimitInformation', _JOBOBJECT_BASIC_LIMIT_INFORMATION), ('IoInfo', _IO_COUNTERS),
                    ('ProcessMemoryLimit', ctypes.c_size_t), ('JobMemoryLimit', ctypes.c_size_t),
                    ('PeakProcessMemoryUsed', ctypes.c_size_t), ('PeakJobMemoryUsed', ctypes.c_size_t)]

    class _PROCESSENTRY32W(ctypes.Structure):
        _fields_ = [('dwSize', wintypes.DWORD), ('cntUsage', wintypes.DWORD), ('th32ProcessID', wintypes.DWORD),
                    ('th32DefaultHeapID', ctypes.c_size_t), ('th32ModuleID', wintypes.DWORD),
                    ('cntThreads', wintypes.DWORD), ('th32ParentProcessID', wintypes.DWORD),
                    ('pcPriClassBase', ctypes.c_long), ('dwFlags', wintypes.DWORD), ('szExeFile', ctypes.c_wchar * 260)]

    _kernel32 = ctypes.WinDLL('kernel32', use_last_error=True)
    _kernel32.GlobalMemoryStatusEx.argtypes = [ctypes.POINTER(_MEMORYSTATUSEX)]
    _kernel32.GlobalMemoryStatusEx.restype = wintypes.BOOL
    _kernel32.K32GetProcessMemoryInfo.argtypes = [wintypes.HANDLE, ctypes.POINTER(_PROCESS_MEMORY_COUNTERS_EX), wintypes.DWORD]
    _kernel32.K32GetProcessMemoryInfo.restype = wintypes.BOOL
    _kernel32.GetPriorityClass.argtypes = [wintypes.HANDLE]
    _kernel32.GetPriorityClass.restype = wintypes.DWORD
    _kernel32.CreateJobObjectW.argtypes = [ctypes.c_void_p, wintypes.LPCWSTR]
    _kernel32.CreateJobObjectW.restype = wintypes.HANDLE
    _kernel32.SetInformationJobObject.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD]
    _kernel32.SetInformationJobObject.restype = wintypes.BOOL
    _kernel32.QueryInformationJobObject.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD, ctypes.c_void_p]
    _kernel32.QueryInformationJobObject.restype = wintypes.BOOL
    _kernel32.AssignProcessToJobObject.argtypes = [wintypes.HANDLE, wintypes.HANDLE]
    _kernel32.AssignProcessToJobObject.restype = wintypes.BOOL
    _kernel32.TerminateJobObject.argtypes = [wintypes.HANDLE, wintypes.UINT]
    _kernel32.TerminateJobObject.restype = wintypes.BOOL
    _kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
    _kernel32.CloseHandle.restype = wintypes.BOOL
    _kernel32.CreateToolhelp32Snapshot.argtypes = [wintypes.DWORD, wintypes.DWORD]
    _kernel32.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
    _kernel32.Process32FirstW.argtypes = [wintypes.HANDLE, ctypes.POINTER(_PROCESSENTRY32W)]
    _kernel32.Process32FirstW.restype = wintypes.BOOL
    _kernel32.Process32NextW.argtypes = [wintypes.HANDLE, ctypes.POINTER(_PROCESSENTRY32W)]
    _kernel32.Process32NextW.restype = wintypes.BOOL
    _kernel32.CreateMutexW.argtypes = [ctypes.c_void_p, wintypes.BOOL, wintypes.LPCWSTR]
    _kernel32.CreateMutexW.restype = wintypes.HANDLE
    _kernel32.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
    _kernel32.WaitForSingleObject.restype = wintypes.DWORD
    _kernel32.ReleaseMutex.argtypes = [wintypes.HANDLE]
    _kernel32.ReleaseMutex.restype = wintypes.BOOL
    _ntdll = ctypes.WinDLL('ntdll')
    _ntdll.NtResumeProcess.argtypes = [wintypes.HANDLE]
    _ntdll.NtResumeProcess.restype = ctypes.c_long  # NTSTATUS, 0 on success

_INVALID_HANDLE = ctypes.c_void_p(-1).value
_JOB_EXTENDED_LIMIT_INFORMATION = 9
_JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000
_TH32CS_SNAPPROCESS = 0x2
_WAIT_OBJECT_0, _WAIT_ABANDONED, _WAIT_TIMEOUT = 0x0, 0x80, 0x102


def _process_handle(proc):
    """The Win32 handle CPython's Popen keeps for a child (``Popen._handle``, a Handle int subclass)."""
    handle = getattr(proc, '_handle', None)
    return int(handle) if handle is not None else None


class WindowsMemoryProbe:
    """Real readings: available physical memory, and a process's private bytes (current and OS-tracked peak)."""

    name = 'GlobalMemoryStatusEx.ullAvailPhys + K32GetProcessMemoryInfo'

    def __init__(self):
        if not _IS_WINDOWS:
            raise OSError('WindowsMemoryProbe requires Windows; this harness does not measure memory elsewhere')

    def _status(self):
        status = _MEMORYSTATUSEX()
        status.dwLength = ctypes.sizeof(_MEMORYSTATUSEX)
        if not _kernel32.GlobalMemoryStatusEx(ctypes.byref(status)):
            raise ctypes.WinError(ctypes.get_last_error())
        return status

    def available_physical_bytes(self):
        return int(self._status().ullAvailPhys)

    def total_physical_bytes(self):
        return int(self._status().ullTotalPhys)

    def process_memory(self, proc):
        """{'private', 'peakPrivate', 'workingSet', 'peakWorkingSet'} in bytes, or None when unreadable."""
        handle = _process_handle(proc)
        if handle is None:
            return None
        counters = _PROCESS_MEMORY_COUNTERS_EX()
        counters.cb = ctypes.sizeof(_PROCESS_MEMORY_COUNTERS_EX)
        if not _kernel32.K32GetProcessMemoryInfo(handle, ctypes.byref(counters), counters.cb):
            return None
        return {'private': int(counters.PrivateUsage), 'peakPrivate': int(counters.PeakPagefileUsage),
                'workingSet': int(counters.WorkingSetSize), 'peakWorkingSet': int(counters.PeakWorkingSetSize)}

    def priority_class(self, proc):
        handle = _process_handle(proc)
        if handle is None:
            return None
        value = _kernel32.GetPriorityClass(handle)
        return int(value) if value else None


class JobObject:
    """A kill-on-close job holding one child; ``assign`` returns None when the OS refuses the assignment."""

    def __init__(self, handle):
        self.handle = handle

    @classmethod
    def assign(cls, proc):
        if not _IS_WINDOWS:
            return None, 'not Windows'
        job = _kernel32.CreateJobObjectW(None, None)
        if not job:
            return None, 'CreateJobObjectW failed (%d)' % ctypes.get_last_error()
        info = _JOBOBJECT_EXTENDED_LIMIT_INFORMATION()
        info.BasicLimitInformation.LimitFlags = _JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if not _kernel32.SetInformationJobObject(job, _JOB_EXTENDED_LIMIT_INFORMATION, ctypes.byref(info), ctypes.sizeof(info)):
            error = ctypes.get_last_error()
            _kernel32.CloseHandle(job)
            return None, 'SetInformationJobObject failed (%d)' % error
        handle = _process_handle(proc)
        if handle is None or not _kernel32.AssignProcessToJobObject(job, handle):
            error = ctypes.get_last_error()
            _kernel32.CloseHandle(job)
            return None, 'AssignProcessToJobObject failed (%d)' % error
        return cls(job), None

    def peak_process_memory(self):
        info = _JOBOBJECT_EXTENDED_LIMIT_INFORMATION()
        if not _kernel32.QueryInformationJobObject(self.handle, _JOB_EXTENDED_LIMIT_INFORMATION, ctypes.byref(info),
                                                   ctypes.sizeof(info), None):
            return None
        return int(info.PeakProcessMemoryUsed)

    def terminate(self, code):
        return bool(_kernel32.TerminateJobObject(self.handle, code))

    def close(self):
        if self.handle:
            _kernel32.CloseHandle(self.handle)
            self.handle = None


def resume_process(proc):
    """Resume every thread of a process created with CREATE_SUSPENDED; returns None on success, else the reason."""
    if not _IS_WINDOWS:
        return None
    handle = _process_handle(proc)
    if handle is None:
        return 'no process handle'
    status = _ntdll.NtResumeProcess(handle)
    return None if status == 0 else 'NtResumeProcess failed (NTSTATUS 0x%08X)' % (status & 0xFFFFFFFF)


def kill_process_tree(proc, job=None):
    """Kill ``proc`` and everything it started: the kill-on-close job when there is one, else ``taskkill /T /F`` (the
    tree by parent id), else the process alone. Returns the method used."""
    if job is not None and job.terminate(KILL_EXIT_CODE):
        return 'job'
    if _IS_WINDOWS:
        try:
            completed = subprocess.run(['taskkill', '/T', '/F', '/PID', str(proc.pid)], capture_output=True, timeout=60)
            if completed.returncode == 0:
                return 'taskkill-tree'
        except (OSError, subprocess.TimeoutExpired):
            pass
    try:
        proc.kill()
    except OSError:
        pass
    return 'process'


def find_processes(image_name, exclude_pids=()):
    """PIDs of running processes whose image name equals ``image_name`` (case-insensitive), via Toolhelp32."""
    if not _IS_WINDOWS:
        return []
    snapshot = _kernel32.CreateToolhelp32Snapshot(_TH32CS_SNAPPROCESS, 0)
    if not snapshot or snapshot == _INVALID_HANDLE:
        raise ctypes.WinError(ctypes.get_last_error())
    found = []
    try:
        entry = _PROCESSENTRY32W()
        entry.dwSize = ctypes.sizeof(_PROCESSENTRY32W)
        ok = _kernel32.Process32FirstW(snapshot, ctypes.byref(entry))
        while ok:
            if entry.szExeFile.lower() == image_name.lower() and entry.th32ProcessID not in exclude_pids:
                found.append(int(entry.th32ProcessID))
            ok = _kernel32.Process32NextW(snapshot, ctypes.byref(entry))
    finally:
        _kernel32.CloseHandle(snapshot)
    return found


class RunLock:
    """The named mutex held for a whole harness run, so two runs never launch Blender side by side."""

    def __init__(self, name=RUN_MUTEX):
        self.name = name
        self.handle = None

    def acquire(self, timeout_seconds):
        if not _IS_WINDOWS:
            return True
        handle = _kernel32.CreateMutexW(None, False, self.name)
        if not handle:
            raise ctypes.WinError(ctypes.get_last_error())
        result = _kernel32.WaitForSingleObject(handle, int(max(0.0, timeout_seconds) * 1000))
        if result in (_WAIT_OBJECT_0, _WAIT_ABANDONED):
            self.handle = handle
            return True
        _kernel32.CloseHandle(handle)
        return False

    def release(self):
        if self.handle:
            _kernel32.ReleaseMutex(self.handle)
            _kernel32.CloseHandle(self.handle)
            self.handle = None


# --------------------------------------------------------------------------------------------------------------------
# Commands and environment
# --------------------------------------------------------------------------------------------------------------------


def is_python_fake(blender_path):
    """A ``.py`` Blender path is a fake for the self-check: it is run through this interpreter."""
    return str(blender_path).lower().endswith('.py')


def executable_prefix(blender_path):
    if is_python_fake(blender_path):
        return [sys.executable, '-X', 'utf8', os.path.abspath(blender_path)]
    return [blender_path]


def blender_script_command(blender_path, script, script_args, blend_file=None):
    """``blender --background --factory-startup --python-exit-code 1 [file.blend] --python <script> -- <args>``."""
    command = executable_prefix(blender_path) + ['--background', '--factory-startup', '--python-exit-code', '1']
    if blend_file:
        command.append(os.path.abspath(blend_file))
    return command + ['--python', os.path.abspath(script), '--'] + [str(a) for a in script_args]


def child_environment(base=None):
    """(environment, scrubbed names): the parent environment minus the names in SCRUBBED_ENVIRONMENT."""
    env = dict(os.environ if base is None else base)
    removed = sorted(name for name in list(env) if name.upper() in SCRUBBED_ENVIRONMENT)
    for name in removed:
        env.pop(name, None)
    return env, removed


# --------------------------------------------------------------------------------------------------------------------
# Admission, watchdog, timeout
# --------------------------------------------------------------------------------------------------------------------


class Launcher:
    """Admission plus one watched process at a time. ``probe`` needs available_physical_bytes(), process_memory(proc)
    and priority_class(proc); ``foreign_processes()`` returns the PIDs of other Blender processes; ``clock`` and
    ``sleep`` drive the admission wait."""

    def __init__(self, limits, probe=None, foreign_processes=None, clock=time.monotonic, sleep=time.sleep,
                 blender_image_name=BLENDER_IMAGE_NAME, log=None):
        self.limits = limits
        self.probe = probe if probe is not None else WindowsMemoryProbe()
        self.clock = clock
        self.sleep = sleep
        self.blender_image_name = blender_image_name
        self._own_pids = set()
        if foreign_processes is None:
            foreign_processes = lambda: find_processes(self.blender_image_name, self._own_pids)  # noqa: E731
        self.foreign_processes = foreign_processes
        self.log = log or (lambda message: None)
        self.active = False

    def admit(self, label):
        """Wait for available memory >= the floor and no other Blender; raise AdmissionTimeout after the timeout."""
        start = self.clock()
        polls = 0
        lowest = None
        while True:
            available = self.probe.available_physical_bytes()
            foreign = list(self.foreign_processes())
            lowest = available if lowest is None else min(lowest, available)
            polls += 1
            waited = self.clock() - start
            if available >= self.limits.min_free_bytes and not foreign:
                return {'admitted': True, 'availableBytes': available, 'availableGiB': gib(available),
                        'waitedSeconds': round(waited, 3), 'polls': polls, 'lowestAvailableBytesWhileWaiting': lowest,
                        'foreignBlenderProcesses': []}
            if waited >= self.limits.admission_timeout_seconds:
                raise AdmissionTimeout('%s: not admitted after %.0f s (available %.3f GiB, floor %.3f GiB, other Blender '
                                       'processes %s); the run aborts rather than bypass the gate'
                                       % (label, waited, available / GIB, self.limits.min_free_bytes / GIB, foreign or 'none'))
            if polls == 1 or polls % 12 == 0:
                self.log('    waiting for admission (%s): available %.2f GiB (need %.2f), other Blender processes %s'
                         % (label, available / GIB, self.limits.min_free_bytes / GIB, foreign or 'none'))
            self.sleep(self.limits.admission_poll_seconds)

    def run(self, command, stdout_path, stderr_path, env=None, cwd=None, timeout_seconds=None):
        """Start ``command`` suspended at BelowNormal priority, put it in a kill-on-close job, resume it, and watch it
        until it exits, times out or the watchdog fires. Whatever escapes the loop kills the process tree first."""
        if self.active:
            raise RuntimeError('a Blender process is already running under this launcher; launches are serial')
        timeout = float(timeout_seconds or self.limits.timeout_seconds)
        record = {'startedUtc': utc_now(), 'timeoutSeconds': timeout, 'outcome': None, 'exitCode': None,
                  'killedBy': None, 'seconds': None, 'peakPrivateBytes': None, 'peakPrivateSources': {},
                  'minAvailableBytes': None, 'memorySamples': 0, 'unmeasuredMemorySamples': 0, 'priorityClass': None,
                  'jobObject': None, 'createdSuspended': _IS_WINDOWS, 'pid': None}
        flags = (BELOW_NORMAL_PRIORITY_CLASS | CREATE_NO_WINDOW | CREATE_SUSPENDED) if _IS_WINDOWS else 0
        os.makedirs(os.path.dirname(os.path.abspath(stdout_path)), exist_ok=True)
        started = self.clock()
        with open(stdout_path, 'wb') as out, open(stderr_path, 'wb') as err:
            try:
                proc = subprocess.Popen(command, stdout=out, stderr=err, stdin=subprocess.DEVNULL, env=env, cwd=cwd,
                                        creationflags=flags)
            except OSError as failure:
                record.update(outcome=OUTCOME_LAUNCH_ERROR, seconds=0.0, error='%s: %s' % (type(failure).__name__, failure),
                              finishedUtc=utc_now())
                return record
            self.active = True
            self._own_pids.add(proc.pid)
            record['pid'] = proc.pid
            job = None
            sampled_private = None
            os_peak = None
            lowest = None
            finished = False
            try:
                job, job_error = (JobObject.assign(proc) if _IS_WINDOWS else (None, 'not Windows'))
                record['jobObject'] = job is not None
                if job_error:
                    record['jobObjectError'] = job_error
                resume_error = resume_process(proc)
                if resume_error:
                    record['killedBy'] = OUTCOME_LAUNCH_ERROR
                    record['error'] = resume_error
                    record['killMethod'] = kill_process_tree(proc, job)
                    proc.wait(timeout=60)
                    finished = True
                unmeasured = 0
                while not finished:
                    code = proc.poll()
                    memory = self.probe.process_memory(proc)
                    available = self.probe.available_physical_bytes()
                    record['memorySamples'] += 1
                    lowest = available if lowest is None else min(lowest, available)
                    if record['priorityClass'] is None:
                        priority = self.probe.priority_class(proc)
                        if priority is not None:
                            record['priorityClass'] = PRIORITY_NAMES.get(priority, hex(priority))
                    if memory is not None:
                        sampled_private = memory['private'] if sampled_private is None else max(sampled_private, memory['private'])
                        if memory.get('peakPrivate') is not None:
                            os_peak = memory['peakPrivate'] if os_peak is None else max(os_peak, memory['peakPrivate'])
                    if code is not None:
                        break
                    if memory is None or record['priorityClass'] is None:
                        unmeasured += 1
                        record['unmeasuredMemorySamples'] += 1
                    else:
                        unmeasured = 0
                    reason = None
                    if available < self.limits.kill_free_bytes:
                        reason = OUTCOME_WATCHDOG_FREE
                    elif memory is not None and memory['private'] > self.limits.max_private_bytes:
                        reason = OUTCOME_WATCHDOG_PRIVATE
                    elif record['priorityClass'] is not None and record['priorityClass'] != 'BelowNormal':
                        reason = OUTCOME_PRIORITY
                    elif unmeasured >= UNMEASURED_SAMPLES:
                        reason = OUTCOME_WATCHDOG_UNMEASURED
                    elif self.clock() - started > timeout:
                        reason = OUTCOME_TIMEOUT
                    if reason:
                        record['killedBy'] = reason
                        record['killDetail'] = {'availableBytes': available, 'privateBytes': memory['private'] if memory else None,
                                                'priorityClass': record['priorityClass'],
                                                'consecutiveUnmeasuredSamples': unmeasured,
                                                'elapsedSeconds': round(self.clock() - started, 3)}
                        record['killMethod'] = kill_process_tree(proc, job)
                        try:
                            proc.wait(timeout=60)
                        except subprocess.TimeoutExpired:
                            record['killDetail']['stillRunningAfter60s'] = True
                        break
                    try:
                        proc.wait(timeout=self.limits.poll_seconds)
                    except subprocess.TimeoutExpired:
                        pass
                finished = True
            finally:
                if not finished and proc.poll() is None:
                    # An exception (or Ctrl+C) escaped the loop: never leave the process tree running behind us.
                    try:
                        kill_process_tree(proc, job)
                        proc.wait(timeout=60)
                    except Exception:  # noqa: BLE001 - the original exception is the one that propagates
                        pass
                job_peak = job.peak_process_memory() if job is not None else None
                if job is not None:
                    job.close()
                self.active = False
                self._own_pids.discard(proc.pid)
        record['seconds'] = round(self.clock() - started, 3)
        record['finishedUtc'] = utc_now()
        record['exitCode'] = proc.returncode
        if isinstance(proc.returncode, int) and proc.returncode < 0:
            record['exitCodeHex'] = hex(proc.returncode & 0xFFFFFFFF)
        elif isinstance(proc.returncode, int):
            record['exitCodeHex'] = hex(proc.returncode)
        sources = {'sampledPrivateUsage': sampled_private, 'osPeakPagefileUsage': os_peak, 'jobPeakProcessMemoryUsed': job_peak}
        record['peakPrivateSources'] = sources
        known = [v for v in sources.values() if v is not None]
        record['peakPrivateBytes'] = max(known) if known else None
        record['peakPrivateGiB'] = gib(record['peakPrivateBytes'])
        record['minAvailableBytes'] = lowest
        record['minAvailableGiB'] = gib(lowest)
        if record['killedBy']:
            record['outcome'] = record['killedBy']
        else:
            record['outcome'] = OUTCOME_OK if proc.returncode == 0 else OUTCOME_NONZERO
        return record


# --------------------------------------------------------------------------------------------------------------------
# Launch receipts and resume
# --------------------------------------------------------------------------------------------------------------------


def overlong_path_arguments(command):
    """The absolute path arguments of ``command`` longer than MAX_PATH_ARGUMENT characters."""
    return [argument for argument in command if os.path.isabs(argument) and len(argument) > MAX_PATH_ARGUMENT]


def safe_name(text, limit=120):
    return ''.join(c if c.isalnum() or c in '-_.' else '_' for c in str(text))[:limit]


def head_text(path, limit=65536):
    """Returns the first ``limit`` bytes of a text file, or None when it cannot be read."""
    try:
        with open(path, 'rb') as f:
            return f.read(limit).decode('utf-8', 'replace')
    except OSError:
        return None


def tail_text(path, limit=4000):
    try:
        with open(path, 'rb') as f:
            f.seek(0, os.SEEK_END)
            size = f.tell()
            f.seek(max(0, size - limit))
            return f.read().decode('utf-8', 'replace')
    except OSError:
        return None


class LaunchSpec:
    """One Blender process: its unit (sample or probe), name, command, input/script/output files and timeout."""

    def __init__(self, hop, unit, name, command, inputs=None, scripts=None, outputs=None, timeout_seconds=None, note=None):
        self.hop = hop
        self.unit = unit
        self.name = name
        self.command = [str(c) for c in command]
        self.inputs = dict(inputs or {})
        self.scripts = dict(scripts or {})
        self.outputs = dict(outputs or {})
        self.timeout_seconds = timeout_seconds
        self.note = note


class LaunchStore:
    """Receipts under ``<root>/launches/<hop>/<unit>/<name>.json`` with the process logs beside them."""

    SCHEMA = 'gate1a-blender-launch/1'

    def __init__(self, root, blender_identity):
        self.root = root
        self.blender_identity = blender_identity
        self._hash_cache = {}

    def directory(self, spec):
        return os.path.join(self.root, 'launches', safe_name(spec.hop), safe_name(spec.unit))

    def receipt_path(self, spec):
        return os.path.join(self.directory(spec), safe_name(spec.name) + '.json')

    def log_paths(self, spec):
        base = os.path.join(self.directory(spec), safe_name(spec.name))
        return base + '.stdout.txt', base + '.stderr.txt'

    def file_hash(self, path):
        if not path or not os.path.isfile(path):
            return None
        stat = os.stat(path)
        key = (os.path.abspath(path), stat.st_size, stat.st_mtime_ns)
        if key not in self._hash_cache:
            self._hash_cache[key] = sha256_file(path)
        return self._hash_cache[key]

    def fingerprint(self, spec):
        document = {'command': spec.command,
                    'inputs': {role: self.file_hash(path) for role, path in sorted(spec.inputs.items())},
                    'scripts': {role: self.file_hash(path) for role, path in sorted(spec.scripts.items())},
                    'blender': self.blender_identity.get('sha256')}
        return hashlib.sha256(json.dumps(document, sort_keys=True).encode('utf-8')).hexdigest(), document

    def load(self, spec):
        path = self.receipt_path(spec)
        if not os.path.isfile(path):
            return None
        try:
            with open(path, 'r', encoding='utf-8') as f:
                return json.load(f)
        except (OSError, ValueError):
            return None

    def reusable(self, spec, retry_failed=False):
        """(receipt, reason): the stored receipt when the launch may be skipped, else (None, why it must run)."""
        receipt = self.load(spec)
        if receipt is None:
            return None, 'no receipt'
        if receipt.get('schema') != self.SCHEMA or not receipt.get('complete'):
            return None, 'incomplete receipt'
        fingerprint, _ = self.fingerprint(spec)
        if receipt.get('fingerprint') != fingerprint:
            return None, 'stale receipt (command, input, script or Blender changed)'
        for role, recorded in (receipt.get('outputs') or {}).items():
            if recorded.get('sha256') and self.file_hash(recorded.get('path')) != recorded['sha256']:
                return None, 'output %s changed or is missing' % role
        if retry_failed and (receipt.get('run') or {}).get('outcome') != OUTCOME_OK:
            return None, 'retrying a failed launch (--retry-failed)'
        return receipt, 'resumed from receipt'

    def write(self, spec, receipt):
        path = self.receipt_path(spec)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        temporary = path + '.tmp'
        with open(temporary, 'w', encoding='utf-8') as f:
            json.dump(receipt, f, indent=1, default=str)
        os.replace(temporary, path)
        return path


def launch(spec, store, launcher, env=None, retry_failed=False, log=None):
    """Run ``spec`` through admission and the watchdog unless its receipt is reusable; returns the receipt dict.

    Raises AdmissionTimeout (the caller aborts the run). A receipt reused from an earlier run carries ``resumed: True``.
    """
    log = log or (lambda message: None)
    receipt, reason = store.reusable(spec, retry_failed)
    if receipt is not None:
        receipt = dict(receipt)
        receipt['resumed'] = True
        log('    %s/%s: %s (%s, exit %s)' % (spec.unit, spec.name, reason, (receipt.get('run') or {}).get('outcome'),
                                             (receipt.get('run') or {}).get('exitCode')))
        return receipt
    fingerprint, fingerprint_document = store.fingerprint(spec)
    missing = [role for role, path in spec.inputs.items() if not path or not os.path.isfile(path)]
    overlong = overlong_path_arguments(spec.command)
    stdout_path, stderr_path = store.log_paths(spec)
    base = {'schema': LaunchStore.SCHEMA, 'hop': spec.hop, 'unit': spec.unit, 'name': spec.name, 'note': spec.note,
            'command': spec.command, 'fingerprint': fingerprint, 'fingerprintDocument': fingerprint_document,
            'blender': store.blender_identity, 'limits': launcher.limits.to_dict(), 'rerunReason': reason,
            'stdout': stdout_path, 'stderr': stderr_path, 'resumed': False}
    for path in spec.outputs.values():
        if path and os.path.isfile(path):
            os.remove(path)  # a stale output must never pass for this launch's result (a failed launch included)
    if missing or overlong:
        error = ('input(s) missing before launch: %s' % ', '.join(missing) if missing else
                 'path argument(s) longer than MAX_PATH (%d characters), which Blender cannot open or write: %s'
                 % (MAX_PATH_ARGUMENT, '; '.join('%d characters: %s' % (len(a), a) for a in overlong)))
        base.update(complete=True, admission=None, environmentScrubbed=[],
                    run={'outcome': OUTCOME_LAUNCH_ERROR, 'exitCode': None, 'seconds': 0.0, 'error': error},
                    outputs={role: {'path': path, 'sha256': None, 'exists': False} for role, path in spec.outputs.items()})
        store.write(spec, base)
        return base
    child_env, scrubbed = child_environment(env)
    admission = launcher.admit('%s/%s/%s' % (spec.hop, spec.unit, spec.name))
    log('    %s/%s: launching (available %.2f GiB after %.1f s)' % (spec.unit, spec.name, admission['availableBytes'] / GIB,
                                                                   admission['waitedSeconds']))
    run = launcher.run(spec.command, stdout_path, stderr_path, env=child_env, timeout_seconds=spec.timeout_seconds)
    outputs = {}
    for role, path in spec.outputs.items():
        exists = bool(path) and os.path.isfile(path)
        outputs[role] = {'path': path, 'exists': exists, 'sha256': store.file_hash(path) if exists else None,
                         'bytes': os.path.getsize(path) if exists else None}
    base.update(complete=True, admission=admission, environmentScrubbed=scrubbed, run=run, outputs=outputs,
                stdoutTail=tail_text(stdout_path, 2000), stderrTail=tail_text(stderr_path, 4000))
    store.write(spec, base)
    log('    %s/%s: %s, exit %s, %.1f s, peak private %s GiB, min available %s GiB'
        % (spec.unit, spec.name, run['outcome'], run.get('exitCode'), run.get('seconds') or 0.0,
           run.get('peakPrivateGiB'), run.get('minAvailableGiB')))
    return base


def launch_failed_by_harness(receipt):
    """Whether the harness (not the program) ended the launch: a timeout, a watchdog kill or a launch error."""
    return ((receipt or {}).get('run') or {}).get('outcome') in HARNESS_OUTCOMES
