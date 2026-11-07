# -*- coding: utf-8 -*-
"""INT-1/P0 bridge server -- long-running stdin/stdout JSON-lines bridge to the
gate-passed VIS-2/P1-R1 detectors (detector_lib.py, ALGORITHMS UNCHANGED).

Protocol (one JSON object per line, UTF-8):
  request : {"id": <int>, "frame": "<lossless image path>", "content_ready": true|false|null,
             "valid_mask": null}
            (full-frame 3840x2088 PNG/BMP -> valid_mask None -> all-real mask,
             exactly the standalone usage the detector README documents)
  response: {"id": <int>, "ok": true, "d1": {...}, "d2": {...}} on success
            {"id": <int>, "ok": false, "error": "<reason>"} on any failure
The server never exits on bad input; it answers an error and keeps serving.
No game access, no window operations, no input -- read-only offline library.
"""
import argparse
import json
import os
import sys

import cv2

HERE = os.path.dirname(os.path.abspath(__file__))
VIS2_DETECTOR = os.path.normpath(os.path.join(HERE, "..", "..", "vis2", "detector"))
TEMPLATE = os.path.normpath(os.path.join(HERE, "..", "..", "vis2", "template", "d1_icon_template.png"))
sys.path.insert(0, VIS2_DETECTOR)

import detector_lib as dl  # noqa: E402  (gate-passed P1-R1, untouched)

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--d1-profile')
    args = parser.parse_args()
    tpl = cv2.imread(TEMPLATE)
    if tpl is None:
        print(json.dumps({"ok": False, "error": "template_unreadable", "path": TEMPLATE}), flush=True)
        return 3
    d1 = dl.D1Detector(tpl)
    if args.d1_profile:
        try:
            sys.path.insert(0, os.path.normpath(os.path.join(HERE, '..', 'calibration')))
            from d1_profile import load
            from location_guard import LocationGuard
            d1 = LocationGuard(d1, load(args.d1_profile))
        except (ValueError, OSError) as ex:
            print(json.dumps({'ok': False, 'error': 'd1_calibration_invalid:' + str(ex)}), flush=True)
            return 4
    d2 = dl.D2Detector()
    print(json.dumps({"ok": True, "event": "ready",
                      "frame_geometry": [dl.FRAME_W, dl.FRAME_H],
                      "template": os.path.basename(TEMPLATE)}), flush=True)
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            req = json.loads(line)
            rid = req.get("id")
            frame = cv2.imread(str(req.get("frame", "")), cv2.IMREAD_COLOR)
            if frame is None:
                resp = {"id": rid, "ok": False, "error": "frame_unreadable"}
            else:
                cr = req.get("content_ready", None)
                mask = req.get("valid_mask", None)  # None -> all-real
                r1 = d1.detect(frame, mask, cr)
                r2 = d2.detect(frame, mask, cr)
                resp = {"id": rid, "ok": True, "d1": r1, "d2": r2}
        except Exception as exc:  # never die on bad input
            resp = {"ok": False, "error": "bridge_exception:%s" % exc}
        print(json.dumps(resp, ensure_ascii=False), flush=True)

if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stdin.reconfigure(encoding="utf-8")
    main()
