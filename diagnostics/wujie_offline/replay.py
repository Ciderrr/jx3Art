"""Saved-frame research for the measured 2560x1440 Wujie window profile."""

import argparse
import json
from pathlib import Path
import sys

import cv2
import numpy as np


CLIENT_SIZE = (2560, 1440)
MAX_PROPOSALS = 12
ENTRY_THRESHOLD = 0.965
ANCHOR_THRESHOLD = 0.90
PANEL_REFERENCE = (900, 767, 1590, 1174)
ROSE_REFERENCE = (951, 869, 1083, 1026)
HEADER_REFERENCE = (937, 783, 1017, 828)
FOLD_REFERENCE = (1495, 773, 1557, 833)
TIMER_REFERENCE = (1018, 789, 1117, 825)


def read_image(path, size=None):
    frame = cv2.imread(str(path))
    if frame is None or (size is not None and frame.shape[:2][::-1] != size):
        raise ValueError(f"missing image or unexpected dimensions: {path.name}")
    return frame


def crop(frame, box):
    x, y, right, bottom = box
    return frame[y:bottom, x:right]


def translate(box, dx, dy):
    x, y, right, bottom = box
    return x + dx, y + dy, right + dx, bottom + dy


def contained(box, width, height):
    x, y, right, bottom = box
    return 0 <= x < right <= width and 0 <= y < bottom <= height


