#!/usr/bin/env python3
"""Makes marker files for a video editor from the event log of a CineCam take.

Two modes, both with the Python standard library only. Run them with python -I:

  python -I events-to-markers.py events EVENTS.CSV [options]
  python -I events-to-markers.py beats --bpm 128 --first-beat-seconds 1.9 [options]

Each mode writes an EDL with markers and a plain CSV. The EDL is the file that
DaVinci Resolve reads with Timeline > Import > Timeline Markers from EDL. The
layout follows the marker EDL that Resolve itself writes with Timeline > Export
> Timeline Markers to EDL (a CMX 3600 event line for each marker, and a second
line "|C:<color> |M:<name> |D:<frames>"). A marker file imported this way adds
the markers to the current timeline at the timecodes of the file.
"""

import argparse
import csv
import math
import os
import re
import sys

# The marker colors that Resolve knows. A marker color in the EDL is "ResolveColor" + the name.
KIND_COLORS = {
    "event": "Blue",
    "shot": "Green",
    "speed": "Yellow",
    "action": "Red",
    "marker": "Cyan",
    "info": "Sand",
    "warning": "Rose",
    "pause": "Lavender",
    "take": "Mint",
}
DEFAULT_KINDS = "event,shot,speed,action"
ALL_KINDS = tuple(KIND_COLORS)


class UsageError(Exception):
    pass


#region Timecode


def timecode_base(fps):
    """The frame count per second that a timecode counts with (30 for 29.97)."""
    return int(round(fps))


def parse_timecode(text, fps):
    match = re.fullmatch(r"(\d+):(\d\d):(\d\d)[:;](\d\d)", text.strip())
    if not match:
        raise UsageError(f'"{text}" is not a timecode like 01:00:00:00')
    hours, minutes, seconds, frames = (int(g) for g in match.groups())
    base = timecode_base(fps)
    if frames >= base:
        raise UsageError(f'the frame part of "{text}" must be below {base}')
    return ((hours * 60 + minutes) * 60 + seconds) * base + frames


def format_timecode(frame, fps):
    base = timecode_base(fps)
    seconds, frames = divmod(frame, base)
    minutes, seconds = divmod(seconds, 60)
    hours, minutes = divmod(minutes, 60)
    return f"{hours:02d}:{minutes:02d}:{seconds:02d}:{frames:02d}"


def seconds_to_frames(seconds, fps):
    return int(math.floor(seconds * fps + 0.5))


#endregion

#region Markers


class Marker:
    def __init__(self, frame, color, name, kind="", detail="", seconds=0.0):
        self.frame = frame  # frames since the timeline start timecode
        self.color = color
        self.name = name
        self.kind = kind
        self.detail = detail
        self.seconds = seconds  # video seconds, for the CSV


def clean(text, limit):
    """EDL marker text has no bar and no line break, and a limit keeps the line readable."""
    text = re.sub(r"[|\r\n\t]+", " ", text).strip()
    return text if len(text) <= limit else text[: limit - 3].rstrip() + "..."


def write_edl(path, title, markers, fps, start_frame, length_frames=1):
    lines = [f"TITLE: {clean(title, 60)}", "FCM: NON-DROP FRAME", ""]
    for number, marker in enumerate(markers, start=1):
        first = format_timecode(start_frame + marker.frame, fps)
        last = format_timecode(start_frame + marker.frame + 1, fps)
        lines.append(f"{number:03d}  001      V     C        {first} {last} {first} {last}  ")
        lines.append(f" |C:ResolveColor{marker.color} |M:{clean(marker.name, 120)} |D:{length_frames}")
        lines.append("")
    with open(path, "w", encoding="ascii", errors="replace", newline="\n") as handle:
        handle.write("\n".join(lines))


def write_marker_csv(path, markers, fps, start_frame):
    with open(path, "w", encoding="utf-8", newline="") as handle:
        writer = csv.writer(handle, lineterminator="\n")
        writer.writerow(["timecode", "frame", "videoSeconds", "kind", "name", "detail", "color"])
        for marker in markers:
            writer.writerow([
                format_timecode(start_frame + marker.frame, fps),
                marker.frame,
                f"{marker.seconds:.3f}",
                marker.kind,
                marker.name,
                marker.detail,
                marker.color,
            ])


#endregion

#region Event log


