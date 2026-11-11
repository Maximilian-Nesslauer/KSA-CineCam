import datetime
import json
from pathlib import Path
import sys
import tempfile
import threading
import time
import unittest
import uuid

# Isolated mode (python -I) leaves the script folder out of the import path.
sys.path.insert(0, str(Path(__file__).resolve().parent))
import cinecam  # noqa: E402


class TransportTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        (self.root / "requests").mkdir()
        (self.root / "responses").mkdir()
        self.nonce = uuid.uuid4().hex
        self.write_lease(self.nonce)

    def tearDown(self):
        self.temp.cleanup()

    def write_lease(self, nonce):
        cinecam.atomic_json(self.root / "session.json", {
            "version": 1, "nonce": nonce,
            "updatedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        })

    def test_response_and_cleanup(self):
        def server():
            while not (paths := list((self.root / "requests").glob("*.json"))):
                time.sleep(0.01)
            payload = json.loads(paths[0].read_text())
            cinecam.atomic_json(self.root / "responses" / paths[0].name, {
                "version": 1, "id": payload["id"], "nonce": payload["nonce"],
                "result": {"ok": True, "code": "ok", "message": "done"},
            })

        thread = threading.Thread(target=server)
        thread.start()
        result = cinecam.request(self.root, {"operation": "status"}, 2)
        thread.join()
        self.assertTrue(result["ok"])
        self.assertEqual(list((self.root / "requests").iterdir()), [])
        self.assertEqual(list((self.root / "responses").iterdir()), [])

    def test_restart_and_timeout(self):
        def restart():
            while not list((self.root / "requests").glob("*.json")):
                time.sleep(0.01)
            self.write_lease(uuid.uuid4().hex)

        thread = threading.Thread(target=restart)
        thread.start()
        with self.assertRaisesRegex(ValueError, "restarted"):
            cinecam.request(self.root, {"operation": "status"}, 2)
        thread.join()
        with self.assertRaises(TimeoutError):
            cinecam.request(self.root, {"operation": "status"}, 0.01)


class CommandTests(unittest.TestCase):
    def build(self, *argv):
        return cinecam.command(cinecam.parser().parse_args(list(argv)))

    def test_replay_commands(self):
        self.assertEqual(self.build("open", "Flight A"), {"operation": "open", "id": "Flight A"})
        self.assertEqual(self.build("seek", "12.5"), {"operation": "seek", "time": 12.5})
        self.assertEqual(self.build("play"), {"operation": "setPlaying", "enabled": True})
        self.assertEqual(self.build("pause"), {"operation": "setPlaying", "enabled": False})
        self.assertEqual(self.build("speed", "0.5"), {"operation": "setSpeed", "speed": 0.5})
        self.assertEqual(self.build("camera", "free"), {"operation": "setCamera", "name": "free"})
        self.assertEqual(self.build("follow", "1"), {"operation": "follow", "actorId": 1})
        self.assertEqual(self.build("follow"), {"operation": "follow"})
        self.assertEqual(self.build("fov", "35"), {"operation": "setFov", "fov": 35.0})
        self.assertEqual(self.build("add-key", "part-mount"), {"operation": "addKey", "name": "part-mount"})
        self.assertEqual(self.build("status"), {"operation": "status"})
        self.assertEqual(self.build("preview"), {"operation": "preview"})

    def test_render_commands(self):
        self.assertEqual(self.build("render"), {"operation": "render", "name": "whole"})
        self.assertEqual(self.build("render", "--from-here"), {"operation": "render", "name": "current"})
        self.assertEqual(self.build("render-stop"), {"operation": "renderStop"})
        with self.assertRaises(SystemExit):
            cinecam.parser().parse_args(["render", "whole"])

    def test_removed_commands_are_rejected(self):
        for name in ("save-view", "play-views", "speed-key", "new-project"):
            with self.assertRaises(SystemExit):
                cinecam.parser().parse_args([name, "x"])

    def test_unknown_key_kind(self):
        with self.assertRaises(SystemExit):
            cinecam.parser().parse_args(["add-key", "dolly"])


class OptionTests(unittest.TestCase):
    def test_default_control_dir(self):
        path = cinecam.default_control_dir()
        self.assertEqual(path.parts[-4:], ("My Games", "Kitten Space Agency", "CineCam", "control"))
        self.assertTrue(path.is_absolute())

    def test_unavailable_message_names_the_folder(self):
        message = cinecam.unavailable_message(Path("X") / "control")
        self.assertIn(str(Path("X") / "control"), message)
        self.assertIn("--control-dir", message)


if __name__ == "__main__":
    unittest.main()
