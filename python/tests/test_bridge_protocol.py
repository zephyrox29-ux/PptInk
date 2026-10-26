"""IPC contract tests for the long-lived Python bridge process."""
from __future__ import annotations

import json
import os
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def test_handle_validates_untrusted_requests_and_stays_usable():
    sys.path.insert(0, str(ROOT))
    import ink_bridge

    hello = ink_bridge.handle({"cmd": "hello"})
    assert hello["ok"] is True
    assert hello["server"] == "pptink-bridge"

    assert ink_bridge.handle({"cmd": "recognize", "points": "not-a-list"})["reason"] == "invalid_points"
    assert ink_bridge.handle({"cmd": "recognize", "points": [[0, 0], [1, 1], [float("nan"), 2]]})["reason"] == "invalid_points"
    assert ink_bridge.handle({"cmd": "config", "options": ["not", "an object"]})["reason"] == "invalid_options"
    # String-encoded NaN/Infinity must be normalized; allow_nan=False JSON output cannot emit them.
    configured = ink_bridge.handle({"cmd": "config", "options": {
        "closure_tol": "nan", "score_threshold": "inf",
        "tolerances": {"circle": "nan", "line": "0.03", "unknown-shape": "inf"},
    }})
    assert configured["ok"] is True
    assert configured["config"]["closure_tol"] == 0.18
    assert configured["config"]["score_threshold"] == 0.52
    assert configured["config"]["tolerances"]["circle"] == 0.045
    assert configured["config"]["tolerances"]["line"] == 0.03
    assert ink_bridge.handle({"cmd": "unknown"})["reason"].startswith("unknown cmd")
    # A malformed request does not poison the singleton session or subsequent valid calls.
    assert ink_bridge.handle({"cmd": "shapes"})["ok"] is True


def test_ping_and_warmup_report_health():
    sys.path.insert(0, str(ROOT))
    import ink_bridge

    ping = ink_bridge.handle({"cmd": "ping"})
    assert ping["ok"] is True
    assert ping["server"] == "pptink-bridge"
    assert isinstance(ping["pid"], int)
    assert ping["numpy"] is True
    assert ping["recognizer"] is True
    assert "stats" in ping

    warm = ink_bridge.handle({"cmd": "warmup"})
    assert warm["ok"] is True
    assert warm["warmed"] is True


def test_stdio_protocol_is_one_json_response_per_input_line():
    lines = [
        "not-json",
        json.dumps({"cmd": "hello"}),
        json.dumps({"cmd": "shutdown"}),
    ]
    proc = subprocess.run(
        [sys.executable, "-u", str(ROOT / "ink_bridge.py")],
        input="\n".join(lines) + "\n",
        text=True,
        capture_output=True,
        cwd=str(ROOT),
        timeout=20,
        check=True,
    )
    responses = [json.loads(line) for line in proc.stdout.splitlines() if line.strip()]
    assert len(responses) == len(lines)
    assert responses[0] == {"ok": False, "reason": "invalid_json"}
    assert responses[1]["ok"] is True and responses[1]["server"] == "pptink-bridge"
    assert responses[2]["ok"] is True and responses[2]["bye"] is True
    assert "stdio ready" in proc.stderr