def read_rows(path):
    with open(path, "r", encoding="utf-8-sig", newline="") as handle:
        reader = csv.DictReader(handle)
        required = {"realSeconds", "kind", "name"}
        if reader.fieldnames is None or not required <= set(reader.fieldnames):
            raise UsageError(f"{path} is not a CineCam event log (it needs the columns {', '.join(sorted(required))})")
        rows = []
        for number, row in enumerate(reader, start=2):
            try:
                seconds = float(row["realSeconds"])
            except (TypeError, ValueError):
                raise UsageError(f"{path} line {number}: realSeconds is not a number")
            rows.append((seconds, row["kind"], row["name"], row.get("detail") or ""))
        return rows


PAUSE_LENGTH = re.compile(r"length=([0-9.]+) s real")


def zero_row(rows):
    """The row that is the zero of the video: the sync flash, else the take start."""
    for seconds, kind, name, _ in rows:
        if kind == "marker" and name == "syncFlash":
            return seconds, "the sync flash"
    for seconds, kind, name, _ in rows:
        if kind == "take" and name == "start":
            return seconds, "the take start (the log has no sync flash row)"
    return rows[0][0], "the first row"


def marker_name(kind, name, detail):
    if kind == "event":
        return name
    if kind == "shot":
        match = re.search(r"type=(\w+)", detail)
        return f"shot {name}" + (f" ({match.group(1)})" if match else "")
    if kind == "speed":
        match = re.search(r"to=([0-9.]+)", detail)
        return f"speed {name}" + (f" to {match.group(1)}x" if match else "")
    if kind == "action":
        # The detail reads "actions[0]; fires sequence 3; next=3; active=2".
        parts = [part.strip() for part in detail.split(";")]
        return f"{name}: {parts[1]}" if len(parts) > 1 and parts[1] else name
    return f"{kind} {name}"


def merge_same_frame(markers):
    """Resolve keeps one marker per frame of a timeline, so markers on one frame become one.

    The name joins the names in log order, and the color is the color of the first marker.
    """
    merged = []
    for marker in markers:
        if merged and merged[-1].frame == marker.frame:
            last = merged[-1]
            last.name = f"{last.name} + {marker.name}"
            last.detail = f"{last.detail} | {marker.detail}" if marker.detail else last.detail
        else:
            merged.append(Marker(marker.frame, marker.color, marker.name, marker.kind, marker.detail, marker.seconds))
    return merged


def build_event_markers(rows, fps, flash_seconds, kinds, pause_shift):
    zero, zero_text = zero_row(rows)
    markers = []
    shift = 0.0
    dropped_before = 0
    for seconds, kind, name, detail in rows:
        # The video keeps recording while the take clock stands still in a pause, so the
        # length of every pause that ended earlier moves the later rows.
        position = seconds - zero + (shift if pause_shift else 0.0)
        if kind == "pause" and name == "end":
            match = PAUSE_LENGTH.search(detail)
            if match:
                shift += float(match.group(1))
        if kind not in kinds:
            continue
        video = flash_seconds + position
        if video < 0.0:
            dropped_before += 1
            continue
        markers.append(Marker(seconds_to_frames(video, fps), KIND_COLORS.get(kind, "Blue"),
                              marker_name(kind, name, detail), kind, detail, video))
    markers.sort(key=lambda m: m.frame)
    return merge_same_frame(markers), zero_text, dropped_before


def run_events(args):
    fps = args.fps
    if args.flash_frames is not None:
        flash_seconds = args.flash_frames / fps
    else:
        flash_seconds = args.flash_seconds
    kinds = set(ALL_KINDS) if args.kinds.strip().lower() == "all" else {k.strip() for k in args.kinds.split(",") if k.strip()}
    unknown = kinds - set(ALL_KINDS)
    if unknown:
        raise UsageError(f"unknown kind(s) {', '.join(sorted(unknown))} (known: {', '.join(ALL_KINDS)}, or all)")

    rows = read_rows(args.events_csv)
    if not rows:
        raise UsageError(f"{args.events_csv} has no rows")
    markers, zero_text, dropped = build_event_markers(rows, fps, flash_seconds, kinds, not args.no_pause_shift)
    start_frame = parse_timecode(args.start_tc, fps)

    prefix = args.out or os.path.join(os.path.dirname(os.path.abspath(args.events_csv)), "markers")
    write_edl(prefix + ".edl", os.path.basename(os.path.dirname(os.path.abspath(args.events_csv))) or "CineCam take",
              markers, fps, start_frame)
    write_marker_csv(prefix + ".csv", markers, fps, start_frame)
    print(f"{len(markers)} marker(s) from {len(rows)} row(s). The zero is {zero_text}, "
          f"placed at {flash_seconds:.3f} s ({seconds_to_frames(flash_seconds, fps)} frames) of the video.")
    if dropped:
        print(f"{dropped} row(s) before the zero were left out.")
    print(f"Wrote {prefix}.edl and {prefix}.csv")


