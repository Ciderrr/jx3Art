import type {ReactNode} from 'react';
import {motion,useReducedMotion,type HTMLMotionProps} from 'motion/react';
import {useMotionTokens} from '@animate-ui/react/utils/motion';
import {Button} from './vendor/stageshow/kit/controls/Button';
import './settings-button.css';

type Props=Omit<HTMLMotionProps<'button'>,'children'>&{children:ReactNode;icon?:ReactNode};

export function SettingsButton({children,icon,className='',disabled,...props}:Props){
 const reduce=useReducedMotion();
 const {spring}=useMotionTokens();
 return <motion.button {...props} type="button" disabled={disabled}
   className={`jx3-settings-button ${className}`}
   whileHover={reduce||disabled?undefined:{y:-1}}
   whileTap={reduce||disabled?undefined:{y:0,scale:.96}} transition={spring.selection}>
   <Button role="presentation" size="large" icon={icon}><span>{children}</span></Button>
 </motion.button>;
}
