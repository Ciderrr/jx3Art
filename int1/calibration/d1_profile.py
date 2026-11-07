"""Explicit, frame-bound D1 calibration. No game/window/input APIs.
The accepted VIS-2 detector/thresholds remain read-only. Real pixels are translated
into its canonical D1 window; a separately versioned current-frame feature supplement
may resolve UNKNOWN appearance evidence. D2 is never translated.
"""
from pathlib import Path
import sys, json, hashlib
import cv2
import numpy as np
ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'vis2/detector'))
import detector_lib as dl
from appearance_features import CurrentFrameFeatures
TEMPLATE = ROOT / 'vis2/template/d1_icon_template.png'

def sha(data): return hashlib.sha256(data).hexdigest()

def validate(profile, geometry=None, require_confirmed=True):
    if not isinstance(profile, dict) or profile.get('schema') != 'jx3-d1-calibration-v1':
        raise ValueError('invalid_profile_schema')
    if require_confirmed and profile.get('confirmed_by_user') is not True:
        raise ValueError('profile_not_confirmed')
    size=profile.get('frame_size'); box=profile.get('icon_bbox')
    if not isinstance(size,list) or len(size)!=2 or any(type(v) is not int for v in size):
        raise ValueError('invalid_frame_size')
    # Current INT-1 and accepted VIS-2 algorithms support only this frame geometry.
    if size != [dl.FRAME_W,dl.FRAME_H]: raise ValueError('unsupported_geometry_recalibrate')
    if geometry is not None and list(geometry)!=size: raise ValueError('geometry_changed_recalibrate')
    if not isinstance(box,list) or len(box)!=4 or any(type(v) is not int for v in box):
        raise ValueError('invalid_icon_bbox')
    x,y,x2,y2=box; a,b,c,d=dl.D1_ROI; margin=dl.D1_SEARCH_MARGIN
    if (x2-x,y2-y)!=(c-a,d-b): raise ValueError('icon_scale_unsupported_recalibrate')
    if x-margin<0 or y-margin<0 or x2+margin>size[0] or y2+margin>size[1]:
        raise ValueError('search_window_out_of_frame')
    if profile.get('template_sha256')!=sha(TEMPLATE.read_bytes()): raise ValueError('template_changed_recalibrate')
    digest=profile.get('reference_frame_sha256')
    if not isinstance(digest,str) or len(digest)!=64 or any(c not in '0123456789abcdef' for c in digest):
        raise ValueError('invalid_reference_hash')
    return profile

def translate(frame, mask, profile):
    validate(profile, [frame.shape[1],frame.shape[0]])
    return align_box(frame, mask, profile['icon_bbox'])

def align_box(frame, mask, box):
    # Shared real-pixel mapping used by calibration and candidate verification.
    if not isinstance(frame,np.ndarray) or frame.dtype!=np.uint8 or frame.shape!=(dl.FRAME_H,dl.FRAME_W,3):
        raise ValueError('unsupported_frame_geometry_or_type')
    if not isinstance(box,(list,tuple)) or len(box)!=4 or any(type(v) is not int for v in box):
        raise ValueError('invalid_icon_bbox')
    x,y,x2,y2=box;m=dl.D1_SEARCH_MARGIN
    if (x2-x,y2-y)!=(dl.D1_ROI[2]-dl.D1_ROI[0],dl.D1_ROI[3]-dl.D1_ROI[1]):
        raise ValueError('icon_scale_unsupported_recalibrate')
    if x-m<0 or y-m<0 or x2+m>frame.shape[1] or y2+m>frame.shape[0]:
        raise ValueError('search_window_out_of_frame')
    if mask is not None and (not isinstance(mask,np.ndarray) or mask.shape!=frame.shape[:2]
            or not (mask.dtype==np.bool_ or mask.dtype==np.uint8 and np.asarray(mask).max()<=1)):
        raise ValueError('invalid_valid_mask')
    x,y,x2,y2=box;a,b,c,d=dl.D1_ROI;m=dl.D1_SEARCH_MARGIN
    aligned=np.zeros_like(frame);valid=np.zeros(frame.shape[:2],np.uint8)
    aligned[b-m:d+m,a-m:c+m]=frame[y-m:y2+m,x-m:x2+m]
    valid[b-m:d+m,a-m:c+m]=1 if mask is None else mask[y-m:y2+m,x-m:x2+m]
    return aligned,valid

