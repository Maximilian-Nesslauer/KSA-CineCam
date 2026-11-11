"""Tests for events-to-markers.py. Run with: python -I scripts/test_events_to_markers.py"""

import importlib.util
import os
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
SPEC = importlib.util.spec_from_file_location("events_to_markers", os.path.join(HERE, "events-to-markers.py"))
tool = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(tool)

SAMPLE = """realSeconds,gameSeconds,missionSeconds,simSpeed,frame,kind,name,detail
0.000,1000.000,0.000,1,,take,start,take=launch; run=1
0.000,1000.000,0.000,1,,marker,syncFlash,white frame drawn
38.100,1038.100,0.000,1,,event,ignition,
38.100,1038.100,0.000,0.25,,speed,speed[0],to=0.25; ramp=0.5
39.800,1039.800,1.700,0.25,,event,liftoff,
50.000,1050.000,12.000,0.25,,shot,camera[1],type=Chase; duration=20; fov=45
60.000,1060.000,22.000,0.25,,pause,start,the take is paused
60.000,1062.000,22.000,0,,pause,end,"the take goes on; length=10.0 s real; total=10.0 s real"
70.000,1072.000,32.000,1,,action,stage,"actions[0]; fires sequence 3; next=3; active=2"
80.000,1082.000,42.000,1,,take,end,the last shot is done
"""


def write_sample(folder):
    path = os.path.join(folder, "events.csv")
    with open(path, "w", encoding="utf-8", newline="") as handle:
        handle.write(SAMPLE)
    return path


class TimecodeTests(unittest.TestCase):
    def test_round_trip(self):
        start = tool.parse_timecode("01:00:00:00", 60)
        self.assertEqual(start, 216000)
        self.assertEqual(tool.format_timecode(start + 61, 60), "01:00:01:01")

    def test_bad_timecode(self):
        with self.assertRaises(tool.UsageError):
            tool.parse_timecode("1:00", 60)
        with self.assertRaises(tool.UsageError):
            tool.parse_timecode("01:00:00:60", 60)


