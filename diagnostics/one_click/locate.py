"""One-frame diagnostic; calls the production locator and frozen D2, no input APIs."""
import argparse,json,sys
from pathlib import Path
import cv2
ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'int1/calibration'))
from location_guard import discover
from d1_profile import dl
from entry_context import inspect as inspect_entry_context
from panel_locator import detect as detect_panel

def request_compute_qos():
    import os,ctypes
    result=dict(pid=os.getpid(),applied=False,scope='own_python_process_only')
    if sys.platform!='win32':return dict(result,reason='not_windows')
    class State(ctypes.Structure):_fields_=[('Version',ctypes.c_uint32),('ControlMask',ctypes.c_uint32),('StateMask',ctypes.c_uint32)]
    try:
        kernel=ctypes.WinDLL('kernel32',use_last_error=True)
        kernel.GetCurrentProcess.restype=ctypes.c_void_p
        kernel.SetProcessInformation.argtypes=[ctypes.c_void_p,ctypes.c_int,ctypes.POINTER(State),ctypes.c_uint32]
        state=State(1,1,0)
        applied=bool(kernel.SetProcessInformation(kernel.GetCurrentProcess(),4,ctypes.byref(state),ctypes.sizeof(state)))
        return dict(result,applied=applied,reason='HighQoS_request_accepted' if applied else 'win32_error_'+str(ctypes.get_last_error()),abiBytes=ctypes.sizeof(state))
    except Exception as error:return dict(result,reason=type(error).__name__)

def verify_entry_patch(image,expected,scale):
    from frame_geometry import FrameGeometry
    if image is None or image.shape[1]>640 or image.shape[0]>320:raise ValueError('local_entry_budget')
    if not isinstance(expected,list) or len(expected)!=4 or any(type(v) is not int for v in expected):raise ValueError('local_entry_expected_box')
    h,w=image.shape[:2];g=FrameGeometry(w,h,(0,0,w,h),scale,minimum_scale=.35)
    pixels,valid=g.normalize(image);entry=discover(pixels,valid,g.normalized_size);context=inspect_entry_context(pixels,entry['candidates'])
    if context['confirmed']:
        a,b,c,d=context['anchor_bbox']
        if not valid[b:d,a:c].all():context=dict(context,confirmed=False)
    # PO policy: gold ornament may be covered; unchanged frozen D1 must pass.
    confirmed=entry['status']=='UNIQUE_CANDIDATE' and len(entry['candidates'])==1
    box=g.frame_box(entry['candidates'][0]['bbox']) if confirmed else None
    if confirmed:confirmed=all(abs(a-b)<=2 for a,b in zip(box,expected))
    return dict(confirmed=bool(confirmed),reason='current_frozen_entry_icon' if confirmed else 'local_entry_unconfirmed',bbox=box,location=entry['status'],context=context['confirmed'],scope='local positive recheck only; recent global uniqueness and source time still required')

def serve(stream,output):
    cv2.setNumThreads(1)
    scheduling=request_compute_qos()
    from portable_locator import locate_auto
    for _ in range(256):
        line=stream.readline(65537)
        if not line:return
        if len(line)>65536:raise ValueError('request_budget_exceeded')
        request=json.loads(line)
        image=cv2.imread(request['frame'])
        if image is None:raise ValueError('image_not_readable')
        result=verify_entry_patch(image,request['content'],request['hint']) if request.get('entryPatch') else locate_auto(image,request['content'],request.get('hint'))
        output.write(json.dumps(dict(id=request['id'],result=result,workerScheduling=scheduling),ensure_ascii=False,allow_nan=False)+'\n');output.flush()

def locate(path):
    frame=cv2.imread(str(path))
    result=discover(frame)
    panel=detect_panel(frame);d2=panel['d2']
    context=inspect_entry_context(frame,result['candidates'])
    return {'location':result,'d2':d2,'d2_location':panel['location'],'entry_context':context,'scope':'current frame candidates and independently localized panel; visibility alone does not authorize input'}

if __name__=='__main__':
    if sys.argv[1:]==['--server']:
        serve(sys.stdin,sys.stdout);sys.exit(0)
    ap=argparse.ArgumentParser();ap.add_argument('--frame',required=True);ap.add_argument('--portable',action='store_true');ap.add_argument('--scale-hint',type=float);ap.add_argument('--content',type=int,nargs=4);args=ap.parse_args()
    if args.portable:
        cv2.setNumThreads(1)  # Bounded two-classifier parallelism in this child only.
        from portable_locator import locate_auto
        image=cv2.imread(args.frame)
        if image is None:raise ValueError('image_not_readable')
        result=locate_auto(image,args.content or [0,0,image.shape[1],image.shape[0]],args.scale_hint)
    else:result=locate(args.frame)
    print(json.dumps(result,ensure_ascii=False,allow_nan=False))
