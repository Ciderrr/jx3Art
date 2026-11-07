using System.IO;
using Int1;
using Jx3.OneClickTest;
using InputNative=Jx3.OneClickTest.Native;
namespace JX3Artist;

internal sealed record ViewState(Phase Phase,string Message,int Starts=0,int Returns=0,string? Preview=null,DateTime? FrameTime=null,double? FrameAgeMs=null,string? Detail=null,double? CountdownEnd=null,string? CountdownStatus=null,double? NextCheck=null,bool AwaitingApproval=false,double? DisplayDeadline=null,double? DisplayUncertaintyMs=null);
internal sealed class AutomationService(string dataRoot,string engineRoot,bool demo=false,ExperienceSession? experience=null)
{
    internal SupportHistory History{get;}=new(dataRoot);
    internal nint AssistantWindow{get;set;}
    internal Func<CancellationToken,bool>? RestorePreparationForeground{get;set;}
    internal LiveTakeoverMode Mode{get;}=new();
    internal void SetUnattended(bool value){History.Record("mode_changed",new{unattended=value});Mode.Set(value);Notices.Observe(Latest,value);}
    internal LifecycleNotices Notices{get;}=new();
    Thread? worker;CancellationTokenSource? cancellation;ViewState latest=new(Phase.Stopped,"等待开始");int buttonPending,approvalRequested,approval;
    internal ViewState Latest=>Volatile.Read(ref latest);
    internal bool IsRunning=>worker?.IsAlive==true;
    internal bool IsStopping=>cancellation?.IsCancellationRequested==true;
    internal bool ButtonPending=>Volatile.Read(ref buttonPending)!=0;
    internal void Publish(ViewState value){if(value.Phase==Phase.Paused&&Latest.Phase!=Phase.Paused)History.Record("problem_detected",new{state=value.Phase.ToString(),value.Starts,value.Returns},ProblemCatalog.Identify(value.Message).Code);Notices.Observe(value,Mode.Enabled);Volatile.Write(ref latest,value);}
    internal void ApproveTakeover(){bool accepted=Volatile.Read(ref approvalRequested)==1;History.Record("approval_requested",new{accepted});if(accepted){Interlocked.Exchange(ref approval,1);Mode.Changed.Set();}}
    internal bool Start(int maxStarts,bool unattended=false,string origin="button")
    {
        if(IsRunning){History.Record("start_requested",new{maxStarts,unattended,origin,accepted=false});return false;}
        experience?.Suspend();
        Mode.Set(unattended);
        if(ButtonPending){if(InputNative.ButtonHeld){History.Record("start_requested",new{maxStarts,unattended,origin,accepted=false});Publish(new(Phase.Paused,"请先人工松开鼠标，再重新开始"));return false;}Interlocked.Exchange(ref buttonPending,0);}
        History.Record("start_requested",new{maxStarts,unattended,origin,accepted=true});
        Interlocked.Exchange(ref approvalRequested,0);Interlocked.Exchange(ref approval,0);
        cancellation?.Dispose();cancellation=new();var token=cancellation.Token;
        Notices.Begin();Publish(new(Phase.Watching,demo?"演示模式：不读取或操作游戏":"正在连接客户端…"));
        worker=new Thread(()=>Work(maxStarts,unattended,token)){IsBackground=true,Name="JX3Artist-session"};worker.SetApartmentState(ApartmentState.STA);worker.Start();return true;
    }
    internal void Stop(string origin="button")
    {
        experience?.Suspend();
        History.Record("stop_requested",new{origin});
        cancellation?.Cancel();
        Publish(IsRunning?Latest with{Message="正在停止后续操作，等待释放输入/资源…"}:Latest with{Phase=Phase.Stopped,Message="已停止后续操作，不会打断游戏中的表演"});
    }
    void Work(int maxStarts,bool unattended,CancellationToken token)
    {
        var core=new AutomationController(maxStarts,FrameTiming.NowMs());string? image=null;DateTime? frameTime=null;double? age=null,nextCheck=null;
        using var journal=new Journal(Path.Combine(dataRoot,"logs"),error=>Publish(Latest with{Phase=Phase.Paused,Message="后续操作已停止，但日志关闭失败",Detail=error}),(kind,data)=>History.Record(kind,data));
        var trace=new SessionTrace(journal);
        void Push(string? detail=null)=>Publish(new(core.State,core.Message,core.Starts,core.Returns,image,frameTime,age,detail,core.Countdown.EndSourceMs,core.Countdown.Status,nextCheck,Volatile.Read(ref approvalRequested)==1,core.Countdown.DisplayDeadline,core.Countdown.DisplayUncertaintyMs));
        try
        {
            if(demo)
            {
                for(int n=0;n<120;n++){token.ThrowIfCancellationRequested();Publish(new(n<2?Phase.Watching:Phase.Performing,n<2?"演示：正在确认画面":"演示：检测到表演面板（零游戏操作）",n<2?0:1));if(token.WaitHandle.WaitOne(500))throw new OperationCanceledException();}
                Publish(new(Phase.Completed,"演示已结束，没有读取或操作游戏",1));return;
            }
            var self=InputPreflight.Read((uint)Environment.ProcessId);if(self.Elevated!=true)throw new InvalidOperationException("需要管理员权限。请正常双击 JX3Artist.exe 启动并由你处理系统授权。");
            using var session=new GameSession(engineRoot,Path.Combine(dataRoot,"cache"));var desktop=new Desktop(session.Pid);
            trace.Begin(new{version=typeof(AutomationService).Assembly.GetName().Version?.ToString(),maxStarts,unattended,mode="temporary foreground takeover; NOT true background input",pid=session.Pid,window=session.Window.ToInt64()});
            var takeover=new TakeoverPolicy(()=>Mode.Enabled);var retryBudget=new TakeoverRetryBudget();bool automaticGrant=false;Func<bool>? focusRetryPermit=null;var preparationForeground=new PreparationForeground();bool entryDiagnosticWritten=false;int localFailures=0;int failures=0,reanchors=0,previousDelay=0;Phase previous=core.State;string previousTimerStatus="",previousTimerReason="";double lastTimerDiagnostic=double.NegativeInfinity;
            ForegroundPreparationResult RestoreAssistant(string reason,bool authorized)
            {
                if(RestorePreparationForeground==null||AssistantWindow==0)return new(false,false,"assistant_window_unavailable",0);
                if(authorized)desktop.SkipIdleWait=true;
                var result=preparationForeground.Request(FrameTiming.NowMs(),authorized,desktop.Idle,desktop.Foreground==AssistantWindow,()=>RestorePreparationForeground(token),token);
                if(result.Requested)journal.Write("preparation_foreground_request",new{reason,result,assistant=AssistantWindow.ToInt64(),observedForeground=desktop.Foreground.ToInt64()});
                return result;
            }
            bool FinishExpiredFocusRetry()
            {
                if(focusRetryPermit==null)return false;
                if(!focusRetryPermit())
                {
                    focusRetryPermit=null;
                    retryBudget.Clear();
                    automaticGrant=false;
                    core.ObservationUnavailable("自动接管许可已撤销，正在重新确认画面");
                    return false;
                }
                if(retryBudget.CanResume(FrameTiming.NowMs()))return false;
                core.Pause(FocusTransaction.ActivationFailure);
                nextCheck=null;
                Push();
                return true;
            }
            RestoreAssistant("explicit_start",true);
            while(!token.IsCancellationRequested && !core.Done)
            {
                if(FinishExpiredFocusRetry())break;
                if(automaticGrant&&!Mode.Enabled){retryBudget.Clear();automaticGrant=false;}
                double cycleStarted=FrameTiming.NowMs();Picture picture;
                try{picture=session.Capture(token);failures=0;}
                catch(OperationCanceledException){throw;}
                catch(Exception ex)
                {
                    Interlocked.Exchange(ref approvalRequested,0);Interlocked.Exchange(ref approval,0);
                    failures++;journal.Write("capture_failed",new{error=ex.Message,failures,phase="watching",exceptionType=ex.GetType().Name,
                        reasons=(ex as CaptureFailureException)?.Reasons,evidence=(ex as CaptureFailureException)?.Evidence});
                    if(!session.Identity || failures>=3){core.Pause(ex.Message);nextCheck=null;Push();break;}
                    experience?.Suspend();core.ObservationUnavailable("取帧暂时失败");nextCheck=FrameTiming.NowMs()+2000;Push(ex.Message);
                    if(token.WaitHandle.WaitOne(2000))throw new OperationCanceledException();continue;
                }
                var context=new{width=picture.Map.Width,height=picture.Map.Height,picture.Map.Snapshot.Dpi,win32Zoomed=picture.Map.Snapshot.Maximized,visualScale=picture.UiScale};
                History.Record("observation_context",context);
                if(picture.Metrics?.RecoveryStatus!=null)journal.Write("panel_scale_recovery",new{picture.Observation.Source,sourceAgeMs=picture.Observation.Now-picture.Observation.Source,picture.Metrics});
                if(FinishExpiredFocusRetry())break;
                if(automaticGrant&&!Mode.Enabled){retryBudget.Clear();automaticGrant=false;}
                bool authorizedPreparation=retryBudget.CanResume(FrameTiming.NowMs())||takeover.CanProceed(Volatile.Read(ref approval)==1);
                if(authorizedPreparation && PreparationForeground.SlowEntry(picture.Observation))
                {
                    var restored=RestoreAssistant("slow_background_entry",true);
                    if(restored.Activated&&restored.Requested){core.ObservationUnavailable("助手已自动恢复前台，正在重新确认本次已授权接管");nextCheck=FrameTiming.NowMs()+50;Push();if(token.WaitHandle.WaitOne(50))throw new OperationCanceledException();continue;}
                }
                if(automaticGrant&&!Mode.Enabled){retryBudget.Clear();automaticGrant=false;}
                if(Interlocked.Exchange(ref approval,0)==1){retryBudget.Grant(FrameTiming.NowMs());automaticGrant=false;Interlocked.Exchange(ref approvalRequested,0);}
                image=picture.Path;frameTime=DateTime.Now;age=picture.Observation.Now-picture.Observation.Source;core.Observe(picture.Observation);
                experience?.Observe(picture.Observation,core.State,core.Returns,core.Countdown.Reanchors,core.NextObservationDelayMs(picture.Observation.Now));
                if(core.State==Phase.Performing){takeover.ExistingPerformanceObserved();focusRetryPermit=null;retryBudget.Clear();localFailures=0;preparationForeground.Clear();}nextCheck=null;Push();
                if(session.EntryEvidencePath!=null || session.EntryEvidenceError!=null){if(!entryDiagnosticWritten){journal.Write("entry_rejection_evidence",new{session.EntryEvidencePath,session.EntryEvidenceError});entryDiagnosticWritten=true;}}
                if(previous!=core.State){journal.Write("state",new{state=core.State.ToString(),core.Message,core.Starts,core.Returns,picture.Observation.Location,picture.Observation.Count,picture.Observation.D2,picture.Observation.EntryContextConfirmed,picture.Observation.CountdownSeconds,picture.Observation.CountdownQuality,sourceAgeMs=age});previous=core.State;}
                if(reanchors!=core.Countdown.Reanchors){reanchors=core.Countdown.Reanchors;journal.Write("countdown_reanchored",new{reanchors,core.Countdown.Status,core.Countdown.EndSourceMs});}
                if(previousTimerStatus!=core.Countdown.Status)
                {previousTimerStatus=core.Countdown.Status;journal.Write("countdown_status",new{core.Countdown.Status,picture.Observation.CountdownSeconds,picture.Observation.CountdownQuality,picture.Observation.CountdownReason,picture.Observation.Source,core.Countdown.EndSourceMs});}
                if((picture.Observation.PanelLocated || picture.Observation.PanelLocation=="UNRESOLVED_PANEL_CANDIDATE" || session.TimerEvidencePath!=null || session.TimerEvidenceError!=null) && (picture.Observation.CountdownReason!=previousTimerReason || FrameTiming.NowMs()-lastTimerDiagnostic>=60000 || session.TimerEvidencePath!=null || session.TimerEvidenceError!=null))
                {previousTimerReason=picture.Observation.CountdownReason;lastTimerDiagnostic=FrameTiming.NowMs();journal.Write("timer_diagnostic",new{picture.Observation.CountdownSeconds,picture.Observation.CountdownQuality,picture.Observation.CountdownReason,picture.Observation.Source,sourceAgeMs=age,picture.Metrics,session.TimerEvidencePath,session.TimerEvidenceError});}
                if(core.WantsStart)
                {
                    if(!retryBudget.CanResume(FrameTiming.NowMs()) && !takeover.CanProceed(Volatile.Read(ref approval)==1))
                    {
                        Interlocked.Exchange(ref approvalRequested,1);
                        core.Deferred("需要续演，请点击“允许本次接管”；确认后会切换游戏并移动鼠标");Push();
                    }
                    else
                    {
                        bool automaticTransaction=retryBudget.CanResume(FrameTiming.NowMs())?automaticGrant:!takeover.HasInitialPermission;
                        var permitted=focusRetryPermit??Mode.Permit(!automaticTransaction);
                        if(!permitted()){focusRetryPermit=null;retryBudget.Clear();automaticGrant=false;core.ObservationUnavailable("已取消自动接管，等待确认");Push();continue;}
                        desktop.SkipIdleWait=true;
                        if(desktop.Foreground!=session.Window)RestoreAssistant("authorized_takeover_preparation",true);
                        bool compatibility=PreparedEntryPolicy.Compatibility(localFailures,picture.EntryProof!=null);
                        if(!desktop.Idle){core.Deferred("请松开鼠标和修饰键，随后继续接管");Push();}
                        else if(!PreparedEntryPolicy.ReadyForRoute(picture.Observation,picture.Icon!=null,compatibility,FrameTiming.NowMs()))
                        {localFailures++;core.Deferred("正在后台准备新鲜入口证据，准备好后再切换并点击");Push();}
                        else
                        {
                            var prepared=compatibility?new FastEntryCheck(false,picture.Observation.Source,0,"compatibility_requested"):session.VerifyPreparedEntry(picture,picture.Observation.Source,token);
                            journal.Write("takeover_pixel_prepare",prepared);
                            if((!compatibility&&!prepared.Confirmed) || !PreparedEntryPolicy.ReadyForRoute(picture.Observation,true,compatibility,FrameTiming.NowMs())){localFailures++;core.Deferred("正在后台重新确认入口画面，尚未接管");nextCheck=FrameTiming.NowMs()+1000;Push();if(token.WaitHandle.WaitOne(1000))throw new OperationCanceledException();continue;}
                            if(!permitted()){focusRetryPermit=null;retryBudget.Clear();automaticGrant=false;core.ObservationUnavailable("已取消自动接管，等待确认");Push();continue;}
                            if(FinishExpiredFocusRetry())break;
                            if(!core.WantsStart)continue;
                            if(!retryBudget.CanResume(FrameTiming.NowMs()))retryBudget.Begin(FrameTiming.NowMs());
                            automaticGrant=automaticTransaction;
                            Interlocked.Exchange(ref approvalRequested,0);Interlocked.Exchange(ref approval,0);takeover.Attempted();
                            token.ThrowIfCancellationRequested();core.BeginAction();Push();
                            journal.Write("takeover_intent",new{target=session.Window.ToInt64(),pid=session.Pid,unattended=Mode.Enabled,automaticTransaction});
                            Interaction? activeLease=null;
                            var startupSamples=new List<Observation>(3);double inputCompleted=double.NaN;
                            var result=FocusTransaction.Run(desktop,session.Window,session.Pid,()=>token.IsCancellationRequested||activeLease?.InputAttempted!=true&&!permitted(),lease=>{activeLease=lease;return StartOnce(session,desktop,lease,token,journal,picture,compatibility,permitted,(sample,completed)=>{startupSamples.Add(sample);inputCompleted=completed;});},authorizedMotion:true);
                            journal.Write("takeover_result",result);
                            if(!permitted()&&activeLease?.InputAttempted!=true){focusRetryPermit=null;retryBudget.Clear();automaticGrant=false;core.ActionDeferred("已取消自动接管，等待确认");}
                            else if(result.Deferred && retryBudget.Defer(FrameTiming.NowMs()))
                            {
                                if(result.Error==FocusTransaction.ActivationFailure)
                                {
                                    focusRetryPermit=permitted;
                                    core.ActionDeferred("暂未切到游戏，正在重新确认入口后重试");
                                }
                                else
                                {
                                    localFailures++;
                                    core.ActionDeferred("尚未点击，正在等待新画面重新确认本次接管");
                                }
                                journal.Write("takeover_deferred",new{retryBudget.Attempts,retryBudget.Expires,result.Error});
                            }
                            else{localFailures=0;focusRetryPermit=null;retryBudget.Clear();core.ActionFinished(result.Started,result.Restored,FrameTiming.NowMs(),result.LastSource,result.Deferred && result.Error!=FocusTransaction.ActivationFailure?"画面或操作持续未稳定，本次接管已暂停，请稍后再试":result.Error,result.ControlTransferred);
                                if(result.Started){double current=FrameTiming.NowMs();bool adopted=core.AdoptStartCountdown(startupSamples,inputCompleted,current);journal.Write("startup_countdown",new{adopted,samples=startupSamples.Select(o=>new{o.Source,o.Now,o.CountdownSeconds,o.CountdownQuality,o.CountdownReason}),core.Countdown.EndSourceMs,appliedAt=current});
                                    foreach(var sample in startupSamples)experience?.Observe(sample,core.State,core.Returns,core.Countdown.Reanchors,core.NextObservationDelayMs(current));
                                    if(startupSamples.Count>0){var latest=startupSamples[^1];image=latest.ImagePath;frameTime=DateTime.Now;age=current-latest.Source;}}
                            }
                            Push();
                        }
                    }
                }
                else {Interlocked.Exchange(ref approvalRequested,0);Interlocked.Exchange(ref approval,0);}

                if(!core.Done)
                {
                    double completed=FrameTiming.NowMs();int desired=core.NextObservationDelayMs(completed);
                    var schedule=ObservationSchedule.Plan(cycleStarted,completed,desired);int delay=schedule.WaitMs;nextCheck=schedule.NextCheck;Push();
                    if(desired<=5000)journal.Write("observation_cycle",new{cycleStarted,source=picture.Observation.Source,completed,processingMs=completed-cycleStarted,requestedIntervalMs=desired,waitMs=delay,nextCheck,picture.Metrics});
                    if(delay!=previousDelay){previousDelay=delay;journal.Write("observation_schedule",new{delayMs=delay,nextCheck,core.Countdown.EndSourceMs,core.Countdown.Status});}
                    if(WaitHandle.WaitAny(new[]{token.WaitHandle,Mode.Changed},delay)==0)throw new OperationCanceledException();
                }
            }
            if(token.IsCancellationRequested)core.Stop();nextCheck=null;Push();
        }
        catch(OperationCanceledException){core.Stop();nextCheck=null;Push();}
        catch(Exception ex){core.Pause(ex.Message);nextCheck=null;Push(ex.ToString());try{journal.Write("paused",new{error=ex.Message,problemCode=ProblemCatalog.Identify(ex.Message).Code});}catch{}}
        finally{experience?.Suspend();if(!demo)try{trace.End(new{state=core.State.ToString(),core.Message,core.Starts,core.Returns,cancelled=token.IsCancellationRequested,problemCode=core.State==Phase.Paused?ProblemCatalog.Identify(core.Message).Code:null});}catch(Exception ex){Publish(Latest with{Phase=Phase.Paused,Message="操作已停止，但结束记录写入失败",Detail=ex.Message});}}
    }
    ActionEvidence StartOnce(GameSession session,Desktop desktop,Interaction lease,CancellationToken token,Journal journal,Picture seed,bool compatibility=false,Func<bool>? permitted=null,Action<Observation,double>? countdownSample=null)
    {
        double foregroundSince=FrameTiming.NowMs();
        bool Cancelled()=>token.IsCancellationRequested||!lease.InputAttempted&&permitted?.Invoke()==false;
        Picture Capture()
        {
            token.ThrowIfCancellationRequested();
            try{return session.Capture(token,foregroundSince);}
            catch(CaptureFailureException ex){journal.Write("capture_failed",new{error=ex.Message,phase="action",reasons=ex.Reasons,evidence=ex.Evidence});throw;}
        }
        Check Check(Picture p)
        {
            var target=InputPreflight.Read(session.Pid);var self=InputPreflight.Read((uint)Environment.ProcessId);
            var current=WindowGeometryReader.Read(session.Window);
            var point=p.Map.ScreenCenter(p.Icon??throw new IOException("当前帧没有入口位置"),current);int x=point.X,y=point.Y;
            return new(session.Identity,desktop.Foreground==session.Window,InputNative.GetAncestor(InputNative.WindowFromPoint(new InputNative.Point{X=x,Y=y}),2)==session.Window,
                p.Map.Matches(current),!FocusTransaction.CanDispatch(desktop,lease,session.Window,session.Pid,Cancelled()),InputNative.ButtonHeld,InputPreflight.Compare(self.IntegrityRid,target.IntegrityRid),
                p.Observation.Location,p.Observation.Count,p.Map.Width,p.Map.Height,FrameTiming.NowMs()-p.Observation.Source,p.Icon,p.Observation.D2,WindowFrame:true,UiScale:p.UiScale);
        }
        // Normal path uses current local classifiers. A logged, single full
        // compatibility recheck remains available if the local path fails.
        var b=seed;
        if(!desktop.Idle)return new(false,b.Observation.Source,"按键尚未释放，返回后台准备",Retryable:true);
        FastEntryCheck fast;
        if(compatibility){b=Capture();fast=new(EntryReadiness.Confirmed(b.Observation),b.Observation.Source,b.Metrics?.TotalMs??0,"compatibility_full_recheck");if(!PreclickEvidence.FreshPair(seed.Observation.Source,b.Observation.Source,foregroundSince)||b.Icon!=seed.Icon||b.Map.Snapshot!=seed.Map.Snapshot)return new(false,b.Observation.Source,"兼容复核的入口或窗口已变化，未点击");}
        else fast=session.VerifyPreparedEntry(seed,foregroundSince,token);
        journal.Write("entry_fast_foreground",fast);
        if(!fast.Confirmed)return new(false,b.Observation.Source,"入口快速复核尚未一致，返回后台准备",Retryable:true);
        string? refusal=ClickPolicy.Refusal(Check(b));if(refusal!=null || !EntryReadiness.Confirmed(b.Observation))return new(false,b.Observation.Source,"入口证据已变化，返回后台准备："+(refusal??"开始栏证据不足"),Retryable:true);
        if(!FocusTransaction.CanDispatch(desktop,lease,session.Window,session.Pid,Cancelled()))return new(false,b.Observation.Source,"接管许可、前台或按键状态变化，未点击",Retryable:true);
        var point=b.Map.ScreenCenter(b.Icon!,WindowGeometryReader.Read(session.Window));int sx=point.X,sy=point.Y;
        journal.Write("click_intent",new{screenPoint=new[]{sx,sy},source=b.Observation.Source,geometry=b.Map.Snapshot,foregroundBeforeClickMs=FrameTiming.NowMs()-foregroundSince,b.Metrics,fastPixelSource=fast.Source,fastPixelCheckMs=fast.ElapsedMs});
        InputNative.LastPressSent=InputNative.LastReleaseSent=null;var shot=new OneShot();bool pressInvoked=false;
        (uint PressSent,uint ReleaseSent) sent;
        try{sent=shot.Submit(Check(b),()=>Mode.Dispatch(permitted??(()=>true),()=>{if(!FocusTransaction.CanDispatch(desktop,lease,session.Window,session.Pid,Cancelled()))throw new OperationCanceledException("接管状态变化，未发起点击");b.Map.EnsureCurrent(WindowGeometryReader.Read(session.Window));pressInvoked=true;lease.InputAttempted=true;lease.SafeToRestore=false;Interlocked.Exchange(ref buttonPending,1);return InputNative.Press(sx,sy);}),()=>{Thread.Sleep(80);token.ThrowIfCancellationRequested();},()=>pressInvoked?InputNative.Release():0);}
        finally
        {
            if(pressInvoked)
            {
                lease.SafeToRestore=InputNative.LastReleaseSent==1 || InputNative.LastPressSent==0;
                if(lease.SafeToRestore)Interlocked.Exchange(ref buttonPending,0);
                Thread.Sleep(60);lease.OwnInput(InputNative.LastPressSent>0?point:lease.ExpectedPointer,desktop.InputTick);
            }
        }
        double completed=FrameTiming.NowMs();journal.Write("input_return",new{sent.PressSent,sent.ReleaseSent,inputCompletedQpcMs=completed});
        if(sent.PressSent!=2 || sent.ReleaseSent!=1)return new(false,b.Observation.Source,"输入未完整提交，请人工确认鼠标已松开");
        // Input is complete and released. From here only target-window WGC evidence
        // is needed; changing foreground must not abort passive confirmation.
        var feedback=new StartFeedback(FeedbackFocus.Any);feedback.InputCompleted(sent.PressSent,sent.ReleaseSent,completed);double lastSource=b.Observation.Source;
        for(int i=0;i<3 && !feedback.Done;i++)
        {
            if(token.WaitHandle.WaitOne(200))throw new OperationCanceledException();var p=Capture();lastSource=p.Observation.Source;
            countdownSample?.Invoke(p.Observation,completed);
            feedback.Observe(new(p.Observation.Source,FrameTiming.NowMs(),session.Identity,desktop.Foreground==session.Window,p.Bounds==b.Bounds,p.Observation.Location,p.Observation.Count,p.Observation.D2));
            journal.Write("start_feedback_sample",new{index=i+1,p.Observation.Source,p.Observation.Now,sourceAgeMs=p.Observation.Now-p.Observation.Source,p.Observation.Identity,p.Observation.Geometry,p.Observation.Location,p.Observation.Count,p.Observation.D2,p.Observation.PanelLocation,p.Observation.CountdownSeconds,p.Observation.CountdownQuality,p.Observation.CountdownReason,p.Metrics,feedbackState=feedback.State.ToString(),feedback.Reason,session.TimerEvidencePath,session.TimerEvidenceError,session.EntryEvidencePath,session.EntryEvidenceError});
            if(feedback.State==FeedbackState.StartFeedbackObserved)break;
        }
        feedback.Complete(FrameTiming.NowMs());bool started=feedback.State==FeedbackState.StartFeedbackObserved;
        journal.Write("start_feedback",new{state=feedback.State.ToString(),feedback.Reason});return new(started,lastSource,started?null:"点击后未确认进入表演，请检查当前位置的地面是否允许开启表演；也可能是画面反馈未识别。调整位置后重新开始辅助");
    }
}
