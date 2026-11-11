namespace JX3Artist;

internal record Pointer(int X,int Y);
internal interface IDesktop
{
    nint Foreground{get;} Pointer Cursor{get;} uint InputTick{get;} bool Idle{get;}
    uint Pid(nint window);bool Alive(nint window,uint pid);bool Activate(nint window);bool Move(Pointer point);
}
internal sealed class Interaction(Pointer pointer,uint tick)
{
    internal Pointer ExpectedPointer=pointer;internal uint ExpectedTick=tick;
    internal bool SafeToRestore=true;
    internal bool InputAttempted;
    internal bool AllowPointerJitter;
    internal bool AuthorizedMotion;
    internal void OwnInput(Pointer point,uint inputTick){ExpectedPointer=point;ExpectedTick=inputTick;}
}
internal sealed record ActionEvidence(bool Started,double LastSource,string? Error=null,bool Retryable=false);
internal sealed record RestoreEvidence(long OriginalWindow,long GameWindow,bool ReleaseConfirmed,uint ExpectedTick,Pointer ExpectedPointer,
    long? ObservedForeground,uint? ObservedTick,Pointer? ObservedPointer,bool? OriginalAlive,string[] Reasons,string? ReadError=null);
internal sealed record FocusResult(bool Deferred,bool Started,bool Restored,double LastSource,string? Error,bool ControlTransferred=false,RestoreEvidence? Restoration=null);
internal static class FocusTransaction
{
    internal const string ActivationFailure="Windows未允许切换到游戏，已暂停";
    internal const string ForegroundRequired="等待手动切回游戏";
    internal static bool Near(Pointer a,Pointer b)=>Math.Abs(a.X-b.X)<=2 && Math.Abs(a.Y-b.Y)<=2;
    internal static bool CanDispatch(IDesktop desktop,Interaction lease,nint game,uint gamePid,bool cancelled)=>!cancelled && lease.SafeToRestore && desktop.Alive(game,gamePid) && desktop.Foreground==game && (lease.AuthorizedMotion?desktop.Idle:lease.AllowPointerJitter?desktop.Idle && Math.Abs((long)desktop.Cursor.X-lease.ExpectedPointer.X)<=24 && Math.Abs((long)desktop.Cursor.Y-lease.ExpectedPointer.Y)<=24:desktop.InputTick==lease.ExpectedTick && Near(desktop.Cursor,lease.ExpectedPointer));
    internal static FocusResult Run(IDesktop desktop,nint game,uint gamePid,Func<bool> cancelled,Func<Interaction,ActionEvidence> action,bool allowPointerJitter=false,bool authorizedMotion=false,bool requireGameForeground=false)
    {
        if(cancelled())return new(false,false,true,0,"已取消");
        if(!desktop.Idle)return new(true,false,true,0,null);
        nint original=desktop.Foreground;uint originalPid=desktop.Pid(original);var cursor=desktop.Cursor;uint input=desktop.InputTick;
        if(requireGameForeground&&original!=game)return new(true,false,true,0,ForegroundRequired);
        if(original==0 || originalPid==0 || !desktop.Alive(game,gamePid))return new(false,false,true,0,"窗口身份未确认");
        if(cancelled() || desktop.Foreground!=original || (authorizedMotion?!desktop.Idle:(!allowPointerJitter && desktop.InputTick!=input) || (allowPointerJitter && (!desktop.Idle || Math.Abs((long)desktop.Cursor.X-cursor.X)>24 || Math.Abs((long)desktop.Cursor.Y-cursor.Y)>24))))return new(true,false,true,0,null);
        var lease=new Interaction(cursor,input){AllowPointerJitter=allowPointerJitter,AuthorizedMotion=authorizedMotion};bool started=false,restored=true,controlTransferred=false,retryable=false,activationFailed=false;double source=0;string? error=null;
        long? observedForeground=null;uint? observedTick=null;Pointer? observedPointer=null;bool? originalAlive=null;var restoreReasons=new List<string>();string? readError=null;
        try
        {
            if(requireGameForeground&&desktop.Foreground!=game)throw new OperationCanceledException(ForegroundRequired);
            if(!requireGameForeground&&(!desktop.Activate(game)||desktop.Foreground!=game))
            {
                activationFailed=true;
                throw new InvalidOperationException(ActivationFailure);
            }
            if(cancelled())throw new OperationCanceledException();
            if(!CanDispatch(desktop,lease,game,gamePid,cancelled()))throw new OperationCanceledException("接管期间前台或按键状态变化，未点击");
            var result=action(lease);started=result.Started;source=result.LastSource;error=result.Error;retryable=result.Retryable;
        }
        catch(Exception ex){error=ex.Message;retryable=!cancelled() && !lease.InputAttempted && (ex is OperationCanceledException || ex is InvalidOperationException && ex.Message is "STALE_FRAME" or "GAME_NOT_FOREGROUND" or "CANCELLED" or "MOUSE_BUTTON_HELD");}
        finally
        {
            try
            {
                if(lease.SafeToRestore)
                {
                    // Read-only evidence; mismatches never grant restoration permission.
                    observedForeground=desktop.Foreground.ToInt64();bool owned=observedForeground==game.ToInt64();
                    if(owned){observedTick=desktop.InputTick;owned=observedTick==lease.ExpectedTick;}
                    if(owned){observedPointer=desktop.Cursor;owned=Near(observedPointer,lease.ExpectedPointer);}
                    // Diagnostic failures after refusal cannot alter control ownership.
                    if(!owned)
                    {
                        try{observedTick??=desktop.InputTick;}catch(Exception ex){readError="InputTick: "+ex.Message;}
                        try{observedPointer??=desktop.Cursor;}catch(Exception ex){readError=(readError==null?"":readError+"; ")+"Cursor: "+ex.Message;}
                    }
                    if(activationFailed&&observedForeground==original.ToInt64())restoreReasons.Add("GAME_ACTIVATION_REFUSED");
                    else if(observedForeground!=game.ToInt64())restoreReasons.Add("FOREGROUND_CHANGED");
                    if(observedTick.HasValue && observedTick!=lease.ExpectedTick)restoreReasons.Add("INPUT_TICK_CHANGED");
                    if(observedPointer!=null && !Near(observedPointer,lease.ExpectedPointer))restoreReasons.Add("CURSOR_MOVED");
                    if(owned)
                    {
                        originalAlive=desktop.Alive(original,originalPid);
                        if(originalAlive!=true){restored=false;restoreReasons.Add("ORIGINAL_WINDOW_GONE");}
                        else if(original!=game && (!desktop.Activate(original) || desktop.Foreground!=original)){restored=false;restoreReasons.Add("ORIGINAL_ACTIVATION_FAILED");}
                        if(restored && !Near(desktop.Cursor,cursor)){restored=desktop.Move(cursor);if(!restored)restoreReasons.Add("POINTER_RESTORE_FAILED");}
                    }
                    else{restored=false;controlTransferred=true;}
                }
                else{restored=false;restoreReasons.Add("RELEASE_NOT_CONFIRMED");}
            }
            catch(Exception ex){restored=false;restoreReasons.Add("RESTORE_CHECK_FAILED");readError=ex.Message;}
        }
        if(activationFailed && readError==null && observedForeground==original.ToInt64() &&
            observedTick==input && observedPointer==cursor)
        {
            controlTransferred=false;
            retryable=!cancelled();
        }
        return new(retryable&&!started&&!lease.InputAttempted,started,restored,source,error??(!restored?"焦点或键鼠状态发生变化，未强制恢复原窗口":null),controlTransferred,
            new(original.ToInt64(),game.ToInt64(),lease.SafeToRestore,lease.ExpectedTick,lease.ExpectedPointer,observedForeground,observedTick,observedPointer,originalAlive,restoreReasons.ToArray(),readError));
    }
}
