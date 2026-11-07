# -*- coding: utf-8 -*-
"""VIS-2/P1 offline D1/D2 visual detectors (library) -- safety-hardened.

Revision P1 keeps the P0 detection algorithms and thresholds UNCHANGED and
only hardens the safety contract required before any future live integration:

1. Game-content readiness gate (SPK-2: CAPTURE_READY != GAME_CONTENT_READY).
   detect() takes `content_ready` supplied by an independent upstream stage.
   R1: STRICT tri-state -- only Python True / False / None are accepted
   (identity check, no bool() coercion); 0, 1, "False", "true", numpy.bool_
   and any other type are refused as NOT_EVALUABLE("invalid_content_ready_type")
     True  -> normal decision logic may emit deterministic states.
     False -> upstream says content is NOT ready: NOT_EVALUABLE
              ("game_content_not_ready_upstream"); scores are still recorded.
     None  -> readiness UNKNOWN: deterministic states are downgraded to
              UNKNOWN ("content_readiness_unknown_*_evidence_held"). Absence
              must never be asserted from icon/panel absence alone.
   Image decodability is NEVER treated as content readiness.

2. Full feature-region coverage via an explicit valid-pixel mask.
   detect() takes `valid_mask` (same HxW as frame). R1: STRICT value domain --
   only a boolean ndarray or a uint8 ndarray with values in {0,1} is legal;
   other dtypes, or uint8 masks containing 2/255/..., are refused as
   NOT_EVALUABLE("invalid_valid_mask_values"/"invalid_valid_mask_dtype").
   No truncation, no casting, no mean-based acceptance (a half-0/half-2 mask
   has mean 1.0 and is refused). 1 = real source pixel, 0 = padding.
   The detector verifies EVERY region it actually reads:
     D1: the whole template search window (D1_ROI +- 30 px).
     D2: panel ROI AND digit box AND interior box AND flower box, each
         individually. (The digit box top edge [y1455] lies 5 px ABOVE the
         panel ROI top edge [y1460] -- P0 checked only the panel ROI, which
         was a real contract hole.)
   Any incomplete region -> NOT_EVALUABLE naming the region and missing
   fraction. Black padding can therefore never manufacture a negative.

3. Degenerate-frame guard: a near-uniform required region (gray std below
   DEGENERACY_STD_MIN) is not game content; the detector refuses to decide
   (NOT_EVALUABLE) instead of matching against flat synthetic input. This is
   an interface-defense guard, NOT an accuracy feature.

States: VISIBLE / NOT_VISIBLE / UNKNOWN / NOT_EVALUABLE. A partial crop can
never produce NOT_VISIBLE. Thresholds are those calibrated in VIS-2/P0 on the
10 labeled VIS-1 full frames; the historical 0.956 correlation is not used.

Offline-only: no game client, no input injection, no capture stream.
"""
import time

import cv2
import numpy as np

FRAME_W, FRAME_H = 3840, 2088

# --- target ROIs, frame-relative [x1,y1,x2,y2] -------------------------------
D1_ROI = (2715, 1470, 2787, 1536)   # PO-confirmed purple dancer icon
D2_PANEL_ROI = (1605, 1460, 2135, 1760)  # dark countdown panel
D2_CLOCK_BOX = (1855, 1465, 1895, 1505)  # clock icon area (diagnostic only)
D2_DIGIT_BOX = (1898, 1455, 2000, 1505)  # MM:SS text (top edge 5px ABOVE panel ROI)
D2_INTERIOR_BOX = (1950, 1530, 2125, 1745)  # panel body right of flower box
D2_FLOWER_BOX = (1640, 1560, 1925, 1755)  # flower basket sub-panel

D1_SEARCH_MARGIN = 30  # template search window = D1_ROI +- 30 px

# regions each detector actually reads and therefore requires to be fully
# real (verified against valid_mask before any feature computation)
D2_REQUIRED_REGIONS = {
    "d2_panel_roi": D2_PANEL_ROI,
    "d2_digit_box": D2_DIGIT_BOX,
    "d2_interior_box": D2_INTERIOR_BOX,
    "d2_flower_box": D2_FLOWER_BOX,
}
D2_DIAGNOSTIC_REGIONS = {"d2_clock_diag": D2_CLOCK_BOX}

# interface-defense guard: required regions flatter than this are treated as
# content-less synthetic input, not as game UI
DEGENERACY_STD_MIN = 2.0

# --- D1 thresholds (unchanged from VIS-2/P0) ---------------------------------
D1_T_HI = 0.76
D1_T_LO = 0.46
D1_PURPLE_PRESENT = 0.22
D1_PURPLE_ABSENT = 0.05

