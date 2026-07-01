import json
import shutil
import unittest
from datetime import datetime, timedelta
from pathlib import Path

from src.codex_usage_bridge import collect_codex_usage


class CodexUsageBridgeTests(unittest.TestCase):
    def setUp(self):
        self.work_root = Path(__file__).resolve().parents[1] / ".test-work"
        shutil.rmtree(self.work_root, ignore_errors=True)
        self.work_root.mkdir(exist_ok=True)

    def tearDown(self):
        shutil.rmtree(self.work_root, ignore_errors=True)

    def test_collects_limits_and_costs(self):
        root = self.work_root / "collects_limits_and_costs"
        sessions = root / "sessions" / "2026" / "07" / "01"
        sessions.mkdir(parents=True)
        now = datetime.now().astimezone()
        reset_5h = int((now + timedelta(hours=2)).timestamp())
        reset_weekly = int((now + timedelta(days=3)).timestamp())
        path = sessions / "rollout-2026-07-01T10-00-00-test.jsonl"
        event = {
            "timestamp": now.isoformat(),
            "type": "event_msg",
            "payload": {
                "type": "token_count",
                "info": {
                    "last_token_usage": {
                        "input_tokens": 1000,
                        "cached_input_tokens": 200,
                        "output_tokens": 100,
                    }
                },
                "rate_limits": {
                    "primary": {
                        "used_percent": 12.0,
                        "window_minutes": 300,
                        "resets_at": reset_5h,
                    },
                    "secondary": {
                        "used_percent": 34.0,
                        "window_minutes": 10080,
                        "resets_at": reset_weekly,
                    },
                    "plan_type": "plus",
                },
            },
        }
        path.write_text(json.dumps(event), encoding="utf-8")

        data = collect_codex_usage(root)

        self.assertTrue(data["available"])
        self.assertEqual(data["limits"]["five_hour"]["used_percent"], 12)
        self.assertEqual(data["limits"]["five_hour"]["remaining_percent"], 88)
        self.assertEqual(data["limits"]["weekly"]["used_percent"], 34)
        self.assertEqual(data["limits"]["weekly"]["remaining_percent"], 66)
        self.assertEqual(data["plan_type"], "plus")
        self.assertRegex(data["display"]["codex_5h"], r"^88%  \d{2}:\d{2}$")
        self.assertRegex(data["display"]["codex_weekly"], r"^66%  \d{2}-\d{2}$")

    def test_empty_response_when_no_sessions_exist(self):
        root = self.work_root / "empty_response"
        root.mkdir()
        data = collect_codex_usage(root)

        self.assertFalse(data["available"])
        self.assertEqual(data["display"]["codex_5h"], "unavailable")
        self.assertEqual(data["display"]["codex_weekly"], "unavailable")
        self.assertEqual(data["display"]["summary"], "Codex unavailable")


if __name__ == "__main__":
    unittest.main()
