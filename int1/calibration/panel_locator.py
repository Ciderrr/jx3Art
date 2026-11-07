"""Current-frame artist panel localization; D1 is independent and never translated.

Rose artwork + nearby clock + frozen D2 body evidence. The old fixed ROI alone
cannot establish global panel absence when the panel is movable.
"""
from pathlib import Path
import math
import cv2,numpy as np
from d1_profile import dl
from location_guard import _bounded_scores

ASSETS=Path(__file__).parent/'assets'
ROSES=(1660,1535,1765,1635);CLOCK=(1857,1467,1893,1501)
REGION=(1605,1455,2135,1760)
MAX_PROPOSALS=8

def clock_core(search,template):
    """Fixed template foreground; no scene-background pixels or new asset."""
    hsv=cv2.cvtColor(template,cv2.COLOR_BGR2HSV)
    core=((hsv[:,:,1]>=60)&(hsv[:,:,2]>=140)).astype(np.uint8)
    shape=core
    if core.sum()<200:raise ValueError('clock_core_invalid')
    scores=cv2.matchTemplate(search,template,cv2.TM_CCORR_NORMED,mask=core)
    scores[~np.isfinite(scores)]=-1
    _,score,_,point=cv2.minMaxLoc(scores);x,y=point
    patch=search[y:y+34,x:x+36];h=cv2.cvtColor(patch,cv2.COLOR_BGR2HSV)
    lo=int(hsv[:,:,0][shape>0].min())-10;hi=int(hsv[:,:,0][shape>0].max())+10
    actual=((h[:,:,0]>=lo)&(h[:,:,0]<=hi)&(h[:,:,1]>=60)&(h[:,:,2]>=140)).astype(np.uint8)
    kernel=np.ones((3,3),np.uint8)
    coverage=float((cv2.dilate(actual,kernel)[shape>0]>0).mean())
    precision=float((cv2.dilate(shape,kernel)[actual>0]>0).mean()) if actual.any() else 0
    return dict(confirmed=score>=.98 and coverage>=.98 and precision>=.98,score=score,coverage=coverage,precision=precision,point=list(point),source='clock-foreground-color-and-shape-v1')

def painted_body(frame,region):
    x,y,r,b=region
    template=cv2.imread(str(ASSETS/'panel_painted_v1.png'));mask=cv2.imread(str(ASSETS/'panel_painted_mask_v1.png'),cv2.IMREAD_GRAYSCALE)
    if template is None or mask is None or template.shape!=(200,180,3) or mask.shape!=(200,180):raise ValueError('painted_panel_asset_invalid')
    expected=mask>0
    if not np.isin(mask,[0,255]).all() or expected.sum()<1500 or expected[110:].sum()<300:raise ValueError('painted_panel_mask_invalid')
    patch=frame[y+70:y+270,x+40:x+220]
    if patch.shape!=template.shape:return dict(confirmed=False,reason='painted_panel_out_of_frame')
    agrees=np.abs(patch.astype(np.int16)-template.astype(np.int16)).max(axis=2)<=30
    total=float(agrees[expected].mean());lower=float(agrees[110:][expected[110:]].mean())
    return dict(confirmed=total>=.98 and lower>=.98,agreement=total,lower_agreement=lower,mask_pixels=int(expected.sum()),source='opaque-panel-painted-core-v1')


