"""Candidate discovery and safe interpretation, independent from the frozen VIS-2 classifier.
Discovery proposes locations only. It never confirms a profile or promotes a held result.
"""
import copy,time
import cv2,numpy as np
import threading
from contextlib import contextmanager
from concurrent.futures import ThreadPoolExecutor,wait,FIRST_COMPLETED
from d1_profile import dl,TEMPLATE,align_box,CalibratedD1,validate

MAX_PROPOSALS=12
_matching_policy=threading.local()

@contextmanager
def matching_workers(workers):
    if workers not in (1,2):raise ValueError('matching_worker_budget')
    previous=getattr(_matching_policy,'workers',1);_matching_policy.workers=workers
    try:yield
    finally:_matching_policy.workers=previous

def _bounded_scores(frame,template):
    """Same NCC windows and global ordering; bound OpenCV's per-call scratch area.

    Tile the *output positions*, with template-size input overlap. Every window
    occurs exactly once; NMS and the proposal budget still apply globally below.
    The full float score map is ~29MiB, instead of a full-frame NCC scratch peak.
    """
    h,w=template.shape[:2];rows=frame.shape[0]-h+1;cols=frame.shape[1]-w+1
    scores=np.empty((rows,cols),np.float32)
    jobs=[(y,min(y+256,rows),x,min(x+512,cols)) for y in range(0,rows,256) for x in range(0,cols,512)]
    def match(job):
        y,bottom,x,right=job;patch=frame[y:bottom+h-1,x:right+w-1]
        return job,cv2.matchTemplate(patch,template,cv2.TM_CCOEFF_NORMED)
    if getattr(_matching_policy,'workers',1)==2:
        with ThreadPoolExecutor(max_workers=2) as workers:
            # At most two unfinished/unconsumed blocks. Executor.map submits the
            # whole frame and can retain every later result behind one slow block.
            remaining=iter(jobs[2:]);pending={workers.submit(match,job) for job in jobs[:2]}
            while pending:
                done,_=wait(pending,return_when=FIRST_COMPLETED)
                while done:
                    future=done.pop();pending.remove(future)
                    (y,bottom,x,right),values=future.result();scores[y:bottom,x:right]=values
                    del values,future
                    job=next(remaining,None)
                    if job is not None:pending.add(workers.submit(match,job))
    else:
        for job in jobs:
            (y,bottom,x,right),values=match(job);scores[y:bottom,x:right]=values
    return scores

def discover(frame, valid_mask=None, search_extent=None):
    started=time.perf_counter()
    def finish(status,candidates,examined=0):
        return {'status':status,'candidates':candidates,'examined':examined,
                'discovery_ms':round((time.perf_counter()-started)*1000,3),
                'requires_user_confirmation':True}
    if not isinstance(frame,np.ndarray) or frame.shape!=(dl.FRAME_H,dl.FRAME_W,3) or frame.dtype!=np.uint8:
        return finish('NOT_EVALUABLE',[])
    if valid_mask is not None and (not isinstance(valid_mask,np.ndarray) or valid_mask.shape!=frame.shape[:2]
            or not (valid_mask.dtype==np.bool_ or valid_mask.dtype==np.uint8 and np.asarray(valid_mask).max()<=1)):
        return finish('NOT_EVALUABLE',[])
    template=cv2.imread(str(TEMPLATE));h,w=template.shape[:2];detector=dl.D1Detector(template)
    search=frame
    if search_extent is not None:
        from frame_geometry import real_search_frame
        search=real_search_frame(frame,valid_mask,search_extent)
    scores=_bounded_scores(search,template)
    candidates=[];examined=0
    for _ in range(MAX_PROPOSALS):
        _,score,_,(x,y)=cv2.minMaxLoc(scores)
        if not np.isfinite(score) or score<dl.D1_T_HI:break
        examined+=1
        # Suppress overlapping template positions, while retaining separate icons.
        scores[max(0,y-h+1):min(scores.shape[0],y+h),max(0,x-w+1):min(scores.shape[1],x+w)]=-1
        box=[x,y,x+w,y+h]
        try:aligned,mask=align_box(frame,valid_mask,box)
        except ValueError:continue
        # Content None keeps this an evidence proposal, never a user readiness assertion.
        evidence=detector.detect(aligned,mask,content_ready=None)
        if evidence.get('raw_state_no_gate')=='VISIBLE':
            candidates.append({'bbox':box,'score_color':evidence['score_color'],
                               'f_purple':evidence['f_purple'],'confirmed_by_user':False})
    # An exhausted proposal budget cannot establish uniqueness.
    remaining=cv2.minMaxLoc(scores)[1]
    if examined==MAX_PROPOSALS and remaining>=dl.D1_T_HI:return finish('SEARCH_LIMIT',candidates,examined)
    return finish('UNIQUE_CANDIDATE' if len(candidates)==1 else 'AMBIGUOUS' if len(candidates)>1 else 'NO_VERIFIED_CANDIDATE',candidates,examined)

class LocationGuard:
    """A confirmed configured position is not evidence of global icon absence.
    Every suggestion comes from the current frame; none are kept across calls.
    """
    def __init__(self,detector,profile):
        self.profile=copy.deepcopy(validate(profile))
        self.local=CalibratedD1(detector,self.profile)
    def detect(self,frame,valid_mask=None,content_ready=None):
        result=self.local.detect(frame,valid_mask,content_ready)
        result['relocation_candidates']=[]
        if result['state']=='NOT_EVALUABLE':
            result['location_status']='NOT_EVALUABLE';return result
        if content_ready is not True:
            result['location_status']='CONTENT_UNCONFIRMED';return result
        if result['state']=='VISIBLE':
            result['location_status']='CONFIRMED_REGION_VISIBLE';return result
        result['configured_region_state']=result['state']
        result['state']='UNKNOWN'
        result['reason']='configured_region_unresolved_not_global_absence'
        proposal=discover(frame,valid_mask)
        result['location_status']=proposal['status']
        result['relocation_candidates']=proposal['candidates']
        result['localization_ms']=proposal['discovery_ms']
        result['requires_location_confirmation']=True
        return result
