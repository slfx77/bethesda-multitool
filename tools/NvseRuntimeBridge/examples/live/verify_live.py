"""Opt-in checks against an already running `runtime live` session (Windows)."""
import argparse
import json
import math
import queue
import struct
import threading
import time
from pathlib import Path


def exchange(session, request, wait=True):
    replies = queue.Queue()

    def work():
        try:
            payload = json.dumps({"request": request, "wait": wait}).encode()
            with open(r"\\.\pipe\BMT.Live." + session, "r+b", buffering=0) as pipe:
                pipe.write(struct.pack("<IHHQII", 0x31544D42, 1, 30, 1, len(payload), 0) + payload)

                def read_exact(size):
                    data = b""
                    while len(data) < size:
                        part = pipe.read(size - len(data))
                        if not part:
                            raise EOFError("Broker disconnected")
                        data += part
                    return data

                while True:
                    magic, version, kind, request_id, size, reserved = struct.unpack("<IHHQII", read_exact(24))
                    assert (magic, version, kind, request_id, reserved) == (0x31544D42, 1, 256, 1, 0)
                    assert size <= 65536
                    result = json.loads(read_exact(size))
                    if not wait or result.get("status") != "running":
                        replies.put(result)
                        return
        except Exception as error:
            replies.put(error)

    threading.Thread(target=work, daemon=True).start()
    result = replies.get(timeout=40)
    if isinstance(result, Exception):
        raise result
    return result


def verify(session, phase, evidence):

    def send(request, wait=True, expected="completed"):
        started = time.perf_counter()
        result = exchange(session, request, wait)
        evidence.append({"op": request["op"], "elapsedMs": round((time.perf_counter()-started)*1000, 2), "result": result})
        if expected is not None:
            assert result.get("status") == expected, result
        return result

    def snapshot():
        return send({"op": "snapshot"})["snapshot"]

    def coordinate(value, axis):
        return value["player.position"][axis]["value"]

    if phase == "control":
        baseline = send({"op": "baseline", "name": "BMT_Live_Reset"})
        assert baseline.get("saveBytes", 0) > 0 and baseline.get("coSaveBytes", 0) > 0
        before = snapshot()
        target = coordinate(before, "x") + 8
        send({"op": "console", "command": f"player.SetPos X {target:.6f}"})
        observed = send({"op": "eval", "expression": "player.GetPos X"})
        assert abs(observed["result"]["value"] - target) < 0.1
        send({"op": "reset"})
        after = snapshot()
        assert abs(coordinate(after, "x") - coordinate(before, "x")) <= 1
    elif phase == "input":
        menu = send({"op": "eval", "expression": "MenuMode"})
        assert menu["result"]["value"] == 0, "Close the pause/menu screen before the movement check"
        before = snapshot()
        state = send({"op": "status"})
        assert state["input"]["backgroundPollingObserved"], state["input"]
        sequence = {"version": 1, "steps": [
            {"type": "input", "durationMs": 2000, "keys": ["W"]},
            {"type": "input", "durationMs": 500, "mouse": {"dx": 120, "dy": 0}},
            {"type": "wait", "durationMs": 100}]}
        result = send({"op": "sequence.run", "sequence": sequence})
        assert result["elapsedMs"] <= 3600, result
        after = snapshot()
        distance = math.dist([coordinate(before, a) for a in "xyz"], [coordinate(after, a) for a in "xyz"])
        assert distance > 1, (before, after)
        assert abs(after["player.rotation"]["z"]["value"] - before["player.rotation"]["z"]["value"]) > 0.1
        active = send({"op": "sequence.run", "sequence": {"version": 1, "steps": [
            {"type": "input", "durationMs": 30000, "keys": ["W"]}]}}, False, "running")
        send({"op": "job.stop", "jobId": active["jobId"]})
        assert not send({"op": "status"})["input"]["active"]
        send({"op": "reset"})
    elif phase == "scripts":
        source = lambda value: f"begin function {{}}\nSetFunctionValue ({value})\nend\n"
        send({"op": "script.load", "name": "revision", "source": source(0)})
        job = send({"op": "script.run", "name": "revision", "intervalMs": 10}, False, "running")
        for revision in range(1, 101):
            send({"op": "script.load", "name": "revision", "source": source(revision)})
            deadline = time.monotonic() + 2
            while True:
                state = send({"op": "status"})
                assert state["ownedScripts"] == state["compiledScripts"] - state["retiredScripts"] == 1
                if state["job"].get("lastResult", {}).get("value") == revision:
                    break
                assert time.monotonic() < deadline, state
                time.sleep(0.02)
        broken = send({"op": "script.load", "name": "revision", "source": "begin function {}\nSetFunctionValue (\nend\n"}, expected="failed")
        assert broken["previousRevisionRetained"]
        assert broken.get("diagnostic"), broken
        send({"op": "job.stop", "jobId": job["jobId"]})
        before = send({"op": "status"})["scripts"]
        time.sleep(0.1)
        assert send({"op": "status"})["scripts"] == before
        assert send({"op": "script.run", "name": "revision"})["lastResult"]["value"] == 100
    return evidence


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--session", required=True)
    parser.add_argument("--phase", required=True, choices=["control", "input", "scripts"])
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    results = []
    report = {"status": "running", "phase": args.phase, "checks": results}
    try:
        verify(args.session, args.phase, results)
        report["status"] = "passed"
    except Exception as error:
        report.update(status="failed", error=f"{type(error).__name__}: {error}")
        raise
    finally:
        Path(args.output).write_text(json.dumps(report, indent=2), encoding="utf-8")
        print(json.dumps({"status": report["status"], "phase": args.phase, "requests": len(results), "output": args.output}))