def small_painted_body(frame,region,ui_scale):
    """Independent colour + bidirectional shape, at a physical-pixel tolerance.

    Reference colours and shape use only the original painted mask. No scene
    background, new template or changed legacy pixel-agreement threshold.
    """
    if isinstance(ui_scale,bool) or not isinstance(ui_scale,(int,float)) or not math.isfinite(ui_scale) or not .35<=ui_scale<=.5:
        return dict(confirmed=False,reason='not_small_scale')
    if not isinstance(frame,np.ndarray) or frame.dtype!=np.uint8 or frame.shape!=(2088,3840,3) or not isinstance(region,(tuple,list)) or len(region)!=4 or any(type(v) is not int for v in region):return dict(confirmed=False,reason='invalid_small_painted_geometry')
    if region[0]<0 or region[1]<0 or region[2]>3840 or region[3]>2088 or (region[2]-region[0],region[3]-region[1])!=(530,305):return dict(confirmed=False,reason='invalid_small_painted_geometry')
    x,y,_,_=region;template=cv2.imread(str(ASSETS/'panel_painted_v1.png'));mask=cv2.imread(str(ASSETS/'panel_painted_mask_v1.png'),cv2.IMREAD_GRAYSCALE)
    if template is None or mask is None or template.shape!=(200,180,3) or mask.shape!=(200,180) or not np.isin(mask,[0,255]).all():raise ValueError('painted_panel_asset_invalid')
    expected=mask>0
    if expected.sum()<1500 or expected[110:].sum()<300:raise ValueError('painted_panel_mask_invalid')
    patch=frame[y+70:y+270,x+40:x+220]
    if patch.shape!=template.shape:return dict(confirmed=False,reason='painted_panel_out_of_frame')
    hsv=cv2.cvtColor(patch,cv2.COLOR_BGR2HSV)
    actual=cv2.erode(((hsv[:,:,1]>=60)&(hsv[:,:,2]>=170)).astype(np.uint8),np.ones((3,3),np.uint8))>0
    radius=min(3,math.ceil(1/ui_scale));kernel=np.ones((radius*2+1,radius*2+1),np.uint8)
    observed_near=cv2.dilate(actual.astype(np.uint8),kernel)>0;reference_near=cv2.dilate(expected.astype(np.uint8),kernel)>0
    def evidence(start):
        reference=expected[start:];observed=actual[start:]
        colour=float(cv2.matchTemplate(patch[start:],template[start:],cv2.TM_CCORR_NORMED,mask=reference.astype(np.uint8))[0,0])
        coverage=float(observed_near[start:][reference].mean());precision=float(reference_near[start:][observed].mean()) if observed.any() else 0
        return dict(colour=colour if np.isfinite(colour) else -1.,coverage=coverage,precision=precision,reference_pixels=int(reference.sum()),observed_pixels=int(observed.sum()))
    total=evidence(0);lower=evidence(110)
    confirmed=total['observed_pixels']>=1500 and lower['observed_pixels']>=300 and all(v[key]>=.98 for v in [total,lower] for key in ['colour','coverage','precision'])
    return dict(confirmed=bool(confirmed),total=total,lower=lower,radius=radius,source='small-painted-colour-and-shape-v1')


def small_clock_core(search,template,ui_scale):
    if isinstance(ui_scale,bool) or not isinstance(ui_scale,(int,float)) or not math.isfinite(ui_scale) or not .35<=ui_scale<=.5:return dict(confirmed=False,reason='not_small_scale')
    if not isinstance(search,np.ndarray) or search.dtype!=np.uint8 or search.ndim!=3 or search.shape[2]!=3 or not 34<=search.shape[0]<=42 or not 36<=search.shape[1]<=44 or not isinstance(template,np.ndarray) or template.shape!=(34,36,3) or template.dtype!=np.uint8:return dict(confirmed=False,reason='invalid_small_clock_geometry')
    hsv=cv2.cvtColor(template,cv2.COLOR_BGR2HSV);shape=((hsv[:,:,1]>=60)&(hsv[:,:,2]>=140)).astype(np.uint8)
    if shape.sum()<200:raise ValueError('clock_core_invalid')
    scores=cv2.matchTemplate(search,template,cv2.TM_CCORR_NORMED,mask=shape);scores[~np.isfinite(scores)]=-1
    _,score,_,point=cv2.minMaxLoc(scores);x,y=point;patch=search[y:y+34,x:x+36];h=cv2.cvtColor(patch,cv2.COLOR_BGR2HSV)
    lo=int(hsv[:,:,0][shape>0].min())-10;hi=int(hsv[:,:,0][shape>0].max())+10
    actual=((h[:,:,0]>=lo)&(h[:,:,0]<=hi)&(h[:,:,1]>=60)&(h[:,:,2]>=140)).astype(np.uint8)
    radius=min(3,math.ceil(1/ui_scale));kernel=np.ones((radius*2+1,radius*2+1),np.uint8)
    coverage=float((cv2.dilate(actual,kernel)[shape>0]>0).mean());precision=float((cv2.dilate(shape,kernel)[actual>0]>0).mean()) if actual.any() else 0
    return dict(confirmed=bool(score>=.98 and coverage>=.98 and precision>=.98),score=score,coverage=coverage,precision=precision,point=list(point),radius=radius,source='small-clock-colour-and-shape-v1')


