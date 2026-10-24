"""Static guards for the hard-exclusive WPF stage layout contract."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
UI = ROOT / "src" / "PptInk.App" / "UI"


def source(name: str) -> str:
    return (UI / name).read_text(encoding="utf-8")


def test_layout_uses_one_shared_active_panel_lease():
    layout = source("StageUiLayout.cs")
    assert "StageElement? _activePopover" in layout
    assert "AllocatePopover(StageElement owner" in layout
    assert "AllocateUtilityPanel(StageElement owner" in layout
    assert "TryAcquireEmbeddedChromePanel(Toolbar owner)" in layout
    assert "ReleasePopover(StageElement owner)" in layout
    assert "IntersectsWith" not in layout


def test_main_toolbar_and_page_islands_are_assigned_disjoint_fixed_lanes():
    orchestrator = source("UiOrchestrator.cs")
    assert "leftSlotEnd + laneGap" in orchestrator
    assert "rightSlotStart - laneGap" in orchestrator
    assert "SetChromeSlot(leftSlotStart, leftSlotEnd, dockBottom)" in orchestrator
    assert "SetChromeSlot(rightSlotStart, rightSlotEnd, dockBottom)" in orchestrator
    assert "tb.SetDockLane(laneLeft, laneRight, dockBottom)" in orchestrator
    assert "IntersectsWith" not in orchestrator


def test_page_turn_islands_clip_and_scroll_inside_their_assigned_slot():
    island = source("PageTurnIsland.cs")
    assert "HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden" in island
    assert "ClipToBounds = true" in island
    assert "Width = slotWidth" in island
    assert "MaxWidth = slotWidth" in island
    assert "_chromeScroller.Width = slotWidth" in island


def test_drag_constraints_are_based_on_owners_slot_not_live_overlap_scans():
    layout = source("StageUiLayout.cs")
    assert "if (owner is Toolbar)" in layout
    assert "return segment.ConstrainToChromeSlot(requested, w, h)" in layout
    assert "ReferenceEquals(owner, _activePopover)" in layout
    assert "Fixed HUD column" in layout or "HUD 占左侧专属列" in layout
    assert "IntersectsWith" not in layout


def test_modal_modes_and_notifications_are_mutually_exclusive_with_panels():
    layout = source("StageUiLayout.cs")
    toast = source("Toast.cs")
    assert "if (HasExclusivePanel || HasActiveModal" in layout
    assert "if (_activeModal is { } active && !ReferenceEquals(active, modal)) return false" in layout
    assert "leftLimit = Math.Max(leftLimit, hud.ScreenLeft + hudWidth + 12)" in layout
    assert "toastRect" not in layout
    assert "IntersectsWith" not in layout
    assert "StageUiLayout.HasExclusivePanel || StageUiLayout.HasActiveModal" in toast


def test_palette_open_failure_resets_all_height_constraints():
    palette = source("Toolbar.Palette.cs")
    assert "ResetPaletteMeasureConstraints();" in palette
    assert "if (_paletteBody is not null) _paletteBody.MaxHeight = double.PositiveInfinity;" in palette
    assert "if (_paletteScroller is not null) _paletteScroller.MaxHeight = double.PositiveInfinity;" in palette
    assert "TryAcquireEmbeddedChromePanel(this)" in palette


def test_all_regular_utility_panels_request_the_exclusive_slot():
    panels = source("PluginPanels.cs")
    stage_element = source("StageElement.cs")
    assert panels.count("ShowInExclusivePanelSlot(") >= 4
    assert "StageUiLayout.AllocateUtilityPanel(this, preferredWidth, preferredHeight)" in stage_element
    assert "StageUiLayout.AllocatePopover(this, anchor, panelW, panelH, gap)" in stage_element


def test_enabling_debug_ui_releases_popup_slot_before_badge_is_painted():
    layout = source("StageUiLayout.cs")
    orchestrator = source("UiOrchestrator.Onboarding.cs")
    assert "PrepareForDebugBadgeChange();" in layout
    assert "Toast.DismissForExclusivePanel();" in layout
    assert "StageUiLayout.PrepareForDebugModeChange(enabled);" in orchestrator
    assert "StageUiLayout.PrepareForDebugBadgeChange();" in source("UiOrchestrator.cs")
    assert "PopupWorkspaceTop" in layout
    assert "96 : 8" in layout
    assert "owner.ScreenTop - PopupWorkspaceTop - reservedSegmentHeight" in layout


def test_debug_hud_is_clipped_to_its_own_column_and_badge_has_capacity_guard():
    layout = source("StageUiLayout.cs")
    hud = source("DebugHud.cs")
    renderer = source("UnifiedStageWindow.InkRender.cs")
    assert "ApplyDebugHudSlot(DebugHudElement hud)" in layout
    assert "Math.Min(430, Math.Min(availableWidth" in layout
    assert "hud.MaxHeight = Math.Max(1, _chromeTop - _screen.Top - 16);" in layout
    assert "VerticalScrollBarVisibility = ScrollBarVisibility.Auto" in hud
    assert "_drag.Wire(dragHeader);" in hud
    assert "if (_renderSize.Width < 430 + bw + 48) return;" in renderer
