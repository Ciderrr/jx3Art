import {createContext,useContext,type ComponentProps} from 'react';
import {DropdownMenu as Radix} from 'radix-ui';
import {AnimatePresence,motion,useReducedMotion} from 'motion/react';

// High-frequency desktop menus share one visible surface transition. Rows respond
// immediately; they do not each stagger, blur, or chase the pointer with a spring.
const Open=createContext(false);
function Root(props:ComponentProps<typeof Radix.Root>){return <Open.Provider value={!!props.open}><Radix.Root {...props}/></Open.Provider>;}
function Portal(props:ComponentProps<typeof Radix.Portal>){return <Radix.Portal {...props} forceMount/>;}
function Content({children,...props}:ComponentProps<typeof Radix.Content>){
 const open=useContext(Open),reduce=useReducedMotion();
 return <AnimatePresence>{open&&<Radix.Content {...props} onCloseAutoFocus={e=>{props.onCloseAutoFocus?.(e);if(document.querySelector('[role="dialog"]'))e.preventDefault();}} forceMount asChild><motion.div data-menu-motion="surface" initial={{opacity:0,scale:reduce?1:.96,y:reduce?0:-10}} animate={{opacity:1,scale:1,y:0}} exit={{opacity:0,scale:reduce?1:.98,y:reduce?0:-6,transition:{duration:reduce?.1:.16}}} transition={{duration:reduce?.12:.28,ease:[.22,.61,.36,1]}} style={{transformOrigin:'var(--radix-dropdown-menu-content-transform-origin)'}}>{children}</motion.div></Radix.Content>}</AnimatePresence>;
}
export const QuickMenu={Root,Portal,Content,Trigger:Radix.Trigger,Label:Radix.Label,Item:Radix.Item,Separator:Radix.Separator,RadioGroup:Radix.RadioGroup,RadioItem:Radix.RadioItem,ItemIndicator:Radix.ItemIndicator};
