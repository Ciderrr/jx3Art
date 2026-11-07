"""Independent positive start-bar context. Never changes frozen D1/D2 states."""
from pathlib import Path
import hashlib
import cv2,numpy as np

ASSET=Path(__file__).parent/'assets/start_bar_gold_v1.png'

def painted_mask(template):
    hsv=cv2.cvtColor(template,cv2.COLOR_BGR2HSV)
    bright=((hsv[:,:,0]>=15)&(hsv[:,:,0]<=45)&(hsv[:,:,1]>=40)&(hsv[:,:,2]>=170)).astype(np.uint8)*255
    # Exclude the one-pixel blended fringe; keep the inner painted control only.
    return cv2.erode(bright,np.ones((3,3),np.uint8))

def inspect(frame,candidates):
    result={'confirmed':False,'source':'local-start-bar-painted-core-v2','scope':'supported 3840x2088 / original UI size; control pixels only, not terrain or D2 absence'}
    if not isinstance(frame,np.ndarray) or frame.shape!=(2088,3840,3) or frame.dtype!=np.uint8 or len(candidates)!=1:
        return result
    box=candidates[0].get('bbox')
    if not isinstance(box,list) or len(box)!=4 or any(type(v) is not int for v in box):return result
    x,y,r,b=box
    if r-x!=72 or b-y!=66:return result
    data=ASSET.read_bytes();template=cv2.imdecode(np.frombuffer(data,np.uint8),cv2.IMREAD_COLOR)
    if template is None or template.shape!=(79,129,3):return result
    # Offset belongs to the gold control in this same start bar, never D2.
    left,top=x-139-6,y-4-6;right,bottom=left+129+12,top+79+12
    if left<0 or top<0 or right>3840 or bottom>2088:return result
    search=frame[top:bottom,left:right]
    _,score,_,point=cv2.minMaxLoc(cv2.matchTemplate(search,template,cv2.TM_CCOEFF_NORMED))
    px,py=point;patch=search[py:py+79,px:px+129]
    gray_score=float(cv2.matchTemplate(cv2.cvtColor(patch,cv2.COLOR_BGR2GRAY),cv2.cvtColor(template,cv2.COLOR_BGR2GRAY),cv2.TM_CCOEFF_NORMED)[0,0])
    def gold(image):
        hsv=cv2.cvtColor(image,cv2.COLOR_BGR2HSV);return (hsv[:,:,0]>=15)&(hsv[:,:,0]<=45)&(hsv[:,:,1]>=40)&(hsv[:,:,2]>=100)
    # Measure color on bright painted strokes, not the yellow/brown ground behind them.
    # The old saved layout has darker terrain; background brightness is not UI evidence.
    mask=painted_mask(template);expected=mask>0
    overlap=float(gold(patch)[expected].mean()) if expected.any() else 0
    finite=all(np.isfinite(v) for v in (score,gray_score,overlap))
    legacy=finite and score>=0.93 and gray_score>=0.93 and overlap>=0.85 and float(patch.std())>=8
    # Full-rectangle correlation is diagnostic only. It cannot authorize input.
    # Matching and color agreement use exactly the same inner painted mask.
    errors=cv2.matchTemplate(search,template,cv2.TM_SQDIFF_NORMED,mask=mask)
    error=float('inf');px2=py2=0;agreement=paint_gold=0
    if np.isfinite(errors).any():
        errors=np.where(np.isfinite(errors),errors,np.inf)
        error,_,point2,_=cv2.minMaxLoc(errors)
        px2,py2=point2;paint=search[py2:py2+79,px2:px2+129]
        delta=np.abs(paint.astype(np.int16)-template.astype(np.int16)).max(axis=2)
        agreement=float((delta[expected]<=24).mean()) if expected.any() else 0
        paint_gold=float(gold(paint)[expected].mean()) if expected.any() else 0
    painted=bool(np.isfinite(error) and error<=0.002 and agreement>=0.98 and paint_gold>=0.85)
    confirmed=painted
    result.update(confirmed=bool(confirmed),color_score=round(float(score),4),gray_score=round(gray_score,4),gold_overlap=round(overlap,4),
                  anchor_bbox=[left+px2,top+py2,left+px2+129,top+py2+79],template_sha256=hashlib.sha256(data).hexdigest(),
                  legacy_confirmed=bool(legacy),legacy_authorizes=False,paint_mask_pixels=int(expected.sum()),paint_mask_sha256=hashlib.sha256(mask.tobytes()).hexdigest(),painted_confirmed=painted,painted_error=float(error) if np.isfinite(error) else None,painted_pixel_agreement=agreement)
    return result
