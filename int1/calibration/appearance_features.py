"""Current-frame SIFT geometry evidence, using ONLY the existing icon template.
This is an independent supplement; it does not edit VIS-2 thresholds or learn from test frames.
"""
import time
import cv2,numpy as np

# Frozen before observing the failing-frame measurements; initial_criteria.json records these.
RATIO_MAX=.70
DESCRIPTOR_L2_MAX=200
MIN_INLIERS=8
MAX_RESIDUAL=2.5
MIN_QUADRANTS=3
MIN_HULL_FRACTION=.20
SCALE_MIN,SCALE_MAX=.85,1.15
MAX_ANGLE=15

class CurrentFrameFeatures:
    def __init__(self,template):
        if not hasattr(cv2,'SIFT_create'):raise ValueError('opencv_sift_unavailable')
        self.height,self.width=template.shape[:2]
        self.sift=cv2.SIFT_create(nfeatures=200)
        self.keys,self.descriptors=self.sift.detectAndCompute(cv2.cvtColor(template,cv2.COLOR_BGR2GRAY),None)
    def inspect(self,window,valid_mask=None):
        started=time.perf_counter()
        result={'version':'current-frame-sift-v1','confirmed':False,'inliers':0,'quadrants':0,'reference_hull_fraction':0.0}
        def finish(reason):
            result['reason']=reason;result['elapsed_ms']=round((time.perf_counter()-started)*1000,3);return result
        expected=(self.height+60,self.width+60,3)
        if not isinstance(window,np.ndarray) or window.dtype!=np.uint8 or window.shape!=expected:
            return finish('unsupported_window')
        if valid_mask is not None and (not isinstance(valid_mask,np.ndarray) or valid_mask.shape!=window.shape[:2]
                or not (valid_mask.dtype==np.bool_ or valid_mask.dtype==np.uint8) or not np.all(valid_mask==1)):
            return finish('incomplete_or_invalid_coverage')
        keys,descriptors=self.sift.detectAndCompute(cv2.cvtColor(window,cv2.COLOR_BGR2GRAY),None)
        if descriptors is None or len(descriptors)<2 or self.descriptors is None:
            return finish('insufficient_descriptors')
        pairs=cv2.BFMatcher(cv2.NORM_L2).knnMatch(self.descriptors,descriptors,k=2)
        good=[];seen_reference=set();seen_scene=set()
        for pair in sorted(pairs,key=lambda p:p[0].distance if p else float('inf')):
            if len(pair)!=2:continue
            a,b=pair;reference=self.keys[a.queryIdx];scene=keys[a.trainIdx]
            angle=abs((scene.angle-reference.angle+180)%360-180);scale=scene.size/reference.size
            reference_cell=tuple(round(v/2) for v in reference.pt);scene_cell=tuple(round(v/2) for v in scene.pt)
            if (a.distance<RATIO_MAX*b.distance and a.distance<=DESCRIPTOR_L2_MAX
                    and SCALE_MIN<=scale<=SCALE_MAX and angle<=MAX_ANGLE
                    and reference_cell not in seen_reference and scene_cell not in seen_scene):
                good.append((reference.pt,scene.pt));seen_reference.add(reference_cell);seen_scene.add(scene_cell)
        result['ratio_geometry_matches']=len(good)
        if len(good)<MIN_INLIERS:return finish('insufficient_unique_matches')
        shifts=np.array([np.subtract(scene,reference) for reference,scene in good]);median=np.median(shifts,axis=0)
        inliers=[g for g,shift in zip(good,shifts) if np.linalg.norm(shift-median)<=MAX_RESIDUAL]
        points=np.float32([reference for reference,scene in inliers])
        quadrants={int(reference[0]>=self.width/2)+2*int(reference[1]>=self.height/2) for reference,scene in inliers}
        area=cv2.contourArea(cv2.convexHull(points)) if len(points)>=3 else 0
        fraction=area/(self.width*self.height)
        result.update(inliers=len(inliers),quadrants=len(quadrants),reference_hull_fraction=round(fraction,6),
                      translation_px=[float(v) for v in median])
        if len(inliers)<MIN_INLIERS:return finish('insufficient_translation_consensus')
        if len(quadrants)<MIN_QUADRANTS or fraction<MIN_HULL_FRACTION:return finish('evidence_not_spatially_distributed')
        if not (0<=median[0]<=60 and 0<=median[1]<=60):return finish('translation_outside_search_window')
        result['confirmed']=True
        return finish('current_frame_distributed_feature_consensus')
