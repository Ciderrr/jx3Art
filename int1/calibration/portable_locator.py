"""Window-frame adapter: original UI pixels, globally merged overlap evidence.
No desktop or input APIs. Frozen classifiers and their thresholds are unchanged.
"""
import cv2
import math
from pathlib import Path
from concurrent.futures import ThreadPoolExecutor
import numpy as np
from tiled_probe import plan_tiles
from location_guard import discover,matching_workers
from panel_locator import detect, ROSES, REGION
from entry_context import inspect


def locate_frame(frame, content, ui_scale=1, parallel=True, target="both"):
    if not isinstance(frame,np.ndarray) or frame.dtype!=np.uint8 or frame.ndim!=3 or frame.shape[2]!=3:
        raise ValueError('invalid_frame')
    h,w=frame.shape[:2]
    if not (800<=w<=4096 and 600<=h<=2304):raise ValueError('unsupported_window_frame')
    geometries=plan_tiles(w,h,tuple(content),ui_scale,minimum_scale=.35,balanced=True)
    entries={};panels={};proposed_panels=set();roses=set();rejected_clocks={};blocked_entry=False;blocked_panel=False;work=[]
    # A fractional resize may round the same anchor differently in overlap
    # tiles. Bound this coordinate-only uncertainty; classifier thresholds stay.
    tolerance=0 if ui_scale==1 else 2
    def near(a,b):return all(abs(x-y)<=tolerance for x,y in zip(a,b))
    def key_for(box,keys):
        matches=[key for key in keys if near(box,key)]
        return matches[0] if len(matches)==1 else tuple(box)
    def classify(g):
        with matching_workers(2 if parallel and target!='both' and len(geometries)==1 else 1):
            return classify_current(g)
    def classify_current(g):
        pixels,valid=g.normalize(frame)
        if target=='entry':
            entry=discover(pixels,valid,g.normalized_size);panel=dict(location=dict(status='NOT_SCANNED',verified=False,candidates=[]),d2=dict(state='UNKNOWN'))
        elif target=='panel':
            entry=dict(status='NOT_SCANNED',candidates=[]);panel=detect(pixels,valid,g.normalized_size,ui_scale=g.ui_scale)
        elif parallel:
            # Two read-only classifiers, one frame at a time; no unbounded queue.
            with ThreadPoolExecutor(max_workers=2) as workers:
                future=workers.submit(discover,pixels,valid,g.normalized_size)
                panel=detect(pixels,valid,g.normalized_size,ui_scale=g.ui_scale);entry=future.result()
        else:
            entry=discover(pixels,valid,g.normalized_size);panel=detect(pixels,valid,g.normalized_size,ui_scale=g.ui_scale)
        context=inspect(pixels,entry['candidates'])
        if context['confirmed']:
            a,b,c,d=context['anchor_bbox']
            if not valid[b:d,a:c].all():context=dict(context,confirmed=False)
        return g,entry,panel,context
    if parallel and target!='both' and len(geometries)>1:
        with ThreadPoolExecutor(max_workers=2) as workers:records=list(workers.map(classify,geometries))
    else:records=map(classify,geometries)
    for g,entry,panel,context in records:
        blocked_entry |= entry['status'] in ('SEARCH_LIMIT','NOT_EVALUABLE')
        loc=panel['location'];blocked_panel |= loc['status'] in ('SEARCH_LIMIT','NOT_EVALUABLE')
        for rejected in loc.get('rejected_clocks',[]):
            box=g.frame_box(rejected['region']);key=key_for(box,rejected_clocks)
            if len(rejected_clocks)<8:rejected_clocks[key]=dict(rejected,region=box)
        for candidate in entry['candidates']:
            box=g.frame_box(candidate['bbox']);key=key_for(box,entries)
            old=entries.get(key,dict(candidate,bbox=box,context=False))
            old['context'] |= context['confirmed'] and entry['status']=='UNIQUE_CANDIDATE'
            entries[key]=old
        for candidate in loc.get('candidates',[]):
            box=g.frame_box(candidate['source_region']);proposed_panels.add(key_for(box,proposed_panels))
        for rose in loc.get('rose_proposals',[]):
            try:roses.add(tuple(g.frame_box(rose)))
            except ValueError:blocked_panel=True
        if loc.get('verified'):
            box=g.frame_box(loc['source_region']);dx,dy=loc['timer_offset']
            timer=g.frame_box([1912+dx,1470+dy,1980+dx,1495+dy])
            rose=tuple(g.frame_box([ROSES[0]+loc['panel_offset'][0],ROSES[1]+loc['panel_offset'][1],ROSES[2]+loc['panel_offset'][0],ROSES[3]+loc['panel_offset'][1]]))
            key=key_for(box,panels);old=panels.get(key)
            value=dict(source_region=box,timer_frame_bbox=timer,rose=rose,d2=panel['d2'])
            if old is not None and (not near(old['timer_frame_bbox'],timer) or old['d2']['state']!=value['d2']['state']):blocked_panel=True
            if old is None:panels[key]=value
        work.append(dict(content=list(g.content),entry=entry['status'],panel=loc['status']))
    # Exact-coordinate duplicate evidence only. Nearby but different boxes remain
    # ambiguous. Every rose proposal must be explained by a verified whole panel.
    explained={p['rose'] for p in panels.values()}
    unresolved=any(not any(near(rose,verified) for verified in explained) for rose in roses)
    panel_status='SEARCH_LIMIT' if blocked_panel else 'AMBIGUOUS' if len(proposed_panels)>1 else 'UNRESOLVED_PANEL_CANDIDATE' if unresolved else 'UNIQUE_PANEL' if len(panels)==1 else 'NO_VERIFIED_PANEL'
    panel_verified=panel_status=='UNIQUE_PANEL'
    d2=dict(state='UNKNOWN',reason='global_panel_unconfirmed')
    panel_out=dict(status=panel_status,verified=panel_verified,candidates=[dict(source_region=list(box)) for box in sorted(proposed_panels)],rejected_clocks=list(rejected_clocks.values()))
    if not panel_verified and len(rejected_clocks)==1:panel_out['diagnostic_region']=next(iter(rejected_clocks.values()))['region']
    if panel_verified:
        p=next(iter(panels.values()));d2=p['d2'];panel_out.update(source_region=p['source_region'],rose_frame_bbox=list(p['rose']),timer_frame_bbox=p['timer_frame_bbox'],timer_offset=[p['timer_frame_bbox'][0]-1912,p['timer_frame_bbox'][1]-1470])
    status='SEARCH_LIMIT' if blocked_entry else 'AMBIGUOUS' if len(entries)>1 else 'UNIQUE_CANDIDATE' if entries else 'NO_VERIFIED_CANDIDATE'
    return dict(location=dict(status=status,candidates=list(entries.values())),d2=d2,d2_location=panel_out,
        entry_context=dict(confirmed=status=='UNIQUE_CANDIDATE' and next(iter(entries.values()))['context']),
        frame_profile=dict(schema='window-ui-v2',width=w,height=h,content=list(content),ui_scale=ui_scale,tile_count=len(geometries),verified=True),
        tiles=work,scope='original UI physical pixels; geometry and global evidence only, not input permission')


