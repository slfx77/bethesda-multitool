"""Bounded live GetDead and cancellation checks for an admitted emulator PID.

Invokes the supported player-get-dead handler. No console commands, game-state
setters, or assumptions about a particular player life state are used.
"""
import argparse
import ctypes
import json
import math
import msvcrt
import struct
import time
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument("pid", type=int)
parser.add_argument("output", type=Path)
args = parser.parse_args()
if args.output.exists():
    raise SystemExit("Preserve the existing receipt; choose a new output path.")
events = []
checks = []


def packet(kind, request, text=""):
    payload = text.encode("utf-8")
    return struct.pack("<IHHQII", 0x31544D42, 1, kind, request, len(payload), 0) + payload


def receive(pipe, deadline):
    available = ctypes.c_ulong()
    handle = ctypes.c_void_p(msvcrt.get_osfhandle(pipe.fileno()))

    def wait_for(size):
        while time.monotonic() < deadline:
            if not ctypes.windll.kernel32.PeekNamedPipe(handle, None, 0, None, ctypes.byref(available), None):
                raise OSError("Runtime pipe disconnected")
            if available.value >= size:
                return
            time.sleep(0.002)
        raise TimeoutError("Runtime event timed out")

    wait_for(24)
    magic, version, kind, request, length, reserved = struct.unpack("<IHHQII", pipe.read(24))
    assert (magic, version, kind, reserved) == (0x31544D42, 1, 256, 0)
    assert length <= 65536
    wait_for(length)
    event = json.loads(pipe.read(length))
    assert event["requestId"] == request
    events.append(event)
    return event


def until(pipe, kind, request):
    deadline = time.monotonic() + 5
    while True:
        event = receive(pipe, deadline)
        if event["kind"] == kind and event["requestId"] == request:
            return event


status = "failed"
failure = None
try:
    with open(rf"\\.\pipe\BMT.Runtime.{args.pid}", "r+b", buffering=0) as pipe:
        pipe.write(packet(1, 1))
        hello = until(pipe, "hello", 1)
        assert hello["capabilities"]["controlledConditionProbe"] is True
        pipe.write(packet(9, 2, "player-get-dead"))
        assert until(pipe, "error", 2)["error"] == "active-capture-required"
        checks.append("query outside capture rejected")
        pipe.write(packet(2, 3, "bmt-condition-lifecycle"))
        until(pipe, "capture-start", 3)
        pipe.write(packet(9, 4, "unsupported-condition"))
        assert until(pipe, "error", 4)["error"] == "guest-action-or-state-evaluator-unavailable"
        checks.append("unsupported query rejected")
        pipe.write(packet(9, 5, "player-get-dead"))
        until(pipe, "condition-probe-queued", 5)
        result = until(pipe, "condition-probe-result", 5)
        assert result["function"] == "GetDead" and result["engineTargetFormId"] == 0x14
        assert result["handlerReturned"] is True and math.isfinite(result["value"])
        assert result["comparisonObserved"] is False
        checks.append("actual bounded handler result")
        # The real scheduling race may resolve either query before cancellation.
        # Each accepted query must still have exactly one terminal result/error.
        pipe.write(packet(9, 6, "player-get-dead") + packet(9, 7, "player-get-dead") + packet(5, 8))
        assert until(pipe, "capture-end", 8)["status"] == "cancelled"
        for request in (6, 7):
            assert sum(e["requestId"] == request and e["kind"] == "condition-probe-queued" for e in events) == 1
            terminal = [e for e in events if e["requestId"] == request and e["kind"] in ("condition-probe-result", "error")]
            assert len(terminal) == 1
        checks.append("accepted queries terminate before cancellation acknowledgement")
        pipe.write(packet(6, 9))
        until(pipe, "pong", 9)
        for request in (6, 7):
            assert sum(e["requestId"] == request and e["kind"] in ("condition-probe-result", "error") for e in events) == 1
        checks.append("connection responds after cancellation without duplicate result")
        status = "passed"
except Exception as ex:
    failure = f"{type(ex).__name__}: {ex}"
finally:
    args.output.write_text(json.dumps({"status": status, "pid": args.pid, "checks": checks,
        "failure": failure, "events": events}, indent=2) + "\n")
print(f"{status}: {len(checks)} condition protocol checks")
if failure:
    raise SystemExit(failure)
