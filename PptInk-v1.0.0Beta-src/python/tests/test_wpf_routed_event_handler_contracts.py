"""Static guards for WPF routed-event delegate signatures."""
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[2]
APP_SOURCE = REPO_ROOT / "src" / "PptInk.App"


def test_preview_stylus_down_uses_the_specific_wpf_delegate_everywhere():
    registrations = []
    for source in APP_SOURCE.rglob("*.cs"):
        for line_number, line in enumerate(source.read_text(encoding="utf-8").splitlines(), 1):
            if "PreviewStylusDownEvent" not in line:
                continue
            registrations.append((source.relative_to(APP_SOURCE), line_number, line.strip()))
            assert "StylusDownEventHandler" in line, (
                f"{source}:{line_number}: PreviewStylusDownEvent requires StylusDownEventHandler"
            )
            assert "StylusEventHandler" not in line, (
                f"{source}:{line_number}: StylusEventHandler is not the PreviewStylusDownEvent delegate"
            )

    # ToolbarFactory, toolbar drag grip, and plugin-panel drag target.
    assert len(registrations) == 3, f"Expected 3 guarded registrations, found {registrations!r}"


def test_stylus_move_and_up_keep_their_move_up_handler_type():
    for source in APP_SOURCE.rglob("*.cs"):
        for line_number, line in enumerate(source.read_text(encoding="utf-8").splitlines(), 1):
            if "PreviewStylusMoveEvent" in line or "PreviewStylusUpEvent" in line:
                assert "StylusEventHandler" in line, (
                    f"{source}:{line_number}: PreviewStylusMove/Up requires StylusEventHandler"
                )
                assert "StylusDownEventHandler" not in line, (
                    f"{source}:{line_number}: down-specific delegate used for move/up event"
                )