def propose_scale(frame, content, parallel=True):
    """Bounded color-template proposal only; never a classifier/input decision."""
    x,y,r,b=content;crop=frame[y:b,x:r];factor=min(1,960/crop.shape[1])
    small=cv2.resize(crop,None,fx=factor,fy=factor,interpolation=cv2.INTER_AREA)
    from d1_profile import TEMPLATE
    assets=[('entry',Path(TEMPLATE)),('roses',Path(__file__).parent/'assets/panel_roses_v1.png')]
    def propose(item):
        name,path=item
        art=cv2.imread(str(path));best=(-1,None,None)
        for step in list(range(35,201,10))+[200]:
            scale=step/100;w=max(4,round(art.shape[1]*scale*factor));h=max(4,round(art.shape[0]*scale*factor))
            if w>small.shape[1] or h>small.shape[0]:continue
            template=cv2.resize(art,(w,h),interpolation=cv2.INTER_AREA)
            _,score,_,point=cv2.minMaxLoc(cv2.matchTemplate(small,template,cv2.TM_CCOEFF_NORMED))
            if score>best[0]:best=(score,scale,point)
        if best[1] is None or best[0]<.6:return None
        _,seed,(px,py)=best;px=round(px/factor);py=round(py/factor)
        left=max(0,px-48);top=max(0,py-48);right=min(crop.shape[1],px+math.ceil(art.shape[1]*(seed+.15))+48);bottom=min(crop.shape[0],py+math.ceil(art.shape[0]*(seed+.15))+48)
        region=crop[top:bottom,left:right];best_score=-1;best_scale=None
        for step in range(max(35,round(seed*100)-12),min(200,round(seed*100)+12)+1):
            scale=step/100;w=round(art.shape[1]*scale);h=round(art.shape[0]*scale)
            if w>region.shape[1] or h>region.shape[0]:continue
            template=cv2.resize(art,(w,h),interpolation=cv2.INTER_AREA if scale<1 else cv2.INTER_LINEAR)
            _,score,_,_=cv2.minMaxLoc(cv2.matchTemplate(region,template,cv2.TM_CCOEFF_NORMED))
            if score>best_score:best_score=score;best_scale=scale
        return dict(anchor=name,scale=best_scale,score=best_score) if best_score>=.9 else None
    if parallel:
        with ThreadPoolExecutor(max_workers=2) as workers:results=list(workers.map(propose,assets))
    else:results=list(map(propose,assets))
    return [result for result in results if result is not None]