def proposals(frame, template, threshold, mask=None):
    method = cv2.TM_CCOEFF_NORMED if mask is None else cv2.TM_CCORR_NORMED
    scores = cv2.matchTemplate(frame, template, method, mask=mask)
    scores[~np.isfinite(scores)] = -1
    h, w = template.shape[:2]
    boxes = []
    for _ in range(MAX_PROPOSALS + 1):
        _, score, _, (x, y) = cv2.minMaxLoc(scores)
        if score < threshold:
            return boxes, False
        boxes.append((x, y, x + w, y + h))
        scores[max(0, y - h // 2):y + h // 2 + 1, max(0, x - w // 2):x + w // 2 + 1] = -1
    return boxes, True


def icon_matches(pixels, template, mask):
    """Check all four quadrants of the icon foreground on the current pixels."""
    error = cv2.absdiff(pixels, template)
    for y in (0, 48):
        for x in (0, 48):
            sector = np.s_[y:y + 48, x:x + 48]
            if float(error[sector][mask[sector] != 0].mean()) > 14:
                return False
    return True


class WujieReplay:
    def __init__(self, fixture_dir):
        self.icon_mask = cv2.circle(np.zeros((96, 96), np.uint8), (48, 48), 38, 255, -1)
        if (fixture_dir / "entry.png").is_file():
            self.entry = read_image(fixture_dir / "entry.png", (96, 96))
            self.stop = read_image(fixture_dir / "stop.png", (96, 96))
            self.rose = read_image(fixture_dir / "rose.png", (132, 157))
            self.header = read_image(fixture_dir / "header.png", (80, 45))
            self.fold = read_image(fixture_dir / "fold.png", (62, 60))
        else:
            start = read_image(fixture_dir / "01-start-crop.png", (291, 180))
            stop = read_image(fixture_dir / "03-stop-crop.png", (243, 165))
            panel = read_image(fixture_dir / "02-performing.png", (2517, 1394))
            self.entry = crop(start, (142, 23, 238, 119))
            self.stop = crop(stop, (121, 19, 217, 115))
            self.rose = crop(panel, ROSE_REFERENCE)
            self.header = crop(panel, HEADER_REFERENCE)
            self.fold = crop(panel, FOLD_REFERENCE)

    def locate_panel(self, client):
        roses, capped = proposals(client, self.rose, ANCHOR_THRESHOLD)
        headers, header_cap = proposals(client, self.header, ANCHOR_THRESHOLD)
        if capped or header_cap:
            return "SEARCH_LIMIT", None, None
        if len(roses) > 1 or len(headers) > 1:
            return "AMBIGUOUS", None, None
        if not roses:
            state = "UNRESOLVED_PANEL_CANDIDATE" if headers else "NO_VERIFIED_PANEL"
            return state, None, None

        x, y, _, _ = roses[0]
        dx, dy = x - ROSE_REFERENCE[0], y - ROSE_REFERENCE[1]
        panel = translate(PANEL_REFERENCE, dx, dy)
        height, width = client.shape[:2]
        if not contained(panel, width, height):
            return "UNRESOLVED_PANEL_CANDIDATE", None, None

        for reference, template in ((HEADER_REFERENCE, self.header), (FOLD_REFERENCE, self.fold)):
            anchor = crop(client, translate(reference, dx, dy))
            score = float(cv2.matchTemplate(anchor, template, cv2.TM_CCOEFF_NORMED)[0, 0])
            if not np.isfinite(score) or score < ANCHOR_THRESHOLD:
                return "UNRESOLVED_PANEL_CANDIDATE", None, None
        return "UNIQUE_PANEL", panel, translate(TIMER_REFERENCE, dx, dy)

    def inspect(self, frame, content):
        height, width = frame.shape[:2]
        x, y, right, bottom = content
        if not contained(content, width, height) or (right - x, bottom - y) != CLIENT_SIZE:
            raise ValueError("unverified client dimensions or bounds")
        client = crop(frame, content)
        proposed, capped = proposals(client, self.entry, ENTRY_THRESHOLD, self.icon_mask)
        accepted = [box for box in proposed if icon_matches(crop(client, box), self.entry, self.icon_mask)]
        stops, stop_cap = proposals(client, self.stop, ENTRY_THRESHOLD, self.icon_mask)
        confirmed_stop = len(stops) == 1 and not stop_cap and icon_matches(crop(client, stops[0]), self.stop, self.icon_mask)
        panel_state, panel, timer = self.locate_panel(client)

        if capped or stop_cap:
            entry_state = "SEARCH_LIMIT"
        elif len(proposed) > 1:
            entry_state = "AMBIGUOUS"
        elif not proposed:
            entry_state = "NO_VERIFIED_CANDIDATE"
        elif len(accepted) != 1 or stops or panel_state != "NO_VERIFIED_PANEL":
            entry_state = "UNRESOLVED_ENTRY"
        else:
            entry_state = "UNIQUE_CANDIDATE"

        performance = "UNKNOWN"
        if not proposed and not stop_cap and len(stops) <= 1:
            if panel_state == "UNIQUE_PANEL":
                performance = "VISIBLE_EXPANDED"
            elif confirmed_stop and panel_state == "NO_VERIFIED_PANEL":
                performance = "VISIBLE_STOP_BUTTON"

        return {
            "scope": "offline saved frame; no input permission",
            "entry": entry_state,
            "entry_boxes": [translate(box, x, y) for box in accepted],
            "stop_proposals": len(stops),
            "stop_confirmed": confirmed_stop,
            "panel": panel_state,
            "performance": performance,
            "panel_box": translate(panel, x, y) if panel is not None else None,
            "timer_region": translate(timer, x, y) if timer is not None else None,
            "countdown_seconds": None,
        }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--templates", required=True, type=Path)
    parser.add_argument("--frame", type=Path)
    parser.add_argument("--content", nargs=4, type=int)
    parser.add_argument("--server", action="store_true")
    args = parser.parse_args()
    cv2.setNumThreads(1)
    recognizer = WujieReplay(args.templates)
    if args.server:
        for _ in range(128):
            line = sys.stdin.readline(65537)
            if not line:
                return
            if len(line) > 65536:
                raise ValueError("request budget exceeded")
            request = json.loads(line)
            if request.get("entryPatch"):
                raise ValueError("Wujie requires a full-frame recheck")
            visual = recognizer.inspect(read_image(Path(request["frame"])), request["content"])
            print(json.dumps({"id": request["id"], "result": visual}, allow_nan=False), flush=True)
    else:
        if args.frame is None or args.content is None:
            parser.error("--frame and --content are required for a replay")
        print(json.dumps(recognizer.inspect(read_image(args.frame), args.content), allow_nan=False))


if __name__ == "__main__":
    main()