# --- D2 thresholds (unchanged from VIS-2/P0) ---------------------------------
D2_DARK_VISIBLE = 49.0
D2_DARK_ABSENT = 70.0
D2_EDGE_VISIBLE = 0.10
D2_EDGE_ABSENT = 0.18
D2_DIGIT_VISIBLE = 0.04
D2_DIGIT_ABSENT = 0.01
D2_PINK_VISIBLE = 0.015
D2_PINK_ABSENT = 0.005

CROP_GEOMETRY = {
    "bottom": (1152, 1688, 2688, 2008),
    "center": (1344, 720, 2496, 1368),
}


def roi_overlap(roi, region):
    """Coverage of `roi` (frame coords) by an image occupying `region`."""
    rx1, ry1, rx2, ry2 = roi
    ix1, iy1, ix2, iy2 = region
    ox1, oy1 = max(rx1, ix1), max(ry1, iy1)
    ox2, oy2 = min(rx2, ix2), min(ry2, iy2)
    if ox2 <= ox1 or oy2 <= oy1:
        return "NONE", 0.0, None
    inter = (ox2 - ox1) * (oy2 - oy1)
    full = (rx2 - rx1) * (ry2 - ry1)
    pct = inter / full
    box = (ox1, oy1, ox2, oy2)
    if abs(pct - 1.0) < 1e-9:
        return "FULL", 1.0, box
    return "PARTIAL", pct, box


def all_real_mask(height, width):
    """Mask asserting every pixel of a full frame is real source pixels."""
    return np.ones((height, width), dtype=np.uint8)


def place_on_canvas(img_bgr, region):
    """Place a mapped crop onto a black full-frame canvas.

    Returns (canvas, valid_mask). The mask marks exactly the placed region as
    real; padding is 0. Callers MUST pass the returned mask to detect() --
    without it the detector cannot distinguish padding from real pixels.
    """
    x1, y1, x2, y2 = region
    h, w = y2 - y1, x2 - x1
    if img_bgr is None or img_bgr.shape[0] != h or img_bgr.shape[1] != w:
        raise ValueError("image shape does not match mapped region")
    canvas = np.zeros((FRAME_H, FRAME_W, 3), np.uint8)
    mask = np.zeros((FRAME_H, FRAME_W), np.uint8)
    canvas[y1:y2, x1:x2] = img_bgr
    mask[y1:y2, x1:x2] = 1
    return canvas, mask


def region_real_fraction(mask, box):
    """(in_bounds, real_fraction) of `box` under `mask`."""
    x1, y1, x2, y2 = box
    h, w = mask.shape[:2]
    if x1 < 0 or y1 < 0 or x2 > w or y2 > h or x2 <= x1 or y2 <= y1:
        return False, 0.0
    return True, float(mask[y1:y2, x1:x2].mean())


def _sub(im, box):
    x1, y1, x2, y2 = box
    return im[y1:y2, x1:x2]


def _frac_hsv(hsv, box, hlo, hhi, slo, vlo):
    h = _sub(hsv, box)[..., 0]
    s = _sub(hsv, box)[..., 1]
    v = _sub(hsv, box)[..., 2]
    return float(((h >= hlo) & (h <= hhi) & (s >= slo) & (v >= vlo)).mean())


def _frac_white(hsv, box, s_max, v_min):
    s = _sub(hsv, box)[..., 1]
    v = _sub(hsv, box)[..., 2]
    return float(((s <= s_max) & (v >= v_min)).mean())


def _edge_density(bgr, box):
    g = cv2.cvtColor(_sub(bgr, box), cv2.COLOR_BGR2GRAY)
    gx = cv2.Sobel(g, cv2.CV_32F, 1, 0, ksize=3)
    gy = cv2.Sobel(g, cv2.CV_32F, 0, 1, ksize=3)
    mag = np.sqrt(gx * gx + gy * gy)
    return float((mag > 60.0).mean())


def _content_gate(raw_state, content_ready):
    """Apply the upstream content-readiness gate to a would-be state."""
    if content_ready is False:
        return "NOT_EVALUABLE", "game_content_not_ready_upstream"
    if content_ready is None and raw_state in ("VISIBLE", "NOT_VISIBLE"):
        kind = "positive" if raw_state == "VISIBLE" else "absence"
        return "UNKNOWN", "content_readiness_unknown_%s_evidence_held" % kind
    if raw_state in ("VISIBLE", "NOT_VISIBLE"):
        return raw_state, "ok"
    return "UNKNOWN", "evidence_conflict_or_insufficient"


def _ne(reason):
    return {"evaluable": False, "state": "NOT_EVALUABLE", "reason": reason}


def _check_content_ready(content_ready):
    """Strict tri-state check: only Python True / False / None are legal.

    Identity comparison (no bool() coercion): 0, 1, "False", "true",
    numpy.bool_ and any other type are rejected as invalid_content_ready_type.
    """
    if content_ready is True or content_ready is False or content_ready is None:
        return None
    return "invalid_content_ready_type"


