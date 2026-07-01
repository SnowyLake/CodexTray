from __future__ import annotations

import argparse
import json
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any
from urllib.parse import urlparse


DEFAULT_HOST = "127.0.0.1"
DEFAULT_PORT = 17890


def _safe_int(value: Any, default: int = 0) -> int:
    try:
        return int(value)
    except (TypeError, ValueError):
        return default


def _safe_float(value: Any, default: float = 0.0) -> float:
    try:
        return float(value)
    except (TypeError, ValueError):
        return default


def _local_now() -> datetime:
    return datetime.now().astimezone()


def _parse_event_timestamp(raw: Any, fallback: datetime) -> datetime:
    if not raw:
        return fallback
    try:
        return datetime.fromisoformat(str(raw).replace("Z", "+00:00")).astimezone()
    except ValueError:
        return fallback


def _format_reset_clock(epoch_seconds: Any, now: datetime) -> str:
    epoch = _safe_int(epoch_seconds, 0)
    if epoch <= 0:
        return "unknown"
    return datetime.fromtimestamp(epoch, tz=now.tzinfo).strftime("%H:%M")


def _format_weekly_reset_label(epoch_seconds: Any, now: datetime) -> str:
    epoch = _safe_int(epoch_seconds, 0)
    if epoch <= 0:
        return "unknown"
    reset_at = datetime.fromtimestamp(epoch, tz=now.tzinfo)
    seconds_until_reset = max(0, int((reset_at - now).total_seconds()))
    if seconds_until_reset < 24 * 60 * 60:
        return reset_at.strftime("%H:%M")
    return reset_at.strftime("%m-%d")


def _format_reset_local(epoch_seconds: Any, now: datetime) -> str:
    epoch = _safe_int(epoch_seconds, 0)
    if epoch <= 0:
        return ""
    return datetime.fromtimestamp(epoch, tz=now.tzinfo).strftime("%Y-%m-%d %H:%M:%S %z")


def _iter_jsonl_files(codex_dir: Path) -> list[Path]:
    sessions_dir = codex_dir / "sessions"
    if not sessions_dir.exists():
        return []
    return sorted(sessions_dir.rglob("*.jsonl"), key=lambda path: path.stat().st_mtime, reverse=True)


def _iter_token_count_events(path: Path) -> list[tuple[datetime, dict[str, Any]]]:
    fallback_time = datetime.fromtimestamp(path.stat().st_mtime, tz=timezone.utc).astimezone()
    events: list[tuple[datetime, dict[str, Any]]] = []
    with path.open("r", encoding="utf-8", errors="ignore") as handle:
        for line in handle:
            if "token_count" not in line:
                continue
            try:
                entry = json.loads(line)
            except json.JSONDecodeError:
                continue
            payload = entry.get("payload")
            if not isinstance(payload, dict) or payload.get("type") != "token_count":
                continue
            event_time = _parse_event_timestamp(entry.get("timestamp"), fallback_time)
            events.append((event_time, payload))
    return events


def _build_limit(name: str, data: dict[str, Any] | None, now: datetime) -> dict[str, Any]:
    data = data or {}
    used_percent = int(round(_safe_float(data.get("used_percent"))))
    used_percent = max(0, min(100, used_percent))
    resets_at = _safe_int(data.get("resets_at"), 0)
    return {
        "name": name,
        "used_percent": used_percent,
        "remaining_percent": 100 - used_percent,
        "window_minutes": _safe_int(data.get("window_minutes")),
        "resets_at": resets_at,
        "reset_at_local": _format_reset_local(resets_at, now),
        "reset_time": _format_reset_clock(resets_at, now),
    }