class CalibratedD1:
    def __init__(self, detector, profile, enable_features=True):
        self.detector=detector;self.profile=validate(profile)
        self.features=CurrentFrameFeatures(detector.tpl) if enable_features else None
    def detect(self,frame,valid_mask=None,content_ready=None):
        try:
            aligned,mask=translate(frame,valid_mask,self.profile)
        except (ValueError,AttributeError,IndexError) as e:
            return {'detector':'D1','state':'NOT_EVALUABLE','evaluable':False,'reason':str(e),'detect_ms':0}
        result=self.detector.detect(aligned,mask,content_ready)
        # The frozen detector remains authoritative for definite/unevaluable results.
        # Only an UNKNOWN appearance can receive independent CURRENT-frame support.
        if self.features is not None and result.get('raw_state_no_gate')=='UNKNOWN' and content_ready is not False:
            a,b,c,d=dl.D1_ROI;m=dl.D1_SEARCH_MARGIN
            region=aligned[b:d,a:c]
            purple=dl._frac_hsv(cv2.cvtColor(region,cv2.COLOR_BGR2HSV),(0,0,c-a,d-b),120,160,80,60)
            if purple>=dl.D1_PURPLE_PRESENT:
                evidence=self.features.inspect(aligned[b-m:d+m,a-m:c+m],mask[b-m:d+m,a-m:c+m])
                evidence['reference_template_sha256']=self.profile['template_sha256']
                result['appearance_features']=evidence
                result['detect_ms']=round(result['detect_ms']+evidence['elapsed_ms'],3)
                if evidence['confirmed']:
                    result['base_detector_state']=result['state']
                    result['base_raw_state_no_gate']=result['raw_state_no_gate']
                    result['raw_state_no_gate']='VISIBLE'
                    result['state'],gate_reason=dl._content_gate('VISIBLE',content_ready)
                    result['reason']='current_frame_features_verified' if content_ready is True else gate_reason
                    result['decision_source']='current-frame-sift-v1'
                    dx,dy=evidence['translation_px'];result['best_pos_frame']=[round(a-m+dx),round(b-m+dy)]

        if 'best_pos_frame' in result:
            x,y=self.profile['icon_bbox'][:2];a,b=dl.D1_ROI[:2]
            px,py=result['best_pos_frame'];result['best_pos_frame']=[px+x-a,py+y-b]
        result['calibration_scope']='configured_region_only'
        result['calibration_reference_sha256']=self.profile['reference_frame_sha256']
        return result

def profile_at(frame_path, center, confirmed=False):
    data=Path(frame_path).read_bytes();frame=cv2.imdecode(np.frombuffer(data,np.uint8),cv2.IMREAD_COLOR)
    if frame is None: raise ValueError('reference_frame_unreadable')
    if list(frame.shape[:2][::-1])!=[dl.FRAME_W,dl.FRAME_H]: raise ValueError('unsupported_geometry_recalibrate')
    tpl=cv2.imread(str(TEMPLATE));h,w=tpl.shape[:2];cx,cy=center
    if not all(type(v) is int for v in center):raise ValueError('invalid_click')
    if not (0<=cx<frame.shape[1] and 0<=cy<frame.shape[0]):raise ValueError('click_out_of_bounds')
    # Snap only near the user's explicit click, never choose an unrelated global match.
    left=max(0,cx-w//2-30);top=max(0,cy-h//2-30)
    right=min(frame.shape[1],cx+w//2+30);bottom=min(frame.shape[0],cy+h//2+30)
    patch=frame[top:bottom,left:right]
    if patch.shape[0]<h or patch.shape[1]<w:raise ValueError('click_out_of_bounds')
    _,score,_,pos=cv2.minMaxLoc(cv2.matchTemplate(patch,tpl,cv2.TM_CCOEFF_NORMED))
    x,y=left+pos[0],top+pos[1]
    profile={'schema':'jx3-d1-calibration-v1','frame_size':[frame.shape[1],frame.shape[0]],
             'icon_bbox':[x,y,x+w,y+h],'template_sha256':sha(TEMPLATE.read_bytes()),
             'reference_frame_sha256':sha(data),'confirmed_by_user':confirmed,
             'selection_method':'user_point_then_local_template_snap','local_match_score':score}
    validate(profile,require_confirmed=False)
    # Reuse accepted color/template/degeneracy decision to reject incorrect clicks.
    diagnostic=dict(profile,confirmed_by_user=True)
    if CalibratedD1(dl.D1Detector(tpl),diagnostic,enable_features=False).detect(frame,content_ready=True)['state']!='VISIBLE':
        raise ValueError('selected_region_not_verified_icon')
    return profile

def load(path): return validate(json.loads(Path(path).read_text(encoding='utf-8-sig')))