def recover_panel_scale(frame,content,panel,scale,total_work,parallel=True):
    """Two neighboring proposal bins; every recheck is global, never a local shortcut."""
    location=panel['d2_location']
    if panel['d2']['state']!='UNKNOWN' or location['status']!='UNIQUE_PANEL' or not location['verified'] or 'rose_frame_bbox' not in location:
        return panel,scale,total_work,None
    neighbors=[round(scale+offset,2) for offset in (-.01,.01) if .35<=round(scale+offset,2)<=2]
    try:costs=[len(plan_tiles(frame.shape[1],frame.shape[0],tuple(content),s,minimum_scale=.35,balanced=True)) for s in neighbors]
    except ValueError as error:
        if str(error)!='tile_budget_exceeded':raise
        return panel,scale,total_work,dict(status='BUDGET_LIMIT',attempts=[])
    if total_work+sum(costs)>9:return panel,scale,total_work,dict(status='BUDGET_LIMIT',attempts=[])
    positive=[];attempts=[];blocked=False
    near=lambda a,b:len(a)==len(b) and all(abs(x-y)<=2 for x,y in zip(a,b))
    def classify(candidate):return locate_frame(frame,content,candidate,parallel=False,target='panel')
    if parallel and len(neighbors)==2 and costs==[1,1]:
        # Exactly one two-worker layer; no nested tile pools or early acceptance.
        with ThreadPoolExecutor(max_workers=2) as workers:trials=list(workers.map(classify,neighbors))
    else:trials=[locate_frame(frame,content,s,parallel=parallel,target='panel') for s in neighbors]
    for candidate,cost,trial in zip(neighbors,costs,trials):
        total_work+=cost
        loc=trial['d2_location'];attempts.append(dict(scale=candidate,panel=loc['status'],d2=trial['d2']['state']))
        if loc['status']=='NO_VERIFIED_PANEL':continue
        if loc['status']!='UNIQUE_PANEL' or not loc['verified'] or not near(location['rose_frame_bbox'],loc.get('rose_frame_bbox',[])):
            blocked=True;continue
        if trial['d2']['state'] not in ('UNKNOWN','VISIBLE'):
            blocked=True;continue
        if trial['d2']['state']=='VISIBLE':positive.append((candidate,trial))
    if blocked or not positive:return panel,scale,total_work,dict(status='UNCONFIRMED',attempts=attempts)
    first=positive[0][1]['d2_location']
    if any(not near(first['rose_frame_bbox'],p['d2_location']['rose_frame_bbox']) or not near(first['timer_frame_bbox'],p['d2_location']['timer_frame_bbox']) for _,p in positive[1:]):
        return panel,scale,total_work,dict(status='CONFLICT',attempts=attempts)
    chosen,result=positive[0]
    return result,chosen,total_work,dict(status='CONFIRMED',original_scale=scale,selected_scale=chosen,attempts=attempts)


