"""Saved-frame only: overlap tiles before canonical normalization.

No input or winner selection. Duplicate/conflicting evidence across tiles stays
separate. A successful tile is never permission to act on the whole frame.
"""
import argparse
import json
import math
import cv2
from frame_geometry import FrameGeometry, CANONICAL_W, CANONICAL_H
from portable_probe import probe

MAX_TILES = 9


def plan_tiles(width, height, content, ui_scale, minimum_scale=.5, balanced=False):
    # Reuse all production-independent geometry validation. Only the explicit
    # canonical canvas overflow is handled here; invalid geometry still fails.
    try:
        return [FrameGeometry(width, height, content, ui_scale, minimum_scale)]
    except ValueError as error:
        if str(error) != 'normalized_content_exceeds_canvas':
            raise
    x, y, right, bottom = content
    span_x, span_y = math.floor(CANONICAL_W*ui_scale), math.floor(CANONICAL_H*ui_scale)
    # Covers a complete 530x305 panel and the entry/context footprint across seams.
    def positions(length, span, overlap):
        if length <= span:
            return [0]
        if balanced:
            count=math.ceil((length-overlap)/(span-overlap))
            span=math.ceil((length+(count-1)*overlap)/count)
        result = list(range(0, length-span, span-overlap))
        result.append(length-span)
        return sorted(set(result))
    xs = positions(right-x, span_x, math.ceil(640*ui_scale))
    ys = positions(bottom-y, span_y, math.ceil(400*ui_scale))
    if len(xs)*len(ys) > MAX_TILES:
        raise ValueError('tile_budget_exceeded')
    return [FrameGeometry(width, height, (x+a, y+b, min(right,x+a+span_x), min(bottom,y+b+span_y)), ui_scale, minimum_scale)
            for b in ys for a in xs]


def probe_tiles(frame, ui_scale, content=None):
    if frame is None:
        raise ValueError('image_not_readable')
    height, width = frame.shape[:2]
    content = tuple(content) if content is not None else (0,0,width,height)
    geometries = plan_tiles(width,height,content,ui_scale)
    reports = []
    for geometry in geometries:
        result = probe(frame,[ui_scale],geometry.content)['hypotheses'][0]
        reports.append(dict(tile_content=list(geometry.content), **result))
    return dict(schema='jx3-offline-tiled-probe-v1', action_eligible=False,
                scope='saved-image simulation only; no global absence/uniqueness or input authority',
                tile_count=len(reports), tiles=reports)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--frame', required=True)
    parser.add_argument('--scale', required=True, type=float)
    parser.add_argument('--content', type=int, nargs=4)
    args = parser.parse_args()
    print(json.dumps(probe_tiles(cv2.imread(args.frame),args.scale,args.content),ensure_ascii=False,allow_nan=False))