#endregion

#region Beats


def build_beat_markers(bpm, first_seconds, length_seconds, beats_per_bar, every, fps):
    interval = 60.0 / bpm
    markers = []
    beat = 0
    seen = set()
    while True:
        seconds = first_seconds + beat * interval
        if seconds > length_seconds + 1e-9:
            break
        if beat % every == 0:
            frame = seconds_to_frames(seconds, fps)
            if frame not in seen:
                seen.add(frame)
                bar, in_bar = divmod(beat, beats_per_bar)
                if in_bar == 0:
                    markers.append(Marker(frame, "Fuchsia", f"bar {bar + 1}", "bar", f"beat {beat + 1}", seconds))
                else:
                    markers.append(Marker(frame, "Pink", f"beat {bar + 1}.{in_bar + 1}", "beat", f"beat {beat + 1}", seconds))
        beat += 1
    return markers


def run_beats(args):
    fps = args.fps
    first = args.first_beat_frames / fps if args.first_beat_frames is not None else args.first_beat_seconds
    if first < 0:
        raise UsageError("the first beat is at 0 s or later")
    if args.bpm <= 0:
        raise UsageError("the BPM is greater than 0")
    if args.beats_per_bar < 1 or args.every < 1:
        raise UsageError("--beats-per-bar and --every are 1 or more")
    markers = build_beat_markers(args.bpm, first, args.length, args.beats_per_bar, args.every, fps)
    start_frame = parse_timecode(args.start_tc, fps)
    prefix = args.out or "beats"
    write_edl(prefix + ".edl", f"Beats {args.bpm:g} BPM", markers, fps, start_frame)
    write_marker_csv(prefix + ".csv", markers, fps, start_frame)
    frames_per_beat = (60.0 / args.bpm) * fps
    print(f"{len(markers)} beat marker(s) at {args.bpm:g} BPM, one beat is {frames_per_beat:.2f} frames at {fps:g} fps. "
          "Each marker is rounded to the nearest frame.")
    print(f"Wrote {prefix}.edl and {prefix}.csv")


#endregion


def make_parser():
    parser = argparse.ArgumentParser(
        prog="events-to-markers.py",
        description="Makes DaVinci Resolve marker files (EDL and CSV) from a CineCam event log or from a BPM.",
    )
    sub = parser.add_subparsers(dest="mode", required=True)

    def common(p):
        p.add_argument("--fps", type=float, default=60.0, help="frame rate of the timeline (default 60)")
        p.add_argument("--start-tc", default="01:00:00:00",
                       help="start timecode of the timeline (default 01:00:00:00, the Resolve default)")
        p.add_argument("-o", "--out", help="output path without extension")

    events = sub.add_parser("events", help="markers from the events.csv of a take")
    events.add_argument("events_csv", help="path of events.csv")
    flash = events.add_mutually_exclusive_group()
    flash.add_argument("--flash-seconds", type=float, default=0.0,
                       help="time of the sync flash in the video, in seconds from the timeline start (default 0)")
    flash.add_argument("--flash-frames", type=int, help="time of the sync flash in the video, in frames")
    events.add_argument("--kinds", default=DEFAULT_KINDS,
                        help=f"kinds of rows to include, comma separated, or all (default {DEFAULT_KINDS}; known: {', '.join(ALL_KINDS)})")
    events.add_argument("--no-pause-shift", action="store_true",
                        help="do not add the length of earlier pauses of the take to the later rows")
    common(events)

    beats = sub.add_parser("beats", help="beat markers from a BPM and the time of the first beat")
    beats.add_argument("--bpm", type=float, required=True, help="beats per minute of the music")
    first = beats.add_mutually_exclusive_group()
    first.add_argument("--first-beat-seconds", type=float, default=0.0,
                       help="time of the first beat on the timeline, in seconds from the timeline start (default 0)")
    first.add_argument("--first-beat-frames", type=int, help="time of the first beat on the timeline, in frames")
    beats.add_argument("--length", type=float, default=300.0, help="last time to mark, in seconds (default 300)")
    beats.add_argument("--beats-per-bar", type=int, default=4, help="beats in a bar (default 4)")
    beats.add_argument("--every", type=int, default=1, help="mark every Nth beat (default 1)")
    common(beats)
    return parser


def main(argv=None):
    args = make_parser().parse_args(argv)
    try:
        if args.fps <= 0:
            raise UsageError("the frame rate is greater than 0")
        if args.mode == "events":
            run_events(args)
        else:
            run_beats(args)
    except UsageError as error:
        print(f"error: {error}", file=sys.stderr)
        return 2
    except OSError as error:
        print(f"error: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