def recover_panel_hint(frame,content,panel,scale,hint,total_work,parallel=True):
    """A missing proposal may use a geometry-bound hint as a fresh global trial.

    Never replace unresolved anchors/ambiguity or bypass the canonical search.
    The hint is not evidence: current complete classifiers must still pass.
    """
    if hint is None or hint==scale or panel['d2_location']['status']!='NO_VERIFIED_PANEL':return panel,scale,total_work,None
    try:cost=len(plan_tiles(frame.shape[1],frame.shape[0],tuple(content),hint,minimum_scale=.35,balanced=True))
    except ValueError as error:
        if str(error)!='tile_budget_exceeded':raise
        return panel,scale,total_work,dict(status='BUDGET_LIMIT',attempts=[],mode='HINT_HYPOTHESIS')
    if total_work+cost>9:return panel,scale,total_work,dict(status='BUDGET_LIMIT',attempts=[],mode='HINT_HYPOTHESIS')
    trial=locate_frame(frame,content,hint,parallel=parallel,target='panel');total_work+=cost;loc=trial['d2_location']
    detail=dict(status='CONFIRMED' if loc['verified'] and loc['status']=='UNIQUE_PANEL' and trial['d2']['state']=='VISIBLE' else 'UNCONFIRMED',original_scale=scale,selected_scale=hint,attempts=[dict(scale=hint,panel=loc['status'],d2=trial['d2']['state'])],mode='HINT_HYPOTHESIS')
    # Propagate ambiguity, failed anchors and definite negatives from this new
    # global trial; do not silently ignore them in favor of the old empty scan.
    return (panel,scale,total_work,detail) if loc['status']=='NO_VERIFIED_PANEL' else (trial,hint,total_work,detail)


def locate_auto(frame, content, hint=None, parallel=True):
    if hint is not None and (isinstance(hint,bool) or not isinstance(hint,(int,float)) or not math.isfinite(hint) or not .35<=hint<=2):raise ValueError('invalid_scale_hint')
    proposals=propose_scale(frame,content,parallel=parallel)
    scales={p['anchor']:p['scale'] for p in proposals}
    # A panel hint must not prescribe the start-bar size. Missing targets use
    # their own canonical fallback; current multi-scale proposals remain global.
    entry_scale=scales.get('entry',1);panel_scale=scales.get('roses',1)
    tile_counts={scale:len(plan_tiles(frame.shape[1],frame.shape[0],tuple(content),scale,minimum_scale=.35,balanced=True)) for scale in set([entry_scale,panel_scale])}
    total_work=sum(tile_counts.values())
    if total_work>9:raise ValueError('combined_target_budget_exceeded')
    if entry_scale==panel_scale:
        common=locate_frame(frame,content,entry_scale,parallel=parallel);evaluated={entry_scale:common}
    else:
        targets=[(entry_scale,'entry'),(panel_scale,'panel')]
        def classify_target(item):
            scale,target=item
            # One shared two-worker layer. Do not nest tile/classifier pools.
            return scale,locate_frame(frame,content,scale,parallel=False,target=target)
        if parallel and tile_counts[entry_scale]>1 and tile_counts[panel_scale]==1:
            with ThreadPoolExecutor(max_workers=2) as workers:evaluated=dict(workers.map(classify_target,targets))
        else:
            # Small UI may require several panel tiles; preserve tile parallelism
            # there. Running both single-tile targets together also raises peak
            # memory without enough latency benefit on ordinary windows.
            evaluated={scale:locate_frame(frame,content,scale,parallel=parallel,target=target) for scale,target in targets}
    entry=evaluated[entry_scale];panel=evaluated[panel_scale]
    hint_recovery=None
    if 'roses' not in scales:panel,panel_scale,total_work,hint_recovery=recover_panel_hint(frame,content,panel,panel_scale,hint,total_work,parallel)
    panel,panel_scale,total_work,recovery=recover_panel_scale(frame,content,panel,panel_scale,total_work,parallel)
    result=dict(entry)
    result['d2']=panel['d2'];result['d2_location']=dict(panel['d2_location'])
    result['frame_profile']=dict(entry['frame_profile'],schema='window-ui-v3',entry_scale=entry_scale,panel_scale=panel_scale,
        ui_scale=entry_scale if entry['location']['candidates'] else panel_scale,tile_count=total_work)
    result['scale_proposals']=proposals
    if recovery is not None:result['panel_scale_recovery']=recovery
    if hint_recovery is not None:
        result['panel_hint_recovery']=hint_recovery
        if recovery is None:result['panel_scale_recovery']=hint_recovery
    result['target_evidence']=dict(entry=entry['location']['status'],panel=panel['d2_location']['status'])
    if result['d2_location']['verified']:
        search,box=locate_timer_text(frame,result['d2_location']['source_region'],result['d2_location']['timer_frame_bbox'],tuple(content))
        result['d2_location']['text_search_region']=search
        result['d2_location']['text_status']='UNIQUE_TEXT_LINE' if box else 'TEXT_UNRESOLVED'
        if box:result['d2_location']['timer_frame_bbox']=box
    return result


