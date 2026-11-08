import {useEffect,useRef} from 'react';
import {AnimatePresence,motion,useReducedMotion} from 'motion/react';
import {useMotionTokens} from '@animate-ui/react/utils/motion';
import {Check,CircleAlert,X} from 'lucide-react';

export type SettingsMessage={text:string;error:boolean};

export function SettingsNotice({value,area,onClose}:{value?:SettingsMessage|null;area:string;onClose:()=>void}){
 const close=useRef(onClose);
 close.current=onClose;
 const reduce=useReducedMotion();
 const {spring}=useMotionTokens();
 useEffect(()=>{
   if(!value||value.error)return;
   const timer=window.setTimeout(()=>close.current(),3000);
   return ()=>window.clearTimeout(timer);
 },[value]);
 return <AnimatePresence initial={false}>
   {value&&<motion.div className="jx3-about-notice" data-area={area} data-error={value.error}
     initial={{opacity:0,y:reduce?0:5}} animate={{opacity:1,y:0}} exit={{opacity:0,y:reduce?0:3}}
     transition={reduce?{duration:0}:spring.selection}>
     {value.error?<CircleAlert size={15} aria-hidden/>:<Check size={15} aria-hidden/>}
     <span role={value.error?'alert':'status'}>{value.text}</span>
     <button type="button" aria-label="关闭提示" onClick={onClose}><X size={14} aria-hidden/></button>
   </motion.div>}
 </AnimatePresence>;
}