def _check_mask(frame_bgr, valid_mask):
    """Strict mask validation. Legal masks ONLY:
    - a boolean ndarray (dtype bool, values inherently 0/1), or
    - a uint8 ndarray whose unique values are a subset of {0, 1}.
    Everything else (int64/float dtypes, uint8 containing 2/255/...) is
    rejected. There is deliberately no truncation/casting and no mean-based
    acceptance: a half-0/half-2 mask has mean 1.0 but is refused.
    """
    if valid_mask is None:
        return all_real_mask(frame_bgr.shape[0], frame_bgr.shape[1]), None
    m = np.asarray(valid_mask)
    if m.ndim != 2 or m.shape[0] != frame_bgr.shape[0] or m.shape[1] != frame_bgr.shape[1]:
        return None, "valid_mask_shape_mismatch"
    if m.dtype == np.bool_:
        return m.astype(np.uint8), None
    if m.dtype == np.uint8:
        if not np.isin(np.unique(m), (0, 1)).all():
            return None, "invalid_valid_mask_values"
        return m, None
    return None, "invalid_valid_mask_dtype"


class D1Detector:
    """Template-matching detector for the PO-confirmed purple dancer icon.

    Algorithm and thresholds are unchanged since VIS-2/P0; P1 adds the mask
    coverage check over the WHOLE search window, the content-readiness gate
    and the degenerate-region guard.
    """

    def __init__(self, template_bgr):
        self.tpl = template_bgr
        self.tpl_gray = cv2.cvtColor(template_bgr, cv2.COLOR_BGR2GRAY)

    def detect(self, frame_bgr, valid_mask=None, content_ready=None):
        t0 = time.perf_counter()
        base = {"detector": "D1"}
        cr_err = _check_content_ready(content_ready)
        if cr_err is not None:
            base.update(_ne(cr_err))
            return self._finish(base, t0)
        if frame_bgr is None or getattr(frame_bgr, "ndim", 0) != 3 or frame_bgr.size == 0:
            base.update(_ne("image_unreadable_or_invalid"))
            return self._finish(base, t0)
        mask, err = _check_mask(frame_bgr, valid_mask)
        if mask is None:
            base.update(_ne(err))
            return self._finish(base, t0)

        x1, y1, x2, y2 = D1_ROI
        m = D1_SEARCH_MARGIN
        window = (x1 - m, y1 - m, x2 + m, y2 + m)
        base["coverage"] = {}
        in_bounds, real = region_real_fraction(mask, window)
        base["coverage"]["d1_search_window"] = in_bounds and real >= 1.0
        if not in_bounds:
            base.update(_ne("frame_smaller_than_required_region:d1_search_window"))
            return self._finish(base, t0)
        if real < 1.0:
            base.update(_ne("region_not_fully_real:d1_search_window(missing=%.1f%%)"
                            % ((1.0 - real) * 100)))
            return self._finish(base, t0)

        win = frame_bgr[window[1]:window[3], window[0]:window[2]]
        win_gray = cv2.cvtColor(win, cv2.COLOR_BGR2GRAY)
        if float(win_gray.std()) < DEGENERACY_STD_MIN:
            base.update(_ne("degenerate_near_uniform_region:d1_search_window"))
            return self._finish(base, t0)

        res = cv2.matchTemplate(win, self.tpl, cv2.TM_CCOEFF_NORMED)
        _, score_color, _, loc_c = cv2.minMaxLoc(res)
        gres = cv2.matchTemplate(win_gray, self.tpl_gray, cv2.TM_CCOEFF_NORMED)
        _, score_gray, _, loc_g = cv2.minMaxLoc(gres)
        hsv = cv2.cvtColor(frame_bgr, cv2.COLOR_BGR2HSV)
        f_purple = _frac_hsv(hsv, D1_ROI, 120, 160, 80, 60)
        best = (window[0] + loc_c[0], window[1] + loc_c[1])

        base.update({
            "score_color": round(float(score_color), 4),
            "score_gray": round(float(score_gray), 4),
            "best_pos_frame": best,
            "f_purple": round(f_purple, 4),
            "scores_finite": bool(np.isfinite(score_color) and np.isfinite(score_gray)),
        })
        if not (np.isfinite(score_color) and np.isfinite(score_gray)):
            base["evaluable"] = False
            base["state"] = "UNKNOWN"
            base["reason"] = "match_score_non_finite_uniform_input"
            return self._finish(base, t0)

        if score_color >= D1_T_HI and f_purple >= D1_PURPLE_PRESENT:
            raw = "VISIBLE"
        elif score_color <= D1_T_LO and f_purple <= D1_PURPLE_ABSENT:
            raw = "NOT_VISIBLE"
        else:
            raw = "UNKNOWN"
        state, reason = _content_gate(raw, content_ready)
        base["raw_state_no_gate"] = raw
        base["evaluable"] = state in ("VISIBLE", "NOT_VISIBLE", "UNKNOWN")
        base["state"] = state
        base["reason"] = reason
        return self._finish(base, t0)

    @staticmethod
    def _finish(base, t0):
        base["detect_ms"] = round((time.perf_counter() - t0) * 1000.0, 3)
        return base