def locate_timer_text(frame, panel, seed, content):
    # Search is semantic (panel header beside the clock), not a fixed text size.
    x,y,r,b=panel;a,c,d,e=seed;pw=r-x;ph=b-y
    left=max(content[0],x,int(a-pw*.03));top=max(content[1],y)
    right=min(content[2],int(x+pw*.9));bottom=min(content[3],int(y+ph*.15))
    search=[left,top,right,bottom]
    if right<=left or bottom<=top:return search,None
    pixels=frame[top:bottom,left:right]
    # Bright text cores can remain separate while translucent background texture
    # joins the glyphs at the broad threshold. Require the same line geometry
    # at two stricter levels; a single brighter fragment is never a recovery.
    strong=[_timer_lines(pixels,level,pw,ph,left,top,right,bottom) for level in (155,200)]
    supported=[]
    for first in strong[0]:
        for second in strong[1]:
            intersection=max(0,min(first[2],second[2])-max(first[0],second[0]))*max(0,min(first[3],second[3])-max(first[1],second[1]))
            union=(first[2]-first[0])*(first[3]-first[1])+(second[2]-second[0])*(second[3]-second[1])-intersection
            if intersection/max(1,union)>=.8:
                box=tuple(min(first[i],second[i]) if i<2 else max(first[i],second[i]) for i in range(4))
                if box not in supported:supported.append(box)
    broad=_timer_lines(pixels,100,pw,ph,left,top,right,bottom)
    # Preserve the old multiple-line veto, including faint competing rows.
    if len(broad)>1 or len(supported)>1:return search,None
    if supported:return search,list(supported[0])
    return search,list(broad[0]) if len(broad)==1 else None


def _timer_lines(pixels,level,pw,ph,left,top,right,bottom):
    mask=(pixels.min(axis=2)>=level).astype(np.uint8)
    _,_,stats,_=cv2.connectedComponentsWithStats(mask,8)
    glyphs=[]
    for gx,gy,gw,gh,area in stats[1:]:
        if 2<=gw<=pw*.16 and 5<=gh<=ph*.24 and area>=5:glyphs.append((int(gx),int(gy),int(gw),int(gh)))
    lines=[]
    for base in glyphs:
        baseline=base[1]+base[3]
        group=sorted([g for g in glyphs if abs(g[1]+g[3]-baseline)<=2 and .6<=g[3]/base[3]<=1.6])
        groups=[[]]
        for glyph in group:
            if groups[-1] and glyph[0]-groups[-1][-1][0]-groups[-1][-1][2]>base[3]*2.5:groups.append([])
            groups[-1].append(glyph)
        for group in groups:
            if not 1<=len(group)<=5:continue
            if len(group)==1 and group[0][2]<group[0][3]*1.8:continue
            if any(group[i][0]+group[i][2]>group[i+1][0] for i in range(len(group)-1)):continue
            box=(max(left,left+min(g[0] for g in group)-2),max(top,top+min(g[1] for g in group)-2),min(right,left+max(g[0]+g[2] for g in group)+2),min(bottom,top+max(g[1]+g[3] for g in group)+2))
            if box not in lines:lines.append(box)
    return lines
