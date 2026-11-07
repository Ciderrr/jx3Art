"""Saved-image-only portability probe. Intentionally NOT the GUI input path.

Each requested scale is reported independently. No winning scale, readiness,
click permission, OCR reliability or cross-machine compatibility is asserted.
"""
import argparse
import json
import cv2
from frame_geometry import FrameGeometry
from location_guard import discover
from panel_locator import detect
from entry_context import inspect


def probe(frame, scales, content=None):
    if frame is None:
        raise ValueError('image_not_readable')
    h, w = frame.shape[:2]
    if not isinstance(scales, (list, tuple)) or not 1 <= len(scales) <= 5:
        raise ValueError('scale_hypothesis_budget_exceeded')
    reports = []
    for scale in scales:
        report = {'ui_scale_hypothesis': scale, 'action_eligible': False}
        try:
            geometry = FrameGeometry(w, h, tuple(content) if content is not None else (0, 0, w, h), scale)
            normalized, valid = geometry.normalize(frame)
            entry = discover(normalized, valid)
            panel = detect(normalized, valid)
            context = inspect(normalized, entry['candidates'])
            # Context searches outside the D1 icon as well. Padding cannot count.
            if context['confirmed']:
                a, b, c, d = context['anchor_bbox']
                if not valid[b:d, a:c].all():
                    context = dict(context, confirmed=False, reason='anchor_outside_real_content')
            mapped = [dict(item, frame_bbox=geometry.frame_box(item['bbox'])) for item in entry['candidates']]
            panel_location = dict(panel['location'])
            if panel_location['verified']:
                panel_location['frame_region'] = geometry.frame_box(panel_location['source_region'])
            report.update(status='EVALUATED', normalized_size=list(geometry.normalized_size),
                          entry=dict(entry, candidates=mapped), entry_context=context,
                          panel=panel_location, d2=panel['d2'])
        except ValueError as error:
            report.update(status='NOT_EVALUABLE', reason=str(error))
        reports.append(report)
    return {'schema': 'jx3-offline-portability-probe-v1', 'action_eligible': False,
            'scope': 'saved image / explicit scale hypotheses; resampling is simulated evidence only',
            'hypotheses': reports}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--frame', required=True)
    parser.add_argument('--scales', type=float, nargs='+', default=[1.0])
    parser.add_argument('--content', type=int, nargs=4, help='physical content rectangle x y right bottom')
    args = parser.parse_args()
    print(json.dumps(probe(cv2.imread(args.frame), args.scales, args.content), ensure_ascii=False, allow_nan=False))
