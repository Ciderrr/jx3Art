export type Snapshot={waitingHint?:"minimized"|"foreground"|null;countdownEstimated?:boolean;settingsRequest?:number;wujieAvailable?:boolean;wujieTrial?:boolean;version?:string;appIcon?:string;iconPreview?:string;maximized?:boolean;schema:number;revision:number;epoch:number;phase:string;title:string;running:boolean;stopping:boolean;closing:boolean;ever:boolean;starts:number;plan:number;automatic:boolean;skin:string;skipReminder:boolean;startHotkey:string;stopHotkey:string;seconds:number|null;reason:string|null;awaitingApproval:boolean;canStart:boolean;preparationToken:string|null;gain:string;total:string|null;partial:boolean;health:string|null;error:string|null};
export type Capture={target:'start'|'stop';value:string};
export interface Bridge{native:boolean;request(type:string,payload?:Record<string,unknown>,epoch?:number):Promise<unknown>;subscribe(fn:(s:Snapshot)=>void):()=>void;onCapture(fn:(c:Capture)=>void):()=>void;}
type WebView={postMessage:(value:unknown)=>void;addEventListener:(name:string,fn:(e:{data:unknown})=>void)=>void};
declare global{interface Window{chrome?:{webview?:WebView};__artistSnapshot?:Snapshot;}}
export class NativeBridge implements Bridge{
 native=true;private id=0;private current:Snapshot|null=null;private listeners=new Set<(s:Snapshot)=>void>();private captures=new Set<(c:Capture)=>void>();private pending=new Map<number,{resolve:(v:unknown)=>void;reject:(e:Error)=>void;timer:number}>();
 constructor(private webview:WebView){webview.addEventListener('message',e=>{const m=e.data as {kind:string;value:Snapshot;id:number;ok:boolean;error:string;result:unknown;target:'start'|'stop'};
 if(m.kind==='snapshot'){if(this.current&&m.value.revision<=this.current.revision)return;this.current=m.value;window.__artistSnapshot=m.value;this.listeners.forEach(f=>f(m.value));}
 else if(m.kind==='reply'){const p=this.pending.get(m.id);if(!p)return;clearTimeout(p.timer);this.pending.delete(m.id);if(m.ok)p.resolve(m.result);else p.reject(new Error(m.error));}
 else if(m.kind==='capture')this.captures.forEach(f=>f(m as unknown as Capture));});}
 subscribe(fn:(s:Snapshot)=>void){this.listeners.add(fn);if(this.current)fn(this.current);return()=>{this.listeners.delete(fn);};}
 onCapture(fn:(c:Capture)=>void){this.captures.add(fn);return()=>{this.captures.delete(fn);};}
 request(type:string,payload:Record<string,unknown>={},epoch?:number){return new Promise<unknown>((resolve,reject)=>{const id=++this.id;const timer=type==='saveReport'||type==='checkUpdate'?0:window.setTimeout(()=>{this.pending.delete(id);reject(new Error('界面请求超时；全局停止键仍可使用。'));},10000);this.pending.set(id,{resolve,reject,timer});this.webview.postMessage({schema:1,id,epoch:epoch??this.current?.epoch??0,type,payload});});}
}