def detect(frame,valid_mask=None,search_extent=None,ui_scale=1):
    location={'status':'NOT_EVALUABLE','verified':False,'candidates':[],'scope':'original 4K and original UI size; panel translation only','rose_proposals':[],'rejected_clocks':[]}
    if not isinstance(frame,np.ndarray) or frame.shape!=(2088,3840,3) or frame.dtype!=np.uint8:
        return {'location':location,'d2':{'state':'NOT_EVALUABLE','reason':'unsupported_frame_geometry_or_type'}}
    if isinstance(ui_scale,bool) or not isinstance(ui_scale,(int,float)) or not math.isfinite(ui_scale) or not .35<=ui_scale<=2:
        return {'location':location,'d2':{'state':'NOT_EVALUABLE','reason':'unsupported_ui_scale'}}
    if valid_mask is not None and (not isinstance(valid_mask,np.ndarray) or valid_mask.shape!=(2088,3840) or valid_mask.dtype not in (np.dtype(bool),np.dtype(np.uint8)) or np.asarray(valid_mask).max()>1):
        return {'location':location,'d2':{'state':'NOT_EVALUABLE','reason':'invalid_valid_mask'}}
    rose=cv2.imread(str(ASSETS/'panel_roses_v1.png'));clock=cv2.imread(str(ASSETS/'panel_clock_v1.png'))
    if rose is None or clock is None or rose.shape!=(100,105,3) or clock.shape!=(34,36,3):raise ValueError('panel_anchor_assets_missing_or_invalid')
    search=frame
    if search_extent is not None:
        from frame_geometry import real_search_frame
        search=real_search_frame(frame,valid_mask,search_extent)
    scores=_bounded_scores(search,rose);h,w=rose.shape[:2];candidates=[];examined=0
    for _ in range(MAX_PROPOSALS):
        _,score,_,(x,y)=cv2.minMaxLoc(scores)
        if not np.isfinite(score) or score<.90:break
        location['rose_proposals'].append([x,y,x+w,y+h])
        examined+=1;scores[max(0,y-h+1):min(scores.shape[0],y+h),max(0,x-w+1):min(scores.shape[1],x+w)]=-1
        dx,dy=x-ROSES[0],y-ROSES[1];cx,cy=CLOCK[0]+dx,CLOCK[1]+dy
        left,top,right,bottom=cx-4,cy-4,cx+clock.shape[1]+4,cy+clock.shape[0]+4
        sx,sy,ex,ey=REGION[0]+dx,REGION[1]+dy,REGION[2]+dx,REGION[3]+dy
        if min(left,top,sx,sy)<0 or right>3840 or bottom>2088 or ex>3840 or ey>2088:continue
        if valid_mask is not None and not valid_mask[sy:ey,sx:ex].all():continue
        search=frame[top:bottom,left:right]
        _,color,_,point=cv2.minMaxLoc(cv2.matchTemplate(search,clock,cv2.TM_CCOEFF_NORMED))
        px,py=point;patch=search[py:py+clock.shape[0],px:px+clock.shape[1]]
        gray=float(cv2.matchTemplate(cv2.cvtColor(patch,cv2.COLOR_BGR2GRAY),cv2.cvtColor(clock,cv2.COLOR_BGR2GRAY),cv2.TM_CCOEFF_NORMED)[0,0])
        legacy_clock=np.isfinite(color) and np.isfinite(gray) and color>=.70 and gray>=.70 and patch.std()>=8
        core=clock_core(search,clock) if not legacy_clock else None
        small_clock=None;body_shape=None
        if not legacy_clock:
            if not core['confirmed'] and .35<=ui_scale<=.5:small_clock=small_clock_core(search,clock,ui_scale)
            clock_confirmed=core['confirmed'] or small_clock is not None and small_clock['confirmed']
            body=painted_body(frame,(sx,sy,ex,ey)) if clock_confirmed else None
            if clock_confirmed and (not body['confirmed'] or small_clock is not None) and .35<=ui_scale<=.5:body_shape=small_painted_body(frame,(sx,sy,ex,ey),ui_scale)
            body_confirmed=body is not None and (body['confirmed'] or body_shape is not None and body_shape['confirmed'])
            # A new small clock always needs the complete new painted-body proof.
            if not clock_confirmed or not body_confirmed or small_clock is not None and not (body_shape is not None and body_shape['confirmed']):
                rejected=dict(region=[left,top,right,bottom],clock_color=float(color),clock_gray=gray,clock_core=core,painted_body=body,verification_stage='PAINTED_BODY_UNCONFIRMED' if clock_confirmed else 'CLOCK_UNCONFIRMED',reason='clock_or_painted_body_unconfirmed')
                if small_clock is not None:rejected['small_clock_core']=small_clock
                if body_shape is not None:rejected['painted_shape']=body_shape
                location['rejected_clocks'].append(rejected)
                continue
            px,py=(core if core['confirmed'] else small_clock)['point']
        candidates.append({'panel_offset':[dx,dy],'timer_offset':[left+px-CLOCK[0],top+py-CLOCK[1]],
            'source_region':[sx,sy,ex,ey],'rose_score':round(float(score),4),'clock_color':round(float(color),4),'clock_gray':round(gray,4),
            'clock_decision_source':'legacy-clock-correlation' if legacy_clock else 'clock-core-and-painted-body','clock_core':core})
        if small_clock is not None:candidates[-1].update(small_clock_core=small_clock,clock_decision_source='small-clock-and-painted-shape')
        if body_shape is not None:
            candidates[-1]['painted_shape']=body_shape
            if small_clock is None:candidates[-1]['clock_decision_source']='clock-core-and-small-painted-shape'
    exhausted=examined==MAX_PROPOSALS and cv2.minMaxLoc(scores)[1]>=.90
    del scores
    location.update(status='SEARCH_LIMIT' if exhausted else 'UNIQUE_PANEL' if len(candidates)==1 else 'AMBIGUOUS' if candidates else 'UNRESOLVED_PANEL_CANDIDATE' if examined else 'NO_VERIFIED_PANEL',candidates=candidates,examined=examined)
    if exhausted or len(candidates)!=1:
        fixed=dl.D2Detector().detect(frame,valid_mask,content_ready=True)
        return {'location':location,'d2':dict(fixed,fixed_roi_state=fixed['state'],fixed_roi_reason=fixed['reason'],state='UNKNOWN',reason='movable_panel_location_unresolved')}
    candidate=candidates[0];sx,sy,ex,ey=candidate['source_region'];a,b,c,d=REGION
    canvas=np.zeros_like(frame);mask=np.zeros((2088,3840),np.uint8)
    canvas[b:d,a:c]=frame[sy:ey,sx:ex];mask[b:d,a:c]=1
    d2=dl.D2Detector().detect(canvas,mask,content_ready=True)
    foreground=painted_body(frame,candidate['source_region'])
    if (d2['state']=='UNKNOWN' or d2.get('reason')=='degenerate_near_uniform_region:d2_interior_box') and foreground['confirmed']:
        d2=dict(d2,legacy_state=d2['state'],state='VISIBLE',evaluable=True,reason='unique-rose-clock-and-painted-body',decision_source='positive-painted-panel-evidence-v1')
    d2['painted_body']=foreground
    eligible=d2['state']=='UNKNOWN' or d2.get('reason')=='degenerate_near_uniform_region:d2_interior_box'
    shape=candidate.get('painted_shape')
    if eligible and not foreground['confirmed'] and .35<=ui_scale<=.5 and shape is None:shape=small_painted_body(frame,candidate['source_region'],ui_scale)
    if shape is not None:
        d2['painted_shape']=shape
        if eligible and shape['confirmed']:d2=dict(d2,legacy_state=d2['state'],state='VISIBLE',evaluable=True,reason='unique-rose-clock-and-small-painted-shape',decision_source='small-painted-colour-and-shape-v1')
    if 'small_clock_core' in candidate:d2.update(small_clock_core=candidate['small_clock_core'],legacy_clock_core=candidate['clock_core'],clock_decision_source=candidate['clock_decision_source'])
    location.update(verified=True,panel_offset=candidate['panel_offset'],timer_offset=candidate['timer_offset'],source_region=candidate['source_region'])
    d2.setdefault('decision_source','current-frame-rose-clock-localized-frozen-D2')
    return {'location':location,'d2':d2}