class D2Detector:
    """Panel-presence detector for the countdown panel (digit-agnostic).

    P1 verifies each required region individually (panel ROI, digit box,
    interior box, flower box) against the valid mask before deciding; the
    clock box is diagnostic only and is reported but never required.
    """

    def detect(self, frame_bgr, valid_mask=None, content_ready=None):
        t0 = time.perf_counter()
        base = {"detector": "D2"}
        cr_err = _check_content_ready(content_ready)
        if cr_err is not None:
            base.update(_ne(cr_err))
            return self._finish(base, t0)
        if frame_bgr is None or getattr(frame_bgr, "ndim", 0) != 3 or frame_bgr.size == 0:
            base.update(_ne("image_unreadable_or_invalid"))
            return self._finish(base, t0)
        mask, err = _check_mask(frame_bgr, valid_mask)
        if mask is None:
            base.update(_ne(err))
            return self._finish(base, t0)

        base["coverage"] = {}
        failing = []
        for name, box in sorted(D2_REQUIRED_REGIONS.items()):
            in_bounds, real = region_real_fraction(mask, box)
            base["coverage"][name] = in_bounds and real >= 1.0
            if not in_bounds:
                failing.append("%s(out_of_frame)" % name)
            elif real < 1.0:
                failing.append("%s(missing=%.1f%%)" % (name, (1.0 - real) * 100))
        for name, box in sorted(D2_DIAGNOSTIC_REGIONS.items()):
            in_bounds, real = region_real_fraction(mask, box)
            base["coverage"][name] = in_bounds and real >= 1.0
            base["%s_real" % name] = bool(in_bounds and real >= 1.0)
        if failing:
            base.update(_ne("region_not_fully_real:" + ";".join(failing)))
            return self._finish(base, t0)

        hsv = cv2.cvtColor(frame_bgr, cv2.COLOR_BGR2HSV)
        interior_gray = cv2.cvtColor(_sub(frame_bgr, D2_INTERIOR_BOX),
                                     cv2.COLOR_BGR2GRAY)
        if float(interior_gray.std()) < DEGENERACY_STD_MIN:
            base.update(_ne("degenerate_near_uniform_region:d2_interior_box"))
            return self._finish(base, t0)

        f_clock = _frac_hsv(hsv, D2_CLOCK_BOX, 80, 105, 40, 50)  # diagnostic
        f_digit = _frac_white(hsv, D2_DIGIT_BOX, 70, 180)
        interior_v = cv2.cvtColor(_sub(frame_bgr, D2_INTERIOR_BOX),
                                  cv2.COLOR_BGR2HSV)[..., 2]
        dark_v = float(interior_v.mean())
        edge_d = _edge_density(frame_bgr, D2_INTERIOR_BOX)
        f_pink = _frac_hsv(hsv, D2_FLOWER_BOX, 130, 180, 80, 80)

        base.update({
            "f_clock_diag": round(f_clock, 4),
            "f_digit": round(f_digit, 4),
            "dark_v": round(dark_v, 2),
            "edge_d": round(edge_d, 4),
            "f_pink": round(f_pink, 4),
        })
        body_panel = dark_v <= D2_DARK_VISIBLE and edge_d <= D2_EDGE_VISIBLE
        body_absent = dark_v >= D2_DARK_ABSENT and edge_d >= D2_EDGE_ABSENT
        deco = (f_digit >= D2_DIGIT_VISIBLE) or (f_pink >= D2_PINK_VISIBLE)
        deco_absent = (f_digit <= D2_DIGIT_ABSENT) and (f_pink <= D2_PINK_ABSENT)
        if body_panel and deco:
            raw = "VISIBLE"
        elif body_absent and deco_absent:
            raw = "NOT_VISIBLE"
        else:
            raw = "UNKNOWN"
        state, reason = _content_gate(raw, content_ready)
        base["raw_state_no_gate"] = raw
        base["evaluable"] = state in ("VISIBLE", "NOT_VISIBLE", "UNKNOWN")
        base["state"] = state
        base["reason"] = reason
        return self._finish(base, t0)

    @staticmethod
    def _finish(base, t0):
        base["detect_ms"] = round((time.perf_counter() - t0) * 1000.0, 3)
        return base