def collect_codex_usage(codex_dir: Path) -> dict[str, Any]:
    now = _local_now()

    files = _iter_jsonl_files(codex_dir)
    if not files:
        return _empty_response(codex_dir, now, "No Codex session JSONL files found")

    latest_event: tuple[datetime, dict[str, Any], Path] | None = None
    scanned_events = 0

    for path in files:
        try:
            events = _iter_token_count_events(path)
        except OSError:
            continue
        for event_time, payload in events:
            scanned_events += 1
            if latest_event is None or event_time > latest_event[0]:
                latest_event = (event_time, payload, path)

    if latest_event is None:
        return _empty_response(codex_dir, now, "No token_count events found")

    _, latest_payload, latest_path = latest_event
    rate_limits = latest_payload.get("rate_limits") or {}
    primary = _build_limit("five_hour", rate_limits.get("primary"), now)
    secondary = _build_limit("weekly", rate_limits.get("secondary"), now)
    secondary["reset_label"] = _format_weekly_reset_label(secondary["resets_at"], now)

    codex_5h_display = f"{primary['remaining_percent']}%  {primary['reset_time']}"
    codex_weekly_display = f"{secondary['remaining_percent']}%  {secondary['reset_label']}"

    return {
        "available": True,
        "error": None,
        "codex_dir": str(codex_dir),
        "source_file": str(latest_path),
        "source": "sessions",
        "plan_type": rate_limits.get("plan_type") or "unknown",
        "updated_at": now.isoformat(timespec="seconds"),
        "scanned": {
            "files": len(files),
            "token_count_events": scanned_events,
        },
        "limits": {
            "five_hour": primary,
            "weekly": secondary,
        },
        "display": {
            "codex_5h": codex_5h_display,
            "codex_weekly": codex_weekly_display,
            "summary": f"Codex 5h: {codex_5h_display} | Codex Weekly: {codex_weekly_display}",
        },
    }


def _empty_response(codex_dir: Path, now: datetime, error: str) -> dict[str, Any]:
    return {
        "available": False,
        "error": error,
        "codex_dir": str(codex_dir),
        "source": "none",
        "plan_type": "unknown",
        "updated_at": now.isoformat(timespec="seconds"),
        "limits": {
            "five_hour": _build_limit("five_hour", None, now),
            "weekly": _build_limit("weekly", None, now),
        },
        "display": {
            "codex_5h": "unavailable",
            "codex_weekly": "unavailable",
            "summary": "Codex unavailable",
        },
    }


class CodexUsageHandler(BaseHTTPRequestHandler):
    server_version = "CodexUsageBridge/1.0"

    def do_GET(self) -> None:
        parsed = urlparse(self.path)
        if parsed.path == "/health":
            self._send_json({"ok": True})
            return
        if parsed.path != "/codex-usage":
            self.send_error(404, "Not Found")
            return
        response = collect_codex_usage(self.server.codex_dir)
        self._send_json(response)

    def log_message(self, format: str, *args: Any) -> None:
        if getattr(self.server, "quiet", False):
            return
        super().log_message(format, *args)

    def _send_json(self, value: dict[str, Any]) -> None:
        data = json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(data)


class CodexUsageServer(ThreadingHTTPServer):
    def __init__(self, server_address: tuple[str, int], handler_class: type[BaseHTTPRequestHandler], codex_dir: Path, quiet: bool) -> None:
        super().__init__(server_address, handler_class)
        self.codex_dir = codex_dir
        self.quiet = quiet


def _default_codex_dir() -> Path:
    return Path.home() / ".codex"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Serve OpenAI Codex usage data for LiteMonitor.")
    parser.add_argument("--host", default=DEFAULT_HOST, help="Host to bind. Keep 127.0.0.1 unless you understand the exposure risk.")
    parser.add_argument("--port", type=int, default=DEFAULT_PORT, help="Port to bind.")
    parser.add_argument("--codex-dir", type=Path, default=_default_codex_dir(), help="Path to the Codex home directory.")
    parser.add_argument("--once", action="store_true", help="Print one JSON response and exit.")
    parser.add_argument("--quiet", action="store_true", help="Suppress HTTP request logs.")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    codex_dir = args.codex_dir.expanduser()
    if args.once:
        print(json.dumps(collect_codex_usage(codex_dir), ensure_ascii=False, indent=2))
        return 0

    server = CodexUsageServer((args.host, args.port), CodexUsageHandler, codex_dir, args.quiet)
    print(f"Codex usage bridge listening on http://{args.host}:{args.port}/codex-usage")
    print(f"Reading Codex sessions from {codex_dir}")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("Stopping Codex usage bridge.")
    finally:
        server.server_close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
