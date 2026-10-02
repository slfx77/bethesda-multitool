# SPDX-License-Identifier: 0BSD
"""Shared helpers for the gate-1a oracle harness: hashing, float32 arithmetic, mismatch records.

Standard library plus numpy only. Nothing here imports or executes writer code: every conversion the
writer declares is re-derived in ``writer_rules.py`` from the source files named there.
"""

import hashlib
import json
import math
import os
import struct
import sys

import numpy as np


class OracleError(Exception):
    """A hop cannot run at all (a missing input, an unparseable artifact). Never a mismatch.

    One exception to "never a mismatch": ``hop_b.check_images`` converts an OracleError raised while building
    the expectation of ONE binding into a failed check ("binding not compared") and keeps walking, so a
    binding the oracle cannot reproduce is never silently uncompared while the check reads pass. The receipt
    labels such a failure ``expectation``, distinct from a ``pixels`` mismatch of the writer.
    """


def sha256_bytes(data):
    return hashlib.sha256(data).hexdigest()


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for chunk in iter(lambda: f.read(1 << 20), b''):
            h.update(chunk)
    return h.hexdigest()


def _parse_int_keeping_negative_zero(token):
    """JSON ``-0`` is an integer token; Python's int cannot carry its sign, so it becomes the float -0.0."""
    return -0.0 if token == '-0' else int(token)


def json_loads(text):
    """json.loads that keeps the sign of a negative-zero token (the dump writes float32 -0 as ``-0``)."""
    return json.loads(text, parse_int=_parse_int_keeping_negative_zero)


def load_json(path):
    with open(path, 'rb') as f:
        return json_loads(f.read().decode('utf-8'))


def f32(value):
    """Round a Python float (double) to the nearest float32, as C#'s ``(float)`` cast does."""
    return float(np.float32(value))


def f32_array(values, shape=None):
    array = np.asarray(values, dtype=np.float64).astype(np.float32)
    return array.reshape(shape) if shape is not None else array


def float_bits(value):
    return struct.unpack('<I', struct.pack('<f', value))[0]


def bits_equal(a, b):
    """Bit-for-bit float32 equality over arrays (signed zero and NaN payloads distinguished)."""
    a = np.ascontiguousarray(a, dtype=np.float32)
    b = np.ascontiguousarray(b, dtype=np.float32)
    if a.shape != b.shape:
        return False
    return bool(np.array_equal(a.view(np.uint32), b.view(np.uint32)))


def first_bit_mismatch(a, b, label):
    """The first differing element of two float32 arrays, as a Mismatch, or None."""
    a = np.ascontiguousarray(a, dtype=np.float32)
    b = np.ascontiguousarray(b, dtype=np.float32)
    if a.shape != b.shape:
        return Mismatch(label, 'shape', 'expected %s, got %s' % (list(b.shape), list(a.shape)))
    diff = a.view(np.uint32) != b.view(np.uint32)
    if not diff.any():
        return None
    index = tuple(int(i) for i in np.argwhere(diff)[0])
    return Mismatch(label, 'value', 'first mismatch at %s: got %r (0x%08x), expected %r (0x%08x); %d of %d differ'
                    % (list(index), float(a[index]), int(a.view(np.uint32)[index]), float(b[index]),
                       int(b.view(np.uint32)[index]), int(diff.sum()), int(diff.size)))


def first_int_mismatch(a, b, label):
    a = np.asarray(a).astype(np.int64)
    b = np.asarray(b).astype(np.int64)
    if a.shape != b.shape:
        return Mismatch(label, 'shape', 'expected %s, got %s' % (list(b.shape), list(a.shape)))
    diff = a != b
    if not diff.any():
        return None
    index = tuple(int(i) for i in np.argwhere(diff)[0])
    return Mismatch(label, 'value', 'first mismatch at %s: got %d, expected %d; %d of %d differ'
                    % (list(index), int(a[index]), int(b[index]), int(diff.sum()), int(diff.size)))


class Mismatch:
    """One detected difference. ``where`` names the stream or field, ``kind`` the class, ``detail`` the first instance."""

    def __init__(self, where, kind, detail):
        self.where = where
        self.kind = kind
        self.detail = detail

    def to_dict(self):
        return {'where': self.where, 'kind': self.kind, 'detail': self.detail}

    def __repr__(self):
        return 'Mismatch(%s: %s: %s)' % (self.where, self.kind, self.detail)


class Check:
    """One named check with its pass/fail state, detail and first mismatch."""

    def __init__(self, name):
        self.name = name
        self.passed = None
        self.detail = None
        self.mismatches = []
        self.data = {}

    def fail(self, mismatch):
        self.passed = False
        self.mismatches.append(mismatch)
        return self

    def ok(self, detail=None):
        if self.passed is None:
            self.passed = True
        if detail is not None:
            self.detail = detail
        return self

    def skip(self, detail):
        self.passed = None
        self.detail = detail
        return self

    def to_dict(self):
        return {
            'name': self.name,
            'status': 'pass' if self.passed else ('fail' if self.passed is False else 'not-applicable'),
            'detail': self.detail,
            'firstMismatch': self.mismatches[0].to_dict() if self.mismatches else None,
            'mismatchCount': len(self.mismatches),
            'data': self.data,
        }


class HopResult:
    """The checks of one hop over one sample, plus its controls.

    ``passed`` speaks for the artifact (the checks alone); ``oracle_valid`` speaks for the oracle (its
    controls). The two are reported separately so a control that was not detected reads as an INVALID
    oracle, never as a failure of the artifact. ``error`` records an OracleError (the hop could not run);
    ``traceback`` is filled when an unexpected exception escaped the hop and was caught by its ``run``.
    """

    def __init__(self, hop, sample_id):
        self.hop = hop
        self.sample_id = sample_id
        self.checks = []
        self.controls = []
        self.error = None
        self.traceback = None

    def check(self, name):
        check = Check(name)
        self.checks.append(check)
        return check

    @property
    def passed(self):
        """Checks only: False on a hop error or any failed check. Controls are reported by oracle_valid."""
        if self.error:
            return False
        return all(c.passed is not False for c in self.checks)

    @property
    def oracle_valid(self):
        """False when any executed control was not detected; a not-applicable control (detected None) is neutral."""
        return all(c.get('detected') is not False for c in self.controls)

    def to_dict(self):
        return {
            'hop': self.hop,
            'sample': self.sample_id,
            'passed': self.passed,
            'oracleValid': self.oracle_valid,
            'error': self.error,
            'traceback': self.traceback,
            'checks': [c.to_dict() for c in self.checks],
            'controls': self.controls,
        }


def tool_versions():
    import numpy
    versions = {'python': sys.version.split()[0], 'numpy': numpy.__version__}
    try:
        import PIL
        versions['pillow'] = PIL.__version__
    except ImportError:
        versions['pillow'] = None
    return versions


def relpath_or_abs(path, root):
    try:
        return os.path.relpath(path, root).replace('\\', '/')
    except ValueError:
        return path
