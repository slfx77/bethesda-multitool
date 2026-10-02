"""Bounded protocol-v1 checks against an already admitted local emulator PID.

This observes guest events and changes host capture state only. It never invokes
a guest function. Run after a successful shipped BMT capture/import.
"""
import argparse
import ctypes
import json
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


def connect():
    deadline = time.monotonic() + 5
    while True:
        try:
            return open(rf"\\.\pipe\BMT.Runtime.{args.pid}", "r+b", buffering=0)
        except OSError:
            if time.monotonic() >= deadline:
                raise
            time.sleep(0.05)


def send(pipe, kind, request, text=""):
    payload = text.encode("utf-8")
    pipe.write(struct.pack("<IHHQII", 0x31544D42, 1, kind, request, len(payload), 0) + payload)


def receive(pipe, deadline):
    # Peek avoids an unbounded blocking read if the server stops responding.
    import msvcrt
    available = ctypes.c_ulong()
    handle = ctypes.c_void_p(msvcrt.get_osfhandle(pipe.fileno()))
    while time.monotonic() < deadline:
        if not ctypes.windll.kernel32.PeekNamedPipe(handle, None, 0, None, ctypes.byref(available), None):
            raise OSError("Runtime pipe disconnected")
        if available.value >= 24:
            header = pipe.read(24)
            magic, version, kind, request, length, reserved = struct.unpack("<IHHQII", header)
            assert (magic, version, kind, reserved) == (0x31544D42, 1, 256, 0)
            assert length <= 65536
            if not ctypes.windll.kernel32.PeekNamedPipe(handle, None, 0, None, ctypes.byref(available), None):
                raise OSError("Runtime pipe disconnected after header")
            while available.value < length:
                if time.monotonic() >= deadline:
                    raise TimeoutError("Truncated runtime event")
                ctypes.windll.kernel32.PeekNamedPipe(handle, None, 0, None, ctypes.byref(available), None)
                time.sleep(0.001)
            event = json.loads(pipe.read(length))
            assert event["requestId"] == request
            events.append(event)
            return event
        time.sleep(0.002)
    raise TimeoutError("Runtime event timed out")


def until(pipe, kind, request=None):
    deadline = time.monotonic() + 5
    while True:
        event = receive(pipe, deadline)
        if event["kind"] == kind and (request is None or event["requestId"] == request):
            return event


with connect() as pipe:
    send(pipe, 1, 1)
    assert until(pipe, "hello", 1)["sequence"] == 1
    send(pipe, 2, 2, "bmt-protocol-cancel-check")
    until(pipe, "capture-start", 2)
    until(pipe, "guest-probe")
    send(pipe, 5, 3)
    assert until(pipe, "capture-end", 3)["status"] == "cancelled"
    send(pipe, 65535, 4, "unsupported-request")
    assert until(pipe, "error", 4)["error"] == "guest-action-or-state-evaluator-unavailable"
    send(pipe, 6, 5)
    until(pipe, "pong", 5)
    send(pipe, 2, 6, "bmt-protocol-disconnect-check")
    until(pipe, "capture-start", 6)
    until(pipe, "guest-probe")
    # Disconnect during capture. New connection must start with inactive capture.
with connect() as pipe:
    send(pipe, 1, 1)
    assert until(pipe, "hello", 1)["sequence"] == 1
    send(pipe, 6, 2)
    assert receive(pipe, time.monotonic() + 5)["kind"] == "pong"
    send(pipe, 2, 3, "bmt-protocol-reconnect-check")
    until(pipe, "capture-start", 3)
    until(pipe, "guest-probe")
    send(pipe, 4, 4)
    assert until(pipe, "capture-end", 4)["status"] == "completed"
args.output.write_text(json.dumps({"status": "passed", "pid": args.pid,
    "checks": ["cancel acknowledgement", "unsupported action rejection", "ping after cancel",
               "disconnect during capture", "reconnect sequence reset", "capture inactive after reconnect",
               "new guest observation after reconnect", "stop acknowledgement"],
    "events": events}, indent=2) + "\n")
print("Passed 8 protocol checks")
