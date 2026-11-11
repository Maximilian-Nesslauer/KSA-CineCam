#!/usr/bin/env python3
"""Control a running CineCam replay through its local control directory.

A replay plays the selected take of the CineCam editor on a recorded flight. Times are take
seconds, the time of the video; the status lists the events with their recording and take times.
"""

import argparse
import datetime
import json
import math
import os
from pathlib import Path
import sys
import time
import uuid

VERSION = 1
MAX_BYTES = 16384


def atomic_json(path, value):
    data = json.dumps(value, separators=(",", ":"), allow_nan=False).encode("utf-8")
    if len(data) > MAX_BYTES:
        raise ValueError("Request exceeds the size limit")
    temporary = path.with_name(path.name + "." + uuid.uuid4().hex + ".tmp")
    try:
        with temporary.open("xb") as stream:
            stream.write(data)
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def read_json(path):
    with path.open("rb") as stream:
        data = stream.read(MAX_BYTES * 64 + 1)
    if len(data) > MAX_BYTES * 64:
        raise ValueError("Response exceeds the size limit")
    return json.loads(data)


def session(root, require_fresh=True):
    lease = read_json(root / "session.json")
    updated = datetime.datetime.fromisoformat(lease["updatedUtc"].replace("Z", "+00:00"))
    age = (datetime.datetime.now(datetime.timezone.utc) - updated).total_seconds()
    nonce = lease.get("nonce")
    if lease.get("version") != VERSION or not isinstance(nonce, str) or len(nonce) != 32 or (require_fresh and not -5 <= age <= 5):
        raise ValueError("CineCam local control is not ready or its session is stale")
    return nonce


def request(root, command, timeout):
    nonce = session(root)
    identifier = uuid.uuid4().hex
    request_path = root / "requests" / (identifier + ".json")
    response_path = root / "responses" / (identifier + ".json")
    atomic_json(request_path, {"version": VERSION, "id": identifier, "nonce": nonce, "command": command})
    deadline = time.monotonic() + timeout
    try:
        while time.monotonic() < deadline:
            try:
                response = read_json(response_path)
            except FileNotFoundError:
                response = None
            if response is not None:
                if response.get("version") != VERSION or response.get("id") != identifier or response.get("nonce") != nonce:
                    raise ValueError("Invalid response envelope")
                result = response["result"]
                if not isinstance(result, dict) or not isinstance(result.get("ok"), bool):
                    raise ValueError("Invalid command result")
                return result
            if session(root, require_fresh=False) != nonce:
                raise ValueError("CineCam restarted before responding")
            time.sleep(0.1)
        raise TimeoutError("CineCam did not respond before the timeout")
    finally:
        request_path.unlink(missing_ok=True)
        response_path.unlink(missing_ok=True)


def documents_folder():
    """Return the Documents folder that the game uses (the Windows "Personal" folder)."""
    if os.name == "nt":
        try:
            import ctypes

            buffer = ctypes.create_unicode_buffer(260)
            # CSIDL_PERSONAL is 5. This folder follows a moved or redirected Documents folder.
            if ctypes.windll.shell32.SHGetFolderPathW(None, 5, None, 0, buffer) == 0 and buffer.value:
                return Path(buffer.value)
        except (AttributeError, OSError):
            pass
    return Path.home() / "Documents"


def default_control_dir():
    return documents_folder() / "My Games" / "Kitten Space Agency" / "CineCam" / "control"


KEY_KINDS = ("fixed", "track", "chase", "orbit", "part-mount", "path-point")


def parser():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--control-dir", type=Path, default=Path(os.environ.get("CINECAM_CONTROL_DIR", default_control_dir())),
                   help="CineCam control folder; defaults to CINECAM_CONTROL_DIR or the game's Documents folder")
    p.add_argument("--timeout", type=float, default=10, help="response timeout in seconds")
    sub = p.add_subparsers(dest="action", required=True)
    for name in ("status", "list", "record", "stop", "retry-save", "close", "play", "pause", "preview", "render-stop"):
        sub.add_parser(name)
    sub.add_parser("open").add_argument("id", type=str)
    sub.add_parser("seek", help="go to a take time in seconds").add_argument("time", type=float)
    sub.add_parser("speed", help="preview speed on top of the speed track").add_argument("speed", type=float)
    sub.add_parser("camera", help="show the take or use the free camera").add_argument("mode", choices=("take", "free"))
    sub.add_parser("fov", help="field of view of the free camera in degrees").add_argument("fov", type=float)
    follow = sub.add_parser("follow", help="the free camera follows a recorded vehicle")
    follow.add_argument("actor_id", type=int, nargs="?", help="actor ID; leave it out to follow nothing")
    sub.add_parser("add-key", help="add a shot from the camera at the replay time").add_argument("kind", choices=KEY_KINDS)
    render = sub.add_parser("render", help="render the take of the open replay to numbered pictures")
    render.add_argument("--from-here", action="store_true", help="start at the replay time instead of the take start")
    p_interface = sub.add_parser("interface")
    p_interface.add_argument("visibility", choices=("show", "hide"))
    return p


def command(args):
    operations = {"status": "status", "list": "list", "record": "startRecording", "stop": "stopRecording",
                  "retry-save": "retrySave", "open": "open", "close": "close", "seek": "seek",
                  "play": "setPlaying", "pause": "setPlaying", "speed": "setSpeed", "camera": "setCamera",
                  "follow": "follow", "fov": "setFov", "add-key": "addKey", "interface": "setInterface", "preview": "preview",
                  "render": "render", "render-stop": "renderStop"}
    result = {"operation": operations[args.action]}
    for attr, field in (("id", "id"), ("actor_id", "actorId"), ("time", "time"), ("speed", "speed"), ("fov", "fov")):
        if getattr(args, attr, None) is not None:
            result[field] = getattr(args, attr)
    if args.action in ("play", "pause"):
        result["enabled"] = args.action == "play"
    elif args.action == "interface":
        result["enabled"] = args.visibility == "show"
    elif args.action == "camera":
        result["name"] = args.mode
    elif args.action == "add-key":
        result["name"] = args.kind
    elif args.action == "render":
        result["name"] = "current" if args.from_here else "whole"
    return result


def unavailable_message(control_dir):
    return (f"CineCam local control is not ready in {control_dir}. Turn on local control in CineCam, "
            "or give the folder with --control-dir or CINECAM_CONTROL_DIR.")


def main():
    args = parser().parse_args()
    try:
        if not math.isfinite(args.timeout) or args.timeout <= 0:
            raise ValueError("Timeout must be positive and finite")
        result = request(args.control_dir, command(args), args.timeout)
    except TimeoutError as exc:
        result = {"ok": False, "code": "timeout", "message": str(exc)}
    except FileNotFoundError:
        result = {"ok": False, "code": "control_unavailable", "message": unavailable_message(args.control_dir)}
    except (OSError, ValueError, KeyError, TypeError) as exc:
        result = {"ok": False, "code": "control_error", "message": str(exc)}
    print(json.dumps(result, indent=2, ensure_ascii=True))
    return 0 if result["ok"] else 1


if __name__ == "__main__":
    sys.exit(main())