class EventTests(unittest.TestCase):
    def build(self, **kwargs):
        rows = tool.read_rows(kwargs.pop("path"))
        return tool.build_event_markers(rows, kwargs.pop("fps", 60.0), kwargs.pop("flash", 0.0),
                                        kwargs.pop("kinds", {"event", "shot", "speed", "action"}),
                                        kwargs.pop("pause_shift", True))

    def test_default_kinds_and_times(self):
        with tempfile.TemporaryDirectory() as folder:
            markers, zero_text, dropped = self.build(path=write_sample(folder), flash=2.0)
        self.assertIn("sync flash", zero_text)
        self.assertEqual(dropped, 0)
        names = [m.name for m in markers]
        self.assertEqual(names[0], "ignition + speed speed[0] to 0.25x")
        self.assertEqual(markers[0].frame, round((2.0 + 38.1) * 60))
        self.assertEqual(markers[0].color, "Blue")
        self.assertIn("shot camera[1] (Chase)", names)

    def test_action_name_has_the_reason(self):
        with tempfile.TemporaryDirectory() as folder:
            markers, _, _ = self.build(path=write_sample(folder))
        self.assertIn("stage: fires sequence 3", [m.name for m in markers])

    def test_pause_length_has_three_decimals(self):
        with tempfile.TemporaryDirectory() as folder:
            path = os.path.join(folder, "events.csv")
            with open(path, "w", encoding="utf-8", newline="") as handle:
                handle.write("realSeconds,gameSeconds,missionSeconds,simSpeed,frame,kind,name,detail\n"
                             "0,0,0,1,,marker,syncFlash,x\n"
                             "5,0,0,1,,pause,end,length=1.234 s real; total=1.234 s real\n"
                             "6,0,0,1,,event,later,\n")
            markers, _, _ = self.build(path=path, fps=1000.0)
        self.assertEqual(markers[0].frame, 7234)

    def test_pause_shifts_later_rows(self):
        with tempfile.TemporaryDirectory() as folder:
            path = write_sample(folder)
            shifted, _, _ = self.build(path=path)
            plain, _, _ = self.build(path=path, pause_shift=False)
        stage_shifted = next(m for m in shifted if m.kind == "action")
        stage_plain = next(m for m in plain if m.kind == "action")
        self.assertEqual(stage_shifted.frame - stage_plain.frame, 600)
        liftoff_shifted = next(m for m in shifted if m.name == "liftoff")
        liftoff_plain = next(m for m in plain if m.name == "liftoff")
        self.assertEqual(liftoff_shifted.frame, liftoff_plain.frame)

    def test_rows_before_the_zero_are_dropped(self):
        with tempfile.TemporaryDirectory() as folder:
            path = os.path.join(folder, "events.csv")
            with open(path, "w", encoding="utf-8", newline="") as handle:
                handle.write("realSeconds,gameSeconds,missionSeconds,simSpeed,frame,kind,name,detail\n"
                             "1.0,0,0,1,,event,early,\n2.0,0,0,1,,marker,syncFlash,x\n3.0,0,0,1,,event,late,\n")
            markers, _, dropped = self.build(path=path)
        self.assertEqual(dropped, 1)
        self.assertEqual([m.name for m in markers], ["late"])

    def test_edl_layout(self):
        with tempfile.TemporaryDirectory() as folder:
            markers, _, _ = self.build(path=write_sample(folder))
            out = os.path.join(folder, "m.edl")
            tool.write_edl(out, "take", markers, 60.0, tool.parse_timecode("01:00:00:00", 60))
            with open(out, encoding="ascii") as handle:
                text = handle.read()
        lines = text.split("\n")
        self.assertEqual(lines[0], "TITLE: take")
        self.assertEqual(lines[1], "FCM: NON-DROP FRAME")
        self.assertTrue(lines[3].startswith("001  001      V     C        01:00:38:06 01:00:38:07 01:00:38:06 01:00:38:07"))
        self.assertEqual(lines[4], " |C:ResolveColorBlue |M:ignition + speed speed[0] to 0.25x |D:1")

    def test_main_writes_both_files(self):
        with tempfile.TemporaryDirectory() as folder:
            path = write_sample(folder)
            prefix = os.path.join(folder, "out")
            code = tool.main(["events", path, "--flash-frames", "120", "--fps", "30", "-o", prefix])
            self.assertEqual(code, 0)
            self.assertTrue(os.path.exists(prefix + ".edl"))
            with open(prefix + ".csv", encoding="utf-8") as handle:
                header = handle.readline().strip()
        self.assertEqual(header, "timecode,frame,videoSeconds,kind,name,detail,color")

    def test_unknown_kind_is_an_error(self):
        with tempfile.TemporaryDirectory() as folder:
            self.assertEqual(tool.main(["events", write_sample(folder), "--kinds", "nonsense", "-o", os.path.join(folder, "o")]), 2)


class BeatTests(unittest.TestCase):
    def test_beats_and_bars(self):
        markers = tool.build_beat_markers(120.0, 1.0, 5.0, 4, 1, 60.0)
        self.assertEqual([m.frame for m in markers[:3]], [60, 90, 120])
        self.assertEqual(markers[0].name, "bar 1")
        self.assertEqual(markers[1].name, "beat 1.2")
        self.assertEqual(markers[4].name, "bar 2")
        self.assertEqual(markers[-1].frame, 300)

    def test_every_other_beat(self):
        markers = tool.build_beat_markers(120.0, 0.0, 3.0, 4, 2, 60.0)
        self.assertEqual([m.frame for m in markers], [0, 60, 120, 180])

    def test_main(self):
        with tempfile.TemporaryDirectory() as folder:
            prefix = os.path.join(folder, "b")
            self.assertEqual(tool.main(["beats", "--bpm", "100", "--first-beat-seconds", "0.5", "--length", "10", "-o", prefix]), 0)
            self.assertTrue(os.path.exists(prefix + ".edl"))
            self.assertEqual(tool.main(["beats", "--bpm", "0", "-o", prefix]), 2)


if __name__ == "__main__":
    unittest.main()
