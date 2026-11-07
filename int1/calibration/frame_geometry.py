"""Offline physical-pixel transforms. No window, input or game APIs.

UI scale is an explicit hypothesis, independent of frame resolution. The supplied
content rectangle excludes window borders; it is never inferred from aspect ratio.
"""
from dataclasses import dataclass
import math
import cv2
import numpy as np

CANONICAL_W, CANONICAL_H = 3840, 2088


@dataclass(frozen=True)
class FrameGeometry:
    width: int
    height: int
    content: tuple
    ui_scale: float
    minimum_scale: float = .5

    def __post_init__(self):
        if any(type(v) is not int or v <= 0 for v in (self.width, self.height)):
            raise ValueError('invalid_frame_size')
        if self.width * self.height > 32_000_000:
            raise ValueError('frame_pixel_budget_exceeded')
        if not isinstance(self.content, tuple) or len(self.content) != 4 or any(type(v) is not int for v in self.content):
            raise ValueError('invalid_content_rectangle')
        x, y, r, b = self.content
        if not (0 <= x < r <= self.width and 0 <= y < b <= self.height):
            raise ValueError('content_out_of_frame')
        if type(self.minimum_scale) not in (int,float) or not math.isfinite(self.minimum_scale) or not .35 <= self.minimum_scale <= .5:
            raise ValueError('unsupported_minimum_scale')
        if isinstance(self.ui_scale, bool) or not isinstance(self.ui_scale, (int, float)) or not math.isfinite(self.ui_scale) or not self.minimum_scale <= self.ui_scale <= 2:
            raise ValueError('unsupported_ui_scale')
        nw, nh = self.normalized_size
        if not (1 <= nw <= CANONICAL_W and 1 <= nh <= CANONICAL_H):
            raise ValueError('normalized_content_exceeds_canvas')

    @property
    def normalized_size(self):
        x, y, r, b = self.content
        return round((r-x)/self.ui_scale), round((b-y)/self.ui_scale)

    def normalize(self, frame):
        if not isinstance(frame, np.ndarray) or frame.dtype != np.uint8 or frame.shape != (self.height, self.width, 3):
            raise ValueError('frame_geometry_mismatch')
        x, y, r, b = self.content
        nw, nh = self.normalized_size
        crop = frame[y:b, x:r]
        interpolation = cv2.INTER_AREA if nw < r-x else cv2.INTER_LINEAR
        pixels = crop if crop.shape[:2] == (nh, nw) else cv2.resize(crop, (nw, nh), interpolation=interpolation)
        canvas = np.zeros((CANONICAL_H, CANONICAL_W, 3), np.uint8)
        valid = np.zeros((CANONICAL_H, CANONICAL_W), np.uint8)
        canvas[:nh, :nw] = pixels
        valid[:nh, :nw] = 1
        return canvas, valid

    def frame_box(self, box):
        if not isinstance(box, (tuple, list)) or len(box) != 4 or any(type(v) is not int for v in box):
            raise ValueError('invalid_normalized_box')
        a, b, c, d = box
        nw, nh = self.normalized_size
        if not (0 <= a < c <= nw and 0 <= b < d <= nh):
            raise ValueError('box_outside_real_content')
        x, y, r, bottom = self.content
        # Use actual rounded resize dimensions, not the nominal scale.
        return [x+math.floor(a*(r-x)/nw), y+math.floor(b*(bottom-y)/nh),
                x+math.ceil(c*(r-x)/nw), y+math.ceil(d*(bottom-y)/nh)]

    def screen_center(self, box, origin):
        if not isinstance(origin, tuple) or len(origin) != 2 or any(type(v) is not int for v in origin):
            raise ValueError('invalid_physical_frame_origin')
        self.frame_box(box)  # Reject padding and invalid rectangles first.
        a, b, c, d = box
        x, y, r, bottom = self.content
        nw, nh = self.normalized_size
        return [origin[0]+x+math.floor((a+c)*(r-x)/(2*nw)),
                origin[1]+y+math.floor((b+d)*(bottom-y)/(2*nh))]


def real_search_frame(frame, valid, extent):
    """Only an explicit solid top-left real rectangle may exclude padding."""
    if not isinstance(extent,(tuple,list)) or len(extent)!=2 or any(type(v) is not int for v in extent):raise ValueError('invalid_search_extent')
    w,h=extent
    if not 1<=w<=CANONICAL_W or not 1<=h<=CANONICAL_H or valid is None or not valid[:h,:w].all() or int(np.count_nonzero(valid))!=w*h:raise ValueError('search_extent_not_real_rectangle')
    return frame[:h,:w]
