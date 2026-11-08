import {useState} from 'react';
import {ArrowUpRight,Copy,RefreshCw} from 'lucide-react';
import {SettingsButton} from './SettingsButton';
import {SettingsNotice,type SettingsMessage} from './SettingsNotice';
import type {Bridge} from './bridge';
import './about.css';

type Area='project'|'updates'|'feedback';
type Notice=SettingsMessage&{area:Area};
type Action='openProfile'|'openAuthor'|'checkUpdate'|'copyEmail'|'openIssues';

export function AboutPanel({bridge}:{bridge:Bridge}){
 const [notice,setNotice]=useState<Notice|null>(null);
 const [busy,setBusy]=useState<Action|null>(null);

 const run=async(action:Action,area:Area)=>{
   if(busy)return;
   setNotice(null);
   setBusy(action);
   try{
     const result=await bridge.request(action);
     if(action==='copyEmail'&&typeof result==='string')setNotice({area,text:result,error:false});
   }catch(error){
     setNotice({area,text:error instanceof Error?error.message:'操作未完成，请重试。',error:true});
   }finally{setBusy(null);}
 };

 const feedback=(area:Area)=><SettingsNotice area={area} value={notice?.area===area?notice:null} onClose={()=>setNotice(null)}/>;

 return <div className="jx3-about-panel">
   <section className="jx3-about-project" aria-label="项目与作者">
     <p className="jx3-about-license"><strong>MIT 开源</strong><span>本助手自有源码可自由使用、修改和分发，须保留版权与许可声明。</span></p>
     <a href="https://github.com/Ciderrr/jx3Art" onClick={event=>{event.preventDefault();void run('openProfile','project');}}>项目 GitHub 主页 <ArrowUpRight size={14} aria-hidden/></a>
     <a href="https://github.com/Ciderrr" onClick={event=>{event.preventDefault();void run('openAuthor','project');}}>作者个人主页 <ArrowUpRight size={14} aria-hidden/></a>
     <p className="jx3-about-identity">唯满侠 <span>双开门盾萝@长安城</span></p>
     {feedback('project')}
   </section>
   <section aria-labelledby="about-updates">
     <div className="jx3-about-row">
       <h3 id="about-updates">软件更新</h3>
       <SettingsButton data-about-action="checkUpdate" icon={<RefreshCw size={14} aria-hidden/>} disabled={busy!==null} onClick={()=>void run('checkUpdate','updates')}>{busy==='checkUpdate'?'正在检查…':'检查更新'}</SettingsButton>
     </div>
     {feedback('updates')}
   </section>
   <section aria-labelledby="about-feedback">
     <h3 id="about-feedback">问题反馈</h3>
     <div className="jx3-about-row">
       <div><strong>邮件反馈</strong><input className="jx3-about-email" aria-label="反馈邮箱" readOnly value="147984829@qq.com" onFocus={event=>event.target.select()}/></div>
       <SettingsButton data-about-action="copyEmail" icon={<Copy size={14} aria-hidden/>} disabled={busy!==null} onClick={()=>void run('copyEmail','feedback')}>复制邮箱</SettingsButton>
     </div>
     <div className="jx3-about-row">
       <div><strong>GitHub Issues</strong><p>项目的公开反馈区，可提交故障或建议，并跟进回复。</p></div>
       <SettingsButton data-about-action="openIssues" icon={<ArrowUpRight size={14} aria-hidden/>} disabled={busy!==null} onClick={()=>void run('openIssues','feedback')}>打开反馈区</SettingsButton>
     </div>
     {feedback('feedback')}
   </section>
 </div>;
}
